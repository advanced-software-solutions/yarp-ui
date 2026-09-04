namespace YARPASUI.LoadTests.Support;

/// <summary>Throwaway directory (deleted on dispose) for content roots and data dirs.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"yarp-loadtests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort — SQLite WAL files can lag a moment behind process exit.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
