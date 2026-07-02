using Xunit;

namespace CsharpLspMux.E2ETests;

public sealed class MonoRepoFixtureTests : IDisposable
{
    private readonly MonoRepoFixture _fixture;

    public MonoRepoFixtureTests()
    {
        _fixture = new MonoRepoFixture();
    }

    [Fact]
    public void Fixture_DoesNotCopyMsBuildAbsolutePathCacheFiles()
    {
        var staleFiles = Directory.GetFiles(_fixture.TempDir, "*.FileListAbsolute.txt", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(_fixture.TempDir, "*.cache", SearchOption.AllDirectories))
            .ToList();

        Assert.Empty(staleFiles);
    }

    public void Dispose() => _fixture.Dispose();
}
