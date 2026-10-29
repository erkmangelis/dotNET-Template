var builder = WebApplication.CreateBuilder(args);

builder.AddApiDefaults();
builder.AddObservability();
builder.AddDatabase();
builder.AddEmail();
builder.AddIdentityAuth();
builder.Services.AddApiKeys();
builder.Services.AddNotes();

var app = builder.Build();

app.UseApiDefaults();
app.MapHealthEndpoints();

var v1 = app.MapGroup("/v1");
v1.MapAuthEndpoints();
v1.MapNoteEndpoints();
v1.MapApiKeyEndpoints();
v1.MapProfileEndpoints();

var admin = v1.MapGroup("/admin").WithTags("Admin");
admin.MapNoteAdminEndpoints();
admin.MapUserAdminEndpoints();

await app.PrepareDatabaseAsync();
await app.RunAsync();
