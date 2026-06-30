using Xunit;

namespace CsharpLspMux.Tests;

[Collection("env-sensitive")]
public sealed class MuxConfigTests
{
    [Fact]
    public void NoFile_MaxServers_DefaultsTen()
    {
        using var dir = TempDir();
        var config = new MuxConfig(dir.Path);
        Assert.Equal(10, config.MaxServers);
    }

    [Fact]
    public void FileWithMaxServers_UsesFileValue()
    {
        using var dir = TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".csharp-lsp-mux.json"), """{"maxServers":5}""");
        var config = new MuxConfig(dir.Path);
        Assert.Equal(5, config.MaxServers);
    }

    [Fact]
    public void EnvVarOnly_UsesEnvVar()
    {
        using var dir = TempDir();
        WithEnv("LSP_ROUTER_MAX_SERVERS", "3", () =>
        {
            var config = new MuxConfig(dir.Path);
            Assert.Equal(3, config.MaxServers);
        });
    }

    [Fact]
    public void EnvVarAndFile_EnvWins()
    {
        using var dir = TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".csharp-lsp-mux.json"), """{"maxServers":5}""");
        WithEnv("LSP_ROUTER_MAX_SERVERS", "3", () =>
        {
            var config = new MuxConfig(dir.Path);
            Assert.Equal(3, config.MaxServers);
        });
    }

    [Fact]
    public void MalformedFile_DefaultsTen()
    {
        using var dir = TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".csharp-lsp-mux.json"), "not json {{{");
        var config = new MuxConfig(dir.Path);
        Assert.Equal(10, config.MaxServers);
    }

    // --- helpers ---

    private static void WithEnv(string key, string value, Action action)
    {
        var prev = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
        try { action(); }
        finally { Environment.SetEnvironmentVariable(key, prev); }
    }

    private static TempDirectory TempDir() => new();

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
