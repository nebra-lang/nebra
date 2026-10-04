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
    public async Task AnUnfinishedStatementAtTheEndReportsALocatedSyntaxError()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local count: number = 3\ncount:\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");

        var error = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("end of file", error.Message);
        Assert.True(error.Range.Start.Line > 0);
    }

    [Fact]
    public async Task DeclarationsFromAnInstalledDependencyResolve()
    {
        await using var session = await LspSession.StartAsync(
            ("nebra_modules/dep/nebra.toml", "name = \"dep\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\ntypes_only = true\n"),
            ("nebra_modules/dep/src/dep.d.neb", "declare function depAnswer(): number\n"),
            ("src/main.neb", "local value: number = depAnswer()\nprint(value)\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");

        Assert.Empty(diagnostics);
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
