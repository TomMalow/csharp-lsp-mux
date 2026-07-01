namespace CsharpLspMux.E2ETests;

/// <summary>
/// Copies fixtures/MonoRepo to a temp directory for test isolation. Deleted on dispose.
/// obj/ is included so Roslyn can open solutions without a network NuGet restore.
/// bin/ is excluded (not needed by Roslyn and large).
/// </summary>
internal sealed class MonoRepoFixture : IDisposable
{
    private static readonly string FixtureSource = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "MonoRepo"));

    public string TempDir { get; } = Path.Combine(Path.GetTempPath(), $"csharp-lsp-mux-e2e-{Guid.NewGuid():N}");

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
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
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
