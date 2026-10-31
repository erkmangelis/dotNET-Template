using System.Text;

namespace Acme.Notes.Api.UnitTests.Features.Webhooks;

public sealed class StandardWebhooksTests
{
    // The Standard Webhooks reference libraries' own test vector.
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string MessageId = "msg_p5jXN8AQM9LWM0D4loKWxJek";
    private const long Timestamp = 1614265330;
    private static readonly byte[] _body = Encoding.UTF8.GetBytes("""{"test": 2432232314}""");
    private static readonly DateTimeOffset _sentAt = DateTimeOffset.FromUnixTimeSeconds(Timestamp);
    private static readonly TimeSpan _tolerance = TimeSpan.FromMinutes(5);

    [Fact]
    public void Signatures_match_the_specification()
    {
        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", StandardWebhooks.Sign(Secret, MessageId, Timestamp, _body));
    }

    [Fact]
    public void A_correct_signature_verifies()
    {
        Assert.Equal(WebhookVerification.Valid, Verify([Secret], StandardWebhooks.Sign(Secret, MessageId, Timestamp, _body)));
    }

    [Fact]
    public void A_changed_body_does_not_verify()
    {
        var signature = StandardWebhooks.Sign(Secret, MessageId, Timestamp, _body);

        Assert.Equal(
            WebhookVerification.BadSignature,
            StandardWebhooks.Verify([Secret], MessageId, $"{Timestamp}", signature, Encoding.UTF8.GetBytes("""{"test": 1}"""), _sentAt, _tolerance));
    }

    [Fact]
    public void Another_secret_does_not_verify()
    {
        var signature = StandardWebhooks.Sign(StandardWebhooks.NewSecret(), MessageId, Timestamp, _body);

        Assert.Equal(WebhookVerification.BadSignature, Verify([Secret], signature));
    }

    [Fact]
    public void During_a_rotation_either_secret_and_either_signature_verifies()
    {
        var next = StandardWebhooks.NewSecret();
        var both = $"{StandardWebhooks.Sign(next, MessageId, Timestamp, _body)} {StandardWebhooks.Sign(Secret, MessageId, Timestamp, _body)}";

        Assert.Equal(WebhookVerification.Valid, Verify([Secret], both));
        Assert.Equal(WebhookVerification.Valid, Verify([next, Secret], StandardWebhooks.Sign(Secret, MessageId, Timestamp, _body)));
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void A_timestamp_outside_the_tolerance_is_refused(int offsetSeconds)
    {
        var signature = StandardWebhooks.Sign(Secret, MessageId, Timestamp, _body);

        Assert.Equal(
            WebhookVerification.TimestampOutOfRange,
            StandardWebhooks.Verify([Secret], MessageId, $"{Timestamp}", signature, _body, _sentAt.AddSeconds(offsetSeconds), _tolerance));
    }

    [Theory]
    [InlineData("v1,not-base64!")]
    [InlineData("v1a,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=")]
    [InlineData("g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=")]
    public void Malformed_signatures_do_not_verify(string signature)
    {
        Assert.Equal(WebhookVerification.BadSignature, Verify([Secret], signature));
    }

    [Fact]
    public void Missing_headers_are_reported()
    {
        Assert.Equal(WebhookVerification.MissingHeaders, StandardWebhooks.Verify([Secret], null, $"{Timestamp}", "v1,x", _body, _sentAt, _tolerance));
        Assert.Equal(WebhookVerification.MissingHeaders, StandardWebhooks.Verify([Secret], MessageId, "yesterday", "v1,x", _body, _sentAt, _tolerance));
    }

    [Fact]
    public void New_secrets_are_valid_and_different()
    {
        var first = StandardWebhooks.NewSecret();

        Assert.True(StandardWebhooks.IsValidSecret(first));
        Assert.StartsWith(StandardWebhooks.SecretPrefix, first, StringComparison.Ordinal);
        Assert.NotEqual(first, StandardWebhooks.NewSecret());
        Assert.False(StandardWebhooks.IsValidSecret("whsec_c2hvcnQ="));
    }

    private static WebhookVerification Verify(string[] secrets, string signature) =>
        StandardWebhooks.Verify(secrets, MessageId, $"{Timestamp}", signature, _body, _sentAt, _tolerance);
}
