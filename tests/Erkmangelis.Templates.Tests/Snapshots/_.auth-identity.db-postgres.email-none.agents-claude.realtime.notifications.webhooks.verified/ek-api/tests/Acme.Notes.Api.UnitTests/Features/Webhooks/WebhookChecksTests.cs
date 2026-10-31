namespace Acme.Notes.Api.UnitTests.Features.Webhooks;

public sealed class WebhookChecksTests
{
    private static readonly OutgoingWebhooksOptions _production = new();
    private static readonly int[] _schedule = [5, 60];

    [Theory]
    [InlineData("https://hooks.example.com/path?x=1", true)]
    [InlineData("https://8.8.8.8/hook", true)]
    [InlineData("http://hooks.example.com/hook", false)]
    [InlineData("ftp://hooks.example.com/hook", false)]
    [InlineData("https://localhost/hook", false)]
    [InlineData("https://127.0.0.1/hook", false)]
    [InlineData("https://192.168.0.10/hook", false)]
    [InlineData("https://[fd00::1]/hook", false)]
    [InlineData("https://user:secret@hooks.example.com/hook", false)]
    [InlineData("/relative", false)]
    public void Subscription_urls_must_be_public_https_without_credentials(string url, bool accepted)
    {
        Assert.Equal(accepted, WebhookEndpoints.CheckUrl(url, _production) is null);
    }

    [Fact]
    public void Development_can_allow_local_http_urls()
    {
        var development = new OutgoingWebhooksOptions { AllowHttp = true, AllowPrivateNetworks = true };

        Assert.Null(WebhookEndpoints.CheckUrl("http://localhost:5005/hook", development));
    }

    [Fact]
    public void Unknown_event_types_are_named_in_the_error()
    {
        var errors = WebhookEndpoints.Check("https://hooks.example.com", ["note.created", "note.exploded"], _production);

        Assert.NotNull(errors);
        Assert.Contains("note.exploded", errors["EventTypes"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_schedules_the_next_attempt_until_the_delays_run_out()
    {
        var now = DateTimeOffset.UnixEpoch;
        var delivery = new WebhookDelivery { EventType = "note.created", Payload = "{}", NextAttemptAt = now };

        Outbox.Failed(delivery, "500", now, _schedule);
        Assert.Equal((OutboxStatus.Pending, 1, now.AddSeconds(5)), (delivery.Status, delivery.Attempts, delivery.NextAttemptAt));

        Outbox.Failed(delivery, "500", now, _schedule);
        Assert.Equal((OutboxStatus.Pending, 2, now.AddSeconds(60)), (delivery.Status, delivery.Attempts, delivery.NextAttemptAt));

        Outbox.Failed(delivery, "500", now, _schedule);
        Assert.Equal((OutboxStatus.Failed, 3), (delivery.Status, delivery.Attempts));

        Outbox.Retry(delivery, now);
        Assert.Equal((OutboxStatus.Pending, 0), (delivery.Status, delivery.Attempts));
    }
}
