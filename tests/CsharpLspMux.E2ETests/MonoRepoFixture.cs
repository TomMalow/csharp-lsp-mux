namespace CsharpLspMux.E2ETests;

/// <summary>
/// Copies fixtures/MonoRepo to a temp directory for test isolation. Deleted on dispose.
/// obj/ is included so Roslyn can open solutions without a network NuGet restore.
/// bin/ is excluded (not needed by Roslyn and large).
/// </summary>
public sealed class MonoRepoFixture : IDisposable
{
    private static readonly string FixtureSource = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "MonoRepo"));

    public string TempDir { get; } = Path.Combine(Path.GetTempPath(), $"csharp-lsp-mux-e2e-{Guid.NewGuid():N}");

    /// <summary>
    /// Reads a fixture file's on-disk text so tests send didOpen content that can never drift
    /// from the fixture (one source of truth instead of a duplicated inline literal).
    /// </summary>
    public string ReadFile(params string[] relativeSegments) =>
        File.ReadAllText(Path.Combine([TempDir, .. relativeSegments]));

    public MonoRepoFixture()
    {
        if (!Directory.Exists(FixtureSource))
            throw new DirectoryNotFoundException(
                $"MonoRepo fixture not found at '{FixtureSource}'. " +
                $"BaseDirectory: {AppContext.BaseDirectory}");
        CopyDirectory(FixtureSource, TempDir);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            // Skip MSBuild cache files that embed absolute paths — they break incremental builds
            // in the temp copy and prevent Roslyn from indexing workspace symbols.
            if (name.EndsWith(".cache", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".FileListAbsolute.txt", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dest, name));
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name == "bin") continue; // compiled output not needed
            CopyDirectory(dir, Path.Combine(dest, name));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(TempDir, recursive: true); } catch { }
    }
}
