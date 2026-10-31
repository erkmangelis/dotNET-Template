using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.Notifications;

/// <summary>What a push notification carries to the device.</summary>
internal sealed record PushMessage(Guid NotificationId, string Type, string Title, string Body, string? Link);

internal enum PushOutcome
{
    Sent,

    /// <summary>The provider says the token is dead (app uninstalled, token rotated): forget the device.</summary>
    InvalidToken,
}

/// <summary>A provider refused the message for a reason retrying cannot fix (bad credentials, bad number).</summary>
internal sealed class NotificationRejectedException(string message) : Exception(message);

/// <summary>Sends a push to one device through the configured transport.</summary>
internal sealed partial class PushRouter(IOptions<NotificationsOptions> options, IServiceProvider services, ILogger<PushRouter> logger)
{
    public Task<PushOutcome> SendAsync(PushDevice device, PushMessage message, CancellationToken cancellationToken)
    {
        var push = options.Value.Push;
        if (push.Transport == PushTransport.Log)
        {
            LogPush(logger, device.Platform, device.Id, message.Title);
            return Task.FromResult(PushOutcome.Sent);
        }

        return device.Platform switch
        {
            PushPlatform.Fcm when push.Fcm.IsConfigured => services.GetRequiredService<FcmPushSender>().SendAsync(device.Token, message, cancellationToken),
            PushPlatform.Apns when push.Apns.IsConfigured => services.GetRequiredService<ApnsPushSender>().SendAsync(device.Token, message, cancellationToken),
            _ => throw new NotificationRejectedException($"Push for {device.Platform} devices is not configured."),
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push (log transport) to {Platform} device {DeviceId}: {Title}")]
    private static partial void LogPush(ILogger logger, PushPlatform platform, Guid deviceId, string title);
}

/// <summary>Sends a text message through the configured transport.</summary>
internal sealed partial class SmsRouter(IOptions<NotificationsOptions> options, IServiceProvider services, ILogger<SmsRouter> logger)
{
    public Task SendAsync(string phoneNumber, string text, CancellationToken cancellationToken)
    {
        if (options.Value.Sms.Transport == SmsTransport.Log)
        {
            // Only the last digits: phone numbers are personal data.
            LogSms(logger, phoneNumber[^Math.Min(4, phoneNumber.Length)..], text);
            return Task.CompletedTask;
        }

        return services.GetRequiredService<TwilioSmsSender>().SendAsync(phoneNumber, text, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SMS (log transport) to a number ending {LastDigits}: {Text}")]
    private static partial void LogSms(ILogger logger, string lastDigits, string text);
}

/// <summary>Short-lived credentials (an OAuth access token, a provider JWT), reused until shortly before they expire.</summary>
internal sealed class ProviderTokens(TimeProvider time) : IDisposable
{
    private readonly Dictionary<string, (string Token, DateTimeOffset RenewAt)> _tokens = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string> GetAsync(string name, Func<CancellationToken, Task<(string Token, TimeSpan Lifetime)>> create, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_tokens.TryGetValue(name, out var cached) && cached.RenewAt > time.GetUtcNow())
            {
                return cached.Token;
            }

            var (token, lifetime) = await create(cancellationToken);
            // Renew five minutes early, so a token never expires on its way to the provider.
            _tokens[name] = (token, time.GetUtcNow() + lifetime - TimeSpan.FromMinutes(5));
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    public void Forget(string name)
    {
        _lock.Wait();
        try
        {
            _tokens.Remove(name);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>JSON Web Tokens signed by the caller's key, for provider sign-in. Not for validating tokens.</summary>
internal static class ProviderJwt
{
    public static string Create(object header, object payload, Func<byte[], byte[]> sign)
    {
        var unsigned = $"{Encode(JsonSerializer.SerializeToUtf8Bytes(header))}.{Encode(JsonSerializer.SerializeToUtf8Bytes(payload))}";
        return $"{unsigned}.{Encode(sign(Encoding.ASCII.GetBytes(unsigned)))}";
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Firebase Cloud Messaging's HTTP v1 API. Signs in as the service account (an RS256 JWT exchanged
/// for an OAuth access token, cached for its hour) and sends one message per device token.
/// </summary>
internal sealed class FcmPushSender(HttpClient http, IOptions<NotificationsOptions> options, ProviderTokens tokens, TimeProvider time)
{
    public const string ClientName = "fcm";
    public const string TokenHost = "oauth2.googleapis.com";
    public static readonly string[] Hosts = ["fcm.googleapis.com", TokenHost];
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    public async Task<PushOutcome> SendAsync(string deviceToken, PushMessage message, CancellationToken cancellationToken)
    {
        var fcm = options.Value.Push.Fcm;
        var accessToken = await tokens.GetAsync(ClientName, token => SignInAsync(fcm, token), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(fcm.ProjectId!)}/messages:send"))
        {
            Content = JsonContent.Create(new
            {
                message = new
                {
                    token = deviceToken,
                    notification = new { title = message.Title, body = message.Body },
                    // Data values must be strings.
                    data = Data(message),
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return PushOutcome.Sent;
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound && error.Contains("UNREGISTERED", StringComparison.Ordinal))
        {
            return PushOutcome.InvalidToken;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            tokens.Forget(ClientName);
        }

        throw (int)response.StatusCode is 400 or 403 or 404
            ? new NotificationRejectedException($"FCM answered {(int)response.StatusCode}: {Trim(error)}")
            : new HttpRequestException($"FCM answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private async Task<(string, TimeSpan)> SignInAsync(FcmOptions fcm, CancellationToken cancellationToken)
    {
        var account = JsonSerializer.Deserialize<ServiceAccount>(fcm.ServiceAccountJson!)
            ?? throw new NotificationRejectedException("Notifications:Push:Fcm:ServiceAccountJson is not a service account key.");
        var tokenUri = new Uri(account.TokenUri ?? $"https://{TokenHost}/token");
        var issuedAt = time.GetUtcNow().ToUnixTimeSeconds();
        using var key = RSA.Create();
        key.ImportFromPem(account.PrivateKey);
        var assertion = ProviderJwt.Create(
            new { alg = "RS256", typ = "JWT" },
            new { iss = account.ClientEmail, scope = Scope, aud = tokenUri.ToString(), iat = issuedAt, exp = issuedAt + 3600 },
            data => key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion,
        });
        using var response = await http.PostAsync(tokenUri, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new NotificationRejectedException($"Google sign-in for FCM answered {(int)response.StatusCode}: {Trim(await response.Content.ReadAsStringAsync(cancellationToken))}");
        }

        var token = await response.Content.ReadFromJsonAsync<AccessToken>(cancellationToken);
        return (token!.Value, TimeSpan.FromSeconds(token.ExpiresIn));
    }

    private static Dictionary<string, string> Data(PushMessage message)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal) { ["notificationId"] = message.NotificationId.ToString(), ["type"] = message.Type };
        if (message.Link is not null)
        {
            data["link"] = message.Link;
        }

        return data;
    }

    private static string Trim(string text) => text.Length > 200 ? text[..200] : text;

    private sealed record ServiceAccount(
        [property: JsonPropertyName("client_email")] string ClientEmail,
        [property: JsonPropertyName("private_key")] string PrivateKey,
        [property: JsonPropertyName("token_uri")] string? TokenUri);

    private sealed record AccessToken(
        [property: JsonPropertyName("access_token")] string Value,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>
/// Apple Push Notification service over HTTP/2, with token-based authentication: an ES256 JWT from the
/// .p8 key, reused for up to an hour as Apple asks.
/// </summary>
internal sealed class ApnsPushSender(HttpClient http, IOptions<NotificationsOptions> options, ProviderTokens tokens, TimeProvider time)
{
    public const string ClientName = "apns";
    public static readonly string[] Hosts = ["api.push.apple.com", "api.sandbox.push.apple.com"];

    public async Task<PushOutcome> SendAsync(string deviceToken, PushMessage message, CancellationToken cancellationToken)
    {
        var apns = options.Value.Push.Apns;
        var jwt = await tokens.GetAsync(ClientName, _ => Task.FromResult((SignIn(apns), TimeSpan.FromMinutes(50))), cancellationToken);
        var host = apns.UseSandbox ? Hosts[1] : Hosts[0];
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{host}/3/device/{Uri.EscapeDataString(deviceToken)}"))
        {
            // APNs speaks HTTP/2 only.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = JsonContent.Create(new
            {
                aps = new { alert = new { title = message.Title, body = message.Body }, sound = "default" },
                notificationId = message.NotificationId,
                type = message.Type,
                link = message.Link,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", jwt);
        request.Headers.Add("apns-topic", apns.BundleId);
        request.Headers.Add("apns-push-type", "alert");
        request.Headers.Add("apns-priority", "10");

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return PushOutcome.Sent;
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone || error.Contains("BadDeviceToken", StringComparison.Ordinal) || error.Contains("Unregistered", StringComparison.Ordinal))
        {
            return PushOutcome.InvalidToken;
        }

        if (error.Contains("ExpiredProviderToken", StringComparison.Ordinal))
        {
            tokens.Forget(ClientName);
            throw new HttpRequestException("APNs refused an expired provider token.", null, response.StatusCode);
        }

        throw (int)response.StatusCode is 400 or 403 or 404 or 413
            ? new NotificationRejectedException($"APNs answered {(int)response.StatusCode}: {(error.Length > 200 ? error[..200] : error)}")
            : new HttpRequestException($"APNs answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private string SignIn(ApnsOptions apns)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(apns.PrivateKey);
        return ProviderJwt.Create(
            new { alg = "ES256", kid = apns.KeyId },
            new { iss = apns.TeamId, iat = time.GetUtcNow().ToUnixTimeSeconds() },
            // JWTs carry the raw r|s form, which is what SignData returns by default.
            data => key.SignData(data, HashAlgorithmName.SHA256));
    }
}

/// <summary>Twilio's Messages API: one POST per text, signed in with the account SID and auth token.</summary>
internal sealed class TwilioSmsSender(HttpClient http, IOptions<NotificationsOptions> options)
{
    public const string ClientName = "twilio";
    public static readonly string[] Hosts = ["api.twilio.com"];

    public async Task SendAsync(string phoneNumber, string text, CancellationToken cancellationToken)
    {
        var twilio = options.Value.Sms.Twilio;
        var form = new Dictionary<string, string>(StringComparer.Ordinal) { ["To"] = phoneNumber, ["Body"] = text };
        form[twilio.From!.StartsWith("MG", StringComparison.Ordinal) ? "MessagingServiceSid" : "From"] = twilio.From;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(twilio.AccountSid!)}/Messages.json"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{twilio.AccountSid}:{twilio.AuthToken}")));

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        throw (int)response.StatusCode is 400 or 401 or 403 or 404
            ? new NotificationRejectedException($"Twilio answered {(int)response.StatusCode}: {(error.Length > 200 ? error[..200] : error)}")
            : new HttpRequestException($"Twilio answered {(int)response.StatusCode}.", null, response.StatusCode);
    }
}
