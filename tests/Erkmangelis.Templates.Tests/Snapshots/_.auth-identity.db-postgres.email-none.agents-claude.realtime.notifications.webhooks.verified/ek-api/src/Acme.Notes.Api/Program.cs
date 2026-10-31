var builder = WebApplication.CreateBuilder(args);

builder.AddApiDefaults();
builder.AddObservability();
builder.AddDatabase();
builder.AddIdentityAuth();
builder.Services.AddApiKeys();
builder.Services.AddScoped<AppEvents>();
builder.AddRealtime();
builder.AddWebhooks();
builder.AddNotifications();
builder.Services.AddNotes();

var app = builder.Build();

app.UseApiDefaults();
app.UseRealtime();
app.MapHealthEndpoints();

var api = app.NewVersionedApi();
// Notes is the first feature with a second version.
api.MapVersionedGroup(ApiVersions.V1, ApiVersions.V2).MapNoteEndpoints();

var v1 = api.MapVersionedGroup(ApiVersions.V1);
v1.MapRealtimeEndpoints();
v1.MapWebhookEndpoints();
v1.MapNotificationEndpoints();
v1.MapAuthEndpoints();
v1.MapApiKeyEndpoints();
v1.MapProfileEndpoints();

var admin = v1.MapGroup("/admin").WithTags("Admin");
admin.MapNoteAdminEndpoints();
admin.MapAuditAdminEndpoints();
admin.MapUserAdminEndpoints();
admin.MapNotificationAdminEndpoints();

app.MapRealtimeHub();

await app.PrepareDatabaseAsync();
await app.RunAsync();
