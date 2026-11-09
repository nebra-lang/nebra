using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nebra.LPS;

/// <summary>
/// Writes import statements the way the compiler reads them: module specifiers relative to the
/// importing file's directory or the source root, existing imports recognised by the file they
/// resolve to rather than by their text, and new names merged into an import of the same module.
/// </summary>
public static class ImportEdits
{
    /// <summary>
    /// The specifier that makes <paramref name="targetPath"/> importable from
    /// <paramref name="importerPath"/>: relative to the importer's directory when the target lies
    /// beneath it, otherwise relative to the project's source root.
    /// </summary>
    public static string ModuleSpecifier(string importerPath, string targetPath, string sourceRoot)
    {
        var importerDirectory = Path.GetDirectoryName(Path.GetFullPath(importerPath))!;
        var target = Path.GetFullPath(targetPath);
        var baseDirectory = IsBeneath(target, importerDirectory) || !IsBeneath(target, sourceRoot)
            ? importerDirectory
            : Path.GetFullPath(sourceRoot);

        var relative = Path.GetRelativePath(baseDirectory, target).Replace('\\', '/');
        foreach (var suffix in new[] { "/init.d.neb", "/init.neb", ".d.neb", ".neb" })
        {
            if (relative.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return relative[..^suffix.Length];
            }
        }

        return relative;
    }

    /// <summary>The file a module specifier written in <paramref name="importerPath"/> resolves to, if any.</summary>
    public static string? Resolve(string importerPath, string specifier, string sourceRoot)
    {
        var module = specifier.EndsWith(".neb", StringComparison.OrdinalIgnoreCase) ? specifier[..^4] : specifier;
        var directories = new[] { Path.GetDirectoryName(Path.GetFullPath(importerPath))!, Path.GetFullPath(sourceRoot) };

        foreach (var directory in directories)
        {
            string[] candidates =
            [
                Path.Combine(directory, module + ".d.neb"),
                Path.Combine(directory, module + ".neb"),
                Path.Combine(directory, module, "init.d.neb"),
                Path.Combine(directory, module, "init.neb")
            ];

            var found = candidates.FirstOrDefault(File.Exists);
            if (found != null) return Path.GetFullPath(found);
        }

        return null;
    }

    /// <summary>
    /// The edit that imports <paramref name="name"/> from the module at <paramref name="targetPath"/>
    /// into <paramref name="result"/>: added to a named import already reaching that module, or
    /// written as a new import after the existing ones. Null when the name is already imported.
    /// </summary>
    public static TextEdit? AddImport(AnalysisResult result, string name, string targetPath)
    {
        var sourceRoot = SourceRoot(result);
        var target = Path.GetFullPath(targetPath);
        var imports = result.Hir.Body.OfType<ImportStmt>().ToList();

        var existing = imports.FirstOrDefault(import => IsMergeable(import, false)
            && string.Equals(Resolve(result.FilePath, import.Module.Name, sourceRoot), target, StringComparison.OrdinalIgnoreCase));

        return AddImport(result, imports, existing, name, ModuleSpecifier(result.FilePath, target, sourceRoot), false);
    }

    /// <summary>
    /// The edit that imports <paramref name="export"/> of an installed package into
    /// <paramref name="result"/>, by the same rules as a name of the project.
    /// </summary>
    public static TextEdit? AddImport(AnalysisResult result, ImportableExport export)
    {
        var imports = result.Hir.Body.OfType<ImportStmt>().ToList();
        var existing = imports.FirstOrDefault(import => IsMergeable(import, export.TypeOnly) && import.Module.Name == export.Module);

        return AddImport(result, imports, existing, export.Name, export.Module, export.TypeOnly);
    }

    private static bool IsMergeable(ImportStmt import, bool typeOnly)
    {
        return import is { Kind: ImportKind.Named, IsReExport: false } && import.IsTypeImport == typeOnly;
    }

    private static TextEdit? AddImport(AnalysisResult result, List<ImportStmt> imports, ImportStmt? existing, string name, string module, bool typeOnly)
    {
        if (existing != null)
        {
            if (existing.Specifiers.Any(specifier => (specifier.Alias ?? specifier.Name).Name == name)) return null;

            var specifiers = existing.Specifiers.Select(Render).Append(name).Order(StringComparer.Ordinal);
            return new TextEdit
            {
                Range = WholeLines(existing),
                NewText = Render(specifiers, existing.Module.Name, typeOnly)
            };
        }

        var line = imports.Count > 0 ? imports.Max(import => import.Span.EndLn) : FirstCodeLine(result.SourceText);
        return new TextEdit
        {
            Range = new Range(line, 0, line, 0),
            NewText = Render([name], module, typeOnly)
        };
    }

    public static string SourceRoot(AnalysisResult result)
    {
        var config = result.Snapshot.Config;
        return Path.GetFullPath(Path.Combine(config.ProjectRoot, config.Source));
    }

    public static string Render(ImportSpecifier specifier)
    {
        return specifier.Alias == null ? specifier.Name.Name : $"{specifier.Name.Name} as {specifier.Alias.Name}";
    }

    public static string Render(IEnumerable<string> specifiers, string module, bool typeOnly = false)
    {
        var keyword = typeOnly ? "import type" : "import";
        return $"{keyword} {{ {string.Join(", ", specifiers)} }} from \"{module}\"\n";
    }

    /// <summary>The lines an import statement occupies, from the start of its first line to the start of the next.</summary>
    public static Range WholeLines(Stmt statement)
    {
        return new Range(statement.Span.StartLn - 1, 0, statement.Span.EndLn, 0);
    }

    /// <summary>The first line after leading comments and blank lines, where a first import goes.</summary>
    private static int FirstCodeLine(string source)
    {
        var lines = source.Split('\n');
        var line = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var text = lines[index].Trim();
            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                line = index + 1;
                continue;
            }

            if (text.Length > 0) break;
        }

        return line;
    }

    private static bool IsBeneath(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
