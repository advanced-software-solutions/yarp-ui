namespace YARPUI.Services;

/// <summary>
/// Resolves the client IP for request logging. The YARP UI itself never installs the ASP.NET
/// Core ForwardedHeaders middleware (the host may), so the leftmost X-Forwarded-For entry is
/// honored when a fronting proxy supplied it and the direct TCP peer is used otherwise.
/// X-Forwarded-For is caller-controlled: logged IPs are informational, not authenticated.
/// </summary>
public static class RequestClientIp
{
    /// <summary>Leftmost X-Forwarded-For value when present, else the remote address; null when neither is known.</summary>
    public static string? Resolve(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"];
        if (forwarded.Count > 0)
        {
            foreach (var segment in forwarded)
            {
                if (string.IsNullOrWhiteSpace(segment))
                {
                    continue;
                }

                var leftmost = segment.Split(',')[0].Trim();
                if (leftmost.Length > 0)
                {
                    return leftmost;
                }
            }
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }
}
