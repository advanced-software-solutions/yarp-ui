using YARPUI.Services.IpBlocking;

namespace YARPUI.Hosting;

/// <summary>
/// Inserts the IP-blocking middleware in front of the host's pipeline for every request
/// outside the UI's own surface (pages, /api/yarp, static assets stay reachable so an admin
/// can always remove a rule), in both hosting modes — standalone hosts get blocking without
/// any code change, and attach-mode apps only ever notice it once a rule exists. Runs as an
/// IStartupFilter like the localization wiring so it wraps the pipeline wherever
/// AddYarpUi/AttachYarpUi was called from. The check runs before any host middleware, so
/// Connection.RemoteIpAddress is the direct TCP peer — or the client address resolved by the
/// forwarded-headers middleware when YarpUi:ForwardedHeaders is enabled, since that filter
/// registers ahead of this one. Deployments chained behind an unconfigured trusted proxy can
/// honor X-Forwarded-For via the block list settings instead.
/// </summary>
internal sealed class YarpUiIpBlockingStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder => next(
            builder.UseWhen(
                context => !YarpUiPaths.IsYarpUiManagedSurface(context.Request.Path),
                branch => branch.UseMiddleware<IpBlockingMiddleware>()));
    }
}
