# Feature flags

Flags turn a feature on or off without a deploy, with
[Microsoft.FeatureManagement](https://learn.microsoft.com/azure/azure-app-configuration/feature-management-dotnet-reference)
(MIT). They are read from configuration, so `appsettings.json`, environment variables and user
secrets can all set them, and a change to `appsettings.json` applies without a restart.

```json
"FeatureManagement": {
  "NoteExport": true
}
```

In compose, override one with an environment variable: `FeatureManagement__NoteExport: "false"`.

## Use a flag

Names live in `Infrastructure/FeatureFlags.cs`. Gate a whole endpoint, which answers 404 while the
flag is off:

```csharp
notes.MapGet("/export", Export).WithFeatureGate(FeatureFlags.NoteExport);
```

Or branch inside a handler:

```csharp
if (await features.IsEnabledAsync(FeatureFlags.NewPricing)) { ... }   // IFeatureManager features
```

`NoteExport` is the example: an operations switch for `GET /v1/notes/export`, which streams every
matching note. It is on; turn it off if exports ever slow the database down.

## On for some callers

A flag can be on for part of the traffic instead of on or off:

```json
"FeatureManagement": {
  "NewPricing": {
    "EnabledFor": [
      { "Name": "Microsoft.Percentage", "Parameters": { "Value": 10 } }
    ]
  },
  "HolidayBanner": {
    "EnabledFor": [
      { "Name": "Microsoft.TimeWindow", "Parameters": { "Start": "2026-12-20T00:00:00Z", "End": "2027-01-02T00:00:00Z" } }
    ]
  }
}
```

With sign-in, targeting turns a flag on for named users, for roles, or for a stable share of users
(the same user always gets the same answer). The user id is the signed-in user's id and the groups
are their roles (`FeatureFlags.UserTargetingContextAccessor`):

```json
"NewEditor": {
  "EnabledFor": [
    {
      "Name": "Microsoft.Targeting",
      "Parameters": {
        "Audience": {
          "Users": [ "<a user id>" ],
          "Groups": [ { "Name": "admin", "RolloutPercentage": 100 } ],
          "DefaultRolloutPercentage": 0
        }
      }
    }
  ]
}
```

## Keep flags few and short-lived

A flag is a branch to test both ways. Remove a release flag, and the code for its losing side, once
the feature is fully on. Keep long-lived flags for operations switches like `NoteExport`, and name
each one in `FeatureFlags` with a comment saying what it is for and who turns it off.
