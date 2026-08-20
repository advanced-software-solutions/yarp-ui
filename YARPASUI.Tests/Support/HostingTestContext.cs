using System.Diagnostics;
using System.Security.Principal;
using YARPUI.Services;

namespace YARPASUI.Tests.Support;

/// <summary>
/// Per-scenario state for data-directory resolution tests. "Not writable" is simulated with a
/// real ACL deny rule (the Windows ReadOnly attribute does not block file creation) — the same
/// denial an IIS application pool identity faces on the site folder.
/// </summary>
internal sealed class HostingTestContext : IDisposable
{
    private bool _contentRootDenyApplied;

    public TempDir Root { get; } = new();
    public string ContentRoot => Path.Combine(Root.Path, "content");
    public string FallbackDirectory => Path.Combine(Root.Path, "fallback");
    public string ConfiguredDirectory => Path.Combine(Root.Path, "configured");
    public TestWebApp? App { get; set; }

    public string FallbackDatabasePath => Path.Combine(FallbackDirectory, SqliteRequestLogStore.DatabaseFileName);
    public string ConfiguredDatabasePath => Path.Combine(ConfiguredDirectory, SqliteRequestLogStore.DatabaseFileName);
    public string ContentRootDatabasePath => Path.Combine(ContentRoot, SqliteRequestLogStore.DatabaseFileName);

    public string CurrentAccount => WindowsIdentity.GetCurrent().Name;

    /// <summary>Denies the current account the right to create files/subdirectories in the content root.</summary>
    public void MakeContentRootNotWritable()
    {
        Directory.CreateDirectory(ContentRoot);
        RunIcacls($"\"{ContentRoot}\" /deny \"{CurrentAccount}:(WD,AD)\"");
        _contentRootDenyApplied = true;
    }

    public void Dispose()
    {
        // Remove the deny rule while the directory still exists, before any disposal deletes it.
        if (_contentRootDenyApplied && Directory.Exists(ContentRoot))
        {
            RunIcacls($"\"{ContentRoot}\" /remove:d \"{CurrentAccount}\"");
            _contentRootDenyApplied = false;
        }

        App?.Dispose();
        Root.Dispose();
    }

    private static void RunIcacls(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("icacls", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
        });
        var stderr = process!.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"icacls {arguments} failed: {stderr}");
    }
}
