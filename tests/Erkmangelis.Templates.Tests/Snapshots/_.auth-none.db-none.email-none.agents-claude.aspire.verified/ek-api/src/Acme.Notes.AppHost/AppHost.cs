// Local development with .NET Aspire: `dotnet run --project src/Acme.Notes.AppHost` starts everything
// below and opens the Aspire dashboard with their logs, traces and metrics. See docs/aspire.md.
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Acme_Notes_Api>("api")
    .WithHttpHealthCheck("/health/ready");

await builder.Build().RunAsync();
