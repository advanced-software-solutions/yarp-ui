using Reqnroll;
using YARPASUI.Tests.Support;

namespace YARPASUI.Tests.StepDefinitions;

/// <summary>
/// Steps around data-directory resolution: the read-only content root of an IIS site
/// must not take the app down — startup falls back to a writable directory instead.
/// </summary>
[Binding]
internal sealed class DataDirectorySteps(HostingTestContext ctx)
{
    // ---- given ----

    [Given("the content root is not writable")]
    public void GivenTheContentRootIsNotWritable() => ctx.MakeContentRootNotWritable();

    [Given("the fallback data directory is a temp folder")]
    public void GivenTheFallbackDataDirectoryIsATempFolder()
    {
        Directory.CreateDirectory(ctx.FallbackDirectory);
    }

    [Given("YarpUi:DataDirectory points at a temp folder")]
    public void GivenYarpUiDataDirectoryPointsAtATempFolder()
    {
        Directory.CreateDirectory(ctx.ConfiguredDirectory);
    }

    // ---- when ----

    [When("the app starts")]
    public async Task WhenTheAppStarts()
    {
        // Shared root: the ACL deny is applied to the same content root the app boots with.
        ctx.App = await TestWebApp.CreateAsync(configureBuilder: builder =>
        {
            if (Directory.Exists(ctx.FallbackDirectory))
            {
                builder.Configuration["YarpUi:FallbackDataDirectory"] = ctx.FallbackDirectory;
            }

            if (Directory.Exists(ctx.ConfiguredDirectory))
            {
                builder.Configuration["YarpUi:DataDirectory"] = ctx.ConfiguredDirectory;
            }
        }, root: ctx.Root);
    }

    // ---- then ----

    [Then("the request log database is created in the fallback data directory")]
    public void ThenTheRequestLogDatabaseIsCreatedInTheFallbackDataDirectory()
    {
        Assert.True(File.Exists(ctx.FallbackDatabasePath),
            $"expected the database in the fallback directory: {ctx.FallbackDatabasePath}");
    }

    [Then("the content root holds no request log database")]
    public void ThenTheContentRootHoldsNoRequestLogDatabase()
    {
        Assert.False(File.Exists(ctx.ContentRootDatabasePath),
            $"the database must not be created in the read-only content root: {ctx.ContentRootDatabasePath}");
    }

    [Then("the fallback data directory is the effective YarpUi:DataDirectory")]
    public void ThenTheFallbackDataDirectoryIsTheEffectiveYarpUiDataDirectory()
    {
        var effective = ctx.App!.App.Configuration["YarpUi:DataDirectory"];
        Assert.True(string.Equals(effective, ctx.FallbackDirectory, StringComparison.OrdinalIgnoreCase),
            $"expected YarpUi:DataDirectory to be the fallback directory, got '{effective}'");
    }

    [Then("the request log database is created in that data directory")]
    public void ThenTheRequestLogDatabaseIsCreatedInThatDataDirectory()
    {
        Assert.True(File.Exists(ctx.ConfiguredDatabasePath),
            $"expected the database in the configured directory: {ctx.ConfiguredDatabasePath}");
    }
}
