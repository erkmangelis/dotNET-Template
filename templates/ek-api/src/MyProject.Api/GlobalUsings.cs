// The project's own namespaces, kept here so every file's using list stays sorted whatever the project is named.
global using MyProject.Api.Common;
#if (UsePostgres)
global using MyProject.Api.Data;
#endif
#if (UseIdentity)
global using MyProject.Api.Features.Auth;
#endif
global using MyProject.Api.Features.Notes;
global using MyProject.Api.Infrastructure;
#if (UseSmtp)
global using MyProject.Api.Infrastructure.Email;
#endif
