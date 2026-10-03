using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class HoverTests
{
    [Fact]
    public async Task HoverOverALocalShowsItsType()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local answer: number = 42\nprint({|use|}answer)\n"));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("use");

        Assert.NotNull(hover);
        Assert.Contains("number", hover);
    }

    [Fact]
    public async Task HoverOverAFunctionCallShowsItsSignature()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function add(a: number, b: number): number\n    return a + b\nend\n\nprint({|call|}add(1, 2))\n"));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("call");

        Assert.NotNull(hover);
        Assert.Contains("add", hover);
        Assert.Contains("a: number", hover);
    }

    [Fact]
    public async Task HoverOverAMethodShowsItsSignature()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "class Counter\n    value: number = 0\n\n    function bump(by: number): number\n        self.value = self.value + by\n        return self.value\n    end\nend\n\nlocal counter = new Counter()\nprint(counter:{|method|}bump(2))\n"));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("method");

        Assert.NotNull(hover);
        Assert.Contains("bump", hover);
        Assert.Contains("by: number", hover);
    }

    [Fact]
    public async Task HoverOverAnImportedFunctionShowsItsSignature()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function greet(name: string): string\n    return \"hi \" .. name\nend\n"),
            ("src/main.neb", "import { greet } from \"lib\"\n\nprint({|call|}greet(\"ada\"))\n"));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("call");

        Assert.NotNull(hover);
        Assert.Contains("name: string", hover);
    }
}
