using Nebra.IR;

namespace Nebra.Compiler.Passes;

public sealed class ResolveImportsPass() : Pass(PassName, PassScope.PerBuild)
{
    public const string PassName = "ResolveImports";

    private ModuleResolver? _resolver;

    public override bool Run(PassContext context)
    {
        _resolver ??= new ModuleResolver(context.Config);

        var newFiles = new List<PreparsedFile>();

        var preexisting = new HashSet<PreparsedFile>();
        foreach (var pkg in context.Pkgs)
            foreach (var f in pkg.Files) preexisting.Add(f);

        // Resolving an import injects the imported file into the package, and that
        // file has imports of its own - a dependency's modules import each other.
        // Walking a single snapshot would leave those unresolved, so keep going
        // until a round finds nothing new to process.
        var processed = new HashSet<PreparsedFile>();
        bool progressed;
        do
        {
            progressed = false;
            foreach (var pkg in context.Pkgs)
            {
                foreach (var file in pkg.Files.ToList())
                {
                    if (!processed.Add(file)) continue;
                    ProcessFileImports(context, pkg, file, newFiles);
                    progressed = true;
                }
            }
        } while (progressed);

        var freshlyInjected = newFiles.Where(f => !preexisting.Contains(f)).ToList();
        if (freshlyInjected.Count > 0)
            BindAndResolveNewFiles(context, freshlyInjected);

        ReportImportCycles(context);
        SortFilesByImportOrder(context);

        return true;
    }

