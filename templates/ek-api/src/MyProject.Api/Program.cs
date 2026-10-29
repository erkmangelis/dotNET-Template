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

var api = app.NewVersionedApi();
// Notes is the first feature with a second version.
api.MapVersionedGroup(ApiVersions.V1, ApiVersions.V2).MapNoteEndpoints();
#if (UseAuth)

var v1 = api.MapVersionedGroup(ApiVersions.V1);
#if (UseIdentity)
v1.MapAuthEndpoints();
#endif
v1.MapApiKeyEndpoints();
v1.MapProfileEndpoints();

var admin = v1.MapGroup("/admin").WithTags("Admin");
admin.MapNoteAdminEndpoints();
admin.MapAuditAdminEndpoints();
#if (UseIdentity)
admin.MapUserAdminEndpoints();
#endif
#endif

#if (UsePostgres)
await app.PrepareDatabaseAsync();
#endif
await app.RunAsync();
