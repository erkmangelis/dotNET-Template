using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.Realtime;

internal static class RealtimeSetup
{
    /// <summary>Every real-time route starts here; the SignalR hub, the event stream and the WebSocket.</summary>
    public const string PathPrefix = "/v1/realtime";

    public const string HubPath = PathPrefix + "/hub";

    public static WebApplicationBuilder AddRealtime(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<RealtimeOptions>()
            .BindConfiguration(RealtimeOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var options = builder.Configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>() ?? new RealtimeOptions();
        services.AddSignalR(hub =>
        {
            hub.KeepAliveInterval = TimeSpan.FromSeconds(options.KeepAliveSeconds);
            // A client that misses two keep-alives is gone.
            hub.ClientTimeoutInterval = TimeSpan.FromSeconds(options.KeepAliveSeconds * 2);
            hub.MaximumReceiveMessageSize = options.MaxMessageBytes;
        });
        services.AddSingleton<RealtimeConnections>();
        services.AddSingleton<RealtimePublisher>();
        services.AddSingleton<IRealtimePublisher>(provider => provider.GetRequiredService<RealtimePublisher>());
        services.AddSingleton<IAppEventSink>(provider => provider.GetRequiredService<RealtimePublisher>());

        return builder;
    }

    /// <summary>Accepts WebSocket upgrades, with the keep-alive from the settings.</summary>
    public static WebApplication UseRealtime(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<RealtimeOptions>>().Value;
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(options.KeepAliveSeconds) });
        return app;
    }

    /// <summary>The SignalR hub. The event stream and the WebSocket are in <see cref="RealtimeEndpoints"/>.</summary>
    public static WebApplication MapRealtimeHub(this WebApplication app)
    {
        app.MapHub<RealtimeHub>(HubPath);
        return app;
    }
}
