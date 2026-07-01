namespace CsharpLspMux.E2ETests;

internal static class MuxPaths
{
    /// <summary>
    /// Path to CsharpLspMux.dll. Infers configuration from the test output directory path
    /// so a Debug test run doesn't accidentally pick up a stale Release build.
    /// </summary>
    public static string MuxDll { get; } = ResolveMuxDll();

    private static string ResolveMuxDll()
    {
        // Test bin: tests/CsharpLspMux.E2ETests/bin/{config}/net10.0/
        // Mux bin:  src/CsharpLspMux/bin/{config}/net10.0/CsharpLspMux.dll
        var baseDir = AppContext.BaseDirectory; // absolute, ends with /

        // Extract the config segment: .../bin/{config}/net10.0/
        var parts = baseDir.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
        var netIdx = Array.FindLastIndex(parts, p => p.StartsWith("net", StringComparison.OrdinalIgnoreCase));
        var detectedConfig = netIdx > 0 ? parts[netIdx - 1] : null;

        var configs = detectedConfig is not null
            ? new[] { detectedConfig, detectedConfig == "Debug" ? "Release" : "Debug" }
            : new[] { "Debug", "Release" };

        foreach (var config in configs)
        {
            var candidate = Path.GetFullPath(
                Path.Combine(baseDir, "..", "..", "..", "..", "..",
                    "src", "CsharpLspMux", "bin", config, "net10.0", "CsharpLspMux.dll"));
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException(
            $"CsharpLspMux.dll not found. Build the main project first. Searched from: {baseDir}");
    }
}
