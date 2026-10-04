using Nebra.Diagnostics;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Handlers;

public sealed class RenameHandler(NebraWorkspace workspace) : RenameHandlerBase
{
    public override async Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var nameRef = NodeFinder.FindNameRef(result.Hir, line, col);
        if (nameRef == null || nameRef.Sym == SymID.Invalid)
            return null;

        if (!result.Syms.GetByID(nameRef.Sym, out var sym))
            return null;

        if (sym.DeclaringNode == NodeID.Invalid)
            return null;

        var declaration = LocateDeclaration(result, nameRef, sym);
        if (declaration == null)
            return null;

        var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();
        foreach (var candidate in NebraWorkspace.FilesMentioning(result, nameRef.Name))
        {
            var edits = CollectEdits(candidate, declaration, nameRef.Name, request.NewName);
            if (edits.Count > 0)
                changes[DocumentUri.Parse(candidate.Uri)] = edits;
        }

        if (changes.Count == 0)
            return null;

        return new WorkspaceEdit { Changes = changes };
    }

    private sealed record DeclarationSite(string FilePath, TextSpan Span);

    /// <summary>
    /// Resolves the name under the cursor to the file and span where it is declared, whether that
    /// is the open document or the module it was imported from.
    /// </summary>
    private static DeclarationSite? LocateDeclaration(AnalysisResult result, NameRef nameRef, Symbol sym)
    {
        if (result.ImportedDeclarations.TryGetValue(nameRef.Sym, out var imported))
            return new DeclarationSite(imported.FilePath, imported.Span);

        if (!result.NodeRegistry.TryGetValue(sym.DeclaringNode, out var declNode))
            return null;

        var declaringFile = result.FileMap.TryGetValue(sym.DeclaringNode, out var mapped)
            && !string.IsNullOrEmpty(mapped)
                ? mapped
                : result.FilePath;

        return new DeclarationSite(declaringFile, declNode.Span);
    }

    /// <summary>
    /// Collects the edits for one file. Symbols are matched against the declaration site rather
    /// than by name, so a local that happens to share the name is left alone.
    /// </summary>
    private static List<TextEdit> CollectEdits(
        AnalysisResult result, DeclarationSite declaration, string oldName, string newName)
    {
        var targets = new HashSet<SymID>();

        if (SamePath(result.FilePath, declaration.FilePath))
        {
            foreach (var (symId, sym) in result.Syms.ByID)
            {
                if (sym.Name != oldName || sym.DeclaringNode == NodeID.Invalid) continue;
                if (!result.NodeRegistry.TryGetValue(sym.DeclaringNode, out var node)) continue;
                if (SameStart(node.Span, declaration.Span)) targets.Add(symId);
            }
        }
        else
        {
            foreach (var (symId, imported) in result.ImportedDeclarations)
            {
                if (!SamePath(imported.FilePath, declaration.FilePath)) continue;
                if (!SameStart(imported.Span, declaration.Span)) continue;
                if (!result.Syms.GetByID(symId, out var sym) || sym.Name != oldName) continue;
                targets.Add(symId);
            }
        }

        if (targets.Count == 0) return [];

        return NodeFinder.CollectAllNameRefs(result.Hir)
            .Where(nr => targets.Contains(nr.Sym))
            .Select(nr => new TextEdit
            {
                Range = NebraWorkspace.SpanToRange(nr.Span),
                NewText = newName
            })
            .ToList();
    }

    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability, ClientCapabilities clientCapabilities)
    {
        return new RenameRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra"),
            PrepareProvider = true
        };
    }

    private static bool SameStart(TextSpan a, TextSpan b)
    {
        return a.StartLn == b.StartLn && a.StartCol == b.StartCol;
    }

    private static bool SamePath(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
