using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Acme.Notes.Api.IntegrationTests.Webhooks;

/// <summary>Webhooks this API sends: signed deliveries, retries, the delivery log and URL checks.</summary>
public sealed class OutgoingWebhookTests(ApiFactory api) : IAsyncLifetime
{
    private static readonly string[] _unknownEventTypes = ["note.exploded"];

    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = (await TestUsers.SignInNewUserAsync(api, Roles.Admin)).Client;

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_subscriber_gets_a_signed_delivery_when_a_note_is_created()
    {
        var url = WebhookReceiver.NewUrl();
        var subscription = await SubscribeAsync(url, AppEventTypes.NoteCreated);
        var title = $"Hooked {Guid.NewGuid():N}";

        using var created = await _admin.PostAsJsonAsync("/v1/notes", new { title, content = "Ping" }, TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();

        // Every note created during the test run reaches this subscription; find this one.
        var delivery = await Eventually.HoldsAsync(
            () => Task.FromResult(api.Subscribers.ReceivedAt(url).FirstOrDefault(webhook => Body(webhook).GetProperty("data").GetProperty("title").GetString() == title)),
            webhook => webhook is not null);
        var verification = StandardWebhooks.Verify(
            [subscription.Secret],
            delivery!.Headers[StandardWebhooks.IdHeader],
            delivery.Headers[StandardWebhooks.TimestampHeader],
            delivery.Headers[StandardWebhooks.SignatureHeader],
            delivery.Body,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        Assert.Equal(WebhookVerification.Valid, verification);
        Assert.Equal(AppEventTypes.NoteCreated, Body(delivery).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_failed_delivery_is_retried_with_the_same_webhook_id_and_logged()
    {
        var url = WebhookReceiver.NewUrl();
        api.Subscribers.Answer(url, HttpStatusCode.InternalServerError);
        var subscription = await SubscribeAsync(url, WebhookEndpoints.TestEventType);

        using var queued = await _admin.PostAsync(new Uri($"/v1/webhooks/subscriptions/{subscription.Subscription.Id}/test", UriKind.Relative), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);

        var log = await Eventually.HoldsAsync(
            () => _admin.GetFromJsonAsync<PagedResult<WebhookDeliveryResponse>>(new Uri($"/v1/webhooks/subscriptions/{subscription.Subscription.Id}/deliveries", UriKind.Relative), TestContext.Current.CancellationToken)!,
            page => page!.Items is [{ Status: "succeeded" }]);
        var entry = log!.Items[0];
        Assert.Equal(2, entry.Attempts);
        Assert.Equal(200, entry.LastStatusCode);
        var received = api.Subscribers.ReceivedAt(url);
        Assert.Equal(2, received.Count);
        Assert.Equal(received[0].Headers[StandardWebhooks.IdHeader], received[1].Headers[StandardWebhooks.IdHeader]);
        Assert.Equal(entry.Id.ToString(), received[0].Headers[StandardWebhooks.IdHeader]);
    }

    [Fact]
    public async Task A_subscriber_that_answers_410_is_turned_off()
    {
        var url = WebhookReceiver.NewUrl();
        api.Subscribers.Answer(url, HttpStatusCode.Gone);
        var subscription = await SubscribeAsync(url, WebhookEndpoints.TestEventType);

        using var queued = await _admin.PostAsync(new Uri($"/v1/webhooks/subscriptions/{subscription.Subscription.Id}/test", UriKind.Relative), null, TestContext.Current.CancellationToken);

        var disabled = await Eventually.HoldsAsync(
            () => _admin.GetFromJsonAsync<WebhookSubscriptionResponse>(new Uri($"/v1/webhooks/subscriptions/{subscription.Subscription.Id}", UriKind.Relative), TestContext.Current.CancellationToken)!,
            current => current is { Enabled: false });
        Assert.Contains("410", disabled!.DisabledReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://hooks.example.com/plain")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://10.0.0.8/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://user:password@hooks.example.com/hook")]
    [InlineData("hooks.example.com/hook")]
    public async Task Urls_that_could_reach_private_networks_or_leak_credentials_are_refused(string url)
    {
        using var response = await _admin.PostAsJsonAsync(
            "/v1/webhooks/subscriptions",
            new { url, eventTypes = new[] { AppEventTypes.NoteCreated } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_event_types_are_refused()
    {
        using var response = await _admin.PostAsJsonAsync(
            "/v1/webhooks/subscriptions",
            new { url = WebhookReceiver.NewUrl(), eventTypes = _unknownEventTypes },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rotating_the_secret_signs_later_deliveries_with_the_new_one()
    {
        var url = WebhookReceiver.NewUrl();
        var subscription = await SubscribeAsync(url, WebhookEndpoints.TestEventType);

        using var rotated = await _admin.PostAsync(new Uri($"/v1/webhooks/subscriptions/{subscription.Subscription.Id}/secret", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var secret = (await rotated.Content.ReadFromJsonAsync<WebhookSecretResponse>(TestContext.Current.CancellationToken))!.Secret;
        Assert.NotEqual(subscription.Secret, secret);
        using var queued = await _admin.PostAsync(new Uri($"/v1/webhooks/subscriptions/{subscription.Subscription.Id}/test", UriKind.Relative), null, TestContext.Current.CancellationToken);

        var delivery = await Eventually.HoldsAsync(() => Task.FromResult(api.Subscribers.ReceivedAt(url) is [var first, ..] ? first : null), webhook => webhook is not null);
        Assert.Equal(
            WebhookVerification.Valid,
            StandardWebhooks.Verify([secret], delivery!.Headers[StandardWebhooks.IdHeader], delivery.Headers[StandardWebhooks.TimestampHeader], delivery.Headers[StandardWebhooks.SignatureHeader], delivery.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Only_users_allowed_to_manage_webhooks_see_subscriptions()
    {
        var member = await TestUsers.SignInNewUserAsync(api);

        using var response = await member.Client.GetAsync(new Uri("/v1/webhooks/subscriptions", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<WebhookSecretResponse> SubscribeAsync(Uri url, string eventType)
    {
        using var response = await _admin.PostAsJsonAsync(
            "/v1/webhooks/subscriptions",
            new { url, eventTypes = new[] { eventType }, description = "Test" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WebhookSecretResponse>(TestContext.Current.CancellationToken))!;
    }

    private static JsonElement Body(ReceivedWebhook webhook) => JsonSerializer.Deserialize<JsonElement>(webhook.Body);
}
