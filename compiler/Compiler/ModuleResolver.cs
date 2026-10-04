using Antlr4.Runtime;
using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;
using Nebra.PackageManager;

namespace Nebra.Compiler;

public enum ModuleKind
{
    NebraSource,
    Declaration,
    DeclareModule
}

public sealed class ResolvedModule
{
    public ModuleKind Kind { get; init; }
    public PreparsedFile? File { get; init; }
    public DeclareModuleDecl? DeclareModule { get; init; }
    public string? FilePath { get; init; }
}

public sealed class ModuleResolver(Config config)
{
    private readonly string _sourceRoot = Path.GetFullPath(Path.Combine(config.ProjectRoot, config.Source));
    private readonly Dictionary<string, ResolvedModule> _cache = new();

    public ResolvedModule? Resolve(string moduleName, string? importerPath,
        List<PackageContext> pkgs, DiagnosticsBag diag, IDAlloc<NodeID> nodeAlloc)
    {
        // A bare module name resolves relative to the importing file first, so the
        // same name means different modules in different directories: a dependency's
        // internal `import { X } from "types"` must not be served the consumer's own
        // `src/types.neb`. Keying the cache on the importer's directory keeps each
        // module's neighbourhood to itself.
        var cacheKey = ImporterDirectory(importerPath) + "\0" + moduleName;
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = DoResolve(moduleName, importerPath, pkgs, diag, nodeAlloc);
        if (result != null)
            _cache[cacheKey] = result;
        return result;
    }

    private static string ImporterDirectory(string? importerPath)
    {
        if (string.IsNullOrEmpty(importerPath)) return "";
        try { return Path.GetDirectoryName(Path.GetFullPath(importerPath)) ?? ""; }
        catch { return ""; }
    }

