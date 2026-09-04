namespace YARPUI.Hosting;

/// <summary>
/// The route surface owned by the management UI: the Razor Pages mapped by MapYarpUi, the
/// auth endpoints (/login, /logout) and the /api/yarp group. Shared by the UI's
/// self-inserting middleware (request localization, IP blocking) so they can scope
/// themselves without the host opting in, no matter where the UI is mounted from.
/// </summary>
internal static class YarpUiPaths
{
    private static readonly string[] Pages =
    [
        "/Index", "/Editor", "/Logs", "/IpBlocking", "/Login", "/logout", "/Error",
    ];

    /// <summary>True for requests to the UI's own pages, auth endpoints and management API.</summary>
    public static bool IsYarpUiRequest(PathString path)
    {
        var value = path.HasValue ? path.Value!.TrimEnd('/') : "/";
        if (value.Length == 0)
        {
            return true; // "/"
        }

        foreach (var candidate in Pages)
        {
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return path.StartsWithSegments("/api/yarp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Everything an admin needs to manage the UI itself: the pages/API above plus the UI's
    /// static assets. The IP block list deliberately never blocks these paths, so even a
    /// too-wide rule can always be removed from the UI.
    /// </summary>
    public static bool IsYarpUiManagedSurface(PathString path) =>
        IsYarpUiRequest(path)
        || path.StartsWithSegments("/_content/YARPUI", StringComparison.OrdinalIgnoreCase)
        || IsExact(path, "/favicon.ico");

    private static bool IsExact(PathString path, string value) =>
        path.HasValue && string.Equals(path.Value!.TrimEnd('/'), value, StringComparison.OrdinalIgnoreCase);
}
