using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Acme.Notes.Api.IntegrationTests.Webhooks;

/// <summary>Webhooks other systems send: signatures, replays, duplicates and queued handling.</summary>
public sealed class IncomingWebhookTests(ApiFactory api) : IAsyncLifetime
{
    /// <summary>The source the test handler handles.</summary>
    public const string Source = "test";

    /// <summary>A configured source with no handler.</summary>
    public const string UnhandledSource = "unhandled";

    public const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = (await TestUsers.SignInNewUserAsync(api, Roles.Admin)).Client;

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_signed_webhook_is_accepted_and_handled_once_even_when_sent_twice()
    {
        using var client = api.CreateClient();
        var messageId = $"msg_{Guid.NewGuid():N}";

        using var first = await SendAsync(client, Source, messageId, """{"type":"contact.created","data":{}}""");
        using var second = await SendAsync(client, Source, messageId, """{"type":"contact.created","data":{}}""");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        // The sender retried; it hears success, and nothing is stored or handled twice.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await Eventually.HoldsAsync(() => Task.FromResult(api.HandledWebhooks.TimesHandled(messageId)), times => times == 1);
        var stored = await ListAsync($"?source={Source}&pageSize=100");
        var message = Assert.Single(stored.Items, item => item.MessageId == messageId);
        Assert.Equal("contact.created", message.EventType);
    }

    [Fact]
    public async Task A_wrong_signature_answers_401_and_stores_nothing()
    {
        using var client = api.CreateClient();
        var messageId = $"msg_{Guid.NewGuid():N}";

        using var response = await SendAsync(client, Source, messageId, """{"type":"x"}""", secret: "whsec_" + Convert.ToBase64String(new byte[32]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain((await ListAsync($"?source={Source}&pageSize=100")).Items, item => item.MessageId == messageId);
    }

    [Fact]
    public async Task A_body_changed_after_signing_answers_401()
    {
        using var client = api.CreateClient();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var messageId = $"msg_{Guid.NewGuid():N}";
        using var request = Request(Source, messageId, timestamp, StandardWebhooks.Sign(Secret, messageId, timestamp, Encoding.UTF8.GetBytes("""{"amount":1}""")), """{"amount":1000}""");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_replayed_old_message_answers_401_even_with_a_valid_signature()
    {
        using var client = api.CreateClient();

        using var response = await SendAsync(client, Source, $"msg_{Guid.NewGuid():N}", """{"type":"x"}""", sentAt: DateTimeOffset.UtcNow.AddMinutes(-10));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_source_answers_404()
    {
        using var client = api.CreateClient();

        using var response = await SendAsync(client, "nobody", $"msg_{Guid.NewGuid():N}", "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_failing_handler_is_retried_until_it_succeeds()
    {
        using var client = api.CreateClient();
        var messageId = $"msg_{Guid.NewGuid():N}";

        using var response = await SendAsync(client, Source, messageId, """{"type":"x","failTimes":1}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var handled = await Eventually.HoldsAsync(
            async () => (await ListAsync($"?source={Source}&pageSize=100")).Items.Single(item => item.MessageId == messageId),
            message => message.Status == "succeeded");
        Assert.Equal(2, handled.Attempts);
        Assert.Equal(1, api.HandledWebhooks.TimesHandled(messageId));
    }

    [Fact]
    public async Task A_message_with_no_handler_fails_and_can_be_retried_by_hand()
    {
        using var client = api.CreateClient();
        var messageId = $"msg_{Guid.NewGuid():N}";
        using var response = await SendAsync(client, UnhandledSource, messageId, "{}");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var failed = await Eventually.HoldsAsync(
            async () => (await ListAsync($"?source={UnhandledSource}&pageSize=100")).Items.Single(item => item.MessageId == messageId),
            message => message.Status == "failed");
        Assert.Contains("No handler", failed.LastError, StringComparison.Ordinal);

        using var retried = await _admin.PostAsync(new Uri($"/v1/webhooks/incoming/{failed.Id}/retry", UriKind.Relative), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string source, string messageId, string body, string secret = Secret, DateTimeOffset? sentAt = null)
    {
        var timestamp = (sentAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var request = Request(source, messageId, timestamp, StandardWebhooks.Sign(secret, messageId, timestamp, Encoding.UTF8.GetBytes(body)), body);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage Request(string source, string messageId, long timestamp, string signature, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/webhooks/incoming/{source}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(StandardWebhooks.IdHeader, messageId);
        request.Headers.Add(StandardWebhooks.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(StandardWebhooks.SignatureHeader, signature);
        return request;
    }

    private async Task<PagedResult<IncomingWebhookResponse>> ListAsync(string query) =>
        (await _admin.GetFromJsonAsync<PagedResult<IncomingWebhookResponse>>(new Uri($"/v1/webhooks/incoming{query}", UriKind.Relative), TestContext.Current.CancellationToken))!;
}
