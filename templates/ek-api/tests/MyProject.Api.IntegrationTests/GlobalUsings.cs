// The project's own namespaces, kept here so every file's using list stays sorted whatever the project is named.
global using MyProject.Api.Common;
#if (UseAuth)
global using MyProject.Api.Features.Auth;
#endif
global using MyProject.Api.Features.Notes;
#if (UseSmtp)
global using MyProject.Api.Infrastructure.Email;
#endif
