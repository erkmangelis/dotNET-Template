// Local development with .NET Aspire: `dotnet run --project src/Acme.Notes.AppHost` starts everything
// below and opens the Aspire dashboard with their logs, traces and metrics. See docs/aspire.md.
var builder = DistributedApplication.CreateBuilder(args);

// The resource is named after the connection string the apps read (ConnectionStrings:Default).
var database = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("Default", databaseName: "app");

// In Development the API applies migrations when it starts.
var api = builder.AddProject<Projects.Acme_Notes_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health/ready");

builder.AddProject<Projects.Acme_Notes_Worker>("worker")
    .WithReference(database)
    .WaitFor(api);

await builder.Build().RunAsync();
