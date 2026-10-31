using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MyProject.Api.UnitTests.Features.Notifications;

public sealed class PushAndSmsTests : IDisposable
{
    private static readonly PushMessage _message = new(Guid.NewGuid(), NotificationTypes.Announcement, "Hello", "World", "/inbox");

    private readonly RSA _googleKey = RSA.Create(2048);
    private readonly ECDsa _appleKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ProviderTokens _tokens = new(TimeProvider.System);

    public void Dispose()
    {
        _googleKey.Dispose();
        _appleKey.Dispose();
        _tokens.Dispose();
    }

    [Fact]
    public async Task Fcm_signs_in_as_the_service_account_and_reuses_the_access_token()
    {
        var provider = new StubProvider(request => request.RequestUri!.Host == FcmPushSender.TokenHost
            ? Json(HttpStatusCode.OK, """{"access_token":"google-token","expires_in":3600}""")
            : Json(HttpStatusCode.OK, """{"name":"projects/demo/messages/1"}"""));
        var sender = Fcm(provider);

        Assert.Equal(PushOutcome.Sent, await sender.SendAsync("device-1", _message, TestContext.Current.CancellationToken));
        Assert.Equal(PushOutcome.Sent, await sender.SendAsync("device-2", _message, TestContext.Current.CancellationToken));

        var signIn = Assert.Single(provider.Requests, request => request.Uri.Host == FcmPushSender.TokenHost);
        var assertion = Uri.UnescapeDataString(signIn.Body.Split("assertion=")[1]);
        Assert.True(VerifyJwt(assertion, (data, signature) => _googleKey.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
        Assert.Equal("push@demo.iam.gserviceaccount.com", Payload(assertion).GetProperty("iss").GetString());

        var sends = provider.Requests.Where(request => request.Uri.Host == "fcm.googleapis.com").ToList();
        Assert.Equal(2, sends.Count);
        Assert.Equal("/v1/projects/demo/messages:send", sends[0].Uri.AbsolutePath);
        Assert.Equal("Bearer google-token", sends[0].Authorization);
        using var body = JsonDocument.Parse(sends[0].Body);
        Assert.Equal("device-1", body.RootElement.GetProperty("message").GetProperty("token").GetString());
        Assert.Equal("/inbox", body.RootElement.GetProperty("message").GetProperty("data").GetProperty("link").GetString());
    }

    [Fact]
    public async Task Fcm_reports_an_unregistered_token_so_the_device_is_forgotten()
    {
        var provider = new StubProvider(request => request.RequestUri!.Host == FcmPushSender.TokenHost
            ? Json(HttpStatusCode.OK, """{"access_token":"google-token","expires_in":3600}""")
            : Json(HttpStatusCode.NotFound, """{"error":{"status":"NOT_FOUND","details":[{"errorCode":"UNREGISTERED"}]}}"""));

        Assert.Equal(PushOutcome.InvalidToken, await Fcm(provider).SendAsync("gone", _message, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Apns_sends_over_http2_with_a_signed_provider_token()
    {
        var provider = new StubProvider(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(PushOutcome.Sent, await Apns(provider).SendAsync("abc123", _message, TestContext.Current.CancellationToken));

        var request = Assert.Single(provider.Requests);
        Assert.Equal(new Uri("https://api.sandbox.push.apple.com/3/device/abc123"), request.Uri);
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal("com.example.app", request.Headers["apns-topic"]);
        var jwt = request.Authorization!["bearer ".Length..];
        Assert.True(VerifyJwt(jwt, (data, signature) => _appleKey.VerifyData(data, signature, HashAlgorithmName.SHA256)));
        Assert.Equal("TEAM123456", Payload(jwt).GetProperty("iss").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, """{"reason":"Unregistered"}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"reason":"BadDeviceToken"}""")]
    public async Task Apns_reports_dead_tokens(HttpStatusCode status, string reason)
    {
        var provider = new StubProvider(_ => Json(status, reason));

        Assert.Equal(PushOutcome.InvalidToken, await Apns(provider).SendAsync("abc123", _message, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Twilio_posts_the_text_with_basic_auth()
    {
        var provider = new StubProvider(_ => Json(HttpStatusCode.Created, """{"sid":"SM1"}"""));

        await Twilio(provider).SendAsync("+905551234567", "Hello", TestContext.Current.CancellationToken);

        var request = Assert.Single(provider.Requests);
        Assert.Equal("/2010-04-01/Accounts/AC123/Messages.json", request.Uri.AbsolutePath);
        Assert.Equal($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("AC123:secret"))}", request.Authorization);
        Assert.Contains("To=%2B905551234567", request.Body, StringComparison.Ordinal);
        Assert.Contains("From=%2B15550001111", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Twilio_rejections_are_not_retried()
    {
        var provider = new StubProvider(_ => Json(HttpStatusCode.BadRequest, """{"code":21211,"message":"Invalid 'To' Phone Number"}"""));

        await Assert.ThrowsAsync<NotificationRejectedException>(() => Twilio(provider).SendAsync("+1", "Hello", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/notes/1", true)]
    [InlineData("https://app.example.com/notes/1", true)]
    [InlineData("http://app.example.com/notes/1", false)]
    [InlineData("//evil.example.com", false)]
    [InlineData("/\\evil.example.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://user:pass@app.example.com", false)]
    public void Only_app_paths_and_https_links_are_safe(string link, bool safe)
    {
        Assert.Equal(safe, NotificationEndpoints.IsSafeLink(link));
    }

    private FcmPushSender Fcm(StubProvider provider)
    {
        var account = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["client_email"] = "push@demo.iam.gserviceaccount.com",
            ["private_key"] = _googleKey.ExportPkcs8PrivateKeyPem(),
            ["token_uri"] = $"https://{FcmPushSender.TokenHost}/token",
        });
        var options = new NotificationsOptions { Push = { Fcm = { ProjectId = "demo", ServiceAccountJson = account } } };
        return new FcmPushSender(new HttpClient(provider), Options.Create(options), _tokens, TimeProvider.System);
    }

    private ApnsPushSender Apns(StubProvider provider)
    {
        var options = new NotificationsOptions
        {
            Push = { Apns = { TeamId = "TEAM123456", KeyId = "KEY1234567", PrivateKey = _appleKey.ExportPkcs8PrivateKeyPem(), BundleId = "com.example.app", UseSandbox = true } },
        };
        return new ApnsPushSender(new HttpClient(provider), Options.Create(options), _tokens, TimeProvider.System);
    }

    private static TwilioSmsSender Twilio(StubProvider provider)
    {
        var options = new NotificationsOptions { Sms = { Twilio = { AccountSid = "AC123", AuthToken = "secret", From = "+15550001111" } } };
        return new TwilioSmsSender(new HttpClient(provider), Options.Create(options));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static bool VerifyJwt(string jwt, Func<byte[], byte[], bool> verify)
    {
        var parts = jwt.Split('.');
        return parts.Length == 3 && verify(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url(parts[2]));
    }

    private static JsonElement Payload(string jwt) => JsonDocument.Parse(Base64Url(jwt.Split('.')[1])).RootElement;

    private static byte[] Base64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }

    private sealed record SeenRequest(Uri Uri, Version Version, string? Authorization, Dictionary<string, string> Headers, string Body);

    /// <summary>Stands in for a provider: answers with <c>answer</c> and keeps what it was sent.</summary>
    private sealed class StubProvider(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SeenRequest(
                request.RequestUri!,
                request.Version,
                request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return answer(request);
        }
    }
}
