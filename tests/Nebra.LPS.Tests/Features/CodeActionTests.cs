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
