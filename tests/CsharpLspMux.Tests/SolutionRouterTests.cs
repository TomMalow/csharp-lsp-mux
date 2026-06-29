using CsharpLspMux;
using Xunit;

namespace CsharpLspMux.Tests;

public sealed class SolutionRouterTests : IDisposable
{
    private readonly string _root;

    public SolutionRouterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MakeDir(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string MakeFile(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void AncestorWalk_DirectParent_ReturnsSln()
    {
        var serviceDir = MakeDir("src", "MyService");
        var slnPath = MakeFile(serviceDir, "MyService.sln");
        var csFile = MakeFile(serviceDir, "Foo.cs");

        var router = new SolutionRouter(_root);
        var result = router.Route(csFile);

        Assert.Equal(slnPath, result);
    }

    [Fact]
    public void AncestorWalk_SeveralLevelsDeep_ReturnsSln()
    {
        var serviceDir = MakeDir("src", "MyService");
        var slnPath = MakeFile(serviceDir, "MyService.sln");
        var deepDir = MakeDir("src", "MyService", "a", "b", "c");
        var csFile = MakeFile(deepDir, "Deep.cs");

        var router = new SolutionRouter(_root);
        var result = router.Route(csFile);

        Assert.Equal(slnPath, result);
    }

    [Fact]
    public void AncestorWalk_SlnxExtension_ReturnsSolution()
    {
        var serviceDir = MakeDir("src", "MyService");
        var slnxPath = MakeFile(serviceDir, "MyService.slnx");
        var csFile = MakeFile(serviceDir, "Foo.cs");

        var router = new SolutionRouter(_root);
        var result = router.Route(csFile);

        Assert.Equal(slnxPath, result);
    }

    [Fact]
    public void AncestorWalk_NoSln_ReturnsNull()
    {
        var serviceDir = MakeDir("src", "Orphan");
        var csFile = MakeFile(serviceDir, "Orphan.cs");

        var router = new SolutionRouter(_root);
        var result = router.Route(csFile);

        Assert.Null(result);
    }

    [Fact]
    public void AncestorWalk_StopsAtRepoRoot_DoesNotEscape()
    {
        // .sln exists ABOVE repo root — must not be found
        var outerDir = Path.GetDirectoryName(_root)!;
        var escapeSln = Path.Combine(outerDir, $"{Path.GetRandomFileName()}.sln");
        File.WriteAllText(escapeSln, "");

        try
        {
            var serviceDir = MakeDir("src", "MyService");
            var csFile = MakeFile(serviceDir, "Foo.cs");

            var router = new SolutionRouter(_root);
            var result = router.Route(csFile);

            Assert.Null(result);
        }
        finally
        {
            File.Delete(escapeSln);
        }
    }

    [Fact]
    public void Route_SameFile_ReturnsCachedResult()
    {
        var serviceDir = MakeDir("src", "MyService");
        var slnPath = MakeFile(serviceDir, "MyService.sln");
        var csFile = MakeFile(serviceDir, "Foo.cs");

        var router = new SolutionRouter(_root);
        var first = router.Route(csFile);
        var second = router.Route(csFile);

        Assert.Equal(first, second);
        Assert.Equal(slnPath, first);
    }

    [Fact]
    public void InvalidateCache_AfterInvalidation_ReResolvesOnNextRoute()
    {
        var serviceDir = MakeDir("src", "MyService");
        var csFile = MakeFile(serviceDir, "Foo.cs");

        var router = new SolutionRouter(_root);
        var before = router.Route(csFile);
        Assert.Null(before);

        var slnPath = MakeFile(serviceDir, "MyService.sln");
        router.InvalidateCache(slnPath);
        var after = router.Route(csFile);

        Assert.Equal(slnPath, after);
    }
}
