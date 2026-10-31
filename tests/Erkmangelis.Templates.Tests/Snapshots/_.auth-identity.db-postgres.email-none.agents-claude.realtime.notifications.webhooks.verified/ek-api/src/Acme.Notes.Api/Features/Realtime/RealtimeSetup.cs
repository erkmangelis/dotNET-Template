using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.Realtime;

internal static class RealtimePolicies
{
    /// <summary>A signed-in user. API keys do not open real-time connections.</summary>
    public const string Connect = "realtime.connect";
}

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

        services.AddAuthorizationBuilder().AddPolicy(RealtimePolicies.Connect, policy => policy
            // Bearer tokens and cookie sessions both work; API keys do not.
            .AddAuthenticationSchemes(IdentityConstants.BearerScheme, IdentityConstants.ApplicationScheme)
            .RequireAuthenticatedUser());

        // Browsers cannot set headers on a WebSocket or EventSource, so on real-time routes only, the
        // token may come in the access_token query parameter, as SignalR's clients send it.
        services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, bearer =>
        {
            var previous = bearer.Events.OnMessageReceived;
            bearer.Events.OnMessageReceived = context =>
            {
                context.Token ??= TokenFromQuery(context.HttpContext);
                return previous(context);
            };
        });

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
        app.MapHub<RealtimeHub>(HubPath).RequireAuthorization(RealtimePolicies.Connect);
        return app;
    }

    private static string? TokenFromQuery(HttpContext context) =>
        context.Request.Path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase)
        && context.Request.Query.TryGetValue("access_token", out var token)
        && !string.IsNullOrEmpty(token)
            ? token.ToString()
            : null;
}
