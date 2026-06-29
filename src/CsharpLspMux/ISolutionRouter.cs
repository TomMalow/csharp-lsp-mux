namespace CsharpLspMux;

public interface ISolutionRouter
{
    /// <summary>
    /// Returns absolute path to the owning .sln/.slnx, or null if not found.
    /// </summary>
    string? Route(string absoluteFilePath);

    void InvalidateCache(string changedPath);
}
