using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using NebraSymbolKind = Nebra.IR.SymbolKind;

namespace Nebra.LPS;

/// <summary>
/// Offers what other project files and installed packages export but the current file has not imported yet. Accepting
/// an item writes the import along with the name. The exports come from the latest workspace
/// snapshot, which may be a few edits old, so completion never waits for the whole workspace, and
/// from the installed packages. Every export is offered whatever has been typed: a client asks
/// once at the start of a word, often before its first letter, and filters that list itself.
/// </summary>
public static class AutoImportCompletion
{
    public static IEnumerable<CompletionItem> Items(AnalysisResult result, WorkspaceSnapshot? exporters, bool typeContext, IReadOnlySet<string> visible)
    {
        return ProjectItems(result, exporters, typeContext, visible).Concat(DependencyItems(result, typeContext, visible));
    }

    private static IEnumerable<CompletionItem> ProjectItems(AnalysisResult result, WorkspaceSnapshot? exporters, bool typeContext, IReadOnlySet<string> visible)
    {
        if (exporters == null) yield break;

        var sourceRoot = ImportEdits.SourceRoot(result);
        foreach (var exporter in exporters.SourceResults())
        {
            if (string.Equals(exporter.FilePath, result.FilePath, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var (name, export) in NebraWorkspace.CollectExports(exporter))
            {
                if (visible.Contains(name)) continue;
                if (typeContext && !IsTypeKind(export.SymKind)) continue;

                var edit = ImportEdits.AddImport(result, name, exporter.FilePath);
                if (edit == null) continue;

                yield return Item(name, export.SymKind, ImportEdits.ModuleSpecifier(result.FilePath, exporter.FilePath, sourceRoot), edit);
            }
        }
    }

    private static IEnumerable<CompletionItem> DependencyItems(AnalysisResult result, bool typeContext, IReadOnlySet<string> visible)
    {
        foreach (var export in DependencyExports.Of(result.Snapshot.Config))
        {
            if (visible.Contains(export.Name)) continue;
            if (typeContext && !IsTypeKind(export.Kind)) continue;
            if (!typeContext && export.TypeOnly) continue;

            var edit = ImportEdits.AddImport(result, export);
            if (edit == null) continue;

            yield return Item(export.Name, export.Kind, export.Module, edit);
        }
    }

    private static CompletionItem Item(string name, NebraSymbolKind kind, string module, TextEdit edit)
    {
        return new CompletionItem
        {
            Label = name,
            Kind = KindOf(kind),
            Detail = $"import from \"{module}\"",
            LabelDetails = new CompletionItemLabelDetails { Description = module },
            SortText = "~" + name,
            AdditionalTextEdits = new TextEditContainer(edit)
        };
    }

    public static CompletionItemKind KindOf(NebraSymbolKind kind)
    {
        return kind switch
        {
            NebraSymbolKind.Function => CompletionItemKind.Function,
            NebraSymbolKind.Enum => CompletionItemKind.Enum,
            NebraSymbolKind.Class => CompletionItemKind.Class,
            NebraSymbolKind.Interface => CompletionItemKind.Interface,
            NebraSymbolKind.TypeParam => CompletionItemKind.TypeParameter,
            _ => CompletionItemKind.Variable
        };
    }

    private static bool IsTypeKind(NebraSymbolKind kind)
    {
        return kind is NebraSymbolKind.Class or NebraSymbolKind.Interface or NebraSymbolKind.Enum or NebraSymbolKind.TypeParam;
    }
}
