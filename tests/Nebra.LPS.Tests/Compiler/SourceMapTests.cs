using System.Text.Json;
using Nebra.Compiler;

namespace Nebra.LPS.Tests.Compiler;

public sealed class SourceMapTests
{
    private static ProjectBuild Build(string source)
    {
        return ProjectBuild.Run(config => config.SourceMaps = true, ("src/main.neb", source));
    }

    private static int[] MapOf(ProjectBuild build)
    {
        var path = Path.Combine(build.Root, "src/main.neb");
        return build.Compiler.Packages.Values.SelectMany(package => package.Files).First(file => file.Filename == path).GeneratedLineSources!;
    }

    private static int SourceLineOf(ProjectBuild build, string generatedText)
    {
        var lines = build.Lua("src/main.neb").Split('\n');
        return MapOf(build)[Array.FindIndex(lines, line => line.Contains(generatedText))];
    }

    [Fact]
    public void AGeneratedLineMapsToTheLineItWasWrittenOn()
    {
        var build = Build("print(1)\n\n\nprint(2)\n");

        Assert.Empty(build.Errors);
        Assert.Equal(1, SourceLineOf(build, "print(1)"));
        Assert.Equal(4, SourceLineOf(build, "print(2)"));
    }

    [Fact]
    public void HelpersAtTheTopDoNotShiftTheLines()
    {
        var build = Build("local value: any = 1\n\nprint(value == nil or `a{value}`)\n\nerror(\"boom\")\n");

        Assert.Empty(build.Errors);
        Assert.Equal(5, SourceLineOf(build, "error(\"boom\")"));
        Assert.Equal(0, MapOf(build)[0]);
    }

    [Fact]
    public void TheBodyOfAFunctionAndAMethodIsMapped()
    {
        var build = Build("local function run(): number\n\n    return 41\nend\n\nclass C\n    function m(): number\n\n        return 42\n    end\nend\n\nprint(run, C)\n");

        Assert.Empty(build.Errors);
        Assert.Equal(3, SourceLineOf(build, "return 41"));
        Assert.Equal(9, SourceLineOf(build, "return 42"));
    }

    [Fact]
    public void AnExpandedStatementKeepsItsLineOnEveryGeneratedLine()
    {
        var build = Build("local list = { 1 }\nfor _, value in ipairs(list) do\n    print(value)\nend\n");

        Assert.Empty(build.Errors);
        var map = MapOf(build);
        Assert.Equal(build.Lua("src/main.neb").Split('\n').Length, map.Length);
        Assert.Equal(3, SourceLineOf(build, "print(value)"));
    }

    [Fact]
    public void TheFileNamesTheGeneratedFileAndItsSource()
    {
        var root = Directory.CreateTempSubdirectory("nebra-map").FullName;
        var map = JsonDocument.Parse(SourceMapFile.Render(Path.Combine(root, "out/server/main.lua"), Path.Combine(root, "src/server/main.neb"), [0, 3])).RootElement;

        Assert.Equal(1, map.GetProperty("version").GetInt32());
        Assert.Equal("main.lua", map.GetProperty("file").GetString());
        Assert.Equal("../../src/server/main.neb", map.GetProperty("source").GetString());
        Assert.Equal([0, 3], map.GetProperty("lines").EnumerateArray().Select(line => line.GetInt32()));
    }

    private static SourceLocations Locations()
    {
        var locations = new SourceLocations();
        locations.Add("/project/out/server/main.lua", "src/server/main.neb", [0, 3, 7]);
        return locations;
    }

    [Theory]
    [InlineData("/project/out/server/main.lua:2: boom", "src/server/main.neb:3: boom")]
    [InlineData("out/server/main.lua:3: boom", "src/server/main.neb:7: boom")]
    [InlineData(".../out/server/main.lua:2: boom", "src/server/main.neb:3: boom")]
    [InlineData("in function </project/out/server/main.lua:3>", "in function <src/server/main.neb:7>")]
    [InlineData("stack traceback:\n\t/project/out/server/main.lua:2: in function 'run'\n\t[C]: in ?", "stack traceback:\n\tsrc/server/main.neb:3: in function 'run'\n\t[C]: in ?")]
    public void APositionInAGeneratedFileIsTranslated(string text, string expected)
    {
        Assert.Equal(expected, Locations().Translate(text));
    }

    [Theory]
    [InlineData("/project/out/server/main.lua:1: helper")]
    [InlineData("/project/out/server/main.lua:9: past the end")]
    [InlineData("/somewhere/other.lua:2: not ours")]
    [InlineData("nothing to see: 12")]
    public void AnythingElseIsLeftAlone(string text)
    {
        Assert.Equal(text, Locations().Translate(text));
    }
}
