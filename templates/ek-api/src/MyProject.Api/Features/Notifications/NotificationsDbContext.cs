using Microsoft.EntityFrameworkCore;

namespace MyProject.Api.Features.Notifications;

/// <summary>Where a notification can reach someone. In-app is always on: it is the notification itself.</summary>
internal enum NotificationChannel
{
    InApp,
#if (UseSmtp && UseIdentity)
    Email,
#endif
    Push,
    Sms,
}

/// <summary>One notification for one user: the in-app inbox entry.</summary>
internal sealed class Notification
{
    public const int TitleMaxLength = 200;
    public const int BodyMaxLength = 2000;
    public const int LinkMaxLength = 2048;

    public Guid Id { get; init; }

    public required string UserId { get; init; }

    /// <summary>One of <see cref="NotificationTypes"/>; preferences are per type.</summary>
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    /// <summary>Where the client should take the user, such as a path in the app.</summary>
    public string? Link { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>A user's choice for one type and channel. No row means the type's default.</summary>
internal sealed class NotificationPreference
{
    public required string UserId { get; init; }

    public required string Type { get; init; }

    public NotificationChannel Channel { get; init; }

    public bool Enabled { get; set; }
}

internal enum PushPlatform
{
    /// <summary>Firebase Cloud Messaging: Android, web, and iOS apps that use Firebase.</summary>
    Fcm,

    /// <summary>Apple Push Notification service, called directly.</summary>
    Apns,
}

/// <summary>A device that receives push notifications for a user.</summary>
internal sealed class PushDevice
{
    // FCM tokens run to about 200 characters and APNs tokens to 64; the unique index on the token must stay
    // under SQL Server's 1700-byte index key limit.
    public const int TokenMaxLength = 512;

    public Guid Id { get; init; }

    public required string UserId { get; set; }

    public PushPlatform Platform { get; init; }

    public required string Token { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>How to reach a user outside the app, beyond their account's email.</summary>
internal sealed class NotificationContact
{
    public required string UserId { get; init; }

    /// <summary>For SMS, in E.164 form (+905551234567).</summary>
    public string? PhoneNumber { get; set; }
}

/// <summary>One notification on one channel outside the app (one per device for push), sent by the background loop.</summary>
internal sealed class NotificationDelivery : IOutboxItem
{
    public Guid Id { get; init; }

    public Guid NotificationId { get; init; }

    public NotificationChannel Channel { get; init; }

    /// <summary>The device, for push.</summary>
    public Guid? DeviceId { get; init; }

    public OutboxStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>The notification tables. A context of its own, with its own migrations (see <see cref="ModuleDatabase"/>).</summary>
internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string HistoryTable = "__EFMigrationsHistory_Notifications";

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();

    public DbSet<PushDevice> Devices => Set<PushDevice>();

    public DbSet<NotificationContact> Contacts => Set<NotificationContact>();

    public DbSet<NotificationDelivery> Deliveries => Set<NotificationDelivery>();
#if (UseSqlite)

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }
#endif

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // User ids are plain columns, not foreign keys: the users may live in another context or at an
        // OpenID Connect provider. Deleting an account should delete its rows too (see docs/notifications.md).
        modelBuilder.Entity<Notification>(notification =>
        {
            notification.ToTable("Notifications");
            notification.Property(item => item.Id).ValueGeneratedNever();
            notification.Property(item => item.UserId).HasMaxLength(450);
            notification.Property(item => item.Type).HasMaxLength(100);
            notification.Property(item => item.Title).HasMaxLength(Notification.TitleMaxLength);
            notification.Property(item => item.Body).HasMaxLength(Notification.BodyMaxLength);
            notification.Property(item => item.Link).HasMaxLength(Notification.LinkMaxLength);
            notification.HasIndex(item => new { item.UserId, item.CreatedAt });
            notification.HasIndex(item => new { item.UserId, item.ReadAt });
        });

        modelBuilder.Entity<NotificationPreference>(preference =>
        {
            preference.ToTable("NotificationPreferences");
            preference.HasKey(item => new { item.UserId, item.Type, item.Channel });
            preference.Property(item => item.UserId).HasMaxLength(450);
            preference.Property(item => item.Type).HasMaxLength(100);
            preference.Property(item => item.Channel).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<PushDevice>(device =>
        {
            device.ToTable("PushDevices");
            device.Property(item => item.Id).ValueGeneratedNever();
            device.Property(item => item.UserId).HasMaxLength(450);
            device.Property(item => item.Platform).HasConversion<string>().HasMaxLength(20);
            device.Property(item => item.Token).HasMaxLength(PushDevice.TokenMaxLength);
            device.HasIndex(item => item.UserId);
            // A device token belongs to one install; registering it again moves it to the new user.
            device.HasIndex(item => new { item.Platform, item.Token }).IsUnique();
        });

        modelBuilder.Entity<NotificationContact>(contact =>
        {
            contact.ToTable("NotificationContacts");
            contact.HasKey(item => item.UserId);
            contact.Property(item => item.UserId).HasMaxLength(450);
            contact.Property(item => item.PhoneNumber).HasMaxLength(20);
        });

        modelBuilder.Entity<NotificationDelivery>(delivery =>
        {
            delivery.ToTable("NotificationDeliveries");
            delivery.Property(item => item.Id).ValueGeneratedNever();
            delivery.Property(item => item.Channel).HasConversion<string>().HasMaxLength(20);
            delivery.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
            delivery.Property(item => item.LastError).HasMaxLength(Outbox.ErrorMaxLength);
            delivery.HasOne<Notification>().WithMany().HasForeignKey(item => item.NotificationId).OnDelete(DeleteBehavior.Cascade);
            delivery.HasIndex(item => new { item.Status, item.NextAttemptAt });
        });
    }
}

internal sealed class NotificationsDbContextFactory()
    : ModuleDbContextFactory<NotificationsDbContext>(NotificationsDbContext.HistoryTable, options => new NotificationsDbContext(options));
