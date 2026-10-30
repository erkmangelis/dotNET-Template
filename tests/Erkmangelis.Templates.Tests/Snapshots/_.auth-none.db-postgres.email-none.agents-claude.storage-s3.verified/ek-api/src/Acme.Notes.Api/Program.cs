var builder = WebApplication.CreateBuilder(args);

builder.AddApiDefaults();
builder.AddObservability();
builder.AddDatabase();
builder.AddStorage();
builder.Services.AddNotes();
builder.Services.AddFiles();

var app = builder.Build();

app.UseApiDefaults();
app.MapHealthEndpoints();

var api = app.NewVersionedApi();
// Notes is the first feature with a second version.
api.MapVersionedGroup(ApiVersions.V1, ApiVersions.V2).MapNoteEndpoints();

var v1 = api.MapVersionedGroup(ApiVersions.V1);
v1.MapFileEndpoints();

await app.PrepareDatabaseAsync();
await app.PrepareStorageAsync();
await app.RunAsync();
