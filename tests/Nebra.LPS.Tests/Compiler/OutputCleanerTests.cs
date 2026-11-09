using Nebra.Compiler;
using Nebra.Configuration;

namespace Nebra.LPS.Tests.Compiler;

public sealed class OutputCleanerTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("nebra-clean").FullName;

    private static readonly DateTime Now = DateTime.UtcNow;

    private string Write(string path, DateTime written)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        File.SetLastWriteTimeUtc(full, written);
        return full;
    }

    private string? Clean(string output = "out", string source = "src")
    {
        return OutputCleaner.Clean(new Config { Output = output, Source = source }, _root, Now);
    }

    private string Old(string path)
    {
        return Write(path, Now.AddHours(-1));
    }

    [Fact]
    public void FilesFromEarlierBuildsAreRemoved()
    {
        var stale = Old("out/stale.lua");
        var nested = Old("out/deleted/module.lua");

        Assert.Null(Clean());
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(nested));
        Assert.False(Directory.Exists(Path.Combine(_root, "out/deleted")));
        Assert.True(Directory.Exists(Path.Combine(_root, "out")));
    }

    [Fact]
    public void FilesWrittenSinceTheBuildStartedStay()
    {
        var script = Write("out/generated/by-script.lua", Now.AddSeconds(1));

        Assert.Null(Clean());
        Assert.True(File.Exists(script));
    }

    [Fact]
    public void AMissingDirectoryIsFine()
    {
        Assert.Null(Clean());
    }

    [Theory]
    [InlineData(".", "src")]
    [InlineData("..", "src")]
    [InlineData("src", "src")]
    [InlineData("code", "code/src")]
    public void ADirectoryThatIsNotItsOwnIsLeftAlone(string output, string source)
    {
        var kept = Old(Path.Combine(source, "main.neb"));

        Assert.NotNull(Clean(output, source));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void ADirectoryHoldingAProjectIsLeftAlone()
    {
        var manifest = Old("out/nebra.toml");

        Assert.NotNull(Clean());
        Assert.True(File.Exists(manifest));
    }

    [Fact]
    public void ADirectoryHoldingPackagesIsLeftAlone()
    {
        var package = Old("out/nebra_modules/a/init.neb");

        Assert.NotNull(Clean());
        Assert.True(File.Exists(package));
    }

    [Fact]
    public void ASymbolicLinkedDirectoryIsLeftAlone()
    {
        var kept = Old("elsewhere/keep.lua");
        Directory.CreateSymbolicLink(Path.Combine(_root, "out"), Path.Combine(_root, "elsewhere"));

        Assert.NotNull(Clean());
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void ALinkInsideTheDirectoryIsNotFollowed()
    {
        var kept = Old("elsewhere/keep.lua");
        Old("out/stale.lua");
        Directory.CreateSymbolicLink(Path.Combine(_root, "out/link"), Path.Combine(_root, "elsewhere"));

        Assert.Null(Clean());
        Assert.True(File.Exists(kept));
    }
}
