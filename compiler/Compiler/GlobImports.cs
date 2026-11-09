using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;

namespace Nebra.Compiler;

/// <summary>
/// Expands a side-effect import whose path holds a glob, such as <c>import "models/*.neb"</c>,
/// into one import per matching file. <c>*</c> matches within a directory and <c>**</c> across
/// directories. Like any bare path, the glob is tried against the importing file's directory
/// first and the source root after it.
/// </summary>
public static class GlobImports
{
    private const string SourceExtension = ".neb";
    private const string DeclarationExtension = ".d.neb";
    private const string ModulesDirectory = "nebra_modules";

    /// <summary>Replaces every glob import of <paramref name="file"/> by the imports it stands for.</summary>
    public static void Expand(PreparsedFile file, Config config, DiagnosticsBag diagnostics, IDAlloc<NodeID> nodes)
    {
        for (var index = file.Hir.Body.Count - 1; index >= 0; index--)
        {
            if (file.Hir.Body[index] is not ImportStmt { Kind: ImportKind.SideEffect } import || !IsGlob(import.Module.Name)) continue;

            var modules = Modules(import.Module.Name, file.Filename, config);
            if (modules.Count == 0)
            {
                diagnostics.Report(import.Module.Span, DiagnosticCode.WarnGlobImportEmpty, import.Module.Name);
            }

            file.Hir.Body.RemoveAt(index);
            file.Hir.Body.InsertRange(index, modules.Select(module => Expanded(import, module, nodes)));
        }
    }

    private static bool IsGlob(string path)
    {
        return path.Contains('*');
    }

    private static ImportStmt Expanded(ImportStmt import, string module, IDAlloc<NodeID> nodes)
    {
        return new ImportStmt(nodes.Next(), import.Span, ImportKind.SideEffect, new NameRef(module, import.Module.Span))
        {
            ExpandedFrom = import.Module.Name
        };
    }

    /// <summary>The modules <paramref name="glob"/> matches from the first directory where it matches anything, in a stable order.</summary>
    private static List<string> Modules(string glob, string importerPath, Config config)
    {
        var pattern = Normalized(glob);
        var importer = Path.GetFullPath(importerPath);
        string[] directories =
        [
            Path.GetDirectoryName(importer)!,
            Path.GetFullPath(Path.Combine(config.ProjectRoot, config.Source))
        ];

        foreach (var directory in directories)
        {
            var modules = ModulesBelow(directory, pattern, importer);
            if (modules.Count > 0) return modules;
        }

        return [];
    }

    private static string Normalized(string glob)
    {
        var pattern = glob.Replace('\\', '/');
        if (pattern.StartsWith("./", StringComparison.Ordinal)) pattern = pattern[2..];

        return pattern.EndsWith(SourceExtension, StringComparison.OrdinalIgnoreCase) ? pattern[..^SourceExtension.Length] : pattern;
    }

    private static List<string> ModulesBelow(string directory, string pattern, string importer)
    {
        var root = Path.GetFullPath(Path.Combine(directory, LiteralPrefix(pattern)));
        if (!Directory.Exists(root)) return [];

        return Directory.EnumerateFiles(root, "*" + SourceExtension, SearchOption.AllDirectories)
            .Where(path => IsModule(path, importer))
            .Select(path => ModuleName(directory, path))
            .Where(module => SidesResolver.Match(pattern, module))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The directories of <paramref name="pattern"/> before its first glob.</summary>
    private static string LiteralPrefix(string pattern)
    {
        var glob = pattern.IndexOf('*');
        var separator = pattern.LastIndexOf('/', glob);
        return separator < 0 ? "" : pattern[..separator];
    }

    private static bool IsModule(string path, string importer)
    {
        if (path.EndsWith(DeclarationExtension, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(Path.GetFullPath(path), importer, StringComparison.OrdinalIgnoreCase)) return false;

        return !path.Replace('\\', '/').Contains($"/{ModulesDirectory}/", StringComparison.Ordinal);
    }

    private static string ModuleName(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
        return relative[..^SourceExtension.Length];
    }
}
