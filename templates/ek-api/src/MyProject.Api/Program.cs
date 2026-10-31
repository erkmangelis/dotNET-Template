var builder = WebApplication.CreateBuilder(args);

builder.AddApiDefaults();
builder.AddObservability();
#if (UseDatabase)
builder.AddDatabase();
#endif
#if (UseSmtp)
builder.AddEmail();
#endif
#if (UseJobs)
builder.AddJobs();
#endif
#if (UseStorage)
builder.AddStorage();
#endif
#if (UseIdentity)
builder.AddIdentityAuth();
#elif (UseOidc)
builder.AddOidcAuth();
#endif
#if (UseAuth)
builder.Services.AddApiKeys();
#endif
#if (UseAppEvents)
builder.Services.AddScoped<AppEvents>();
#endif
#if (UseRealtime)
builder.AddRealtime();
#endif
#if (UseWebhooks)
builder.AddWebhooks();
#endif
#if (UseNotifications)
builder.AddNotifications();
#endif
builder.Services.AddNotes();
#if (UseStorage)
builder.Services.AddFiles();
#endif

var app = builder.Build();

app.UseApiDefaults();
#if (UseRealtime)
app.UseRealtime();
#endif
app.MapHealthEndpoints();

var api = app.NewVersionedApi();
// Notes is the first feature with a second version.
api.MapVersionedGroup(ApiVersions.V1, ApiVersions.V2).MapNoteEndpoints();
#if (UseAuth || UseStorage || UseRealtime || UseWebhooks)

var v1 = api.MapVersionedGroup(ApiVersions.V1);
#endif
#if (UseStorage)
v1.MapFileEndpoints();
#endif
#if (UseRealtime)
v1.MapRealtimeEndpoints();
#endif
#if (UseWebhooks)
v1.MapWebhookEndpoints();
#endif
#if (UseNotifications)
v1.MapNotificationEndpoints();
#endif
#if (UseAuth)
#if (UseIdentity)
v1.MapAuthEndpoints();
#endif
v1.MapApiKeyEndpoints();
v1.MapProfileEndpoints();

var admin = v1.MapGroup("/admin").WithTags("Admin");
admin.MapNoteAdminEndpoints();
admin.MapAuditAdminEndpoints();
#if (UseJobs)
admin.MapJobAdminEndpoints();
#endif
#if (UseIdentity)
admin.MapUserAdminEndpoints();
#endif
#if (UseNotifications)
admin.MapNotificationAdminEndpoints();
#endif
#endif
#if (UseRealtime)

app.MapRealtimeHub();
#endif

#if (UseHangfire)
app.MapJobsDashboard();

#endif
#if (UseDatabase)
await app.PrepareDatabaseAsync();
#endif
#if (UseStorage)
await app.PrepareStorageAsync();
#endif
await app.RunAsync();
