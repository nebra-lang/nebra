using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class AutoImportTests
{
    private const string Library =
        "export function greet(): string\n    return \"hi\"\nend\n\n" +
        "export function farewell(): string\n    return \"bye\"\nend\n\n" +
        "export class Square\n    side: number = 1\nend\n";

    [Fact]
    public async Task AnUnimportedExportIsOfferedWithItsImport()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", Library),
            ("src/main.neb", "import { greet } from \"lib\"\n\nprint(greet(), far{|cursor|})\n"));

        await session.OpenAsync("src/main.neb");
        await session.PublishedDiagnostics("src/lib.neb");
        var items = await session.CompletionItemsAsync("cursor");

        var farewell = Assert.Single(items, item => item.Label == "farewell");
        Assert.Equal("lib", farewell.LabelDetails?.Description);

        var edit = new WorkspaceEdit
        {
            Changes = new Dictionary<OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri, IEnumerable<TextEdit>>
            {
                [session.Uri("src/main.neb")] = farewell.AdditionalTextEdits!
            }
        };
        Assert.StartsWith("import { farewell, greet } from \"lib\"\n", session.Apply(edit)["src/main.neb"]);
    }

    [Fact]
    public async Task AnExportIsOfferedBeforeTheFirstLetterIsTyped()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", Library),
            ("src/main.neb", "local value = {|cursor|}\nprint(value)\n"));

        await session.OpenAsync("src/main.neb");
        await session.PublishedDiagnostics("src/lib.neb");

        Assert.Contains("Square", await session.CompletionLabelsAsync("cursor"));
    }

    [Fact]
    public async Task AnExportIsNotOfferedInsideAString()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", Library),
            ("src/main.neb", "print(\"{|cursor|}\")\n"));

        await session.OpenAsync("src/main.neb");
        await session.PublishedDiagnostics("src/lib.neb");

        Assert.DoesNotContain("Square", await session.CompletionLabelsAsync("cursor"));
    }

    private static readonly (string Path, string Text)[] Package =
    [
        ("nebra_modules/orm/nebra.toml", "name = \"orm\"\nversion = \"1.0.0\"\nsource = \"src\"\n"),
        ("nebra_modules/orm/src/init.neb", "export { Model } from \"./model/Model\"\nexport function connect(): nil\nend\n"),
        ("nebra_modules/orm/src/model/Model.neb", "export class Model\nend\n"),
        ("nebra_modules/orm/src/types/Uuid.neb", "export class Uuid\nend\n\nexport function parseUuid(): nil\nend\n"),
    ];

    private static readonly (string Path, string Text)[] RuntimeGlobalPackage =
    [
        ("nebra_modules/orm/nebra.toml", "name = \"orm\"\nversion = \"1.0.0\"\nsource = \"src\"\nruntime_global = \"Orm\"\n"),
        .. Package.Skip(1),
    ];

    private static async Task<IReadOnlyList<CompletionItem>> PackageCompletionAsync((string Path, string Text)[] package, string source)
    {
        await using var session = await LspSession.StartAsync([.. package, ("src/main.neb", source)]);
        await session.OpenAsync("src/main.neb");
        return await session.CompletionItemsAsync("cursor");
    }

    [Fact]
    public async Task AnExportOfAnInstalledPackageIsOfferedFromThePackage()
    {
        var items = await PackageCompletionAsync(Package, "local value = Mod{|cursor|}\nprint(value)\n");

        var model = Assert.Single(items, item => item.Label == "Model");
        Assert.Equal(CompletionItemKind.Class, model.Kind);
        Assert.Equal("orm", model.LabelDetails?.Description);
        Assert.Equal("import { Model } from \"orm\"\n", Assert.Single(model.AdditionalTextEdits!).NewText);
    }

    [Fact]
    public async Task AModuleOfAnInstalledPackageIsOfferedByItsPath()
    {
        var items = await PackageCompletionAsync(Package, "local value = parse{|cursor|}\nprint(value)\n");

        Assert.Equal("orm/types/Uuid", Assert.Single(items, item => item.Label == "parseUuid").LabelDetails?.Description);
    }

    [Fact]
    public async Task ATypeOfARuntimeGlobalPackageIsImportedAsAType()
    {
        var items = await PackageCompletionAsync(RuntimeGlobalPackage, "local value: Uu{|cursor|}\nprint(value)\n");

        Assert.Equal("import type { Uuid } from \"orm/types/Uuid\"\n", Assert.Single(Assert.Single(items, item => item.Label == "Uuid").AdditionalTextEdits!).NewText);
    }

    [Fact]
    public async Task ARuntimeGlobalPackageOffersValuesOnlyFromItsRoot()
    {
        var labels = (await PackageCompletionAsync(RuntimeGlobalPackage, "local value = {|cursor|}\nprint(value)\n")).Select(item => item.Label).ToList();

        Assert.Contains("connect", labels);
        Assert.DoesNotContain("parseUuid", labels);
        Assert.DoesNotContain("Uuid", labels);
    }

    [Fact]
    public async Task AnImportIsNotMergedIntoATypeImportOrAReExport()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", Library),
            ("src/main.neb", "import type { Square } from \"lib\"\n\nlocal shape: Square\nprint(shape, far{|cursor|})\n"));

        await session.OpenAsync("src/main.neb");
        await session.PublishedDiagnostics("src/lib.neb");
        var items = await session.CompletionItemsAsync("cursor");

        Assert.Equal("import { farewell } from \"lib\"\n", Assert.Single(Assert.Single(items, item => item.Label == "farewell").AdditionalTextEdits!).NewText);
    }

    [Fact]
    public async Task AnAlreadyImportedNameIsNotOfferedAgain()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", Library),
            ("src/main.neb", "import { greet } from \"lib\"\n\nprint(gre{|cursor|})\n"));

        await session.OpenAsync("src/main.neb");
        await session.PublishedDiagnostics("src/lib.neb");
        var items = await session.CompletionItemsAsync("cursor");

        var greet = Assert.Single(items, item => item.Label == "greet");
        Assert.Null(greet.AdditionalTextEdits);
    }

    [Fact]
    public async Task ATypeAnnotationIsOfferedOnlyTypes()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", Library),
            ("src/main.neb", "local shape: S{|cursor|}\n"));

        await session.OpenAsync("src/main.neb");
        await session.PublishedDiagnostics("src/lib.neb");
        var labels = (await session.CompletionItemsAsync("cursor")).Where(item => item.AdditionalTextEdits != null).Select(item => item.Label).ToList();

        Assert.Equal(["Square"], labels);
    }
}
