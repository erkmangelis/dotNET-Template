using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Api.IntegrationTests.Notifications;

/// <summary>In-app notifications, preferences, and delivery by push, SMS and email (the log transports in tests).</summary>
public sealed class NotificationTests(ApiFactory api) : IAsyncLifetime
{
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = (await TestUsers.SignInNewUserAsync(api, Roles.Admin)).Client;

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task An_announcement_lands_in_the_inbox_and_can_be_read_and_deleted()
    {
        var (user, userId) = await NewUserAsync();
        var sent = await SendAsync(userId, "Maintenance tonight", link: "/status");

        var inbox = await user.Client.GetFromJsonAsync<PagedResult<NotificationResponse>>(new Uri("/v1/notifications", UriKind.Relative), Cancel);
        var notification = Assert.Single(inbox!.Items);
        Assert.Equal((sent, "Maintenance tonight", "/status", false), (notification.Id, notification.Title, notification.Link, notification.Read));
        Assert.Equal(1, (await UnreadAsync(user.Client)).Count);

        using (var read = await user.Client.PostAsync(new Uri($"/v1/notifications/{sent}/read", UriKind.Relative), null, Cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        }

        Assert.Equal(0, (await UnreadAsync(user.Client)).Count);
        var unreadOnly = await user.Client.GetFromJsonAsync<PagedResult<NotificationResponse>>(new Uri("/v1/notifications?unreadOnly=true", UriKind.Relative), Cancel);
        Assert.Empty(unreadOnly!.Items);

        using (var deleted = await user.Client.DeleteAsync(new Uri($"/v1/notifications/{sent}", UriKind.Relative), Cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var again = await user.Client.DeleteAsync(new Uri($"/v1/notifications/{sent}", UriKind.Relative), Cancel);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Read_all_clears_the_unread_count()
    {
        var (user, userId) = await NewUserAsync();
        await SendAsync(userId, "First");
        await SendAsync(userId, "Second");
        Assert.Equal(2, (await UnreadAsync(user.Client)).Count);

        using var read = await user.Client.PostAsync(new Uri("/v1/notifications/read-all", UriKind.Relative), null, Cancel);

        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        Assert.Equal(0, (await UnreadAsync(user.Client)).Count);
    }

    [Fact]
    public async Task Users_cannot_touch_each_others_notifications()
    {
        var (_, ownerId) = await NewUserAsync();
        var (other, _) = await NewUserAsync();
        var sent = await SendAsync(ownerId, "Private");

        using var read = await other.Client.PostAsync(new Uri($"/v1/notifications/{sent}/read", UriKind.Relative), null, Cancel);
        using var deleted = await other.Client.DeleteAsync(new Uri($"/v1/notifications/{sent}", UriKind.Relative), Cancel);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (read.StatusCode, deleted.StatusCode));
        Assert.Equal(0, (await UnreadAsync(other.Client)).Count);
    }

    [Fact]
    public async Task Only_senders_can_send_and_links_must_be_safe()
    {
        var (user, userId) = await NewUserAsync();

        using var forbidden = await user.Client.PostAsJsonAsync("/v1/admin/notifications", new { userIds = new[] { userId }, title = "Hi", body = "Hi" }, Cancel);
        using var unsafeLink = await _admin.PostAsJsonAsync("/v1/admin/notifications", new { userIds = new[] { userId }, title = "Hi", body = "Hi", link = "javascript:alert(1)" }, Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unsafeLink.StatusCode);
    }

    [Fact]
    public async Task A_registered_device_gets_a_push_until_the_user_turns_push_off()
    {
        var (user, userId) = await NewUserAsync();
        using (var registered = await user.Client.PostAsJsonAsync("/v1/notifications/devices", new { platform = "fcm", token = $"token-{Guid.NewGuid():N}" }, Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        }

        var pushed = await SendAsync(userId, "Pushed");
        var delivery = await Eventually.HoldsAsync(() => DeliveriesAsync(pushed), deliveries => deliveries.Any(item => item.Status == OutboxStatus.Succeeded));
        Assert.Contains(delivery, item => item is { Channel: NotificationChannel.Push, Status: OutboxStatus.Succeeded });

        using var off = await user.Client.PutAsJsonAsync($"/v1/notifications/preferences/{NotificationTypes.Announcement}", new { channels = new Dictionary<string, bool> { ["push"] = false } }, Cancel);
        var preference = await off.Content.ReadFromJsonAsync<NotificationPreferenceResponse>(Cancel);
        Assert.False(preference!.Channels["push"]);

        var quiet = await SendAsync(userId, "Not pushed");
        Assert.DoesNotContain(await DeliveriesAsync(quiet), item => item.Channel == NotificationChannel.Push);
    }

    [Fact]
    public async Task Text_messages_go_to_users_who_add_a_number_and_turn_sms_on()
    {
        var (user, userId) = await NewUserAsync();
        using (var badNumber = await user.Client.PutAsJsonAsync("/v1/notifications/contact", new { phoneNumber = "0555 123 45 67" }, Cancel))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badNumber.StatusCode);
        }

        using (var contact = await user.Client.PutAsJsonAsync("/v1/notifications/contact", new { phoneNumber = "+905551234567" }, Cancel))
        {
            contact.EnsureSuccessStatusCode();
        }

        // SMS is off for announcements until the user turns it on.
        var before = await SendAsync(userId, "No text");
        Assert.DoesNotContain(await DeliveriesAsync(before), item => item.Channel == NotificationChannel.Sms);

        using (var on = await user.Client.PutAsJsonAsync($"/v1/notifications/preferences/{NotificationTypes.Announcement}", new { channels = new Dictionary<string, bool> { ["sms"] = true } }, Cancel))
        {
            on.EnsureSuccessStatusCode();
        }

        var texted = await SendAsync(userId, "Text me");
        await Eventually.HoldsAsync(() => DeliveriesAsync(texted), deliveries => deliveries.Any(item => item is { Channel: NotificationChannel.Sms, Status: OutboxStatus.Succeeded }));
    }

    [Fact]
    public async Task Preferences_list_each_type_and_refuse_unknown_channels_and_types()
    {
        var (user, _) = await NewUserAsync();

        var preferences = await user.Client.GetFromJsonAsync<List<NotificationPreferenceResponse>>(new Uri("/v1/notifications/preferences", UriKind.Relative), Cancel);
        using var unknownChannel = await user.Client.PutAsJsonAsync($"/v1/notifications/preferences/{NotificationTypes.Announcement}", new { channels = new Dictionary<string, bool> { ["pigeon"] = true } }, Cancel);
        using var unknownType = await user.Client.PutAsJsonAsync("/v1/notifications/preferences/no-such-type", new { channels = new Dictionary<string, bool> { ["push"] = true } }, Cancel);

        var announcement = Assert.Single(preferences!, preference => preference.Type == NotificationTypes.Announcement);
        Assert.True(announcement.Channels["push"]);
        Assert.False(announcement.Channels["sms"]);
        Assert.Equal(HttpStatusCode.BadRequest, unknownChannel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownType.StatusCode);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private async Task<(TestUser User, string Id)> NewUserAsync()
    {
        var user = await TestUsers.SignInNewUserAsync(api);
        await using var scope = api.Services.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(user.Email);
        return (user, account!.Id);
    }

    private async Task<Guid> SendAsync(string userId, string title, string? link = null)
    {
        using var response = await _admin.PostAsJsonAsync("/v1/admin/notifications", new { userIds = new[] { userId }, title, body = "Details inside.", link }, Cancel);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var sent = await response.Content.ReadFromJsonAsync<SendNotificationResponse>(Cancel);
        return Assert.Single(sent!.NotificationIds);
    }

    private static async Task<UnreadCountResponse> UnreadAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<UnreadCountResponse>(new Uri("/v1/notifications/unread-count", UriKind.Relative), Cancel))!;

    private async Task<List<NotificationDelivery>> DeliveriesAsync(Guid notificationId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Deliveries
            .AsNoTracking()
            .Where(delivery => delivery.NotificationId == notificationId)
            .ToListAsync(Cancel);
    }
}
