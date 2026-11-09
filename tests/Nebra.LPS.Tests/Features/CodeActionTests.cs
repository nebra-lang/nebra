using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class CodeActionTests
{
    [Fact]
    public async Task MissingInterfaceMembersOfferAnImplementQuickFix()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "interface Named\n    function name(): string\nend\n\nclass Person implements Named\nend\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAsync("src/main.neb", diagnostics);

        Assert.Contains(actions, action =>
            action.Kind == CodeActionKind.QuickFix && action.Title.Contains("Implement missing members"));
    }

    [Fact]
    public async Task AnUnresolvedNameOffersAnImportQuickFix()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function greet(): string\n    return \"hi\"\nend\n"),
            ("src/main.neb", "print(greet())\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAsync("src/main.neb", diagnostics);

        Assert.Contains(actions, action =>
            action.Kind == CodeActionKind.QuickFix && action.Title.Contains("Import 'greet'"));
    }
}

public sealed class ImportFixTests
{
    [Fact]
    public async Task AnImportFixFindsAnAbstractClass()
    {
        await using var session = await LspSession.StartAsync(
            ("src/shape.neb", "export abstract class Shape\nend\n"),
            ("src/main.neb", "local shape: Shape\nprint(shape)\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAsync("src/main.neb", diagnostics);

        Assert.Contains(actions, action => action.Title == "Import 'Shape' from 'shape'");
    }

    [Fact]
    public async Task AnImportFixIgnoresAFileThatOnlyMentionsTheName()
    {
        await using var session = await LspSession.StartAsync(
            ("src/types.neb", "export class Holder\nend\n"),
            ("src/other.neb", "import { Holder } from \"types\"\n\nexport local held: Holder = new Holder()\n"),
            ("src/main.neb", "local value: Holder\nprint(value)\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAsync("src/main.neb", diagnostics);

        Assert.Single(actions, action => action.Title.StartsWith("Import 'Holder'"));
        Assert.Contains(actions, action => action.Title == "Import 'Holder' from 'types'");
    }

    [Fact]
    public async Task AnImportFixReachesAnInstalledPackage()
    {
        await using var session = await LspSession.StartAsync(
            ("nebra_modules/orm/nebra.toml", "name = \"orm\"\nversion = \"1.0.0\"\nsource = \"src\"\n"),
            ("nebra_modules/orm/src/init.neb", "export class Model\nend\n"),
            ("src/main.neb", "local value: Model\nprint(value)\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAsync("src/main.neb", diagnostics);

        Assert.Contains(actions, action => action.Title == "Import 'Model' from 'orm'");
    }

    [Fact]
    public async Task OrganizingImportsKeepsAGlobAsWritten()
    {
        await using var session = await LspSession.StartAsync(
            ("src/models/A.neb", "print(1)\n"),
            ("src/models/B.neb", "print(2)\n"),
            ("src/lib.neb", "export local value = 1\n"),
            ("src/main.neb", "import \"models/*.neb\"\nimport { value } from \"lib\"\n\nprint(value)\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var organize = Assert.Single(await session.CodeActionsAsync("src/main.neb", diagnostics), action => action.Title == "Organize imports");

        var organized = organize.Edit == null ? "" : session.Apply(organize.Edit)["src/main.neb"];
        Assert.DoesNotContain("models/A", organized);
        Assert.Equal(organized.Length == 0 ? 0 : 1, organized.Split("models/*.neb").Length - 1);
    }

    [Fact]
    public async Task AnImportFixExtendsAnImportOfTheSameModule()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function greet(): string\n    return \"hi\"\nend\n\nexport function farewell(): string\n    return \"bye\"\nend\n"),
            ("src/main.neb", "import { greet } from \"lib\"\n\nprint(greet(), farewell())\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAsync("src/main.neb", diagnostics);
        var fix = Assert.Single(actions, action => action.Title == "Import 'farewell' from 'lib'");

        Assert.Equal(
            "import { farewell, greet } from \"lib\"\n\nprint(greet(), farewell())\n",
            session.Apply(fix.Edit)["src/main.neb"]);
    }

    [Fact]
    public async Task AnImportFixReachesASubdirectoryThroughTheSourceRoot()
    {
        await using var session = await LspSession.StartAsync(
            ("src/shapes/square.neb", "export function area(side: number): number\n    return side * side\nend\n"),
            ("src/main.neb", "-- entry point\nprint(area(2))\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAsync("src/main.neb", diagnostics);
        var fix = Assert.Single(actions, action => action.Title.StartsWith("Import 'area'"));

        Assert.Equal("Import 'area' from 'shapes/square'", fix.Title);
        Assert.Equal(
            "-- entry point\nimport { area } from \"shapes/square\"\nprint(area(2))\n",
            session.Apply(fix.Edit)["src/main.neb"]);
    }
}
