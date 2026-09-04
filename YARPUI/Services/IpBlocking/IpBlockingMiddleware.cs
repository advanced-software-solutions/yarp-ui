namespace YARPUI.Services.IpBlocking;

/// <summary>
/// Rejects requests from blocked client addresses with 403 before they reach anything else
/// (routing, the proxy, the host's own endpoints). Inserted by
/// <see cref="Hosting.YarpUiIpBlockingStartupFilter"/> for every request outside the UI's own
/// surface, in both hosting modes. With an empty list the middleware is a single volatile
/// read; blocked requests are captured into the request log like proxied ones (the store's
/// channel-backed Add never touches the database on the request path).
/// </summary>
public sealed class IpBlockingMiddleware
{
    private const string BlockedReasonPrefix = "Blocked by IP rule '";

    private readonly RequestDelegate _next;

    public IpBlockingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IpBlockListService blockList, SqliteRequestLogStore logStore)
    {
        var matcher = blockList.CurrentMatcher;
        if (!matcher.IsEmpty)
        {
            var clientIp = blockList.ResolveClientIp(context);
            if (clientIp is not null && matcher.IsBlocked(clientIp, out var rule))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("Forbidden", context.RequestAborted);
                logStore.Add(
                    context.Request.Method,
                    context.Request.Path + context.Request.QueryString,
                    StatusCodes.Status403Forbidden,
                    durationMs: 0,
                    routeId: null,
                    clusterId: null,
                    destinationId: null,
                    destinationAddress: null,
                    error: $"{BlockedReasonPrefix}{rule!.Value}'",
                    clientIp: clientIp.ToString());
                return;
            }
        }

        await _next(context);
    }
}
