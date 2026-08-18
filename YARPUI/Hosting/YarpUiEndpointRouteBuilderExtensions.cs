using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using YARPUI;
using YARPUI.Api;
using YARPUI.Services;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Endpoint and middleware registration for YARP UI, mirroring
/// <see cref="Microsoft.Extensions.DependencyInjection.YarpUiServiceCollectionExtensions.AddYarpUi"/>.
/// </summary>
public static class YarpUiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Records proxied requests for the Logs page. Call after UseRouting/UseAuthorization
    /// so it can observe the proxy's route/cluster/destination selection.
    /// </summary>
    public static IApplicationBuilder UseYarpUiRequestLogging(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RequestLoggingMiddleware>();
    }

    /// <summary>
    /// Maps the management UI: Razor Pages (all require sign-in via the UI's own auth scheme;
    /// /login is anonymous), the /api/yarp/* endpoints and the logout endpoint. Does not map
    /// the proxy itself — call <c>app.MapReverseProxy()</c> separately.
    /// </summary>
    public static IEndpointRouteBuilder MapYarpUi(this IEndpointRouteBuilder app)
    {
        app.MapRazorPages().RequireAuthorization(YarpUiDefaults.Policy);
        app.MapYarpApi();

        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(YarpUiDefaults.Scheme);
            return Results.Redirect("/login");
        });

        return app;
    }
}
