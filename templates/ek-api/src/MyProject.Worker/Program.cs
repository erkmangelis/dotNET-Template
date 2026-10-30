using Microsoft.Extensions.DependencyInjection.Extensions;

// Runs the jobs the API queues, in its own process: the API keeps Jobs:RunServer off and this
// process runs the Hangfire server. It serves only the health endpoints.
var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();
builder.AddDatabase();
#if (UseSmtp)
builder.AddEmail();
#endif
builder.AddJobs();
// Lets a running job finish before the container is stopped (compose waits 35 seconds).
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(30));

builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
#if (UseAuth)
// Jobs run outside a request, so changes they save have no actor.
builder.Services.AddHttpContextAccessor();
builder.Services.TryAddSingleton<ICurrentActor, HttpCurrentActor>();
#endif
builder.Services.AddNotes();

var app = builder.Build();

app.MapHealthEndpoints();

await app.RunAsync();
