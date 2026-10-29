// Local development with .NET Aspire: `dotnet run --project src/MyProject.AppHost` starts everything
// below and opens the Aspire dashboard with their logs, traces and metrics. See docs/aspire.md.
var builder = DistributedApplication.CreateBuilder(args);

#if (UsePostgres)
// The resource is named after the connection string the apps read (ConnectionStrings:Default).
var database = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("Default", databaseName: "app");

#elif (UseSqlServer)
// The resource is named after the connection string the apps read (ConnectionStrings:Default).
var database = builder.AddSqlServer("sqlserver")
    .WithDataVolume()
    .AddDatabase("Default", databaseName: "app");

#endif
#if (UseDatabase)
// In Development the API applies migrations when it starts.
#endif
#if (UseWorker)
var api = builder.AddProject<Projects.MyProject_Api>("api")
#else
builder.AddProject<Projects.MyProject_Api>("api")
#endif
#if (UsePostgres || UseSqlServer)
    .WithReference(database)
    .WaitFor(database)
#endif
    .WithHttpHealthCheck("/health/ready");
#if (UseWorker)

builder.AddProject<Projects.MyProject_Worker>("worker")
#if (UsePostgres || UseSqlServer)
    .WithReference(database)
#endif
    .WaitFor(api);
#endif

await builder.Build().RunAsync();
