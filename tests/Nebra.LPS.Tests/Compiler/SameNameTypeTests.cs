using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class SameNameTypeTests
{
    private const string ServerNet = "export class Net\n    function serverOnly(): number\n        return 1\n    end\nend\n";
    private const string ClientNet = "export class Net\n    function clientOnly(): string\n        return \"c\"\n    end\nend\n";

    private static ProjectBuild Build(params (string Name, string Source)[] files)
    {
        return ProjectBuild.Run(null, files);
    }

    private static List<DiagnosticCode> Codes(ProjectBuild build)
    {
        return build.Errors.Select(error => error.Code).ToList();
    }

    [Fact]
    public void AClassKeepsOnlyItsOwnMembers()
    {
        var own = Build(("src/server/Net.neb", ServerNet), ("src/client/Net.neb", ClientNet), ("src/main.neb", "import { Net } from \"client/Net\"\n\nprint(new Net():clientOnly())\n"));
        var other = Build(("src/server/Net.neb", ServerNet), ("src/client/Net.neb", ClientNet), ("src/main.neb", "import { Net } from \"client/Net\"\n\nprint(new Net():serverOnly())\n"));

        Assert.Empty(own.Errors);
        Assert.Equal([DiagnosticCode.ErrNoSuchMethod], Codes(other));
    }

    [Fact]
    public void ClassesOfOneNameAreNotAssignableToEachOther()
    {
        var build = Build(
            ("src/server/Net.neb", ServerNet),
            ("src/client/Net.neb", ClientNet),
            ("src/main.neb", "import { Net } from \"client/Net\"\nimport { Net as ServerNet } from \"server/Net\"\n\nlocal net: Net = new ServerNet()\nprint(net)\n"));

        Assert.Equal([DiagnosticCode.ErrTypeMismatch], Codes(build));
    }

    [Fact]
    public void AClassImportedTwiceStaysOneType()
    {
        var build = Build(
            ("src/net/Net.neb", ServerNet),
            ("src/maker.neb", "import { Net } from \"net/Net\"\n\nexport function make(): Net\n    return new Net()\nend\n"),
            ("src/main.neb", "import { Net } from \"net/Net\"\nimport { make } from \"maker\"\n\nlocal net: Net = make()\nprint(net:serverOnly())\n"));

        Assert.Empty(build.Errors);
    }

    [Fact]
    public void InterfacesOfOneNameAreSeparate()
    {
        var build = Build(
            ("src/a/Shape.neb", "export interface Shape\n    area: number\nend\n"),
            ("src/b/Shape.neb", "export interface Shape\n    name: string\nend\n"),
            ("src/main.neb", "import { Shape } from \"a/Shape\"\n\nlocal shape: Shape = { area = 1 }\nprint(shape.area)\n"));

        Assert.Empty(build.Errors);
    }

    [Fact]
    public void EnumsOfOneNameAreSeparate()
    {
        var build = Build(
            ("src/a/Kind.neb", "export enum Kind\n    Fire\nend\n"),
            ("src/b/Kind.neb", "export enum Kind\n    Ice\nend\n"),
            ("src/main.neb", "import { Kind } from \"a/Kind\"\n\nprint(Kind.Ice)\n"));

        Assert.Equal([DiagnosticCode.ErrNoSuchMember], Codes(build));
    }

    [Fact]
    public void GenericClassesOfOneNameAreSeparate()
    {
        var build = Build(
            ("src/a/Box.neb", "export class Box<T>\n    function first(value: T): T\n        return value\n    end\nend\n"),
            ("src/b/Box.neb", "export class Box<T>\n    function second(value: T): T\n        return value\n    end\nend\n"),
            ("src/main.neb", "import { Box } from \"a/Box\"\n\nlocal box: Box<number> = new Box()\nprint(box:second(1))\n"));

        Assert.Equal([DiagnosticCode.ErrNoSuchMethod], Codes(build));
    }

    [Fact]
    public void GeneratedLuaDoesNotContainTheFileOfAType()
    {
        var build = Build(("src/a/Vec.neb", "export class Vec\nend\n\nextend Vec\n    function len(): number\n        return 1\n    end\nend\n\nprint(new Vec():len())\n"));

        Assert.Empty(build.Errors);
        Assert.DoesNotContain("nebra_project", build.Lua("src/a/Vec.neb").Replace("-", "_"));
        Assert.Contains("__ext_class_Vec__len", build.Lua("src/a/Vec.neb"));
    }
}
