using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Acme.Notes.Api.Features.Notifications;

internal static class NotificationEndpoints
{
    /// <summary>E.164: a plus, a country code that does not start with 0, and up to 15 digits in all.</summary>
    public const string E164Pattern = @"^\+[1-9][0-9]{6,14}$";

    public static WebApplicationBuilder AddNotifications(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<NotificationsOptions>()
            .BindConfiguration(NotificationsOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(NotificationsOptions.AreValid, "Notifications: retry delays cannot be negative, and the chosen push or SMS transport needs its settings (see docs/notifications.md).")
            .ValidateOnStart();
        ModuleDatabase.Add<NotificationsDbContext>(services, NotificationsDbContext.HistoryTable);

        services.AddSingleton<NotificationChannels>();
        services.AddScoped<INotifier, Notifier>();
        services.AddScoped<NotificationDispatcher>();
        services.AddHostedService<NotificationDispatchLoop>();

        services.AddSingleton<ProviderTokens>();
        services.AddSingleton<PushRouter>();
        services.AddSingleton<SmsRouter>();
        // Each provider's client may only call that provider's hosts. Sends are POSTs, which the
        // resilience handler does not retry: the dispatcher's schedule does, so nobody gets a push twice.
        services.AddOutboundClient<FcmPushSender, FcmPushSender>(FcmPushSender.ClientName, FcmPushSender.Hosts);
        services.AddOutboundClient<ApnsPushSender, ApnsPushSender>(ApnsPushSender.ClientName, ApnsPushSender.Hosts);
        services.AddOutboundClient<TwilioSmsSender, TwilioSmsSender>(TwilioSmsSender.ClientName, TwilioSmsSender.Hosts);
        return builder;
    }

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder routes)
    {
        var notifications = routes.MapGroup("/notifications").WithTags("Notifications").RequireAuthorization();

        notifications.MapGet("/", ListAsync).WithName("ListNotifications").WithSummary("Your notifications, newest first");
        notifications.MapGet("/unread-count", UnreadCountAsync).WithName("CountUnreadNotifications").WithSummary("How many of your notifications are unread");
        notifications.MapPost("/{id:guid}/read", MarkReadAsync).WithName("MarkNotificationRead").WithSummary("Mark a notification read");
        notifications.MapPost("/read-all", MarkAllReadAsync).WithName("MarkAllNotificationsRead").WithSummary("Mark all your notifications read");
        notifications.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteNotification").WithSummary("Delete a notification");

        notifications.MapGet("/preferences", GetPreferencesAsync).WithName("GetNotificationPreferences").WithSummary("Which channels each type of notification uses");
        notifications.MapPut("/preferences/{type}", UpdatePreferenceAsync).WithName("UpdateNotificationPreference").WithSummary("Turn channels on or off for one type");

        notifications.MapPost("/devices", RegisterDeviceAsync).WithName("RegisterPushDevice").WithSummary("Register this device for push notifications");
        notifications.MapGet("/devices", ListDevicesAsync).WithName("ListPushDevices").WithSummary("Your devices registered for push");
        notifications.MapDelete("/devices/{id:guid}", RemoveDeviceAsync).WithName("RemovePushDevice").WithSummary("Stop push notifications to a device");

        notifications.MapGet("/contact", GetContactAsync).WithName("GetNotificationContact").WithSummary("Your phone number for text messages");
        notifications.MapPut("/contact", UpdateContactAsync).WithName("UpdateNotificationContact").WithSummary("Set or remove your phone number for text messages");

        return routes;
    }

    /// <summary><c>POST /v1/admin/notifications</c>: send an announcement to users.</summary>
    public static IEndpointRouteBuilder MapNotificationAdminEndpoints(this IEndpointRouteBuilder admin)
    {
        admin.MapPost("/notifications", SendAsync)
            .RequirePermission(Permissions.NotificationsSend)
            .WithName("SendNotification")
            .WithSummary("Send an announcement to users");
        return admin;
    }

