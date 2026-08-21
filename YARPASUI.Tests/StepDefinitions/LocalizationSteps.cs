using System.Net;
using Reqnroll;
using YARPASUI.Tests.Support;

namespace YARPASUI.Tests.StepDefinitions;

/// <summary>Steps for the UI's request localization: culture selection, direction, localized text.</summary>
[Binding]
internal sealed class LocalizationSteps(ApiTestContext ctx)
{
    [Given("the UI culture cookie is set to {string}")]
    public void GivenCultureCookie(string culture)
    {
        // The exact format CookieRequestCultureProvider writes/parses (no quotes), the
        // same one the in-page language switcher writes from JavaScript.
        ctx.App!.Cookies.Set(".AspNetCore.Culture", $"c={culture}|uic={culture}");
    }

    [Then("the page declares culture {string} and direction {string}")]
    public void ThenPageDeclaresCulture(string culture, string direction)
    {
        Assert.NotNull(ctx.Body);
        Assert.Contains($"<html lang=\"{culture}\" dir=\"{direction}\">", ctx.Body);
    }

    // Razor HTML-encodes non-ASCII text (Arabic/Chinese/em dashes) into numeric entities,
    // so comparisons run against the decoded body.
    [Then("the page contains {string}")]
    public void ThenPageContains(string text)
    {
        Assert.NotNull(ctx.Body);
        Assert.Contains(text, WebUtility.HtmlDecode(ctx.Body));
    }
}
