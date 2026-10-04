using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Handlers;

public sealed class ReferencesHandler(NebraWorkspace workspace) : ReferencesHandlerBase
{
    public override async Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var nameRef = NodeFinder.FindNameRef(result.Hir, line, col);
        if (nameRef == null || nameRef.Sym == SymID.Invalid)
            return null;

        var usages = workspace.FindUsages(nameRef.Sym, result);
        var declaration = FindDeclaration(result.Snapshot, nameRef.Sym);

        var locations = usages
            .Where(location => declaration == null || !SameLocation(location, declaration))
            .ToList();

        if (request.Context.IncludeDeclaration && declaration != null)
            locations.Insert(0, declaration);

        return new LocationContainer(locations);
    }

    /// <summary>
    /// The location of the name that declares the symbol <paramref name="symId"/> stands for,
    /// following an import back to the declaring file.
    /// </summary>
    private static Location? FindDeclaration(WorkspaceSnapshot snapshot, SymID symId)
    {
        if (!snapshot.TryGetSymbol(snapshot.Origin(symId), out var origin) || origin.DeclaringNode == NodeID.Invalid)
            return null;

        if (!snapshot.TryGetNode(origin.DeclaringNode, out var node, out var file))
            return null;

        return new Location
        {
            Uri = DocumentUri.Parse(snapshot.GetResult(file)?.Uri ?? DocumentUri.FromFileSystemPath(file).ToString()),
            Range = NebraWorkspace.SpanToRange(NodeFinder.DeclaredNameSpan(node, origin.Name))
        };
    }

    private static bool SameLocation(Location left, Location right)
    {
        return left.Uri == right.Uri && left.Range.Start == right.Range.Start;
    }

    protected override ReferenceRegistrationOptions CreateRegistrationOptions(
        ReferenceCapability capability, ClientCapabilities clientCapabilities)
    {
        return new ReferenceRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra")
        };
    }
}
