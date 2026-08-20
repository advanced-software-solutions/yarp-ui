using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using YARPUI;
using YARPUI.Services;

namespace YARPASUI.Tests.Support;

/// <summary>
/// Boots the real YARP UI (AddYarpUi + MapYarpUi, exactly like the standalone host)
/// on an in-memory TestServer with a throwaway content root / data directory, so
/// scenarios can exercise pages, the management API and authentication end to end.
/// </summary>
internal sealed partial class TestWebApp : IDisposable
{
    public const string DataDirToken = "__DATA_DIR__";

    public static async Task<TestWebApp> CreateAsync(
        Action<TestWebApp>? seed = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        TempDir? root = null)
    {
        var app = new TestWebApp(seed, configureBuilder, root);
        await app.App.StartAsync();
        app.Client = app.App.GetTestServer().CreateClient();
        return app;
    }

    public TempDir Root { get; }
    public string ContentRoot => Path.Combine(Root.Path, "content");
    public string DataDirectory => Path.Combine(Root.Path, "data");
    public string SeedPath => Path.Combine(ContentRoot, "appsettings.json");
    public string UiConfigPath => Path.Combine(DataDirectory, ProxyConfigService.UiConfigFileName);
    public string DatabasePath => Path.Combine(DataDirectory, SqliteRequestLogStore.DatabaseFileName);

    public WebApplication App { get; }
    public CookieJar Cookies { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    private readonly bool _ownsRoot;

    private TestWebApp(Action<TestWebApp>? seed, Action<WebApplicationBuilder>? configureBuilder, TempDir? root)
    {
        _ownsRoot = root is null;
        Root = root ?? new TempDir();
        Directory.CreateDirectory(ContentRoot);
        Directory.CreateDirectory(DataDirectory);
        seed?.Invoke(this);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = ContentRoot,
            EnvironmentName = "Testing",
            ApplicationName = typeof(TestWebApp).Assembly.GetName().Name,
        });
        builder.WebHost.UseTestServer();

        // Make sure the Razor Pages compiled into the YARPUI library are part of this app.
        // Plain AssemblyPart does not expose compiled razor items in .NET 10; the library's
        // ProvideApplicationPartFactory would normally wrap it, which auto-discovery does
        // not reliably do under the test host.
        builder.Services.AddMvcCore().ConfigureApplicationPartManager(parts =>
        {
            var uiAssembly = typeof(YarpUiDefaults).Assembly;
            if (!parts.ApplicationParts.Any(p => p.Name == uiAssembly.GetName().Name))
            {
                parts.ApplicationParts.Add(new CompiledRazorAssemblyPart(uiAssembly));
            }
        });

        configureBuilder?.Invoke(builder);
        builder.AddYarpUi();

        App = builder.Build();
        App.MapYarpUi();
    }

    // ---- requests ----

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, FormUrlEncodedContent? form = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (Cookies.HeaderValue.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", Cookies.HeaderValue);
        }

        if (form is not null)
        {
            request.Content = form;
        }
        else if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var response = await Client.SendAsync(request);
        Cookies.Store(response);
        return response;
    }

    /// <summary>Performs the real browser sign-in flow: GET /login, pick up the antiforgery token, POST credentials.</summary>
    public async Task<HttpResponseMessage> SignInAsync(string username, string password, string? returnUrl = null, bool withAntiforgeryToken = true)
    {
        var loginPage = await SendAsync(HttpMethod.Get, "/login");
        var html = await loginPage.Content.ReadAsStringAsync();

        var fields = new Dictionary<string, string>
        {
            ["Username"] = username,
            ["Password"] = password,
        };
        if (returnUrl is not null)
        {
            fields["returnUrl"] = returnUrl;
        }

        if (withAntiforgeryToken)
        {
            var token = AntiforgeryTokenRegex().Match(html).Groups[1].Value;
            Assert.NotEmpty(token);
            fields["__RequestVerificationToken"] = token;
        }

        return await SendAsync(HttpMethod.Post, "/login", form: new FormUrlEncodedContent(fields));
    }

    public void Dispose()
    {
        Client.Dispose();
        App.StopAsync().GetAwaiter().GetResult();
        App.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_ownsRoot)
        {
            Root.Dispose();
        }
    }

    [GeneratedRegex("""""name="__RequestVerificationToken"[^>]*value="([^"]*)""""")]
    private static partial Regex AntiforgeryTokenRegex();
}
