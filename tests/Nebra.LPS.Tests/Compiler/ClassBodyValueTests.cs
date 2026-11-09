using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class ClassBodyValueTests
{
    private const string Table = "local Table = { Fire = 1 }\n\n";
    private const string Exporter = "export local Table = { Fire = 1 }\nexport local Typed: { Fire: number } = { Fire = 1 }\n";

    private static List<DiagnosticCode> Errors(string source)
    {
        return ProjectBuild.Run(null, ("src/spells.neb", Exporter), ("src/main.neb", source)).Errors.Select(error => error.Code).ToList();
    }

    [Theory]
    [InlineData("class C\n    function m()\n        print(Table.Nope)\n    end\nend\n\nprint(C)\n")]
    [InlineData("class C\n    constructor()\n        print(Table.Nope)\n    end\nend\n\nprint(C)\n")]
    [InlineData("class C\n    value = Table.Nope\nend\n\nprint(C)\n")]
    [InlineData("class C\n    value: number = Table.Nope\nend\n\nprint(C)\n")]
    [InlineData("class C\n    static value = Table.Nope\nend\n\nprint(C)\n")]
    public void AValueOfTheFileIsCheckedInsideAClass(string body)
    {
        Assert.Equal([DiagnosticCode.ErrNoSuchKey], Errors(Table + body));
    }

    [Theory]
    [InlineData("Table")]
    [InlineData("Typed")]
    public void AnImportedValueIsCheckedInsideAClass(string name)
    {
        Assert.Equal([DiagnosticCode.ErrNoSuchKey], Errors($"import {{ {name} }} from \"spells\"\n\nclass C\n    function m()\n        print({name}.Nope)\n    end\nend\n\nprint(C)\n"));
    }

    [Fact]
    public void ACorrectUseIsAccepted()
    {
        Assert.Empty(Errors(Table + "class C\n    value = Table.Fire\n\n    function m(): number\n        return Table.Fire + self.value\n    end\nend\n\nprint(new C():m())\n"));
    }

    [Fact]
    public void AnUnannotatedFieldTakesTheTypeOfItsDefault()
    {
        Assert.Equal([DiagnosticCode.ErrTypeMismatch], Errors(Table + "class C\n    value = Table.Fire\nend\n\nlocal text: string = new C().value\nprint(text)\n"));
    }

    [Fact]
    public void AFieldDefaultIsReportedOnce()
    {
        Assert.Equal([DiagnosticCode.ErrUndeclaredSymbol], Errors("class C\n    value = missing\nend\n\nprint(C)\n"));
    }

    [Fact]
    public void AClassMayStillUseAMemberOfAClassBelowIt()
    {
        Assert.Empty(Errors("class First\n    function make(): number\n        return new Second():value()\n    end\nend\n\nclass Second\n    function value(): number\n        return 1\n    end\nend\n\nprint(new First():make())\n").Where(code => code != DiagnosticCode.ErrUsedBeforeDefinition));
    }
}
