namespace YARPASUI.LoadTests.Support;

/// <summary>
/// Locates the repository root (the folder holding the solution file) so the harness
/// can write raw NBomber reports next to the project and curated charts into
/// docs/load-tests/ regardless of which bin directory the tests run from.
/// </summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string DocsLoadTests => Path.Combine(Root, "docs", "load-tests");

    public static string ReportsRoot => Path.Combine(Root, "YARPASUI.LoadTests", "reports");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = dir; current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "AdvancedSoftwareProjects.slnx")))
            {
                return current.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}
