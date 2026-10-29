var builder = WebApplication.CreateBuilder(args);

builder.AddApiDefaults();
builder.AddObservability();
builder.AddDatabase();
builder.Services.AddNotes();

var app = builder.Build();

app.UseApiDefaults();
app.MapHealthEndpoints();

var v1 = app.MapGroup("/v1");
v1.MapNoteEndpoints();

await app.PrepareDatabaseAsync();
await app.RunAsync();
