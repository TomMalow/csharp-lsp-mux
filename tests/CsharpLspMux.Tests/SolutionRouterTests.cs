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

    // Sibling scan tests

    [Fact]
    public void SiblingScan_OneSln_ReturnsThatSln()
    {
        // src/
        //   ServiceA/           ← file lives here, no .sln ancestor
        //     Feature/
        //       Foo.cs
        //   ServiceB/
        //     ServiceB.sln
        var serviceADeep = MakeDir("src", "ServiceA", "Feature");
        var csFile = MakeFile(serviceADeep, "Foo.cs");
        var serviceBDir = MakeDir("src", "ServiceB");
        var slnPath = MakeFile(serviceBDir, "ServiceB.sln");

        var router = new SolutionRouter(_root);
        var result = router.Route(csFile);

        Assert.Equal(slnPath, result);
    }

    [Fact]
    public void SiblingScan_MultipleSlns_ReturnsOneWithMostCsprojRefs()
    {
        // src/
        //   ServiceA/Feature/Foo.cs    ← file, no .sln ancestor
        //   Small/Small.sln            ← 1 .csproj ref
        //   Big/Big.sln                ← 3 .csproj refs  ← expected winner
        var serviceADeep = MakeDir("src", "ServiceA", "Feature");
        var csFile = MakeFile(serviceADeep, "Foo.cs");

        var smallDir = MakeDir("src", "Small");
        var smallSln = Path.Combine(smallDir, "Small.sln");
        File.WriteAllText(smallSln, "Project(\"{FAE04EC0}\") = \"A\", \"A\\A.csproj\", \"{GUID}\"");

        var bigDir = MakeDir("src", "Big");
        var bigSln = Path.Combine(bigDir, "Big.sln");
        File.WriteAllText(bigSln,
            "Project = \"A\\A.csproj\"\nProject = \"B\\B.csproj\"\nProject = \"C\\C.csproj\"");

        var router = new SolutionRouter(_root);
        var result = router.Route(csFile);

        Assert.Equal(bigSln, result);
    }

    [Fact]
    public void SiblingScan_NoSrcAncestor_ReturnsNull()
    {
        // tools/scripts/Foo.cs — not under src/, no .sln ancestor
        var toolsDir = MakeDir("tools", "scripts");
        var csFile = MakeFile(toolsDir, "Foo.cs");
        // a .sln exists under src/ but the file has no src/ ancestor
        var srcDir = MakeDir("src", "SomeService");
        MakeFile(srcDir, "SomeService.sln");

        var router = new SolutionRouter(_root);
        var result = router.Route(csFile);

        Assert.Null(result);
    }

    [Fact]
    public void SiblingScan_SrcAncestorButNoSolutions_ReturnsNull()
    {
        // src/ServiceA/Feature/Foo.cs — under src/, but src/ has no .sln/.slnx
        var deepDir = MakeDir("src", "ServiceA", "Feature");
        var csFile = MakeFile(deepDir, "Foo.cs");

        var router = new SolutionRouter(_root);
        var result = router.Route(csFile);

        Assert.Null(result);
    }
}
