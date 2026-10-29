// The project's own namespaces, kept here so every file's using list stays sorted whatever the project is named.
global using MyProject.Api.Common;
#if (UsePostgres)
global using MyProject.Api.Data;
#endif
#if (UseAuth)
global using MyProject.Api.Features.ApiKeys;
#endif
#if (UsePostgres)
global using MyProject.Api.Features.Audit;
#endif
#if (UseAuth)
global using MyProject.Api.Features.Auth;
#endif
global using MyProject.Api.Features.Notes;
#if (UseAuth)
global using MyProject.Api.Features.Profiles;
#endif
global using MyProject.Api.Infrastructure;
global using MyProject.Api.Infrastructure.Idempotency;
#if (UseSmtp)
global using MyProject.Api.Infrastructure.Email;
#endif
