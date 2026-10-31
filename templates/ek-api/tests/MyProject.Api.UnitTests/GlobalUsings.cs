// The project's own namespaces, kept here so every file's using list stays sorted whatever the project is named.
global using MyProject.Api.Common;
#if (UseDatabase)
global using MyProject.Api.Data;
#endif
#if (UseAuth)
global using MyProject.Api.Features.ApiKeys;
global using MyProject.Api.Features.Auth;
#endif
#if (UseStorage)
global using MyProject.Api.Features.Files;
#endif
#if (!UseDatabase || UseAuth)
global using MyProject.Api.Features.Notes;
#endif
#if (UseNotifications)
global using MyProject.Api.Features.Notifications;
#endif
#if (UseWebhooks)
global using MyProject.Api.Features.Webhooks;
#endif
global using MyProject.Api.Infrastructure;
#if (UseDocuments)
global using MyProject.Api.Infrastructure.Documents;
#endif
#if (UseSmtp)
global using MyProject.Api.Infrastructure.Email;
#endif
global using MyProject.Api.Infrastructure.Http;
global using MyProject.Api.Infrastructure.Jobs;
