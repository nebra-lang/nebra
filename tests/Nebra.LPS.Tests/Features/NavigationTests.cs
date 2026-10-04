using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class NavigationTests
{
    [Fact]
    public async Task DefinitionOfALocalFunctionJumpsToItsName()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function {|decl|}add(a: number, b: number): number\n    return a + b\nend\n\nprint({|call|}add(1, 2))\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("call");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact]
    public async Task DefinitionOfAnImportedFunctionJumpsIntoTheDeclaringFile()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function {|decl|}greet(name: string): string\n    return \"hi \" .. name\nend\n"),
            ("src/main.neb", "import { greet } from \"lib\"\n\nprint({|call|}greet(\"ada\"))\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("call");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact]
    public async Task DefinitionOfAParameterJumpsToItsName()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function scale(value: number, {|decl|}factor: number): number\n    return value * {|use|}factor\nend\n\nprint(scale(2, 3))\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("use");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact]
    public async Task DefinitionOfATypedLocalJumpsToItsName()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local first: number = 1\nlocal {|decl|}second: number = first + 1\nprint({|use|}second)\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("use");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact]
    public async Task DefinitionOfAMethodJumpsToItsName()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "class Counter\n    value: number = 0\n\n    function {|decl|}bump(): number\n        self.value = self.value + 1\n        return self.value\n    end\nend\n\nlocal counter = new Counter()\nprint(counter:{|call|}bump())\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("call");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact]
    public async Task DefinitionOfAFieldJumpsToItsName()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "class Counter\n    {|decl|}value: number = 0\nend\n\nlocal counter = new Counter()\nprint(counter.{|use|}value)\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("use");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact]
    public async Task DefinitionOfAnInheritedMethodJumpsToTheBaseClass()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "class Animal\n    function {|decl|}speak(): string\n        return \"...\"\n    end\nend\n\nclass Cat extends Animal\nend\n\nlocal cat = new Cat()\nprint(cat:{|use|}speak())\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("use");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact]
    public async Task DefinitionOfAnInterfaceMethodJumpsToTheInterface()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "interface Named\n    function {|decl|}name(): string\nend\n\nclass Person implements Named\n    function name(): string\n        return \"ada\"\n    end\nend\n\nlocal named: Named = new Person()\nprint(named:{|use|}name())\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("use");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact]
    public async Task DefinitionOfAnExtensionMethodJumpsToTheExtendBlock()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "extend string\n    function {|decl|}shout(): string\n        return string.upper(self)\n    end\nend\n\nprint((\"hi\"):{|use|}shout())\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("use");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact]
    public async Task DefinitionOfAMethodOnAnImportedClassJumpsIntoItsFile()
    {
        await using var session = await LspSession.StartAsync(
            ("src/shapes.neb",
                "export class Square\n    side: number = 2\n\n    function {|decl|}area(): number\n        return self.side * self.side\n    end\nend\n"),
            ("src/main.neb", "import { Square } from \"shapes\"\n\nlocal square = new Square()\nprint(square:{|use|}area())\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("use");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact]
    public async Task ReferencesWithinOneFileListEveryUse()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local {|decl|}total: number = 0\n{|first|}total = {|second|}total + 1\nprint({|third|}total)\n"));

        await session.OpenAsync("src/main.neb");
        var references = await session.ReferencesAsync("third", includeDeclaration: false);

        Assert.Equal(
            [session.Location("first"), session.Location("second"), session.Location("third")],
            references.Where(r => r != session.Location("decl")));
    }

    [Fact(Skip = "#66: references are collected from the requesting file only")]
    public async Task ReferencesOfAnExportedFunctionSpanEveryImporter()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function {|decl|}greet(name: string): string\n    return \"hi \" .. name\nend\n"),
            ("src/main.neb", "import { {|mainImport|}greet } from \"lib\"\n\nprint({|mainCall|}greet(\"ada\"))\n"),
            ("src/other.neb", "import { {|otherImport|}greet } from \"lib\"\n\nprint({|otherCall|}greet(\"bob\"))\n"));

        await session.OpenAsync("src/main.neb");
        var references = await session.ReferencesAsync("mainCall", includeDeclaration: true);

        Assert.Equal(
            [
                session.Location("decl"),
                session.Location("mainImport"),
                session.Location("mainCall"),
                session.Location("otherImport"),
                session.Location("otherCall")
            ],
            references);
    }
}
