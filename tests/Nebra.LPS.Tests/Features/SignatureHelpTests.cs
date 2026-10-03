using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class SignatureHelpTests
{
    [Fact]
    public async Task InsideACallTheActiveParameterFollowsTheCursor()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function add(a: number, b: number): number\n    return a + b\nend\n\nprint(add({|first|}1, {|second|}2))\n"));

        await session.OpenAsync("src/main.neb");
        var atFirst = await session.SignatureHelpAsync("first");
        var atSecond = await session.SignatureHelpAsync("second");

        Assert.NotNull(atFirst);
        Assert.NotNull(atSecond);
        Assert.Contains("add", atFirst.Signatures.First().Label);
        Assert.Equal(0, atFirst.ActiveParameter);
        Assert.Equal(1, atSecond.ActiveParameter);
    }
}
