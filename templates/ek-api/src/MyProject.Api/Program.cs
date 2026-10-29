var builder = WebApplication.CreateBuilder(args);

builder.AddApiDefaults();
builder.AddObservability();
#if (UsePostgres)
builder.AddDatabase();
#endif
#if (UseSmtp)
builder.AddEmail();
#endif
#if (UseIdentity)
builder.AddIdentityAuth();
#elif (UseOidc)
builder.AddOidcAuth();
#endif
#if (UseAuth)
builder.Services.AddApiKeys();
#endif
builder.Services.AddNotes();

var app = builder.Build();

app.UseApiDefaults();
app.MapHealthEndpoints();

var v1 = app.MapGroup("/v1");
#if (UseIdentity)
v1.MapAuthEndpoints();
#endif
v1.MapNoteEndpoints();
#if (UseAuth)
v1.MapApiKeyEndpoints();
v1.MapProfileEndpoints();

var admin = v1.MapGroup("/admin").WithTags("Admin");
admin.MapNoteAdminEndpoints();
#if (UseIdentity)
admin.MapUserAdminEndpoints();
#endif
#endif

#if (UsePostgres)
await app.PrepareDatabaseAsync();
#endif
await app.RunAsync();