    private static async Task<Ok<PagedResult<NotificationResponse>>> ListAsync(
        ClaimsPrincipal user,
        NotificationsDbContext db,
        CancellationToken cancellationToken,
        bool unreadOnly = false,
        [Range(1, Paging.MaxPage)] int page = 1,
        [Range(1, Paging.MaxPageSize)] int pageSize = Paging.DefaultPageSize)
    {
        var userId = user.GetUserId();
        var query = db.Notifications.AsNoTracking().Where(notification => notification.UserId == userId);
        if (unreadOnly)
        {
            query = query.Where(notification => notification.ReadAt == null);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Skip(Paging.Skip(page, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedResult<NotificationResponse>([.. items.Select(NotificationResponse.From)], page, pageSize, total));
    }

    private static async Task<Ok<UnreadCountResponse>> UnreadCountAsync(ClaimsPrincipal user, NotificationsDbContext db, CancellationToken cancellationToken)
    {
        var userId = user.GetUserId();
        var count = await db.Notifications.CountAsync(notification => notification.UserId == userId && notification.ReadAt == null, cancellationToken);
        return TypedResults.Ok(new UnreadCountResponse(count));
    }

    private static async Task<Results<NoContent, NotFound>> MarkReadAsync(
        Guid id,
        ClaimsPrincipal user,
        NotificationsDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var userId = user.GetUserId();
        var notification = await db.Notifications.FirstOrDefaultAsync(notification => notification.Id == id && notification.UserId == userId, cancellationToken);
        if (notification is null)
        {
            return TypedResults.NotFound();
        }

        notification.ReadAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> MarkAllReadAsync(ClaimsPrincipal user, NotificationsDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        await db.Notifications
            .Where(notification => notification.UserId == userId && notification.ReadAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(notification => notification.ReadAt, now), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(Guid id, ClaimsPrincipal user, NotificationsDbContext db, CancellationToken cancellationToken)
    {
        var userId = user.GetUserId();
        return await db.Notifications.Where(notification => notification.Id == id && notification.UserId == userId).ExecuteDeleteAsync(cancellationToken) == 0
            ? TypedResults.NotFound()
            : TypedResults.NoContent();
    }

    private static async Task<Ok<List<NotificationPreferenceResponse>>> GetPreferencesAsync(
        ClaimsPrincipal user,
        NotificationsDbContext db,
        NotificationChannels channels,
        CancellationToken cancellationToken)
    {
        var userId = user.GetUserId();
        var saved = await db.Preferences.AsNoTracking().Where(preference => preference.UserId == userId).ToListAsync(cancellationToken);
        return TypedResults.Ok(NotificationTypes.DefaultChannels
            .OrderBy(type => type.Key, StringComparer.Ordinal)
            .Select(type => Preferences(type.Key, type.Value, saved, channels))
            .ToList());
    }

    private static async Task<Results<Ok<NotificationPreferenceResponse>, NotFound, ValidationProblem>> UpdatePreferenceAsync(
        string type,
        UpdateNotificationPreferenceRequest request,
        ClaimsPrincipal user,
        NotificationsDbContext db,
        NotificationChannels channels,
        CancellationToken cancellationToken)
    {
        if (!NotificationTypes.DefaultChannels.TryGetValue(type, out var defaults))
        {
            return TypedResults.NotFound();
        }

        var changes = new Dictionary<NotificationChannel, bool>();
        foreach (var (name, enabled) in request.Channels)
        {
            if (!Enum.TryParse<NotificationChannel>(name, ignoreCase: true, out var channel) || !channels.Available.Contains(channel))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["Channels"] = [$"'{name}' is not a channel here. Channels: {string.Join(", ", channels.Available.Select(Name))}."],
                });
            }

            changes[channel] = enabled;
        }

        var userId = user.GetUserId();
        var saved = await db.Preferences.Where(preference => preference.UserId == userId && preference.Type == type).ToListAsync(cancellationToken);
        foreach (var (channel, enabled) in changes)
        {
            if (saved.Find(preference => preference.Channel == channel) is { } preference)
            {
                preference.Enabled = enabled;
            }
            else
            {
                var added = new NotificationPreference { UserId = userId, Type = type, Channel = channel, Enabled = enabled };
                db.Preferences.Add(added);
                saved.Add(added);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(Preferences(type, defaults, saved, channels));
    }

    private static NotificationPreferenceResponse Preferences(string type, NotificationChannel[] defaults, List<NotificationPreference> saved, NotificationChannels channels) =>
        new(type, channels.Available.ToDictionary(
            Name,
            channel => saved.Find(preference => preference.Type == type && preference.Channel == channel)?.Enabled ?? defaults.Contains(channel),
            StringComparer.Ordinal));

    private static async Task<Results<Ok<PushDeviceResponse>, ValidationProblem>> RegisterDeviceAsync(
        RegisterPushDeviceRequest request,
        ClaimsPrincipal user,
        NotificationsDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<PushPlatform>(request.Platform, ignoreCase: true, out var platform) || !Enum.IsDefined(platform))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["Platform"] = ["Use fcm or apns."],
            });
        }

        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var device = await db.Devices.FirstOrDefaultAsync(device => device.Platform == platform && device.Token == request.Token, cancellationToken);
        if (device is null)
        {
            device = new PushDevice { Id = Guid.CreateVersion7(now), UserId = userId, Platform = platform, Token = request.Token, CreatedAt = now };
            db.Devices.Add(device);
        }

        // A token belongs to the install, not the account: whoever signs in on the device now gets its pushes.
        device.UserId = userId;
        device.LastSeenAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(PushDeviceResponse.From(device));
    }

