namespace YARPUI.Services;

/// <summary>
/// Resolves the client IP for request logging. The leftmost X-Forwarded-For entry is honored
/// when a fronting proxy supplied it and the direct TCP peer is used otherwise; when the UI's
/// forwarded-headers support resolved <see cref="HttpContext.Connection.RemoteIpAddress"/> from
/// a trusted header (YarpUi:ForwardedHeaders), that resolved address wins over the
/// caller-controlled chain. X-Forwarded-For is caller-controlled: logged IPs are informational,
/// not authenticated, unless forwarded headers are enabled from a front clients cannot bypass.
/// </summary>
public static class RequestClientIp
{
    /// <summary>
    /// HttpContext.Items key set on every request by the UI's forwarded-headers middleware
    /// (YarpUi:ForwardedHeaders enabled). Marks that Connection.RemoteIpAddress was resolved
    /// from the configured trusted header and should be preferred over X-Forwarded-For.
    /// </summary>
    public const string ForwardedResolvedItemKey = "YarpUi.ForwardedResolved";

    /// <summary>Resolved remote address when forwarded headers are enabled, otherwise the
    /// leftmost X-Forwarded-For value when present, else the remote address; null when nothing
    /// is known.</summary>
    public static string? Resolve(HttpContext context)
    {
        if (context.Items.ContainsKey(ForwardedResolvedItemKey))
        {
            return context.Connection.RemoteIpAddress?.ToString();
        }

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
