using Microsoft.EntityFrameworkCore;

namespace Acme.Notes.Api.Features.Webhooks;

/// <summary>Someone who wants to hear about events: where to send them, which ones, and the secret to sign them with.</summary>
internal sealed class WebhookSubscription
{
    public const int UrlMaxLength = 2048;
    public const int DescriptionMaxLength = 200;

    public Guid Id { get; init; }

    public required string Url { get; set; }

    public string? Description { get; set; }

    /// <summary>Event types to deliver, from <see cref="AppEventTypes"/>.</summary>
    public required string[] EventTypes { get; set; }

    /// <summary>The signing secret. Shown when the subscription is created or the secret rotated, never listed.</summary>
    public required string Secret { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Why it was turned off by the app, such as the subscriber answering 410 Gone.</summary>
    public string? DisabledReason { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One event for one subscriber, and the delivery log entry for it. Its id is the <c>webhook-id</c> header.</summary>
internal sealed class WebhookDelivery : IOutboxItem
{
    public Guid Id { get; init; }

    public Guid SubscriptionId { get; init; }

    public required string EventType { get; init; }

    /// <summary>The exact body sent, so every retry carries the same bytes and signature input.</summary>
    public required string Payload { get; init; }

    public OutboxStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public string? LastError { get; set; }

    public int? LastStatusCode { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? DeliveredAt { get; set; }
}

/// <summary>A webhook another system sent, stored before it is handled so a slow or failing handler never loses it.</summary>
internal sealed class IncomingWebhook : IOutboxItem
{
    public const int SourceMaxLength = 50;
    public const int MessageIdMaxLength = 200;

    public Guid Id { get; init; }

    /// <summary>The configured source name, in lower case.</summary>
    public required string Source { get; init; }

    /// <summary>The sender's <c>webhook-id</c>. Unique per source: a second delivery of the same message is recognised and dropped.</summary>
    public required string MessageId { get; init; }

    /// <summary>The body's <c>type</c>, when it has one.</summary>
    public string? EventType { get; init; }

    public required string Payload { get; init; }

    public OutboxStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>The webhook tables. A context of its own, with its own migrations (see <see cref="ModuleDatabase"/>).</summary>
internal sealed class WebhooksDbContext(DbContextOptions<WebhooksDbContext> options) : DbContext(options)
{
    public const string HistoryTable = "__EFMigrationsHistory_Webhooks";

    public DbSet<WebhookSubscription> Subscriptions => Set<WebhookSubscription>();

    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();

    public DbSet<IncomingWebhook> Incoming => Set<IncomingWebhook>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WebhookSubscription>(subscription =>
        {
            subscription.ToTable("WebhookSubscriptions");
            subscription.Property(item => item.Id).ValueGeneratedNever();
            subscription.Property(item => item.Url).HasMaxLength(WebhookSubscription.UrlMaxLength);
            subscription.Property(item => item.Description).HasMaxLength(WebhookSubscription.DescriptionMaxLength);
            subscription.Property(item => item.Secret).HasMaxLength(100);
            subscription.Property(item => item.DisabledReason).HasMaxLength(200);
        });

        modelBuilder.Entity<WebhookDelivery>(delivery =>
        {
            delivery.ToTable("WebhookDeliveries");
            delivery.Property(item => item.Id).ValueGeneratedNever();
            delivery.Property(item => item.EventType).HasMaxLength(100);
            delivery.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
            delivery.Property(item => item.LastError).HasMaxLength(Outbox.ErrorMaxLength);
            delivery.HasOne<WebhookSubscription>().WithMany().HasForeignKey(item => item.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
            delivery.HasIndex(item => new { item.Status, item.NextAttemptAt });
            delivery.HasIndex(item => new { item.SubscriptionId, item.CreatedAt });
        });

        modelBuilder.Entity<IncomingWebhook>(incoming =>
        {
            incoming.ToTable("IncomingWebhooks");
            incoming.Property(item => item.Id).ValueGeneratedNever();
            incoming.Property(item => item.Source).HasMaxLength(IncomingWebhook.SourceMaxLength);
            incoming.Property(item => item.MessageId).HasMaxLength(IncomingWebhook.MessageIdMaxLength);
            incoming.Property(item => item.EventType).HasMaxLength(100);
            incoming.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
            incoming.Property(item => item.LastError).HasMaxLength(Outbox.ErrorMaxLength);
            // The duplicate check: one row per message per sender.
            incoming.HasIndex(item => new { item.Source, item.MessageId }).IsUnique();
            incoming.HasIndex(item => new { item.Status, item.NextAttemptAt });
            incoming.HasIndex(item => item.ReceivedAt);
        });
    }
}

internal sealed class WebhooksDbContextFactory()
    : ModuleDbContextFactory<WebhooksDbContext>(WebhooksDbContext.HistoryTable, options => new WebhooksDbContext(options));
