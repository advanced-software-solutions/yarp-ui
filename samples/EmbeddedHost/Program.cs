// Embedded hosting sample: YARP UI added to a plain ASP.NET Core app via the YARPUI package.
// Compare with YARPUI.Host — the standalone executable hosting mode.

var builder = WebApplication.CreateBuilder(args);

// Registers the proxy config, services, auth and Razor Pages for the UI.
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

app.UseYarpUiRequestLogging();

app.MapYarpUi();       // management UI pages + /api/yarp/*
app.MapReverseProxy(); // the proxy itself

app.Run();
