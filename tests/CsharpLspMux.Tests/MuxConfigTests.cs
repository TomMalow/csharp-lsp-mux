using Xunit;

namespace CsharpLspMux.Tests;

[Collection("env-sensitive")]
public sealed class MuxConfigTests
{
    [Fact]
    public void NoFile_MaxServers_DefaultsTen()
    {
        using var dir = TempDir();
        WithEnv("LSP_ROUTER_MAX_SERVERS", null, () =>
        {
            var config = new MuxConfig(dir.Path, userConfigPath: NoUserConfig);
            Assert.Equal(10, config.MaxServers);
        });
    }

    [Fact]
    public void FileWithMaxServers_UsesFileValue()
    {
        using var dir = TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".csharp-lsp-mux.json"), """{"maxServers":5}""");
        WithEnv("LSP_ROUTER_MAX_SERVERS", null, () =>
        {
            var config = new MuxConfig(dir.Path, userConfigPath: NoUserConfig);
            Assert.Equal(5, config.MaxServers);
        });
    }

    [Fact]
    public void EnvVarOnly_UsesEnvVar()
    {
        using var dir = TempDir();
        WithEnv("LSP_ROUTER_MAX_SERVERS", "3", () =>
        {
            var config = new MuxConfig(dir.Path, userConfigPath: NoUserConfig);
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
            var config = new MuxConfig(dir.Path, userConfigPath: NoUserConfig);
            Assert.Equal(3, config.MaxServers);
        });
    }

    [Fact]
    public void MalformedFile_DefaultsTen()
    {
        using var dir = TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".csharp-lsp-mux.json"), "not json {{{");
        WithEnv("LSP_ROUTER_MAX_SERVERS", null, () =>
        {
            var config = new MuxConfig(dir.Path, userConfigPath: NoUserConfig);
            Assert.Equal(10, config.MaxServers);
        });
    }

    [Fact]
    public void UserFile_MaxServers_UsesUserValue()
    {
        using var repoDir = TempDir();
        using var userDir = TempDir();
        var userConfigPath = Path.Combine(userDir.Path, "config.json");
        File.WriteAllText(userConfigPath, """{"maxServers":8}""");

        WithEnv("LSP_ROUTER_MAX_SERVERS", null, () =>
        {
            var config = new MuxConfig(repoDir.Path, userConfigPath);
            Assert.Equal(8, config.MaxServers);
            Assert.Equal("user", config.MaxServersSource);
        });
    }

    [Fact]
    public void UserFileAndRepoFile_RepoWins()
    {
        using var repoDir = TempDir();
        using var userDir = TempDir();
        File.WriteAllText(Path.Combine(repoDir.Path, ".csharp-lsp-mux.json"), """{"maxServers":5}""");
        var userConfigPath = Path.Combine(userDir.Path, "config.json");
        File.WriteAllText(userConfigPath, """{"maxServers":8}""");

        WithEnv("LSP_ROUTER_MAX_SERVERS", null, () =>
        {
            var config = new MuxConfig(repoDir.Path, userConfigPath);
            Assert.Equal(5, config.MaxServers);
            Assert.Equal("file", config.MaxServersSource);
        });
    }

    [Fact]
    public void EnvVarAndUserFile_EnvWins()
    {
        using var repoDir = TempDir();
        using var userDir = TempDir();
        var userConfigPath = Path.Combine(userDir.Path, "config.json");
        File.WriteAllText(userConfigPath, """{"maxServers":8}""");

        WithEnv("LSP_ROUTER_MAX_SERVERS", "3", () =>
        {
            var config = new MuxConfig(repoDir.Path, userConfigPath);
            Assert.Equal(3, config.MaxServers);
            Assert.Equal("env", config.MaxServersSource);
        });
    }

    // --- helpers ---

    private static string NoUserConfig =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "config.json");

    private static void WithEnv(string key, string? value, Action action)
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
