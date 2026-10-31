var builder = WebApplication.CreateBuilder(args);

builder.AddApiDefaults();
builder.AddObservability();
builder.AddDatabase();
builder.Services.AddScoped<AppEvents>();
builder.AddWebhooks();
builder.Services.AddNotes();

var app = builder.Build();

app.UseApiDefaults();
app.MapHealthEndpoints();

var api = app.NewVersionedApi();
// Notes is the first feature with a second version.
api.MapVersionedGroup(ApiVersions.V1, ApiVersions.V2).MapNoteEndpoints();

var v1 = api.MapVersionedGroup(ApiVersions.V1);
v1.MapWebhookEndpoints();

await app.PrepareDatabaseAsync();
await app.RunAsync();
