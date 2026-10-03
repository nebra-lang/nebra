using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class DiagnosticsTests
{
    [Fact]
    public async Task CleanFilePublishesNoDiagnostics()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local count: number = 3\nprint(count)\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task TypeMismatchIsReportedWhereTheCompilerReportsIt()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local {|name|}count: number = \"three\"\nprint(count)\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");

        var error = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal(session.Marker("name").Position, error.Range.Start);
    }

    [Fact]
    public async Task EditingAFileRepublishesItsDiagnostics()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local count: number = 3\nprint(count)\n"));

        await session.OpenAsync("src/main.neb");
        var broken = await session.ChangeAsync("src/main.neb", "local count: number = \"three\"\nprint(count)\n", 2);
        var fixedAgain = await session.ChangeAsync("src/main.neb", "local count: number = 4\nprint(count)\n", 3);

        Assert.Single(broken);
        Assert.Empty(fixedAgain);
    }

    [Fact]
    public async Task ChangingAnImportedModuleRechecksItsOpenImporter()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function answer(): number\n    return 42\nend\n"),
            ("src/main.neb", "import { answer } from \"lib\"\n\nlocal value: number = answer()\nprint(value)\n"));

        await session.OpenAsync("src/lib.neb");
        Assert.Empty(await session.OpenAsync("src/main.neb"));

        var importer = session.NextDiagnostics("src/main.neb");
        await session.ChangeAsync("src/lib.neb", "export function answer(): string\n    return \"42\"\nend\n", 2);

        Assert.Single(await importer);
    }
}
