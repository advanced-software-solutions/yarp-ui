using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YARPUI;

namespace YARPUI.Hosting;

/// <summary>
/// Wiring for the UI's request localization. The UI ships English, Arabic, Spanish and
/// Simplified Chinese; hosts configure the set through <c>YarpUi:Cultures</c> and
/// <c>YarpUi:DefaultCulture</c> (comma-separated culture names).
/// </summary>
internal static class YarpUiLocalization
{
    /// <summary>
    /// Cultures offered when the host does not configure <c>YarpUi:Cultures</c>. zh-CN is
    /// accepted as an alias for zh-Hans (browsers send the regional tag); .NET's resource
    /// fallback chain resolves zh-CN to the zh-Hans resources.
    /// </summary>
    public static readonly CultureInfo[] DefaultCultures =
        new[] { "en", "ar", "es", "zh-Hans", "zh-CN" }.Select(CultureInfo.GetCultureInfo).ToArray();

    public static void AddYarpUiLocalization(IServiceCollection services, IConfiguration configuration)
    {
        services.AddLocalization();
        services.AddOptions<RequestLocalizationOptions>()
            .Configure(options =>
            {
                var supported = ResolveCultures(configuration["YarpUi:Cultures"]) ?? DefaultCultures;
                var defaultName = configuration["YarpUi:DefaultCulture"];
                CultureInfo? defaultCulture = null;
                if (!string.IsNullOrWhiteSpace(defaultName))
                {
                    try { defaultCulture = CultureInfo.GetCultureInfo(defaultName); }
                    catch (CultureNotFoundException) { /* fall through to the first supported culture */ }
                }

                defaultCulture ??= supported[0];
                options.DefaultRequestCulture = new RequestCulture(defaultCulture);
                options.SupportedCultures = supported.ToList();
                options.SupportedUICultures = supported.ToList();
                options.ApplyCurrentCultureToResponseHeaders = false;
            });
        services.AddSingleton<IStartupFilter, YarpUiRequestLocalizationStartupFilter>();
    }

    private static CultureInfo[]? ResolveCultures(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var cultures = configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name =>
            {
                try { return CultureInfo.GetCultureInfo(name); }
                catch (CultureNotFoundException) { return null; }
            })
            .Where(c => c is not null)
            .Cast<CultureInfo>()
            .ToList();

        return cultures.Count > 0 ? cultures.ToArray() : null;
    }

    /// <summary>
    /// The cultures to offer in the UI's language switcher: the supported list minus
    /// cultures whose parent is also supported (zh-CN disappears when zh-Hans is listed,
    /// since both resolve to the same resources).
    /// </summary>
    public static IReadOnlyList<CultureInfo> SwitcherCultures(IEnumerable<CultureInfo>? supported)
    {
        var list = supported as IReadOnlyList<CultureInfo> ?? supported?.ToList() ?? new List<CultureInfo>();
        var names = list.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list.Where(c => !names.Contains(c.Parent.Name)).ToList();
    }
}

/// <summary>
/// Inserts ASP.NET Core's request-localization middleware scoped to the UI's own routes
/// (pages, /login, /logout, /api/yarp/*), so a host never needs to call
/// <c>UseRequestLocalization</c> itself and its own pages keep whatever culture behavior
/// the host configured. Runs as an IStartupFilter so it wraps the host's pipeline no
/// matter where AddYarpUi/AttachYarpUi was called from.
/// </summary>
internal sealed class YarpUiRequestLocalizationStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder => next(
            builder.UseWhen(
                context => IsYarpUiRequest(context.Request.Path),
                branch => branch.UseRequestLocalization()));
    }

    private static bool IsYarpUiRequest(PathString path)
    {
        var value = path.HasValue ? path.Value!.TrimEnd('/') : "/";
        if (value.Length == 0)
        {
            return true; // "/"
        }

        foreach (var candidate in UiPaths)
        {
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return path.StartsWithSegments("/api/yarp", StringComparison.OrdinalIgnoreCase);
    }

    // Razor Pages routes mapped by MapYarpUi plus the auth endpoints. Static assets
    // (~/_content/YARPUI/...) are culture-neutral — their text comes from the per-request
    // inline strings script — so they are deliberately excluded, as is everything else
    // the host itself serves.
    private static readonly string[] UiPaths =
    [
        "/Index", "/Editor", "/Logs", "/Login", "/logout", "/Error",
    ];
}
