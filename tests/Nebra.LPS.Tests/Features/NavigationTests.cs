using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class NavigationTests
{
    [Fact(Skip = "#63: the target is the start of the declaration, not the name")]
    public async Task DefinitionOfALocalFunctionJumpsToItsName()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function {|decl|}add(a: number, b: number): number\n    return a + b\nend\n\nprint({|call|}add(1, 2))\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("call");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact(Skip = "#63: the target is the start of the declaration, not the name")]
    public async Task DefinitionOfAnImportedFunctionJumpsIntoTheDeclaringFile()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function {|decl|}greet(name: string): string\n    return \"hi \" .. name\nend\n"),
            ("src/main.neb", "import { greet } from \"lib\"\n\nprint({|call|}greet(\"ada\"))\n"));

        await session.OpenAsync("src/main.neb");
        var targets = await session.DefinitionAsync("call");

        Assert.Equal([session.Location("decl")], targets);
    }

    [Fact(Skip = "#64: no location is returned for a method call")]
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
