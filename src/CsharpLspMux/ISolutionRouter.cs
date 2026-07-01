namespace CsharpLspMux;

public interface ISolutionRouter
{
    /// <summary>
    /// Returns absolute path to the owning .sln/.slnx, or null if not found.
    /// </summary>
    string? Route(string absoluteFilePath);

    /// <summary>
    /// Evicts cache entries affected by <paramref name="changedPath"/>.
    /// Only entries whose resolved solution directory shares a directory prefix
    /// with <paramref name="changedPath"/> are removed; unrelated solutions are preserved.
    /// </summary>
    void NotifyFileChanged(string changedPath);
}
