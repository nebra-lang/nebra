using System.Text;
using Antlr4.Runtime;

namespace Nebra.LPS.Tests.Compiler;

public sealed class ParserLookaheadTests
{
    /// <summary>
    /// A declared interface lists signatures only. If the parser has to look past a member to
    /// decide what it is, the lookahead grows with the interface and parsing turns quadratic -
    /// a generated API with a few hundred members took over half a minute to parse.
    /// </summary>
    [Theory]
    [InlineData("declare interface Large\n{0}end\n")]
    [InlineData("declare module \"api\"\n    interface Large\n{0}    end\nend\n")]
    public void DeclaredInterfaceMembersNeedOnlyLocalLookahead(string template)
    {
        var members = new StringBuilder();
        for (var index = 0; index < 200; index++)
        {
            members.Append($"        function method{index}(value: number): number\n");
        }

        var parser = new NebraParser(new CommonTokenStream(new NebraLexer(new AntlrInputStream(string.Format(template, members)))));
        parser.RemoveErrorListeners();
        parser.Profile = true;

        parser.script();

        Assert.Equal(0, parser.NumberOfSyntaxErrors);
        var deepest = parser.ParseInfo.getDecisionInfo().Max(decision => decision.SLL_MaxLook);
        Assert.True(deepest < 50, $"a decision looked {deepest} tokens ahead");
    }
}
