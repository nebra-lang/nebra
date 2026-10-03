using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class RenameTests
{
    [Fact]
    public async Task RenamingALocalEditsEveryUseInTheFile()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local {|decl|}total: number = 0\n{|first|}total = {|second|}total + 1\nprint({|third|}total)\n"));

        await session.OpenAsync("src/main.neb");
        var edits = await session.RenameAsync("third", "sum");

        Assert.Equal(
            [session.Location("decl"), session.Location("first"), session.Location("second"), session.Location("third")],
            edits);
    }

    [Fact]
    public async Task RenamingAnExportedFunctionEditsTheDeclarationAndEveryImporter()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function {|decl|}greet(name: string): string\n    return \"hi \" .. name\nend\n"),
            ("src/main.neb", "import { {|mainImport|}greet } from \"lib\"\n\nprint({|mainCall|}greet(\"ada\"))\n"),
            ("src/other.neb", "import { {|otherImport|}greet } from \"lib\"\n\nprint({|otherCall|}greet(\"bob\"))\n"),
            ("src/unrelated.neb", "local function greet(): string\n    return \"own\"\nend\n\nprint(greet())\n"));

        await session.OpenAsync("src/main.neb");
        var edits = await session.RenameAsync("mainCall", "welcome");

        Assert.Equal(
            [
                session.Location("decl"),
                session.Location("mainImport"),
                session.Location("mainCall"),
                session.Location("otherImport"),
                session.Location("otherCall")
            ],
            edits);
    }

    [Fact]
    public async Task RenamingFromTheDeclarationReachesTheImporters()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function {|decl|}greet(name: string): string\n    return \"hi \" .. name\nend\n"),
            ("src/main.neb", "import { {|mainImport|}greet } from \"lib\"\n\nprint({|mainCall|}greet(\"ada\"))\n"));

        await session.OpenAsync("src/lib.neb");
        var edits = await session.RenameAsync("decl", "welcome");

        Assert.Equal(
            [session.Location("decl"), session.Location("mainImport"), session.Location("mainCall")],
            edits);
    }
}
