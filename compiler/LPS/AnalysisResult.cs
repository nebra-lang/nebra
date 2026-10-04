using Antlr4.Runtime;
using Nebra.Diagnostics;
using Nebra.IR;

namespace Nebra.LPS;

public sealed record ImportedDecl(string FilePath, TextSpan Span, Node DeclNode);

/// <summary>
/// One file as seen through a <see cref="WorkspaceSnapshot"/>. Everything it exposes - symbols,
/// types, node and file maps - belongs to that snapshot, so features that reach across files
/// through <see cref="Snapshot"/> always see the same state of the workspace.
/// </summary>
public sealed class AnalysisResult
{
    private CommonTokenStream? _tokenStream;

    public required string Uri { get; init; }
    public required string FilePath { get; init; }
    public required string SourceText { get; init; }
    public required PreparsedFile File { get; init; }
    public required PackageContext Package { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public required IReadOnlyDictionary<NodeID, Node> NodeRegistry { get; init; }
    public required IReadOnlyDictionary<NodeID, string> FileMap { get; init; }
    public required WorkspaceSnapshot Snapshot { get; init; }

    public Dictionary<SymID, ImportedDecl> ImportedDeclarations { get; init; } = new();

    /// <summary>The lexed tokens of <see cref="SourceText"/>, produced on first use.</summary>
    public CommonTokenStream TokenStream => _tokenStream ??= Lex(SourceText);

    public IRScript Hir => File.Hir;
    public SymbolArena Syms => Package.Syms;
    public ScopeGraph Scopes => Package.Scopes;
    public TypeTable Types => Package.Types;

    private static CommonTokenStream Lex(string text)
    {
        var lexer = new NebraLexer(new AntlrInputStream(text));
        lexer.RemoveErrorListeners();
        var stream = new CommonTokenStream(lexer);
        stream.Fill();
        return stream;
    }
}
