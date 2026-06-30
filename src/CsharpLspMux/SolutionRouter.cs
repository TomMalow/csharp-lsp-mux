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
        var changedDir = Path.GetDirectoryName(Path.GetFullPath(changedPath)) ?? _repoRoot;

        foreach (var (fileKey, solutionPath) in _cache)
        {
            // Null entries encode "no solution found" — any structural change may make them
            // resolvable (e.g. a new .sln dropping in a sibling directory), so always evict.
            if (solutionPath is null)
            {
                _cache.TryRemove(fileKey, out _);
                continue;
            }

            var solutionDir = Path.GetDirectoryName(solutionPath) ?? _repoRoot;
            if (IsAncestorOrEqual(changedDir, solutionDir) || IsAncestorOrEqual(solutionDir, changedDir))
                _cache.TryRemove(fileKey, out _);
        }
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

        return SiblingScan(filePath);
    }

    private string? SiblingScan(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);

        while (dir is not null && IsAtOrInsideRepoRoot(dir))
        {
            if (new DirectoryInfo(dir).Name.Equals("src", StringComparison.OrdinalIgnoreCase))
            {
                var solutions = SolutionExtensions
                    .SelectMany(ext => Directory.GetFiles(dir, $"*{ext}", SearchOption.AllDirectories))
                    .ToList();

                if (solutions.Count == 0)
                    return null;

                return solutions
                    .OrderByDescending(CountCsprojReferences)
                    .First();
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static int CountCsprojReferences(string slnPath)
    {
        try
        {
            var content = File.ReadAllText(slnPath);
            var count = 0;
            var index = 0;
            while ((index = content.IndexOf(".csproj", index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index++;
            }
            return count;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private bool IsAtOrInsideRepoRoot(string dir) =>
        IsAncestorOrEqual(_repoRoot, Path.GetFullPath(dir));

    private static bool IsAncestorOrEqual(string ancestor, string descendant) =>
        descendant.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
        || descendant.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
