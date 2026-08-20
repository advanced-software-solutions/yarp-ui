using System.Net;
using Reqnroll;
using YARPASUI.Tests.Support;

namespace YARPASUI.Tests.StepDefinitions;

/// <summary>Steps around the cookie sign-in flow that protects the whole management UI.</summary>
[Binding]
internal sealed class AuthenticationSteps(ApiTestContext ctx)
{
    private const string Username = "admin";
    private const string Password = "correct-password";

    [When("I submit the login form with username {string} and password {string}")]
    public async Task WhenSubmitLogin(string username, string password)
    {
        ctx.Response = await ctx.App!.SignInAsync(username, password);
        ctx.Body = await ctx.Response.Content.ReadAsStringAsync();
    }

    [When("I submit the login form with username {string} and password {string} and return url {string}")]
    public async Task WhenSubmitLoginWithReturnUrl(string username, string password, string returnUrl)
    {
        ctx.Response = await ctx.App!.SignInAsync(username, password, returnUrl);
    }

    [When("I submit the login form with valid credentials but no antiforgery token")]
    public async Task WhenSubmitLoginWithoutToken()
    {
        ctx.Response = await ctx.App!.SignInAsync(Username, Password, withAntiforgeryToken: false);
    }

    [When("I log out")]
    public async Task WhenLogoutAsync()
    {
        ctx.Response = await ctx.App!.SendAsync(HttpMethod.Post, "/logout");
        await ReadBodyAsync();
    }

    [When("I open the home page")]
    public async Task WhenOpenHomeAsync()
    {
        ctx.Response = await ctx.App!.SendAsync(HttpMethod.Get, "/");
        await ReadBodyAsync();
    }

    [Then("the login page shows the sign-in form")]
    public async Task ThenLoginPageShowsForm()
    {
        Assert.NotNull(ctx.Body);
        Assert.Contains("__RequestVerificationToken", ctx.Body);
        Assert.Contains("Password", ctx.Body);
        await Task.CompletedTask;
    }

    [Then("the login page shows the message {string}")]
    public void ThenLoginPageShowsMessage(string message)
    {
        Assert.NotNull(ctx.Body);
        Assert.Contains(message, ctx.Body);
    }

    [Then("a UI session cookie is issued")]
    public void ThenSessionCookieIssued()
    {
        Assert.True(ctx.App!.Cookies.Has("yarpui.auth"));
    }

    [Then("no UI session cookie is issued")]
    public void ThenNoSessionCookieIssued()
    {
        Assert.False(ctx.App!.Cookies.Has("yarpui.auth"));
    }

    [Then("the UI home page loads")]
    public void ThenHomePageLoads()
    {
        Assert.NotNull(ctx.Response);
        Assert.Equal(HttpStatusCode.OK, ctx.Response!.StatusCode);
    }

    private async Task ReadBodyAsync()
    {
        ctx.Body = ctx.Response!.Content.Headers.ContentType is not null
            ? await ctx.Response.Content.ReadAsStringAsync()
            : null;
    }
}
