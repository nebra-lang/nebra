using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Handlers;

public sealed class DefinitionHandler(NebraWorkspace workspace) : DefinitionHandlerBase
{
    public override Task<LocationOrLocationLinks?> Handle(DefinitionParams request, CancellationToken ct)
    {
        var result = workspace.GetResult(request.TextDocument.Uri.ToString());
        if (result == null) return Task.FromResult<LocationOrLocationLinks?>(null);

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var importTarget = TryResolveImportPath(result, request.Position);
        if (importTarget != null)
        {
            var location = new Location
            {
                Uri = DocumentUri.FromFileSystemPath(importTarget),
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(0, 0), new Position(0, 0))
            };
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(location));
        }

        var nameRef = NodeFinder.FindNameRef(result.Hir, line, col);
        if (nameRef == null)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        if (nameRef.Sym == SymID.Invalid)
            return Task.FromResult(FindMemberDefinition(result, nameRef, line, col));

        if (!result.Syms.GetByID(nameRef.Sym, out var sym))
            return Task.FromResult<LocationOrLocationLinks?>(null);

        if (IsOnDeclaration(nameRef, sym, result))
        {
            var usages = workspace.FindUsages(nameRef.Sym, result);
            if (usages.Count > 0)
            {
                var links = usages.Select(l => new LocationOrLocationLink(l)).ToList();
                return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(links));
            }
        }

        if (result.ImportedDeclarations.TryGetValue(nameRef.Sym, out var imported))
        {
            var loc = new Location
            {
                Uri = DocumentUri.FromFileSystemPath(imported.FilePath),
                Range = NebraWorkspace.SpanToRange(NodeFinder.DeclaredNameSpan(imported.DeclNode, sym.Name))
            };
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(loc));
        }

        if (sym.DeclaringNode == NodeID.Invalid)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        if (!result.NodeRegistry.TryGetValue(sym.DeclaringNode, out var declNode))
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var fileUri = result.Uri;
        if (result.FileMap.TryGetValue(sym.DeclaringNode, out var declFile))
            fileUri = DocumentUri.FromFileSystemPath(declFile).ToString();

        var location2 = new Location
        {
            Uri = DocumentUri.Parse(fileUri),
            Range = NebraWorkspace.SpanToRange(NodeFinder.DeclaredNameSpan(declNode, sym.Name))
        };

        return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(location2));
    }

    /// <summary>
    /// Resolves a member name (<c>obj:method()</c>, <c>obj.field</c>) through the receiver's type to
    /// the place the member is declared, which may be in another file.
    /// </summary>
    private static LocationOrLocationLinks? FindMemberDefinition(AnalysisResult result, NameRef nameRef, int line, int col)
    {
        var access = MemberResolver.FindAccess(NodeFinder.Find(result.Hir, line, col), nameRef);
        if (access == null || access.Receiver.Type == TypID.Invalid)
            return null;

        if (!result.Types.GetByID(access.Receiver.Type, out var receiverType))
            return null;

        var declaration = MemberResolver.FindDeclaration(receiverType, access.Member.Name);
        if (declaration == null)
            return null;

        var file = string.IsNullOrEmpty(declaration.Span.File) ? result.FilePath : declaration.Span.File;
        if (!File.Exists(file))
            return null;

        return new LocationOrLocationLinks(new Location
        {
            Uri = DocumentUri.FromFileSystemPath(file),
            Range = NebraWorkspace.SpanToRange(declaration.Span)
        });
    }

    private static bool IsOnDeclaration(NameRef nameRef, Symbol sym, AnalysisResult result)
    {
        if (sym.DeclaringNode == NodeID.Invalid) return false;
        if (!result.NodeRegistry.TryGetValue(sym.DeclaringNode, out var dn)) return false;
        return dn switch
        {
            FunctionDecl fd => fd.NamePath.Any(n => ReferenceEquals(n, nameRef)),
            LocalFunctionDecl lfd => ReferenceEquals(lfd.Name, nameRef),
            LocalDecl ld => ld.Variables.Any(v => ReferenceEquals(v.Name, nameRef)),
            EnumDecl ed => ReferenceEquals(ed.Name, nameRef),
            ClassDecl cd => ReferenceEquals(cd.Name, nameRef),
            InterfaceDecl id => ReferenceEquals(id.Name, nameRef),
            DeclareFunctionDecl dfd => dfd.NamePath.Any(n => ReferenceEquals(n, nameRef)),
            DeclareVariableDecl dvd => ReferenceEquals(dvd.Name, nameRef),
            _ => false
        };
    }

    private string? TryResolveImportPath(AnalysisResult result, Position pos)
    {
        var lines = result.SourceText.Split('\n');
        if (pos.Line < 0 || pos.Line >= lines.Length) return null;
        var lineText = lines[pos.Line].TrimEnd('\r');

        foreach (var stmt in result.Hir.Body)
        {
            if (stmt is not ImportStmt import) continue;
            if (import.Module.Span.StartLn - 1 != pos.Line) continue;

            var modStart = import.Module.Span.StartCol - 1;
            var modEnd = import.Module.Span.EndCol - 1;
            if (pos.Character < modStart || pos.Character > modEnd) continue;

            var moduleName = import.Module.Name;
            if (moduleName.EndsWith(".neb")) moduleName = moduleName[..^4];

            var dir = Path.GetDirectoryName(result.FilePath);
            if (dir == null) return null;

            var candidate = Path.GetFullPath(Path.Combine(dir, moduleName + ".neb"));
            if (File.Exists(candidate)) return candidate;

            var dCandidate = Path.GetFullPath(Path.Combine(dir, moduleName + ".d.neb"));
            if (File.Exists(dCandidate)) return dCandidate;

            return null;
        }

        return null;
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(
        DefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra")
        };
    }
}
