using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class SymbolTests
{
    [Fact]
    public async Task DocumentSymbolsNestMembersUnderTheirClass()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "class Counter\n    value: number = 0\n\n    function bump(): number\n        return self.value\n    end\nend\n\nlocal function helper(): nil\nend\n"));

        await session.OpenAsync("src/main.neb");
        var symbols = await session.DocumentSymbolsAsync("src/main.neb");

        var counter = Assert.Single(symbols, s => s.Name == "Counter");
        Assert.Equal(SymbolKind.Class, counter.Kind);
        Assert.Contains(counter.Children!, child => child.Name == "bump" && child.Kind == SymbolKind.Method);
        Assert.Contains(counter.Children!, child => child.Name == "value" && child.Kind == SymbolKind.Field);
        Assert.Contains(symbols, s => s.Name == "helper" && s.Kind == SymbolKind.Function);
    }

    [Fact]
    public async Task SemanticTokensCoverTheFile()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local function add(a: number, b: number): number\n    return a + b\nend\n\nprint(add(1, 2))\n"));

        await session.OpenAsync("src/main.neb");
        var tokens = await session.SemanticTokensAsync("src/main.neb");

        Assert.NotNull(tokens);
        Assert.NotEmpty(tokens.Data);
        Assert.Equal(0, tokens.Data.Length % 5);
    }
}
