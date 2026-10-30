namespace Acme.Notes.Api.Infrastructure;

internal static class SecurityHeaders
{
    // The API returns JSON only, so the strictest policy fits. The Scalar UI needs scripts and styles.
    private const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    /// <summary>Adds response headers that stop browsers from sniffing, framing or leaking the API.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Cross-Origin-Resource-Policy"] = "same-origin";
                headers["Permissions-Policy"] = "camera=(), geolocation=(), microphone=(), payment=(), usb=()";
                if (!context.Request.Path.StartsWithSegments("/scalar"))
                {
                    headers.ContentSecurityPolicy = ApiContentSecurityPolicy;
                }

                return Task.CompletedTask;
            });

            await next(context);
        });
}
