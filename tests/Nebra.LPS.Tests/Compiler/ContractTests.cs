using Nebra.Diagnostics;
using Nebra.Runtime;

namespace Nebra.LPS.Tests.Compiler;

public sealed class ContractTests
{
    private const string Player = """
        contract Address
            @nonEmpty
            city: string
        end

        contract Player
            @length(1, 16)
            @pattern("^%a+$")
            name: string
            @range(0, 150)
            @integer
            age: number
            nick: string?
            mode: "a" | "b"
            home: Address
            friend: Player?
        end


        """;

    private const string Plain = "contract Plain\n    name: string\n    age: number\nend\n\n";

    private const string Valid = "name = \"Ann\", age = 30, mode = \"a\", home = { city = \"X\" }";

    private static ProjectBuild Build(string source)
    {
        return ProjectBuild.Run(null, ("src/main.neb", source));
    }

    private static List<DiagnosticCode> Errors(string source)
    {
        return Build(source).Errors.Select(error => error.Code).ToList();
    }

    private static string Assign(string fields)
    {
        return Player + $"local value: Player = {{ {fields} }}\nprint(value)\n";
    }

    [Fact]
    public void AValueWithExactlyTheFieldsFits()
    {
        Assert.Empty(Errors(Assign(Valid)));
    }

    [Theory]
    [InlineData(Valid + ", extra = 1")]
    [InlineData("name = \"Ann\", age = \"old\", mode = \"a\", home = { city = \"X\" }")]
    [InlineData("name = \"Ann\", age = 30, mode = \"a\"")]
    public void AnythingElseIsAMismatch(string fields)
    {
        Assert.Equal([DiagnosticCode.ErrTypeMismatch], Errors(Assign(fields)));
    }

    [Fact]
    public void AnAdditionalFieldIsRejectedThroughAVariable()
    {
        Assert.Equal([DiagnosticCode.ErrTypeMismatch], Errors(Plain + $"local written = {{ name = \"Ann\", age = 30, extra = 1 }}\nlocal value: Plain = written\nprint(value)\n"));
    }

    [Theory]
    [InlineData("name = \"Ann\", age = 200, mode = \"a\", home = { city = \"X\" }")]
    [InlineData("name = \"Ann\", age = -1, mode = \"a\", home = { city = \"X\" }")]
    [InlineData("name = \"Ann\", age = 1.5, mode = \"a\", home = { city = \"X\" }")]
    [InlineData("name = \"An1\", age = 30, mode = \"a\", home = { city = \"X\" }")]
    [InlineData("name = \"Ann\", age = 30, mode = \"a\", home = { city = \"\" }")]
    public void ALiteralBreakingARuleIsReported(string fields)
    {
        Assert.Equal([DiagnosticCode.ErrContractViolation], Errors(Assign(fields)));
    }

    [Fact]
    public void ALiteralIsCheckedAsAnArgumentAndAReturnValue()
    {
        var bad = "{ name = \"Ann\", age = 200, mode = \"a\", home = { city = \"X\" } }";

        Assert.Equal([DiagnosticCode.ErrContractViolation], Errors(Player + $"local function take(player: Player): nil\nend\n\ntake({bad})\n"));
        Assert.Equal([DiagnosticCode.ErrContractViolation], Errors(Player + $"local function make(): Player\n    return {bad}\nend\n\nprint(make)\n"));
    }

    [Fact]
    public void AValueUnknownAtCompileTimeIsAccepted()
    {
        Assert.Empty(Errors(Player + "local function age(): number\n    return 500\nend\n\nlocal value: Player = { name = \"Ann\", age = age(), mode = \"a\", home = { city = \"X\" } }\nprint(value)\n"));
    }

    [Theory]
    [InlineData("local value: T = { items = { 1 } }\nprint(value)\n", DiagnosticCode.ErrContractViolation)]
    [InlineData("local value: T = { items = { 1, 2 } }\nprint(value)\n", null)]
    public void TheLengthOfAListIsChecked(string use, DiagnosticCode? expected)
    {
        var errors = Errors("contract T\n    @length(2)\n    items: number[]\nend\n\n" + use);

        Assert.Equal(expected == null ? [] : [expected.Value], errors);
    }

    [Theory]
    [InlineData("@bogus\n    x: number", DiagnosticCode.ErrContractRuleInvalid)]
    [InlineData("@range(5, 1)\n    x: number", DiagnosticCode.ErrContractRuleInvalid)]
    [InlineData("@range\n    x: number", DiagnosticCode.ErrContractRuleInvalid)]
    [InlineData("@integer(1)\n    x: number", DiagnosticCode.ErrContractRuleInvalid)]
    [InlineData("@pattern(\"[\")\n    x: string", DiagnosticCode.ErrContractRuleInvalid)]
    [InlineData("@range(1, 2)\n    x: string", DiagnosticCode.ErrContractRuleType)]
    [InlineData("@pattern(\"a\")\n    x: number", DiagnosticCode.ErrContractRuleType)]
    public void AMalformedRuleIsReported(string field, DiagnosticCode expected)
    {
        Assert.Equal([expected], Errors($"contract T\n    {field}\nend\n\nprint(T)\n"));
    }

