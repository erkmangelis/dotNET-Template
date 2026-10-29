var builder = WebApplication.CreateBuilder(args);

builder.AddApiDefaults();
builder.AddObservability();
builder.AddDatabase();
builder.AddJobs();
builder.AddOidcAuth();
builder.Services.AddApiKeys();
builder.Services.AddNotes();

var app = builder.Build();

app.UseApiDefaults();
app.MapHealthEndpoints();

var api = app.NewVersionedApi();
// Notes is the first feature with a second version.
api.MapVersionedGroup(ApiVersions.V1, ApiVersions.V2).MapNoteEndpoints();

var v1 = api.MapVersionedGroup(ApiVersions.V1);
v1.MapApiKeyEndpoints();
v1.MapProfileEndpoints();

var admin = v1.MapGroup("/admin").WithTags("Admin");
admin.MapNoteAdminEndpoints();
admin.MapAuditAdminEndpoints();

app.MapJobsDashboard();

await app.PrepareDatabaseAsync();
await app.RunAsync();
