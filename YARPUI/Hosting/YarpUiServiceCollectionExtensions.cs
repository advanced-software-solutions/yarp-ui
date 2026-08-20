using Microsoft.AspNetCore.Authentication.Cookies;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using YARPUI;
using YARPUI.Api;
using YARPUI.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Hosting entry points for embedding YARP UI in any ASP.NET Core application.
/// The standalone host (YARPUI.Host) and embedded consumers use the same calls.
/// </summary>
public static class YarpUiServiceCollectionExtensions
{
    /// <summary>
    /// Standalone/embedded mode: the app gives YARP UI full ownership of the proxy
    /// configuration. Registers the proxy (loaded from yarp-ui.routes.json when present,
    /// otherwise the appsettings.json seed), the config/log services, cookie authentication
    /// (credentials from YarpUi:Auth) and the Razor Pages UI.
    /// Use this when the app does not configure YARP itself.
    /// </summary>
    public static IServiceCollection AddYarpUi(this WebApplicationBuilder builder)
    {
        var dataDirectory = ResolveAndWireDataDirectory(builder);
        var initialConfig = ProxyConfigService.LoadInitial(
            dataDirectory,
            builder.Environment.ContentRootPath,
            builder.Environment.EnvironmentName);

        var services = builder.Services;
        services.AddReverseProxy()
            .LoadFromMemory(initialConfig.Routes, initialConfig.Clusters);

        return AddYarpUiCore(services, builder.Configuration, dataDirectory, attachMode: false);
    }

    /// <summary>
    /// Attach mode for apps that already configure YARP themselves (their own
    /// LoadFromConfig/custom providers, transforms and filters stay fully in charge).
    /// The UI shows the host's entire live configuration read-only and manages a separate
    /// overlay (yarp-ui.routes.json) that merges alongside it — nothing from the app's own
    /// configuration is ever read, seeded, rewritten or replaced.
    /// </summary>
    public static IServiceCollection AttachYarpUi(this WebApplicationBuilder builder)
    {
        var dataDirectory = ResolveAndWireDataDirectory(builder);
        var overlay = ProxyConfigService.LoadOverlay(dataDirectory);

        // The overlay is just another config source from YARP's point of view; the host's
        // own sources are untouched and both are merged into the live proxy state.
        var overlayProvider = new InMemoryConfigProvider(overlay.Routes, overlay.Clusters);
        builder.Services.AddSingleton(overlayProvider);
        builder.Services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<InMemoryConfigProvider>());

        return AddYarpUiCore(builder.Services, builder.Configuration, dataDirectory, attachMode: true);
    }

    private static string ResolveAndWireDataDirectory(WebApplicationBuilder builder)
    {
        var explicitlyConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["YarpUi:DataDirectory"]);
        var dataDirectory = ProxyConfigService.ResolveDataDirectory(
            builder.Configuration,
            builder.Environment.ContentRootPath);

        // IIS (and any other host with a read-only deployment folder): the default application
        // pool identity cannot write to the content root, which would crash startup with
        // SQLite error 14 when the request log database is opened. When nothing was configured
        // explicitly, fall back to a writable per-app folder instead of taking the proxy down.
        if (!explicitlyConfigured && !IsDirectoryWritable(dataDirectory))
        {
            var fallback = ResolveFallbackDataDirectory(builder);
            if (fallback is not null && IsDirectoryWritable(fallback))
            {
                // Write it back so every consumer (the overlay config source below,
                // ProxyConfigService, the log store) resolves the same folder.
                builder.Configuration["YarpUi:DataDirectory"] = fallback;
                builder.Services.AddHostedService(sp => new DataDirectoryFallbackWarningService(
                    sp.GetRequiredService<ILogger<DataDirectoryFallbackWarningService>>(),
                    dataDirectory,
                    fallback));
                dataDirectory = fallback;
            }
        }

        // A data-directory (e.g. Docker volume) appsettings.json overrides the shipped one —
        // this must happen before configuration is read anywhere else.
        if (!string.Equals(dataDirectory, builder.Environment.ContentRootPath, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(dataDirectory);
            builder.Configuration.AddJsonFile(Path.Combine(dataDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
        }

        return dataDirectory;
    }

    /// <summary>
    /// Where mutable state goes when the content root is not writable: YarpUi:FallbackDataDirectory
    /// if set, otherwise %ProgramData%\YarpUi\&lt;application name&gt;.
    /// </summary>
    private static string? ResolveFallbackDataDirectory(WebApplicationBuilder builder)
    {
        var configured = builder.Configuration["YarpUi:FallbackDataDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(
                Path.IsPathRooted(configured) ? configured : Path.Combine(builder.Environment.ContentRootPath, configured));
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(programData))
        {
            return null;
        }

        return Path.Combine(programData, "YarpUi", SanitizeApplicationName(builder.Environment.ApplicationName));
    }

    private static string SanitizeApplicationName(string applicationName)
    {
        var name = new string(applicationName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).TrimEnd('.');
        return name.Length > 0 ? name : "app";
    }

    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".yarpui-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Open(probe, FileMode.CreateNew))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The initial retention policy (days to keep request logs, 0 = forever) from
    /// YarpUi:Logs:RetentionDays. It seeds the database setting; the value saved from the
    /// Logs page (also in that database) takes precedence from then on. Defaults to 30.
    /// </summary>
    private static int ResolveRetentionSeed(IConfiguration configuration)
    {
        var raw = configuration["YarpUi:Logs:RetentionDays"];
        return int.TryParse(raw, out var days) && days >= 0
            ? Math.Min(days, SqliteRequestLogStore.MaxRetentionDays)
            : 30;
    }

    private static IServiceCollection AddYarpUiCore(
        IServiceCollection services, IConfiguration configuration, string dataDirectory, bool attachMode)
    {
        services.AddRazorPages();

        services.AddSingleton(sp => new ProxyConfigService(
            configuration,
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<InMemoryConfigProvider>(),
            sp.GetRequiredService<IConfigValidator>(),
            sp.GetService<IProxyStateLookup>(),
            attachMode,
            sp.GetRequiredService<ILogger<ProxyConfigService>>()));

        // Request logs: SQLite-backed (survives restarts) in the data directory, fed by a
        // background writer; a retention service purges entries older than the policy allows.
        services.AddSingleton(sp => new SqliteRequestLogStore(
            Path.Combine(dataDirectory, SqliteRequestLogStore.DatabaseFileName),
            ResolveRetentionSeed(configuration),
            sp.GetRequiredService<ILogger<SqliteRequestLogStore>>()));
        services.AddHostedService<RequestLogWriter>();
        services.AddHostedService<LogRetentionService>();

        // UI sign-in with credentials from configuration (YarpUi:Auth). The UI registers its own
        // named cookie scheme and policy — it never changes the host's default authentication
        // scheme, so it is safe to add next to an app's existing JWT/cookie setup.
        services.AddAuthentication()
            .AddCookie(YarpUiDefaults.Scheme, options =>
            {
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/login";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Cookie.Name = "yarpui.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Events.OnRedirectToLogin = context =>
                {
                    // API calls get a plain 401 so client-side code can redirect to login itself.
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(YarpUiDefaults.Policy, policy =>
            {
                policy.AddAuthenticationSchemes(YarpUiDefaults.Scheme);
                policy.RequireAuthenticatedUser();
            });
        });

        return services;
    }
}
