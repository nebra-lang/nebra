using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class IteratorTypingTests
{
    private static List<DiagnosticCode> Errors(string source)
    {
        return ProjectBuild.Run(null, ("src/main.neb", source)).Errors.Select(error => error.Code).ToList();
    }

    [Fact]
    public void IpairsTakesTheElementTypeOfAList()
    {
        Assert.Empty(Errors("local list: number[] = { 1 }\nfor index, value in ipairs(list) do\n    local sum: number = index + value\n    print(sum)\nend\n"));
        Assert.Equal([DiagnosticCode.ErrTypeMismatch], Errors("local list: number[] = { 1 }\nfor _, value in ipairs(list) do\n    local text: string = value\n    print(text)\nend\n"));
    }

    [Fact]
    public void PairsTakesTheTypesOfAMapAndAList()
    {
        Assert.Equal([DiagnosticCode.ErrTypeMismatch, DiagnosticCode.ErrTypeMismatch], Errors("local map: { [string]: number } = {}\nfor key, value in pairs(map) do\n    local a: number = key\n    local b: string = value\n    print(a, b)\nend\n"));
        Assert.Equal([DiagnosticCode.ErrTypeMismatch], Errors("local list: number[] = { 1 }\nfor _, value in pairs(list) do\n    local text: string = value\n    print(text)\nend\n"));
    }

    [Fact]
    public void AGenericElementKeepsItsTypeArguments()
    {
        const string types = "interface Data\n    value: number\nend\n\ninterface Row<T>\n    value: T\nend\n\ndeclare function rows<T>(sample: T): Row<T>[]\ndeclare function sample(): Data\n\n";

        Assert.Equal([DiagnosticCode.ErrNoSuchMember], Errors(types + "for _, row in ipairs(rows(sample())) do\n    print(row.value.nope)\nend\n"));
    }

    [Fact]
    public void AnOptionalListIsIterated()
    {
        Assert.Equal([DiagnosticCode.ErrTypeMismatch], Errors("local list: number[]? = { 1 }\nfor _, value in ipairs(list) do\n    local text: string = value\n    print(text)\nend\n"));
    }

    [Theory]
    [InlineData("local list: any = {}\nfor _, value in ipairs(list) do\n    local text: string = value\n    print(text)\nend\n")]
    [InlineData("local function ipairs(value: any): any\n    return value\nend\n\nlocal list: number[] = { 1 }\nfor _, value in ipairs(list) do\n    local text: string = value\n    print(text)\nend\n")]
    [InlineData("local map: { [string]: number } = {}\nfor _, value in ipairs(map) do\n    local text: string = value\n    print(text)\nend\n")]
    public void OtherIteratorsStayUntyped(string source)
    {
        Assert.Empty(Errors(source));
    }
}