    private static async Task<Ok<List<PushDeviceResponse>>> ListDevicesAsync(ClaimsPrincipal user, NotificationsDbContext db, CancellationToken cancellationToken)
    {
        var userId = user.GetUserId();
        var devices = await db.Devices.AsNoTracking().Where(device => device.UserId == userId).OrderBy(device => device.CreatedAt).ToListAsync(cancellationToken);
        return TypedResults.Ok(devices.Select(PushDeviceResponse.From).ToList());
    }

    private static async Task<Results<NoContent, NotFound>> RemoveDeviceAsync(Guid id, ClaimsPrincipal user, NotificationsDbContext db, CancellationToken cancellationToken)
    {
        var userId = user.GetUserId();
        return await db.Devices.Where(device => device.Id == id && device.UserId == userId).ExecuteDeleteAsync(cancellationToken) == 0
            ? TypedResults.NotFound()
            : TypedResults.NoContent();
    }

    private static async Task<Ok<NotificationContactResponse>> GetContactAsync(ClaimsPrincipal user, NotificationsDbContext db, CancellationToken cancellationToken)
    {
        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(contact => contact.UserId == user.GetUserId(), cancellationToken);
        return TypedResults.Ok(new NotificationContactResponse(contact?.PhoneNumber));
    }

    private static async Task<Ok<NotificationContactResponse>> UpdateContactAsync(
        UpdateNotificationContactRequest request,
        ClaimsPrincipal user,
        NotificationsDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = user.GetUserId();
        var contact = await db.Contacts.FindAsync([userId], cancellationToken);
        if (contact is null)
        {
            contact = new NotificationContact { UserId = userId };
            db.Contacts.Add(contact);
        }

        contact.PhoneNumber = request.PhoneNumber;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new NotificationContactResponse(contact.PhoneNumber));
    }

    private static async Task<Results<Accepted<SendNotificationResponse>, ValidationProblem>> SendAsync(
        SendNotificationRequest request,
        INotifier notifier,
        CancellationToken cancellationToken)
    {
        if (request.Link is not null && !IsSafeLink(request.Link))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["Link"] = ["Use a path in the app (starting with /) or an https URL."],
            });
        }

        var userIds = request.UserIds.Distinct(StringComparer.Ordinal).ToList();
        var ids = new List<Guid>(userIds.Count);
        foreach (var userId in userIds)
        {
            ids.Add(await notifier.NotifyAsync(userId, NotificationTypes.Announcement, request.Title, request.Body, request.Link, cancellationToken));
        }

        return TypedResults.Accepted((string?)null, new SendNotificationResponse(ids));
    }

    /// <summary>
    /// Links end up in emails and on lock screens, so only app paths and https URLs: never
    /// <c>javascript:</c> or a protocol-relative <c>//host</c>.
    /// </summary>
    internal static bool IsSafeLink(string link) =>
        (link.StartsWith('/') && !link.StartsWith("//", StringComparison.Ordinal) && !link.StartsWith("/\\", StringComparison.Ordinal))
        || (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo));

    private static string Name(NotificationChannel channel) => channel.ToString().ToLowerInvariant();
}
