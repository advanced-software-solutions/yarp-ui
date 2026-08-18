using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Yarp.ReverseProxy.Configuration;
using YARPUI.Api;
using YARPUI.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

// YARP loads from memory so the UI can update the proxy at runtime without a restart.
// Initial source: yarp-ui.routes.json when present (UI-managed), otherwise the appsettings.json seed.
var initialConfig = ProxyConfigService.LoadInitial(
    builder.Environment.ContentRootPath,
    builder.Environment.EnvironmentName);

builder.Services.AddReverseProxy()
    .LoadFromMemory(initialConfig.Routes, initialConfig.Clusters);

builder.Services.AddSingleton<ProxyConfigService>();
builder.Services.AddSingleton<RequestLogStore>();

// UI sign-in with credentials from appsettings.json (YarpUi:Auth). The proxy itself stays public.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
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
builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<RequestLoggingMiddleware>();

app.MapRazorPages().RequireAuthorization(); // the login page is [AllowAnonymous]
app.MapYarpApi();

app.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// The reverse proxy is intentionally public — only the management UI requires sign-in.
app.MapReverseProxy();

app.Run();
