# Notifications

A notification goes to one user. It lands in their in-app inbox at once and, in the background,
<!--#if (UseSmtp && UseIdentity) -->
on the other channels they want: email, mobile push and text messages (SMS).
<!--#else -->
on the other channels they want: mobile push and text messages (SMS).
<!--#endif -->

```csharp
await notifier.NotifyAsync(userId, NotificationTypes.Announcement, "Maintenance tonight", "From 22:00 to 23:00.", "/status", cancellationToken);
```

Inject `INotifier`. Each kind of notification is a type in `NotificationTypes`, with the channels it
uses until the user says otherwise. Add a type there before sending it. Links must be a path in the
app or an `https` URL: they end up in emails and on lock screens.
<!--#if (UseRealtime) -->

Connected clients also get a `notification.created` real-time event, so an inbox badge can update
without polling.
<!--#endif -->

## Endpoints

| Endpoint | What it does |
| --- | --- |
| `GET /v1/notifications?unreadOnly=true` | The user's notifications, newest first, paged |
| `GET /v1/notifications/unread-count` | For the badge |
| `POST /v1/notifications/{id}/read`, `POST /v1/notifications/read-all` | Mark read |
| `DELETE /v1/notifications/{id}` | Remove from the inbox |
| `GET /v1/notifications/preferences` | Each type with each channel on or off |
| `PUT /v1/notifications/preferences/{type}` | `{"channels": {"push": false, "sms": true}}` |
| `POST /v1/notifications/devices` | Register a device for push: `{"platform": "fcm" or "apns", "token": "…"}` |
| `GET`, `DELETE /v1/notifications/devices/{id}` | List and remove devices |
| `GET`, `PUT /v1/notifications/contact` | The phone number for SMS, in E.164 form (`+905551234567`) |
| `POST /v1/admin/notifications` | Send an announcement to users (needs `notifications.send`) |

The in-app inbox is always on. A channel appears in preferences only when it is set up here.

## Delivery

Each notification on each channel is a delivery row (one per device for push), sent by
`NotificationDispatchLoop`. A failure is retried after each of `Notifications:RetryDelaysSeconds`;
a refusal that retrying cannot fix (bad credentials, a number the provider rejects) fails at once.
When a push service says a device token is dead (the app was uninstalled), the device is removed.
Notifications older than `RetentionDays` (90) are deleted with their deliveries.

## Push

`Notifications:Push:Transport` is `None` (no push), `Log` (development: writes each push to the
log) or `Providers`:

- **Firebase Cloud Messaging** (Android, web, and iOS apps that use Firebase), through the HTTP v1
  API. Set `Push:Fcm:ProjectId` and `Push:Fcm:ServiceAccountJson` (the service account's JSON key,
  as text, from a secret store). The API signs in as the service account and reuses the access token
  for its hour.
- **Apple Push Notification service** (iOS apps called directly). Set `Push:Apns:TeamId`, `KeyId`,
  `PrivateKey` (the `.p8` key's text) and `BundleId`, and `UseSandbox` for development builds. The
  API signs a provider token with the key and reuses it for 50 minutes, as Apple asks.

Either or both can be set. Calls go through clients that may only reach the providers' hosts (see
[outbound-http.md](outbound-http.md)).

## SMS

`Notifications:Sms:Transport` is `None`, `Log` or `Twilio`. For Twilio set `Sms:Twilio:AccountSid`,
`AuthToken` and `From` (a number in E.164 form, or a messaging service id starting with `MG`). SMS
is off for every type until the user turns it on, because texts cost money and interrupt. To add
another provider, add a transport and a sender like `TwilioSmsSender`, and route to it in
`SmsRouter`.

## Removing a user

User ids in the notification tables are plain columns, not foreign keys, so the tables work with
any sign-in. When you delete an account, delete its rows too: `Notifications`,
`NotificationPreferences`, `PushDevices` and `NotificationContacts` by `UserId`.

## Tables

Notification tables have their own `NotificationsDbContext`, migrations folder
(`Data/Migrations/Notifications`) and migration history table, and are migrated with the app's
database.