    private ResolvedModule? DoResolve(string moduleName, string? importerPath,
        List<PackageContext> pkgs, DiagnosticsBag diag, IDAlloc<NodeID> nodeAlloc)
    {
        if (moduleName.EndsWith(".neb"))
            moduleName = moduleName[..^4];

        var found = FindDeclareModule(moduleName, pkgs);
        if (found != null) return found;

        var inPackage = ResolveInsideInstalledPackage(moduleName, pkgs, diag, nodeAlloc);
        if (inPackage != null) return inPackage;

        var searchDirs = BuildSearchPaths(importerPath);

        foreach (var dir in searchDirs)
        {
            var dnebra = Path.Combine(dir, moduleName + ".d.neb");
            if (File.Exists(dnebra))
            {
                var file = LoadAndInject(dnebra, pkgs, diag, nodeAlloc);
                if (file != null)
                    return new ResolvedModule { Kind = ModuleKind.Declaration, File = file, FilePath = dnebra };
            }

            var nebra = Path.Combine(dir, moduleName + ".neb");
            if (File.Exists(nebra))
            {
                var file = LoadAndInject(nebra, pkgs, diag, nodeAlloc);
                if (file != null)
                    return new ResolvedModule { Kind = ModuleKind.NebraSource, File = file, FilePath = nebra };
            }

            var dnebraIdx = Path.Combine(dir, moduleName, "init.d.neb");
            if (File.Exists(dnebraIdx))
            {
                var file = LoadAndInject(dnebraIdx, pkgs, diag, nodeAlloc);
                if (file != null)
                    return new ResolvedModule { Kind = ModuleKind.Declaration, File = file, FilePath = dnebraIdx };
            }

            var nebraIdx = Path.Combine(dir, moduleName, "init.neb");
            if (File.Exists(nebraIdx))
            {
                var file = LoadAndInject(nebraIdx, pkgs, diag, nodeAlloc);
                if (file != null)
                    return new ResolvedModule { Kind = ModuleKind.NebraSource, File = file, FilePath = nebraIdx };
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a module that lives inside an installed package's own source directory. The
    /// generic search treats <c>nebra_modules/</c> as a flat root, which only finds a package
    /// whose code sits at its root; a package that keeps its code where its own manifest says
    /// (<c>source = "src"</c>, say) is invisible to it. The bare package name resolves to that
    /// directory's <c>init</c>, and <c>&lt;pkg&gt;/&lt;rest&gt;</c> to the file below it.
    /// </summary>
    private ResolvedModule? ResolveInsideInstalledPackage(string moduleName, List<PackageContext> pkgs,
        DiagnosticsBag diag, IDAlloc<NodeID> nodeAlloc)
    {
        var slash = moduleName.IndexOf('/');
        var head = slash < 0 ? moduleName : moduleName[..slash];
        var rest = slash < 0 ? "init" : moduleName[(slash + 1)..];

        foreach (var pkg in InstalledPackagesOnDisk())
        {
            var dirName = Path.GetFileName(pkg.RootPath);
            if (!string.Equals(dirName, head, StringComparison.Ordinal)
                && !string.Equals(pkg.Name, head, StringComparison.Ordinal)) continue;

            var source = pkg.Manifest?.Source;
            if (string.IsNullOrWhiteSpace(source) || source == ".") continue;

            var sourceRoot = Path.GetFullPath(Path.Combine(pkg.RootPath, source));
            if (!Directory.Exists(sourceRoot)) continue;

            foreach (var (candidate, kind) in new[]
                     {
                         (Path.Combine(sourceRoot, rest + ".d.neb"), ModuleKind.Declaration),
                         (Path.Combine(sourceRoot, rest + ".neb"), ModuleKind.NebraSource),
                         (Path.Combine(sourceRoot, rest, "init.d.neb"), ModuleKind.Declaration),
                         (Path.Combine(sourceRoot, rest, "init.neb"), ModuleKind.NebraSource),
                     })
            {
                if (!File.Exists(candidate)) continue;

                var file = LoadAndInject(candidate, pkgs, diag, nodeAlloc);
                if (file != null)
                    return new ResolvedModule { Kind = kind, File = file, FilePath = candidate };
            }
        }

        return null;
    }

    private IReadOnlyList<InstalledPackage> InstalledPackagesOnDisk()
    {
        return _installed ??= InstalledPackages.Discover(config.ProjectRoot);
    }

    private List<InstalledPackage>? _installed;

    private List<string> BuildSearchPaths(string? importerPath)
    {
        var paths = new List<string>();

        if (importerPath != null)
        {
            var importerDir = Path.GetDirectoryName(Path.GetFullPath(importerPath));
            if (importerDir != null) paths.Add(importerDir);
        }

        if (Directory.Exists(_sourceRoot))
            paths.Add(_sourceRoot);

        var modulesDir = Path.GetFullPath(Path.Combine(config.ProjectRoot, InstalledPackages.ModulesDirName));
        if (Directory.Exists(modulesDir))
            paths.Add(modulesDir);

        foreach (var lib in config.Code.Libs)
        {
            var libPath = Path.IsPathRooted(lib)
                ? lib
                : Path.GetFullPath(Path.Combine(config.ProjectRoot, lib));
            if (Directory.Exists(libPath))
                paths.Add(libPath);
        }

        foreach (var g in config.Globals)
        {
            var gPath = Path.IsPathRooted(g)
                ? g
                : Path.GetFullPath(Path.Combine(config.ProjectRoot, g));
            if (Directory.Exists(gPath))
                paths.Add(gPath);
        }

        return paths;
    }

    private static ResolvedModule? FindDeclareModule(string moduleName, List<PackageContext> pkgs)
    {
        foreach (var pkg in pkgs)
        {
            foreach (var file in pkg.Files)
            {
                foreach (var stmt in file.Hir.Body)
                {
                    if (stmt is DeclareModuleDecl dmd && dmd.ModuleName.Name == moduleName)
                    {
                        return new ResolvedModule
                        {
                            Kind = ModuleKind.DeclareModule,
                            DeclareModule = dmd,
                            File = file,
                            FilePath = file.Filename
                        };
                    }
                }
            }
        }
        return null;
    }

    private PreparsedFile? LoadAndInject(string filePath, List<PackageContext> pkgs,
        DiagnosticsBag diag, IDAlloc<NodeID> nodeAlloc)
    {
        foreach (var existingPkg in pkgs)
        {
            var existing = existingPkg.Files.FirstOrDefault(f => f.Filename == filePath);
            if (existing != null) return existing;
        }

        var targetPkg = pkgs.FirstOrDefault();
        if (targetPkg == null) return null;

        string source;
        try { source = File.ReadAllText(filePath); }
        catch { return null; }

        var inputStream = new AntlrInputStream(source);
        var lexer = new NebraLexer(inputStream);
        lexer.RemoveErrorListeners();
        var tokenStream = new CommonTokenStream(lexer);
        var parser = new NebraParser(tokenStream);
        parser.RemoveErrorListeners();
        var visitor = new IRVisitor(filePath, nodeAlloc, diag, config);
        var ir = visitor.Visit(parser.script());

        if (ir is not IRScript script) return null;

        var file = new PreparsedFile(filePath, source) { Hir = script };
        targetPkg.Files.Add(file);
        return file;
    }
}
