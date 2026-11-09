using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class FunctionTypeParameterTests
{
    private static ProjectBuild Build(params (string Name, string Source)[] files)
    {
        return ProjectBuild.Run(null, files);
    }

    private static ProjectBuild Build(string source)
    {
        return Build(("src/main.neb", source));
    }

    [Theory]
    [InlineData("(entity: number) -> nil")]
    [InlineData("(entity: number, value: string) -> nil")]
    [InlineData("(data: string, player: number?) -> nil")]
    [InlineData("(entity: number, string) -> nil")]
    [InlineData("(class: number, module: string) -> nil")]
    [InlineData("(items: number[], map: { [string]: number }) -> nil")]
    public void ParametersOfAFunctionTypeCanBeNamed(string type)
    {
        Assert.Empty(Build($"local callback: {type} = nil\nprint(callback)\n").Errors);
    }

    [Fact]
    public void AFunctionFitsWhateverItsParametersAreCalled()
    {
        var build = Build("local function run(handler: (entity: number, value: string) -> nil): nil\n    handler(1, \"a\")\nend\n\nrun(function(id: number, text: string): nil\n    print(id, text)\nend)\n");

        Assert.Empty(build.Errors);
    }

    [Fact]
    public void TheParametersOfACallbackAreTypedFromTheNamedOnes()
    {
        var build = Build("local function run(handler: (entity: number, value: string) -> nil): nil\nend\n\nrun(function(a, b)\n    local text: number = b\n    print(a, text)\nend)\n");

        Assert.Equal(DiagnosticCode.ErrTypeMismatch, Assert.Single(build.Errors).Code);
    }

    [Fact]
    public void NamedParametersKeepTheOtherDeclarationsOfAFile()
    {
        var build = Build(
            ("src/items.d.neb", "declare class Items\n    function each(handler: (entity: number, value: string) -> nil): nil\nend\n\ndeclare function other(): number\n"),
            ("src/main.neb", "print(Items, other())\n"));

        Assert.Empty(build.Errors);
    }

    [Fact]
    public void AGroupedTypeAndATupleStillParse()
    {
        Assert.Empty(Build("local first: (number | string)[] = {}\nlocal second: () -> (number, string) = nil\nprint(first, second)\n").Errors);
    }
}
