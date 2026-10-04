using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class DiagnosticCodeTests
{
    public static TheoryData<DiagnosticCode> AllCodes()
    {
        var codes = new TheoryData<DiagnosticCode>();
        foreach (var code in Enum.GetValues<DiagnosticCode>())
        {
            codes.Add(code);
        }

        return codes;
    }

    /// <summary>
    /// Every message and help text goes through <see cref="string.Format(string, object[])"/>, so
    /// a stray brace throws at the moment the diagnostic is reported. For a syntax error that
    /// happens inside the parser's error listener and aborts the whole parse.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCodes))]
    public void EveryCodeFormatsItsMessageAndHelp(DiagnosticCode code)
    {
        var bag = new DiagnosticsBag();
        var arguments = Enumerable.Range(0, 8).Select(index => (object)$"arg{index}").ToArray();

        bag.Report(TextSpan.Empty, code, arguments);

        Assert.Single(bag.Diagnostics);
    }
}
