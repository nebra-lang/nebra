using System.Collections.Concurrent;
using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;
using Nebra.PackageManager;

using NebraSymbolKind = Nebra.IR.SymbolKind;

namespace Nebra.LPS;

/// <summary>A name an installed package exports, with the module to import it from.</summary>
public sealed record ImportableExport(string Name, NebraSymbolKind Kind, string Module, bool TypeOnly);

/// <summary>
/// The names the installed packages of a project export. A package nobody imports yet is not
/// part of a workspace snapshot, so its files are read here, parsed only and cached by their
/// modification time. A package with a <c>runtime_global</c> offers values from its root module
/// alone, and the types of its other modules through <c>import type</c>.
/// </summary>
public static class DependencyExports
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "nebra_modules", "out", "tests", "test", "examples", ".git"
    };

    private static readonly ConcurrentDictionary<string, (DateTime Written, FileExports Exports)> Cache = new();

    private sealed record ReExport(string Name, string SourceName, string Module);

    private sealed record FileExports(Dictionary<string, NebraSymbolKind> Declared, List<ReExport> ReExports);

    public static IReadOnlyList<ImportableExport> Of(Config config)
    {
        return InstalledPackages.Discover(config.ProjectRoot).SelectMany(package => OfPackage(package, config)).ToList();
    }

    private static IEnumerable<ImportableExport> OfPackage(InstalledPackage package, Config config)
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(package.RootPath, package.Manifest?.Source ?? "."));
        if (!Directory.Exists(sourceRoot)) return [];

        var files = SourceFiles(sourceRoot).ToDictionary(path => path, path => Read(path, config), StringComparer.Ordinal);
        var packageName = Path.GetFileName(package.RootPath);
        var hasRuntimeGlobal = !string.IsNullOrWhiteSpace(package.Manifest?.RuntimeGlobal);

        return files
            .SelectMany(file => Exports(file.Key, file.Value, files, sourceRoot, packageName, hasRuntimeGlobal))
            .GroupBy(export => export.Name, StringComparer.Ordinal)
            .Select(group => group.OrderBy(export => export.Module.Length).ThenBy(export => export.Module, StringComparer.Ordinal).First());
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.neb"))
        {
            yield return Path.GetFullPath(file);
        }

        var nested = Directory.EnumerateDirectories(directory).Where(path => !SkippedDirectories.Contains(Path.GetFileName(path)));
        foreach (var file in nested.SelectMany(SourceFiles))
        {
            yield return file;
        }
    }

    private static IEnumerable<ImportableExport> Exports(string path, FileExports exports, Dictionary<string, FileExports> files,
        string sourceRoot, string packageName, bool hasRuntimeGlobal)
    {
        var module = ModuleName(path, sourceRoot, packageName);
        var isRoot = module == packageName;

        var declared = exports.Declared.Select(export => (export.Key, export.Value));
        var reExported = exports.ReExports.Select(export => (export.Name, KindOf(export, path, files, sourceRoot, 0)));

        foreach (var (name, kind) in declared.Concat(reExported))
        {
            if (hasRuntimeGlobal && !isRoot && !IsType(kind)) continue;

            yield return new ImportableExport(name, kind, module, hasRuntimeGlobal && !isRoot);
        }
    }

    private static string ModuleName(string path, string sourceRoot, string packageName)
    {
        var relative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
        foreach (var suffix in new[] { ".d.neb", ".neb" })
        {
            if (relative.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                relative = relative[..^suffix.Length];
                break;
            }
        }

        if (relative == "init") return packageName;

        return packageName + "/" + (relative.EndsWith("/init", StringComparison.Ordinal) ? relative[..^"/init".Length] : relative);
    }

    private static bool IsType(NebraSymbolKind kind)
    {
        return kind is NebraSymbolKind.Class or NebraSymbolKind.Interface or NebraSymbolKind.Enum or NebraSymbolKind.TypeParam;
    }

    /// <summary>Follows a re-export to the declaration behind it, for the kind of the name.</summary>
    private static NebraSymbolKind KindOf(ReExport export, string path, Dictionary<string, FileExports> files, string sourceRoot, int depth)
    {
        const int maximumDepth = 16;

        var target = ModuleFile(export.Module, Path.GetDirectoryName(path)!, sourceRoot, files);
        if (target == null || depth >= maximumDepth) return NebraSymbolKind.Variable;

        var exports = files[target];
        if (exports.Declared.TryGetValue(export.SourceName, out var kind)) return kind;

        var next = exports.ReExports.FirstOrDefault(candidate => candidate.Name == export.SourceName);
        return next == null ? NebraSymbolKind.Variable : KindOf(next, target, files, sourceRoot, depth + 1);
    }

    private static string? ModuleFile(string module, string importerDirectory, string sourceRoot, Dictionary<string, FileExports> files)
    {
        foreach (var directory in new[] { importerDirectory, sourceRoot })
        {
            var stem = Path.GetFullPath(Path.Combine(directory, module));
            var found = new[] { ".d.neb", ".neb", "/init.d.neb", "/init.neb" }
                .Select(suffix => stem + suffix)
                .FirstOrDefault(files.ContainsKey);
            if (found != null) return found;
        }

        return null;
    }

    private static FileExports Read(string path, Config config)
    {
        var written = File.GetLastWriteTimeUtc(path);
        if (Cache.TryGetValue(path, out var cached) && cached.Written == written) return cached.Exports;

        var exports = Parse(path, config);
        Cache[path] = (written, exports);
        return exports;
    }

    private static FileExports Parse(string path, Config config)
    {
        var declared = new Dictionary<string, NebraSymbolKind>(StringComparer.Ordinal);
        var reExports = new List<ReExport>();

        var visitor = new IRVisitor(path, new IDAlloc<NodeID>(), new DiagnosticsBag(), config);
        if (visitor.Visit(ParseTreeCache.Parse(config.ReadSource(path))) is not IRScript script)
        {
            return new FileExports(declared, reExports);
        }

        foreach (var statement in script.Body)
        {
            if (statement is ImportStmt { IsReExport: true } reExport)
            {
                reExports.AddRange(reExport.Specifiers.Select(specifier =>
                    new ReExport((specifier.Alias ?? specifier.Name).Name, specifier.Name.Name, reExport.Module.Name)));
            }
            else if (statement is ExportStmt export)
            {
                AddDeclared(export.Declaration, declared);
            }
        }

        return new FileExports(declared, reExports);
    }

    private static void AddDeclared(Stmt declaration, Dictionary<string, NebraSymbolKind> declared)
    {
        switch (declaration)
        {
            case FunctionDecl { NamePath.Count: > 0 } function:
                declared[function.NamePath[0].Name] = NebraSymbolKind.Function;
                break;
            case LocalFunctionDecl function:
                declared[function.Name.Name] = NebraSymbolKind.Function;
                break;
            case LocalDecl local:
                foreach (var variable in local.Variables)
                {
                    declared[variable.Name.Name] = NebraSymbolKind.Variable;
                }
                break;
            case EnumDecl enumeration:
                declared[enumeration.Name.Name] = NebraSymbolKind.Enum;
                break;
            case ClassDecl type:
                declared[type.Name.Name] = NebraSymbolKind.Class;
                break;
            case InterfaceDecl type:
                declared[type.Name.Name] = NebraSymbolKind.Interface;
                break;
        }
    }
}
