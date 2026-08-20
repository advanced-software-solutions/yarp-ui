using System.Diagnostics;
using Yarp.ReverseProxy.Model;

namespace YARPUI.Services;

/// <summary>
/// Records requests that were handled by the YARP proxy (route, cluster and selected
/// destination, status code and duration) into the <see cref="SqliteRequestLogStore"/>.
/// Non-proxied requests (UI pages, APIs, static files) are not captured.
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, SqliteRequestLogStore store)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? error = null;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            stopwatch.Stop();

            // The feature is only present on requests the proxy actually handled.
            var proxyFeature = context.Features.Get<IReverseProxyFeature>();
            if (proxyFeature is not null)
            {
                try
                {
                    store.Add(
                        context.Request.Method,
                        context.Request.Path + context.Request.QueryString,
                        context.Response.StatusCode,
                        stopwatch.Elapsed.TotalMilliseconds,
                        proxyFeature.Route?.Config?.RouteId,
                        proxyFeature.Cluster?.Config?.ClusterId,
                        proxyFeature.ProxiedDestination?.DestinationId,
                        proxyFeature.ProxiedDestination?.Model?.Config?.Address,
                        error?.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to record a request log entry.");
                }
            }
        }
    }
}
