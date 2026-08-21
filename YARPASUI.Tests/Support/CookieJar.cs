namespace YARPASUI.Tests.Support;

/// <summary>
/// Minimal cookie container for the in-memory test server: stores cookies issued via
/// Set-Cookie and replays them as a Cookie header; honors cookie deletion (past expiry).
/// </summary>
internal sealed class CookieJar
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.OrdinalIgnoreCase);

    public bool Has(string name) => _cookies.ContainsKey(name);

    /// <summary>Plants a cookie as if the server had issued it (e.g. the culture cookie).</summary>
    public void Set(string name, string value) => _cookies[name] = value;

    public void Store(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            return;
        }

        foreach (var header in headers)
        {
            var firstSegment = header.Split(';')[0];
            var separator = firstSegment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = firstSegment[..separator].Trim();
            var value = firstSegment[(separator + 1)..].Trim();
            var deleted = header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
            if (deleted)
            {
                _cookies.Remove(name);
            }
            else
            {
                _cookies[name] = value;
            }
        }
    }

    public string HeaderValue => string.Join("; ", _cookies.Select(pair => $"{pair.Key}={pair.Value}"));
}
