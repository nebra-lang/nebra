using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class GlobImportTests
{
    private const string Module = "print(\"loaded\")\n";

    private static ProjectBuild Build(params (string Name, string Source)[] files)
    {
        return ProjectBuild.Run(null, files);
    }

    [Fact]
    public void AStarImportsEveryFileOfADirectoryInAStableOrder()
    {
        var build = Build(("src/models/B.neb", Module), ("src/models/A.neb", Module), ("src/models/sub/C.neb", Module), ("src/main.neb", "import \"models/*.neb\"\n"));

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        var first = lua.IndexOf("models/A", StringComparison.Ordinal);
        var second = lua.IndexOf("models/B", StringComparison.Ordinal);
        Assert.True(first >= 0 && first < second, lua);
        Assert.DoesNotContain("models/sub/C", lua);
    }

    [Fact]
    public void TheExtensionOfAGlobIsOptional()
    {
        var build = Build(("src/models/A.neb", Module), ("src/main.neb", "import \"models/*\"\n"));

        Assert.Empty(build.Errors);
        Assert.Contains("models/A", build.Lua("src/main.neb"));
    }

    [Fact]
    public void ADoubleStarReachesNestedDirectories()
    {
        var build = Build(("src/models/A.neb", Module), ("src/models/sub/deep/C.neb", Module), ("src/main.neb", "import \"models/**/*.neb\"\n"));

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("models/A", lua);
        Assert.Contains("models/sub/deep/C", lua);
    }

    [Fact]
    public void AGlobPrefersTheDirectoryOfTheImportingFile()
    {
        var build = Build(("src/items/Root.neb", Module), ("src/feature/items/Near.neb", Module), ("src/feature/main.neb", "import \"items/*.neb\"\n"));

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/feature/main.neb");
        Assert.Contains("items/Near", lua);
        Assert.DoesNotContain("items/Root", lua);
    }

    [Fact]
    public void AGlobFallsBackToTheSourceRoot()
    {
        var build = Build(("src/items/Root.neb", Module), ("src/feature/main.neb", "import \"items/*.neb\"\n"));

        Assert.Empty(build.Errors);
        Assert.Contains("items/Root", build.Lua("src/feature/main.neb"));
    }

    [Fact]
    public void AGlobSkipsTheImportingFileAndDeclarationFiles()
    {
        var build = Build(("src/a.neb", Module), ("src/types.d.neb", "declare function ambient(): nil\n"), ("src/main.neb", "import \"*.neb\"\n"));

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("\"a\"", lua);
        Assert.DoesNotContain("\"main\"", lua);
        Assert.DoesNotContain("types", lua);
    }

    [Fact]
    public void AGlobMatchingNothingWarns()
    {
        var build = Build(("src/main.neb", "import \"missing/*.neb\"\n"));

        Assert.Empty(build.Errors);
        Assert.Contains(build.Compiler.Diagnostics.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCode.WarnGlobImportEmpty);
    }

    [Fact]
    public void ANamedImportCannotUseAGlob()
    {
        var build = Build(("src/models/A.neb", "export local value = 1\n"), ("src/main.neb", "import { value } from \"models/*.neb\"\nprint(value)\n"));

        Assert.Contains(build.Errors, error => error.Code == DiagnosticCode.ErrModuleNotFound);
    }
}
