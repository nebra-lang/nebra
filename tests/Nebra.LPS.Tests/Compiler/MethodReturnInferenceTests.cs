using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class MethodReturnInferenceTests
{
    private static List<DiagnosticCode> Errors(string source)
    {
        var root = Directory.CreateTempSubdirectory("nebra-method-returns").FullName;
        var compiler = new NebraCompiler { Config = new Config { ProjectRoot = root, Source = "src" } };
        compiler.AddSource(Path.Combine(root, "src", "main.neb"), source);
        compiler.Check();
        return compiler.Diagnostics.Diagnostics
            .Where(diagnostic => diagnostic.Level == DiagnosticLevel.Error)
            .Select(diagnostic => diagnostic.Code)
            .ToList();
    }

    private const string Counter = "class Counter\n    function count()\n        return 1\n    end\n\n    static function label()\n        return \"counter\"\n    end\nend\n\n";

    [Fact]
    public void AMethodWithoutAReturnTypeMayReturnAValue()
    {
        Assert.Empty(Errors(Counter + "print(new Counter():count(), Counter.label())\n"));
    }

    [Fact]
    public void TheReturnTypeOfAMethodIsTakenFromItsBody()
    {
        Assert.Empty(Errors(Counter + "local value: number = new Counter():count()\nprint(value)\n"));
        Assert.Equal([DiagnosticCode.ErrTypeMismatch], Errors(Counter + "local value: string = new Counter():count()\nprint(value)\n"));
        Assert.Equal([DiagnosticCode.ErrTypeMismatch], Errors(Counter + "local value: number = Counter.label()\nprint(value)\n"));
    }

    [Fact]
    public void AMethodMayCallOneDeclaredLater()
    {
        Assert.Empty(Errors("class Pair\n    function first()\n        return self:second()\n    end\n\n    function second()\n        return 1\n    end\nend\n\nprint(new Pair():first())\n"));
    }

    [Fact]
    public void AnOverrideOfAMethodReturningNothingMayReturnAValue()
    {
        Assert.Empty(Errors("class Base\n    function run()\n    end\nend\n\nclass Child extends Base\n    override function run()\n        return 1\n    end\nend\n\nprint(new Child():run())\n"));
    }

    [Fact]
    public void AnOverrideKeepsTheReturnTypeItsBasePromises()
    {
        Assert.Equal([DiagnosticCode.ErrTypeMismatch], Errors("class Base\n    function run(): number\n        return 1\n    end\nend\n\nclass Child extends Base\n    override function run()\n        return \"text\"\n    end\nend\n\nprint(new Child():run())\n"));
    }

    [Fact]
    public void AnImplementationWithoutAReturnTypeMayReturnAValue()
    {
        Assert.Empty(Errors("interface Runner\n    function run()\nend\n\nclass Fast implements Runner\n    function run()\n        return 1\n    end\nend\n\nprint(new Fast():run())\n"));
    }

    [Fact]
    public void AnImplementationAnnotatedAgainstItsInterfaceIsStillReported()
    {
        Assert.Equal([DiagnosticCode.ErrInterfaceMethodReturnType], Errors("interface Runner\n    function run()\nend\n\nclass Fast implements Runner\n    function run(): number\n        return 1\n    end\nend\n\nprint(new Fast():run())\n"));
    }

    [Theory]
    [InlineData("interface Named\n    function name()\n        return \"x\"\n    end\nend\n\nprint(Named)\n")]
    [InlineData("extend string\n    function shout()\n        return self\n    end\nend\n\nprint((\"a\"):shout())\n")]
    [InlineData("class Point\n    operator +(other: Point)\n        return other\n    end\nend\n\nprint(new Point() + new Point())\n")]
    [InlineData("class Gate\n    function open(level: number)\n        if level > 1 then\n            return 1\n        end\n    end\nend\n\nprint(new Gate():open(1))\n")]
    public void OtherMethodsWithoutAReturnTypeAcceptAReturn(string source)
    {
        Assert.Empty(Errors(source));
    }
}
