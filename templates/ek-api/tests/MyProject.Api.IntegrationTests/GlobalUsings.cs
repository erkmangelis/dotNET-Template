// The project's own namespaces, kept here so every file's using list stays sorted whatever the project is named.
global using MyProject.Api.Common;
#if (UseDatabase)
global using MyProject.Api.Data;
#endif
#if (UseAuth)
global using MyProject.Api.Features.ApiKeys;
#endif
#if (UseDatabase)
global using MyProject.Api.Features.Audit;
#endif
#if (UseAuth)
global using MyProject.Api.Features.Auth;
#endif
#if (UseStorage)
global using MyProject.Api.Features.Files;
#endif
global using MyProject.Api.Features.Notes;
#if (UseNotifications)
global using MyProject.Api.Features.Notifications;
#endif
#if (UseAuth)
global using MyProject.Api.Features.Profiles;
#endif
#if (UseRealtime)
global using MyProject.Api.Features.Realtime;
#endif
#if (UseWebhooks)
global using MyProject.Api.Features.Webhooks;
#endif
#if (UseSmtp)
global using MyProject.Api.Infrastructure.Email;
#endif
global using MyProject.Api.Infrastructure.Idempotency;
#if (UseJobs)
global using MyProject.Api.Infrastructure.Jobs;
#endif
#if (UseStorage)
global using MyProject.Api.Infrastructure.Storage;
#endif
#if (UseWebhooks)
global using MyProject.Api.IntegrationTests.Webhooks;
#endif