    [Theory]
    [InlineData("@range(min = 0)\n    x: number")]
    [InlineData("@range(max = 10)\n    x: number")]
    [InlineData("@length(min = 1)\n    x: string")]
    [InlineData("@range(0, 1)\n    x: number?")]
    public void OneSidedAndOptionalRulesAreAccepted(string field)
    {
        Assert.Empty(Errors($"contract T\n    {field}\nend\n\nprint(T)\n"));
    }

    [Fact]
    public void AClassCannotImplementAContract()
    {
        Assert.Equal([DiagnosticCode.ErrImplementsContract], Errors("contract T\nend\n\nclass C implements T\nend\n\nprint(C)\n"));
    }

    [Fact]
    public void AContractIncludesTheFieldsItExtends()
    {
        const string types = "contract A\n    x: number\nend\n\ncontract B extends A\n    y: number\nend\n\n";

        Assert.Empty(Errors(types + "local value: B = { x = 1, y = 2 }\nprint(value)\n"));
        Assert.Equal([DiagnosticCode.ErrTypeMismatch], Errors(types + "local value: A = { x = 1, y = 2 }\nprint(value)\n"));
    }

    [Fact]
    public void AnInterfaceStaysLenient()
    {
        Assert.Empty(Errors("interface T\n    age: number\nend\n\nlocal value: T = { age = 1, extra = 2 }\nprint(value)\n"));
    }

    [Fact]
    public void RulesAreOnlyKnownInAContract()
    {
        Assert.Equal([DiagnosticCode.ErrUnknownAnnotation], Errors("interface T\n    @range(1, 2)\n    age: number\nend\n\nprint(T)\n"));
    }

    [Theory]
    [InlineData("local contract = 1\nprint(contract)\n")]
    [InlineData("local function contract(value: any): nil\nend\n\ncontract \"x\"\ncontract({})\ncontract { a = 1 }\n")]
    public void ContractStaysAnOrdinaryName(string source)
    {
        Assert.Empty(Errors(source));
    }

    [Fact]
    public void AContractCanBeExportedAndDeclared()
    {
        var build = ProjectBuild.Run(
            null,
            ("src/types.neb", "export contract A\n    @range(0, 1)\n    x: number\nend\n"),
            ("src/host.d.neb", "declare contract D\n    @range(0, 1)\n    x: number\nend\n"),
            ("src/main.neb", "import { A } from \"types\"\n\nlocal a: A = { x = 5 }\nlocal d: D = { x = 5 }\nprint(a, d)\n"));

        Assert.Equal([DiagnosticCode.ErrContractViolation, DiagnosticCode.ErrContractViolation], build.Errors.Select(error => error.Code));
    }

    private static void AssertRuns(string source)
    {
        var build = Build(source);
        Assert.Empty(build.Errors);

        using var runtime = NebraRuntime.CreateSandboxed();
        Assert.Null(runtime.LoadAndRun(build.Lua("src/main.neb"), "main"));
    }

    [Theory]
    [InlineData(Valid, true)]
    [InlineData(Valid + ", nick = \"n\"", true)]
    [InlineData(Valid + ", friend = { name = \"Bob\", age = 1, mode = \"b\", home = { city = \"Y\" } }", true)]
    [InlineData(Valid + ", extra = 1", false)]
    [InlineData(Valid + ", friend = { name = \"Bob\", age = 999, mode = \"b\", home = { city = \"Y\" } }", false)]
    [InlineData("name = \"An1\", age = 30, mode = \"a\", home = { city = \"X\" }", false)]
    [InlineData("name = \"Ann\", age = 151, mode = \"a\", home = { city = \"X\" }", false)]
    [InlineData("name = \"Ann\", age = 1.5, mode = \"a\", home = { city = \"X\" }", false)]
    [InlineData("name = \"Ann\", age = \"x\", mode = \"a\", home = { city = \"X\" }", false)]
    [InlineData("name = \"Ann\", age = 30, mode = \"c\", home = { city = \"X\" }", false)]
    [InlineData("name = \"Ann\", age = 30, mode = \"a\", home = { city = \"\" }", false)]
    [InlineData("name = \"Ann\", age = 30, mode = \"a\"", false)]
    public void IsTestsEveryFieldAndRuleAtRuntime(string fields, bool expected)
    {
        AssertRuns(Player + $"local value: any = {{ {fields} }}\nassert((value is Player) == {expected.ToString().ToLowerInvariant()})\n");
    }

    [Fact]
    public void IsAcceptsOnlyTables()
    {
        AssertRuns(Player + "local nothing: any = nil\nlocal number: any = 5\nassert(not (nothing is Player))\nassert(not (number is Player))\n");
    }

    [Fact]
    public void IsNarrowsTheValue()
    {
        AssertRuns(Player + $"local value: any = {{ {Valid} }}\nlocal name = \"\"\nif value is Player then\n    name = value.name\nend\nassert(name == \"Ann\")\n");
    }

    [Fact]
    public void AMatchCaseTestsTheContract()
    {
        AssertRuns(Player + $"local value: any = {{ {Valid} }}\nlocal found = match value\n    case other: Address then \"address\"\n    case player: Player then \"player\"\n    case _ then \"none\"\nend\nassert(found == \"player\")\n");
    }
}
