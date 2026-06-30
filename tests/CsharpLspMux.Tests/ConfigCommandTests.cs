using Xunit;

namespace CsharpLspMux.Tests;

[Collection("env-sensitive")]
public sealed class ConfigCommandTests
{
    [Fact]
    public void Set_CreatesFile_WithMaxServers()
    {
        using var dir = TempDir();
        var code = ConfigCommand.Run(["set", "max-servers", "5"], dir.Path);
        Assert.Equal(0, code);
        var json = File.ReadAllText(Path.Combine(dir.Path, ".csharp-lsp-mux.json"));
        Assert.Contains("\"maxServers\"", json);
        Assert.Contains("5", json);
    }

    [Fact]
    public void Set_UpdatesExistingFile_MaxServers()
    {
        using var dir = TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".csharp-lsp-mux.json"), """{"maxServers":5}""");
        var code = ConfigCommand.Run(["set", "max-servers", "3"], dir.Path);
        Assert.Equal(0, code);
        var json = File.ReadAllText(Path.Combine(dir.Path, ".csharp-lsp-mux.json"));
        Assert.Contains("\"maxServers\":3", json.Replace(" ", ""));
    }

    [Fact]
    public void Set_InvalidKey_ReturnsError()
    {
        using var dir = TempDir();
        var stderr = CaptureStderr(() => ConfigCommand.Run(["set", "unknown-key", "5"], dir.Path));
        Assert.Contains("unknown-key", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void Set_OutOfRangeOrInvalidValue_ReturnsError(string value)
    {
        using var dir = TempDir();
        var code = ConfigCommand.Run(["set", "max-servers", value], dir.Path);
        Assert.Equal(1, code);
    }

    [Fact]
    public void Get_NoFileNoEnv_ShowsDefault()
    {
        using var dir = TempDir();
        WithEnv("LSP_ROUTER_MAX_SERVERS", null, () =>
        {
            var output = CaptureStdout(() => ConfigCommand.Run(["get", "max-servers"], dir.Path, NoUserConfig));
            Assert.Equal("max-servers = 10 (source: default)", output.Trim());
        });
    }

    [Fact]
    public void Get_FileValue_ShowsFileSource()
    {
        using var dir = TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".csharp-lsp-mux.json"), """{"maxServers":7}""");
        WithEnv("LSP_ROUTER_MAX_SERVERS", null, () =>
        {
            var output = CaptureStdout(() => ConfigCommand.Run(["get", "max-servers"], dir.Path, NoUserConfig));
            Assert.Equal("max-servers = 7 (source: file)", output.Trim());
        });
    }

    [Fact]
    public void Get_EnvVar_ShowsEnvSource()
    {
        using var dir = TempDir();
        string output = "";
        WithEnv("LSP_ROUTER_MAX_SERVERS", "4", () =>
        {
            output = CaptureStdout(() => ConfigCommand.Run(["get", "max-servers"], dir.Path, NoUserConfig));
        });
        Assert.Equal("max-servers = 4 (source: env)", output.Trim());
    }

    [Fact]
    public void List_ShowsAllKeys()
    {
        using var dir = TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".csharp-lsp-mux.json"), """{"maxServers":6}""");
        WithEnv("LSP_ROUTER_MAX_SERVERS", null, () =>
        {
            var output = CaptureStdout(() => ConfigCommand.Run(["list"], dir.Path, NoUserConfig));
            Assert.Contains("max-servers = 6 (source: file)", output);
        });
    }

    [Fact]
    public void Set_Global_WritesToUserConfigPath()
    {
        using var repoDir = TempDir();
        using var userDir = TempDir();
        var userConfigPath = Path.Combine(userDir.Path, "config.json");

        var code = ConfigCommand.Run(["set", "--global", "max-servers", "5"], repoDir.Path, userConfigPath);

        Assert.Equal(0, code);
        var json = File.ReadAllText(userConfigPath);
        Assert.Contains("\"maxServers\"", json);
        Assert.Contains("5", json);
    }

    [Fact]
    public void Set_Global_CreatesParentDirectory()
    {
        using var repoDir = TempDir();
        using var baseDir = TempDir();
        var userConfigPath = Path.Combine(baseDir.Path, "subdir", "config.json");

        var code = ConfigCommand.Run(["set", "--global", "max-servers", "7"], repoDir.Path, userConfigPath);

        Assert.Equal(0, code);
        Assert.True(File.Exists(userConfigPath));
    }

    [Fact]
    public void Get_UserFileValue_ShowsUserSource()
    {
        using var repoDir = TempDir();
        using var userDir = TempDir();
        var userConfigPath = Path.Combine(userDir.Path, "config.json");
        File.WriteAllText(userConfigPath, """{"maxServers":9}""");

        WithEnv("LSP_ROUTER_MAX_SERVERS", null, () =>
        {
            var output = CaptureStdout(() => ConfigCommand.Run(["get", "max-servers"], repoDir.Path, userConfigPath));
            Assert.Equal("max-servers = 9 (source: user)", output.Trim());
        });
    }

    // --- helpers ---

    private static string NoUserConfig =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "config.json");

    private static string CaptureStdout(Func<int> action)
    {
        var writer = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(writer);
        try { action(); }
        finally { Console.SetOut(prev); }
        return writer.ToString();
    }

    private static string CaptureStderr(Func<int> action)
    {
        var writer = new StringWriter();
        var prev = Console.Error;
        Console.SetError(writer);
        try { action(); }
        finally { Console.SetError(prev); }
        return writer.ToString();
    }

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