    /// <summary>
    /// Reorders each package's files so that a file always follows the ones it imports.
    /// </summary>
    /// <remarks>
    /// Every pass after this one walks the files in order and resolves each in turn, so a class
    /// used across a module boundary is only complete once its defining file has been through
    /// them. Discovery order does not respect that: the project scan is filesystem order, and a
    /// dependency's modules are appended as imports pull them in. Sorting here makes the result
    /// independent of both. <see cref="ReportImportCycles"/> has already rejected cycles, so a
    /// topological order exists; files that import nothing keep their original relative order.
    /// </remarks>
    private static void SortFilesByImportOrder(PassContext context)
    {
        foreach (var pkg in context.Pkgs)
        {
            var byPath = new Dictionary<string, PreparsedFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in pkg.Files)
            {
                if (file.Filename == null) continue;
                byPath.TryAdd(Path.GetFullPath(file.Filename), file);
            }

            var ordered = new List<PreparsedFile>(pkg.Files.Count);
            var state = new Dictionary<PreparsedFile, int>();

            foreach (var file in pkg.Files.ToList())
                VisitForOrder(context, pkg, file, byPath, state, ordered);

            var placed = new HashSet<PreparsedFile>(ordered);
            foreach (var file in pkg.Files)
                if (placed.Add(file)) ordered.Add(file);

            pkg.Files.Clear();
            pkg.Files.AddRange(ordered);
        }
    }

    private static void VisitForOrder(PassContext context, PackageContext pkg, PreparsedFile file,
        Dictionary<string, PreparsedFile> byPath, Dictionary<PreparsedFile, int> state,
        List<PreparsedFile> ordered)
    {
        if (state.TryGetValue(file, out var mark))
        {
            // 1 means "on the current path": a cycle, already reported. Stop rather than recurse.
            return;
        }

        state[file] = 1;

        if (file.Filename != null)
        {
            foreach (var stmt in file.Hir.Body)
            {
                if (stmt is not ImportStmt import) continue;

                var key = $"import_resolved:{file.Filename}:{import.Module.Name}";
                if (!context.Cache.TryGetValue(key, out var obj) || obj is not ResolvedModule resolved) continue;
                if (resolved.FilePath == null) continue;
                if (!byPath.TryGetValue(Path.GetFullPath(resolved.FilePath), out var target)) continue;
                if (ReferenceEquals(target, file)) continue;

                VisitForOrder(context, pkg, target, byPath, state, ordered);
            }
        }

        state[file] = 2;
        ordered.Add(file);
    }

    /// <summary>
    /// Reports a cycle in the source-level import graph. Each module is emitted as a Lua chunk
    /// that <c>require</c>s the ones it imports, and Lua only caches a module once it has finished
    /// loading, so a cycle recurses until the C stack overflows. The failure surfaces at load time
    /// with no indication of which imports formed the loop, which is why it is rejected here.
    /// Declaration modules are left out: they carry no code to load.
    /// </summary>
    private void ReportImportCycles(PassContext context)
    {
        var edges = new Dictionary<string, List<ImportEdge>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in context.Pkgs)
        {
            foreach (var file in pkg.Files)
            {
                if (file.Filename == null) continue;

                foreach (var stmt in file.Hir.Body)
                {
                    if (stmt is not ImportStmt import) continue;

                    var key = $"import_resolved:{file.Filename}:{import.Module.Name}";
                    if (!context.Cache.TryGetValue(key, out var obj) || obj is not ResolvedModule resolved) continue;
                    if (resolved.Kind != ModuleKind.NebraSource || resolved.FilePath == null) continue;

                    var from = Path.GetFullPath(file.Filename);
                    if (!edges.TryGetValue(from, out var list))
                    {
                        list = [];
                        edges[from] = list;
                    }

                    list.Add(new ImportEdge(Path.GetFullPath(resolved.FilePath), import.Module.Name, import.Module.Span));
                }
            }
        }

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in edges.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            WalkForCycle(context, edges, state, start);
        }
    }

    /// <summary>
    /// Depth-first walk marking nodes 1 while they are on the stack and 2 once finished, so an
    /// edge back to a node still on the stack is the import that closes a cycle.
    /// </summary>
    private static void WalkForCycle(PassContext context, Dictionary<string, List<ImportEdge>> edges,
        Dictionary<string, int> state, string node)
    {
        if (state.TryGetValue(node, out var mark) && mark != 0) return;

        state[node] = 1;
        if (edges.TryGetValue(node, out var outgoing))
        {
            foreach (var edge in outgoing)
            {
                if (state.TryGetValue(edge.To, out var targetMark) && targetMark == 1)
                {
                    var key = $"import_cycle_reported:{edge.To}:{node}";
                    if (context.Cache.TryAdd(key, true))
                        context.Diag.Report(edge.Span, Diagnostics.DiagnosticCode.ErrTopLevelCycle, edge.ModuleName);
                    continue;
                }

                WalkForCycle(context, edges, state, edge.To);
            }
        }

        state[node] = 2;
    }

    private sealed record ImportEdge(string To, string ModuleName, Diagnostics.TextSpan Span);

    private void ProcessFileImports(PassContext ctx, PackageContext pkg, PreparsedFile file,
        List<PreparsedFile> newFiles)
    {
        foreach (var stmt in file.Hir.Body)
        {
            if (stmt is not ImportStmt import) continue;

            var resolved = _resolver!.Resolve(
                import.Module.Name, file.Filename, ctx.Pkgs, ctx.Diag, ctx.NodeAlloc);

            if (resolved == null)
            {
                if (!import.IsTypeOnly)
                    ctx.Diag.Report(import.Module.Span, Diagnostics.DiagnosticCode.ErrModuleNotFound, import.Module.Name);
                continue;
            }

            ctx.Cache[$"import_resolved:{file.Filename}:{import.Module.Name}"] = resolved;

            if (resolved is { Kind: ModuleKind.NebraSource or ModuleKind.Declaration, File: not null })
            {
                if (!newFiles.Contains(resolved.File))
                    newFiles.Add(resolved.File);
            }
        }
    }

    /// <summary>
    /// Runs <see cref="BindDeclarePass"/> on each freshly-loaded library file
    /// inside its own sub-scope of the package root. The sub-scope keeps the
    /// library's <c>export</c> names from clashing with the consumer's
    /// <c>import { … }</c> declarations (which BindDeclare put into the package
    /// root earlier in this pass cycle); <see cref="ResolveFromNebraSource"/>
    /// then walks the sub-scope to copy types onto the import bindings.
    /// </summary>
    private static void BindAndResolveNewFiles(PassContext ctx, List<PreparsedFile> newFiles)
    {
        var bindPass = new BindDeclarePass();

        foreach (var pkg in ctx.Pkgs)
        {
            foreach (var file in newFiles)
            {
                if (!pkg.Files.Contains(file)) continue;

                if (file.BindingScopeOverride == null)
                    file.BindingScopeOverride = pkg.Scopes.NewScope(pkg.Root);

                var fileCtx = new PassContext(ctx.Diag, ctx.Pkgs, pkg, file, ctx.Types,
                    ctx.SymAlloc, ctx.ScopeAlloc, ctx.NodeAlloc, ctx.Names, ctx.Cache, ctx.Config);
                bindPass.Run(fileCtx);
            }
        }
    }

    /// <summary>
    /// Walks each import statement in <paramref name="file"/> and copies the
    /// resolved type from the source symbol onto the importer's symbol.
    /// Invoked from <see cref="ResolveTypeRefsPass"/> after class/interface/enum
    /// types have been pre-declared, so cross-file <c>import { Vec2 }</c> sees a
    /// fully-built source type at copy time.
    /// </summary>
    public static void PropagateImportTypes(PassContext ctx, PackageContext pkg, PreparsedFile file)
    {
        foreach (var stmt in file.Hir.Body)
        {
            if (stmt is not ImportStmt import) continue;

            var cacheKey = $"import_resolved:{file.Filename}:{import.Module.Name}";
            if (!ctx.Cache.TryGetValue(cacheKey, out var obj) || obj is not ResolvedModule resolved)
                continue;

            switch (resolved.Kind)
            {
                case ModuleKind.DeclareModule:
                    ResolveFromDeclareModule(ctx, pkg, import, resolved.DeclareModule!);
                    break;
                case ModuleKind.Declaration:
                {
                    var sourcePkg = FindPackageOf(ctx, resolved.File!) ?? pkg;
                    ResolveFromDeclFile(ctx, pkg, sourcePkg, import, resolved.File!);
                    break;
                }
                case ModuleKind.NebraSource:
                {
                    var sourcePkg = FindPackageOf(ctx, resolved.File!) ?? pkg;
                    ResolveFromNebraSource(ctx, pkg, sourcePkg, import, resolved.File!);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Each <c>.neb</c> source file lives in its own <see cref="PackageContext"/>
    /// (see <c>NebraCompiler.AddSource</c>), so a cross-file import has to look up
    /// the source symbol in the EXPORTER's package, not the importer's. This
    /// helper finds the package that owns <paramref name="file"/>.
    /// </summary>
    private static PackageContext? FindPackageOf(PassContext ctx, PreparsedFile file)
    {
        foreach (var p in ctx.Pkgs)
        {
            if (p.Files.Contains(file)) return p;
        }
        return null;
    }

    private static void ResolveFromDeclareModule(PassContext ctx, PackageContext pkg,
        ImportStmt import, DeclareModuleDecl declModule)
    {
        if (!pkg.Scopes.EnclosingScope(declModule.ID, out var moduleScope))
            return;

        switch (import.Kind)
        {
            case ImportKind.Named:
                foreach (var spec in import.Specifiers)
                {
                    var memberName = spec.Name.Name;
                    if (pkg.Scopes.LookupOnlyCurrent(moduleScope, memberName, out var memberSym))
                    {
                        var importName = spec.Alias ?? spec.Name;
                        CopySymbolType(pkg, memberSym, importName.Sym);
                    }
                }
                break;
            case ImportKind.Default:
            case ImportKind.Namespace:
                if (import.Alias != null)
                {
                    var moduleSym = FindModuleSymbol(pkg, declModule.ModuleName.Name);
                    if (moduleSym != SymID.Invalid)
                        CopySymbolType(pkg, moduleSym, import.Alias.Sym);
                }
                break;
        }
    }

    private static void ResolveFromDeclFile(PassContext ctx, PackageContext pkg, PackageContext sourcePkg,
        ImportStmt import, PreparsedFile declFile)
    {
        foreach (var stmt in declFile.Hir.Body)
        {
            if (stmt is DeclareModuleDecl dmd && dmd.ModuleName.Name == import.Module.Name)
            {
                ResolveFromDeclareModule(ctx, pkg, import, dmd);
                return;
            }
        }

        ResolveFromTopLevelDeclarations(ctx, pkg, sourcePkg, import, declFile);
    }

    private static void ResolveFromNebraSource(PassContext ctx, PackageContext pkg, PackageContext sourcePkg,
        ImportStmt import, PreparsedFile sourceFile)
    {
        // exports live in the SOURCE file's package (each .neb file gets its
        // own package); use sourcePkg for the lookups, pkg for the target.
        var exports = CollectExportedSymbols(sourcePkg, sourceFile);

        switch (import.Kind)
        {
            case ImportKind.Named:
                foreach (var spec in import.Specifiers)
                {
                    var memberName = spec.Name.Name;
                    if (exports.TryGetValue(memberName, out var exportSym))
                    {
                        var importName = spec.Alias ?? spec.Name;
                        CopySymbolType(sourcePkg, exportSym, pkg, importName.Sym);
                        continue;
                    }

                    ReportMissingImportMember(ctx, pkg, sourcePkg, import, sourceFile, spec);
                }
                break;
            case ImportKind.Default:
            case ImportKind.Namespace:
                break;
        }
    }

    /// <summary>
    /// Reports a named import the source module does not provide, separating a member that exists
    /// but was never exported from one that does not exist at all. Without this the binding is
    /// left untyped and the name resolves to nil at runtime. Guarded through the pass cache
    /// because import types are propagated again after every package is typed, and the same
    /// specifier would otherwise be reported once per round.
    /// </summary>
    private static void ReportMissingImportMember(PassContext ctx, PackageContext pkg, PackageContext sourcePkg,
        ImportStmt import, PreparsedFile sourceFile, ImportSpecifier spec)
    {
        var key = $"import_member_missing:{pkg.Path}:{import.Module.Name}:{spec.Name.Name}";
        if (!ctx.Cache.TryAdd(key, true)) return;

        var lookupScope = sourceFile.BindingScopeOverride ?? sourcePkg.Root;
        var code = sourcePkg.Scopes.Lookup(lookupScope, spec.Name.Name, out _)
            ? Diagnostics.DiagnosticCode.ErrSymbolNotExported
            : Diagnostics.DiagnosticCode.ErrSymbolNotFound;

        ctx.Diag.Report(spec.Name.Span, code, spec.Name.Name, import.Module.Name);
    }

    private static void ResolveFromTopLevelDeclarations(PassContext ctx, PackageContext pkg, PackageContext sourcePkg,
        ImportStmt import, PreparsedFile file)
    {
        var topLevel = new Dictionary<string, SymID>();

        foreach (var stmt in file.Hir.Body)
        {
            switch (stmt)
            {
                case DeclareFunctionDecl dfd when dfd.NamePath.Count == 1:
                    if (sourcePkg.Scopes.Lookup(sourcePkg.Root, dfd.NamePath[0].Name, out var dfSym))
                        topLevel[dfd.NamePath[0].Name] = dfSym;
                    break;
                case DeclareVariableDecl dvd:
                    if (sourcePkg.Scopes.Lookup(sourcePkg.Root, dvd.Name.Name, out var dvSym))
                        topLevel[dvd.Name.Name] = dvSym;
                    break;
            }
        }

        if (topLevel.Count == 0) return;

        switch (import.Kind)
        {
            case ImportKind.Named:
                foreach (var spec in import.Specifiers)
                {
                    if (topLevel.TryGetValue(spec.Name.Name, out var sym))
                    {
                        var importName = spec.Alias ?? spec.Name;
                        CopySymbolType(sourcePkg, sym, pkg, importName.Sym);
                    }
                }
                break;
        }
    }

    private static Dictionary<string, SymID> CollectExportedSymbols(PackageContext pkg, PreparsedFile file)
    {
        var exports = new Dictionary<string, SymID>();
        var lookupScope = file.BindingScopeOverride ?? pkg.Root;

        foreach (var stmt in file.Hir.Body)
        {
            if (stmt is not ExportStmt export) continue;

            switch (export.Declaration)
            {
                case FunctionDecl { NamePath.Count: > 0 } fd:
                {
                    var name = fd.NamePath[0].Name;
                    if (fd.NamePath[0].Sym != SymID.Invalid)
                        exports[name] = fd.NamePath[0].Sym;
                    else if (pkg.Scopes.Lookup(lookupScope, name, out var sym))
                        exports[name] = sym;
                    break;
                }
                case LocalFunctionDecl lfd:
                {
                    if (lfd.Name.Sym != SymID.Invalid)
                        exports[lfd.Name.Name] = lfd.Name.Sym;
                    else if (pkg.Scopes.Lookup(lookupScope, lfd.Name.Name, out var sym))
                        exports[lfd.Name.Name] = sym;
                    break;
                }
                case LocalDecl ld:
                {
                    foreach (var v in ld.Variables)
                    {
                        if (v.Name.Sym != SymID.Invalid)
                            exports[v.Name.Name] = v.Name.Sym;
                        else if (pkg.Scopes.Lookup(lookupScope, v.Name.Name, out var sym))
                            exports[v.Name.Name] = sym;
                    }
                    break;
                }
                case ClassDecl cd:
                {
                    if (cd.Name.Sym != SymID.Invalid)
                        exports[cd.Name.Name] = cd.Name.Sym;
                    else if (pkg.Scopes.Lookup(lookupScope, cd.Name.Name, out var sym))
                        exports[cd.Name.Name] = sym;
                    break;
                }
                case InterfaceDecl id:
                {
                    if (id.Name.Sym != SymID.Invalid)
                        exports[id.Name.Name] = id.Name.Sym;
                    else if (pkg.Scopes.Lookup(lookupScope, id.Name.Name, out var sym))
                        exports[id.Name.Name] = sym;
                    break;
                }
                case EnumDecl ed:
                {
                    if (ed.Name.Sym != SymID.Invalid)
                        exports[ed.Name.Name] = ed.Name.Sym;
                    else if (pkg.Scopes.Lookup(lookupScope, ed.Name.Name, out var sym))
                        exports[ed.Name.Name] = sym;
                    break;
                }
            }
        }

        return exports;
    }

    /// <summary>
    /// Copies the resolved type of <paramref name="source"/> (looked up in
    /// <paramref name="srcPkg"/>'s symbol arena) onto <paramref name="target"/>
    /// (looked up in <paramref name="tgtPkg"/>'s arena). The two arenas can be
    /// the same (intra-package import) or different (cross-package import,
    /// e.g. between two sibling source files); SymIDs are unique across the
    /// shared <c>SymAlloc</c> but every <see cref="PackageContext"/> only
    /// stores its own subset of <see cref="Symbol"/> records.
    /// </summary>
    private static void CopySymbolType(PackageContext srcPkg, SymID source, PackageContext tgtPkg, SymID target)
    {
        if (source == SymID.Invalid || target == SymID.Invalid) return;
        if (!srcPkg.Syms.GetByID(source, out var srcSym)) return;
        if (!tgtPkg.Syms.GetByID(target, out var tgtSym)) return;
        if (srcSym.Type != TypID.Invalid)
            tgtSym.Type = srcSym.Type;
        tgtSym.Side = srcSym.Side;
        tgtSym.ImportedFrom = srcSym.ImportedFrom != SymID.Invalid ? srcSym.ImportedFrom : source;
    }

    private static void CopySymbolType(PackageContext pkg, SymID source, SymID target)
        => CopySymbolType(pkg, source, pkg, target);

    private static SymID FindModuleSymbol(PackageContext pkg, string name)
    {
        if (pkg.Scopes.Lookup(pkg.Root, name, out var sym))
            return sym;
        return SymID.Invalid;
    }
}
