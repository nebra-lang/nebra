using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class CompletionTests
{
    private const string CounterClass =
        "class Counter\n    value: number = 0\n    label: string = \"c\"\n\n    function bump(): number\n        self.value = self.value + 1\n        return self.value\n    end\n\n    function reset(): nil\n        self.value = 0\n    end\nend\n\nlocal counter = new Counter()\n";

    [Fact]
    public async Task DotOnAnInstanceOffersItsFields()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", CounterClass + "print(counter.{|cursor|})\n"));

        await session.OpenAsync("src/main.neb");
        var labels = await session.CompletionLabelsAsync("cursor");

        Assert.Contains("value", labels);
        Assert.Contains("label", labels);
    }

    [Fact]
    public async Task ColonOnAnInstanceOffersItsMethods()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", CounterClass + "print(counter:{|cursor|})\n"));

        await session.OpenAsync("src/main.neb");
        var labels = await session.CompletionLabelsAsync("cursor");

        Assert.Contains("bump", labels);
        Assert.Contains("reset", labels);
    }

    [Fact]
    public async Task ColonAtStatementStartOffersMethods()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", CounterClass + "counter:{|cursor|}\n"));

        await session.OpenAsync("src/main.neb");
        var labels = await session.CompletionLabelsAsync("cursor");

        Assert.Contains("bump", labels);
    }

    [Fact]
    public async Task DotAtStatementStartOffersFields()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", CounterClass + "counter.{|cursor|}\n"));

        await session.OpenAsync("src/main.neb");
        var labels = await session.CompletionLabelsAsync("cursor");

        Assert.Contains("value", labels);
    }

    [Fact]
    public async Task PartlyTypedMethodAtStatementStartOffersMethods()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", CounterClass + "counter:b{|cursor|}\n"));

        await session.OpenAsync("src/main.neb");
        var labels = await session.CompletionLabelsAsync("cursor");

        Assert.Contains("bump", labels);
    }

    [Fact]
    public async Task UnfinishedMemberAccessFollowedByCodeOffersMethods()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", CounterClass + "counter:{|cursor|}\nprint(counter.value)\n"));

        await session.OpenAsync("src/main.neb");
        var labels = await session.CompletionLabelsAsync("cursor");

        Assert.Contains("bump", labels);
    }

    [Fact]
    public async Task BareIdentifierOffersLocalsInScope()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local firstValue: number = 1\nlocal secondValue: number = 2\nprint({|cursor|})\n"));

        await session.OpenAsync("src/main.neb");
        var labels = await session.CompletionLabelsAsync("cursor");

        Assert.Contains("firstValue", labels);
        Assert.Contains("secondValue", labels);
    }

    [Fact]
    public async Task ImportBracesOfferTheModulesExports()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function greet(): string\n    return \"hi\"\nend\n\nexport function farewell(): string\n    return \"bye\"\nend\n"),
            ("src/main.neb", "import { {|cursor|} } from \"lib\"\n"));

        await session.OpenAsync("src/main.neb");
        var labels = await session.CompletionLabelsAsync("cursor");

        Assert.Contains("greet", labels);
        Assert.Contains("farewell", labels);
    }
}

