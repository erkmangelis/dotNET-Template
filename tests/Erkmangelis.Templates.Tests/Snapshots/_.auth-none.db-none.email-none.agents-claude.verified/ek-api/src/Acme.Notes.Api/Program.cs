var builder = WebApplication.CreateBuilder(args);

builder.AddApiDefaults();
builder.AddObservability();
builder.Services.AddNotes();

var app = builder.Build();

app.UseApiDefaults();
app.MapHealthEndpoints();

var v1 = app.MapGroup("/v1");
v1.MapNoteEndpoints();

await app.RunAsync();
