using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Features.Notifications;

/// <summary>Notification settings, bound from the <c>Notifications</c> section.</summary>
internal sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    [Range(1, 3600)]
    public int DispatchIntervalSeconds { get; set; } = 5;

    /// <summary>Waits before each retry of a failed email, push or SMS. One retry per entry.</summary>
    public int[] RetryDelaysSeconds { get; set; } = [];

    /// <summary>How long notifications are kept, read or not.</summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 90;

    public PushOptions Push { get; set; } = new();

    public SmsOptions Sms { get; set; } = new();

    public static bool AreValid(NotificationsOptions options) =>
        options.RetryDelaysSeconds.All(delay => delay >= 0)
        && options.Push.Transport switch
        {
            PushTransport.Providers => options.Push.Fcm.IsConfigured || options.Push.Apns.IsConfigured,
            _ => true,
        }
        && options.Sms.Transport switch
        {
            SmsTransport.Twilio => options.Sms.Twilio.IsConfigured,
            _ => true,
        };
}

internal enum PushTransport
{
    /// <summary>No mobile push: the channel is not offered.</summary>
    None,

    /// <summary>Writes each push to the log instead of sending it. For development.</summary>
    Log,

    /// <summary>Firebase Cloud Messaging for Android (and web), Apple Push Notification service for iOS, as configured.</summary>
    Providers,
}

internal enum SmsTransport
{
    None,
    Log,
    Twilio,
}

internal sealed class PushOptions
{
    public PushTransport Transport { get; set; }

    public FcmOptions Fcm { get; set; } = new();

    public ApnsOptions Apns { get; set; } = new();
}

/// <summary>Firebase Cloud Messaging (HTTP v1 API), signed in with a service account.</summary>
internal sealed class FcmOptions
{
    /// <summary>The Firebase project id.</summary>
    public string? ProjectId { get; set; }

    /// <summary>The service account's JSON key file, as text. Keep it in a secret store or an environment variable.</summary>
    public string? ServiceAccountJson { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(ServiceAccountJson);
}

/// <summary>Apple Push Notification service with a token-based (.p8) key.</summary>
internal sealed class ApnsOptions
{
    public string? TeamId { get; set; }

    public string? KeyId { get; set; }

    /// <summary>The .p8 key's PEM text. Keep it in a secret store or an environment variable.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>The app's bundle id, sent as apns-topic.</summary>
    public string? BundleId { get; set; }

    /// <summary>Use the development gateway, for builds signed for development.</summary>
    public bool UseSandbox { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TeamId) && !string.IsNullOrWhiteSpace(KeyId)
        && !string.IsNullOrWhiteSpace(PrivateKey) && !string.IsNullOrWhiteSpace(BundleId);
}

internal sealed class SmsOptions
{
    public SmsTransport Transport { get; set; }

    public TwilioOptions Twilio { get; set; } = new();
}

internal sealed class TwilioOptions
{
    public string? AccountSid { get; set; }

    public string? AuthToken { get; set; }

    /// <summary>The sending number in E.164 form, or a messaging service id (MG…).</summary>
    public string? From { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountSid) && !string.IsNullOrWhiteSpace(AuthToken) && !string.IsNullOrWhiteSpace(From);
}
