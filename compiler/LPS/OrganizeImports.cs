using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS;

/// <summary>
/// Tidies a file's imports: names the file never uses are dropped, imports that end up empty go
/// away, imports reaching the same module are merged, and names and imports are sorted. The
/// rewritten block takes the place of the first import.
/// </summary>
public static class OrganizeImports
{
    /// <summary>The edits that organize the imports of <paramref name="result"/>, or none when they already are.</summary>
    public static List<TextEdit> Compute(AnalysisResult result)
    {
        var imports = result.Hir.Body.OfType<ImportStmt>().DistinctBy(import => (object?)import.ExpandedFrom ?? import).ToList();
        if (imports.Count == 0) return [];

        var block = string.Concat(Organize(result, imports));
        var current = string.Concat(imports.Select(import => Lines(result.SourceText, import)));
        if (block == current) return [];

        var edits = new List<TextEdit> { new() { Range = ImportEdits.WholeLines(imports[0]), NewText = block } };
        edits.AddRange(imports.Skip(1).Select(import => new TextEdit { Range = ImportEdits.WholeLines(import), NewText = string.Empty }));
        return edits;
    }

    private static IEnumerable<string> Organize(AnalysisResult result, List<ImportStmt> imports)
    {
        var used = UsedSymbols(result);
        var sourceRoot = ImportEdits.SourceRoot(result);
        var lines = new List<(string Module, string Text)>();

        foreach (var group in imports.GroupBy(import => GroupKey(result, import, sourceRoot)))
        {
            var first = group.First();
            if (first.Kind != ImportKind.Named)
            {
                lines.AddRange(group.Select(import => (import.Module.Name, Lines(result.SourceText, import))));
                continue;
            }

            var specifiers = group
                .SelectMany(import => import.Specifiers)
                .Where(specifier => used.Contains((specifier.Alias ?? specifier.Name).Sym))
                .Select(ImportEdits.Render)
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();

            if (specifiers.Count > 0)
            {
                lines.Add((first.Module.Name, ImportEdits.Render(specifiers, first.Module.Name)));
            }
        }

        return lines.OrderBy(line => line.Module, StringComparer.Ordinal).Select(line => line.Text);
    }

    /// <summary>Named imports group by the file they resolve to; any other import stands alone.</summary>
    private static string GroupKey(AnalysisResult result, ImportStmt import, string sourceRoot)
    {
        if (import.Kind != ImportKind.Named) return "#" + import.Span.StartLn;
        return ImportEdits.Resolve(result.FilePath, import.Module.Name, sourceRoot) ?? import.Module.Name;
    }

    /// <summary>The symbols the file refers to outside of its import statements.</summary>
    private static HashSet<SymID> UsedSymbols(AnalysisResult result)
    {
        var importNames = result.Hir.Body.OfType<ImportStmt>()
            .SelectMany(import => import.Specifiers)
            .SelectMany(specifier => specifier.Alias == null ? [specifier.Name] : new[] { specifier.Name, specifier.Alias })
            .ToHashSet(ReferenceEqualityComparer.Instance);

        return NodeFinder.CollectAllNames(result.Hir)
            .Where(name => name.Sym != SymID.Invalid && !importNames.Contains(name))
            .Select(name => name.Sym)
            .ToHashSet();
    }

    private static string Lines(string source, ImportStmt import)
    {
        var lines = source.Split('\n');
        var start = import.Span.StartLn - 1;
        var count = import.Span.EndLn - start;
        return string.Concat(lines.Skip(start).Take(count).Select(line => line.TrimEnd('\r') + "\n"));
    }
}
