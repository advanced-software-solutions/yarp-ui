using YARPUI;

// Standalone host for YARP UI: proxy + management UI in a single executable.
// The same UI can be embedded in any ASP.NET Core app via the YARPUI package —
// see AddYarpUi()/MapYarpUi() and samples/EmbeddedHost.

var builder = WebApplication.CreateBuilder(args);

builder.AddYarpUi();

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

// Records proxied requests for the Logs page.
app.UseYarpUiRequestLogging();

// The management UI (pages + /api/yarp/* + logout).
app.MapYarpUi();

// The reverse proxy itself — intentionally public; only the UI requires sign-in.
app.MapReverseProxy();

app.Run();
