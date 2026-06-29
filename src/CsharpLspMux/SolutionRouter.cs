using System.Collections.Concurrent;

namespace CsharpLspMux;

public sealed class SolutionRouter(string repoRoot) : ISolutionRouter
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx"];

    private readonly string _repoRoot = Path.GetFullPath(repoRoot);
    private readonly ConcurrentDictionary<string, string?> _cache = new(
        StringComparer.OrdinalIgnoreCase);

    public string? Route(string absoluteFilePath)
    {
        var key = Path.GetFullPath(absoluteFilePath);
        return _cache.GetOrAdd(key, Resolve);
    }

    public void InvalidateCache(string changedPath)
    {
        _cache.Clear();
    }

    private string? Resolve(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);

        while (dir is not null && IsAtOrInsideRepoRoot(dir))
        {
            foreach (var ext in SolutionExtensions)
            {
                var candidates = Directory.GetFiles(dir, $"*{ext}");
                if (candidates.Length > 0)
                    return candidates[0];
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private bool IsAtOrInsideRepoRoot(string dir)
    {
        var normalized = Path.GetFullPath(dir);
        return normalized.StartsWith(_repoRoot, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(_repoRoot, StringComparison.OrdinalIgnoreCase);
    }
}
