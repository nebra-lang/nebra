using System.Collections.Concurrent;
using System.Text;
using Antlr4.Runtime;
using Nebra.Compiler;
using Nebra.Compiler.Passes;
using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;

using NebraDiagnostic = Nebra.Diagnostics.Diagnostic;
using NebraDiagnosticCode = Nebra.Diagnostics.DiagnosticCode;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;

namespace Nebra.LPS;

public sealed class NebraWorkspace
{
    private readonly ConcurrentDictionary<string, AnalysisResult> _results = new();
    private readonly ConcurrentDictionary<string, string> _openDocuments = new();
    private ILanguageServerFacade? _server;
    private Config _config = new();

    private string? _rootPath;

    /// <summary>
    /// Cache for <see cref="AnalyzeImportedFile"/> keyed by absolute path.
    /// Each entry stores the cache stamp (mtime + length for closed files,
    /// or a content version for currently-open files) so we re-analyze only
    /// when the file actually changes. Without this, every keystroke in a
    /// consumer file re-parses every imported file from disk.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Stamp, AnalysisResult Result)> _importCache = new();

    /// <summary>
    /// Cache for <see cref="ResolveImportPath"/>. Module resolution itself
    /// can do a recursive directory walk (looking for matching
    /// <c>declare module</c> headers), which is too expensive to repeat on
    /// every keystroke. Keyed by <c>importerDir|moduleName</c>; invalidated
    /// only when the workspace root changes (rare).
    /// </summary>
    private readonly ConcurrentDictionary<string, string?> _resolveCache = new();

    public void Initialize(string? rootPath)
    {
        _rootPath = rootPath;
        if (rootPath != null)
        {
            var configPath = Path.Combine(rootPath, "nebra.toml");
            var loaded = Config.LoadFromFile(configPath);
            if (loaded != null) _config = loaded;
        }
    }

    public void SetServer(ILanguageServerFacade server)
    {
        _server = server;
        // Pre-warm the import cache so the first hover/symbol query in any
        // workspace file doesn't pay the cold-parse cost. VSCode is supposed
        // to send didOpen for already-open files once the dynamic
        // registration completes, but in practice it sometimes drops them —
        // pre-warming guarantees that GetResult's disk fallback hits a
        // populated import cache and finishes fast.
        if (_rootPath != null) Task.Run(() => PreWarmWorkspace(_rootPath));
    }

    private void PreWarmWorkspace(string rootPath)
    {
        try
        {
            var sourceRoot = Path.IsPathRooted(_config.Source)
                ? _config.Source
                : Path.Combine(rootPath, _config.Source);
            if (!Directory.Exists(sourceRoot)) sourceRoot = rootPath;

            string[] files;
            try { files = Directory.GetFiles(sourceRoot, "*.neb", SearchOption.AllDirectories); }
            catch { return; }

            foreach (var f in files)
            {
                // Skip files that are also under common ignore directories
                // ("out/", "nebra_modules/" generated bits): they're transient
                // build artefacts and not worth indexing.
                if (f.Contains("/out/") || f.Contains("/nebra_modules/")) continue;
                try
                {
                    var cfg = _config.Clone();
                    var dir = Path.GetDirectoryName(f);
                    if (dir != null) cfg.Source = dir;
                    AnalyzeImportedFile(f, cfg);
                }
                catch { /* best-effort warmup */ }
            }
        }
        catch { /* best-effort warmup */ }
    }

    /// <summary>
    /// Returns the cached analysis for the given document URI, or — if the
    /// document was never opened via <c>textDocument/didOpen</c> — falls back
    /// to reading the file from disk and analyzing it on demand. The fallback
    /// is necessary because VSCode sometimes drops the <c>didOpen</c>
    /// notification for files that were already open when the language client
    /// became ready (the dynamic-capability-registration race): without it,
    /// every hover/symbol/definition request silently returns null until the
    /// user types in the file.
    /// </summary>
    public AnalysisResult? GetResult(string uri)
    {
        if (_results.TryGetValue(uri, out var r)) return r;

        var path = DocumentUri.GetFileSystemPath(DocumentUri.Parse(uri));
        if (path == null || !File.Exists(path)) return null;

        string text;
        try { text = File.ReadAllText(path); }
        catch { return null; }

        AnalyzeDocument(uri, text);
        return _results.TryGetValue(uri, out r) ? r : null;
    }

    public void OnDocumentOpened(string uri, string text)
    {
        _openDocuments[uri] = text;
        InvalidateImportCacheFor(uri);
        AnalyzeDocument(uri, text);
    }

    public void OnDocumentChanged(string uri, string text)
    {
        _openDocuments[uri] = text;
        InvalidateImportCacheFor(uri);
        AnalyzeDocument(uri, text);
        ReanalyzeImporters(uri);
    }

    public void OnDocumentClosed(string uri)
    {
        _openDocuments.TryRemove(uri, out _);
        _results.TryRemove(uri, out _);
        InvalidateImportCacheFor(uri);
        _server?.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = DocumentUri.Parse(uri),
            Diagnostics = new Container<LspDiagnostic>()
        });
    }

    /// <summary>
    /// Drops any cached import analysis pointing at <paramref name="uri"/>'s
    /// path so consumers re-analyze the freshly edited content next time
    /// they're checked. Required for both open/change/close so we never
    /// serve stale type info from a previously cached version.
    /// </summary>
    private void InvalidateImportCacheFor(string uri)
    {
        var path = DocumentUri.GetFileSystemPath(DocumentUri.Parse(uri));
        if (path == null) return;
        var full = Path.GetFullPath(path);
        _importCache.TryRemove(full, out _);

        // Path resolution can reach into recursive directory walks (the
        // `declare module "X"` lookup), so a newly-created file might make a
        // previously-unresolved import suddenly resolvable. Cheapest fix:
        // drop the whole resolve cache whenever a document state changes —
        // re-resolving on a hot import cache is fast.
        _resolveCache.Clear();
    }

    public void AnalyzeDocument(string uri, string sourceText)
    {
        var (analysed, diagnostics, path) = BuildAnalysis(uri, sourceText);
        if (analysed != null) _results[uri] = analysed;
        PublishDiagnostics(uri, path, diagnostics);
    }

    /// <summary>
    /// Runs the analysis for a document without caching the result or publishing its diagnostics.
    /// Used for files a request has to reach into (a workspace-wide rename, say) that the user has
    /// never opened, so they do not gain diagnostics or evict an open document's cached result.
    /// </summary>
    private (AnalysisResult? Result, DiagnosticsBag Diagnostics, string FilePath) BuildAnalysis(
        string uri, string sourceText)
    {
        var filePath = DocumentUri.GetFileSystemPath(DocumentUri.Parse(uri)) ?? uri;
        var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath));

        var diag = new DiagnosticsBag();
        var nodeAlloc = new IDAlloc<NodeID>();
        var symAlloc = new IDAlloc<SymID>();
        var scopeAlloc = new IDAlloc<ScopeID>();
        var types = new TypeTable(new IDAlloc<TypID>());
        var names = new NameMap();

        var effectiveConfig = _config.Clone();
        if (fileDir != null)
            effectiveConfig.Source = fileDir;

        CommonTokenStream tokenStream;
        IRScript? hir;
        try
        {
            var inputStream = new AntlrInputStream(sourceText);
            var lexer = new NebraLexer(inputStream);
            lexer.RemoveErrorListeners();
            lexer.AddErrorListener(new DiagnosticsSymbolErrorListener(diag, filePath));
            tokenStream = new CommonTokenStream(lexer);
            var parser = new NebraParser(tokenStream);
            parser.RemoveErrorListeners();
            parser.AddErrorListener(new DiagnosticsTokenErrorListener(diag, filePath));
            var visitor = new IRVisitor(filePath, nodeAlloc, diag, effectiveConfig);
            var ir = visitor.Visit(parser.script());
            hir = ir as IRScript;
        }
        catch
        {
            return (null, diag, filePath);
        }

        if (hir == null)
        {
            return (null, diag, filePath);
        }

        var scopes = new ScopeGraph(diag, scopeAlloc);
        var pkg = new PackageContext(filePath, new SymbolArena(symAlloc), scopes, types, scopes.Root);
        var file = new PreparsedFile(filePath, sourceText) { Hir = hir };
        pkg.Files.Add(file);
        Nebra.Doc.DocBinder.Bind(hir, sourceText);

        var cache = new Dictionary<string, object>();

        try
        {
            var pm1 = new PassManager();
            pm1.BuildOrder(PassManager.SingleFilePhase1);
            pm1.Run(diag, [pkg], types, symAlloc, scopeAlloc, nodeAlloc, names, cache, effectiveConfig);
        }
        catch
        {
        }

        var (importedFiles, importedDecls) = PostResolveImports(hir, filePath, pkg, types, diag, effectiveConfig);

        try
        {
            var pm2 = new PassManager();
            pm2.BuildOrder(PassManager.SingleFilePhase2);
            pm2.Run(diag, [pkg], types, symAlloc, scopeAlloc, nodeAlloc, names, cache, effectiveConfig);
        }
        catch
        {
        }

        ValidateAnnotations(hir, diag);

        var nodeRegistry = NodeFinder.BuildNodeRegistry(hir);
        var fileMap = new Dictionary<NodeID, string>();
        foreach (var (id, _) in nodeRegistry)
            fileMap.TryAdd(id, filePath);

        var result = new AnalysisResult
        {
            Uri = uri,
            FilePath = filePath,
            SourceText = sourceText,
            File = file,
            Package = pkg,
            Diagnostics = diag,
            TokenStream = tokenStream,
            NodeRegistry = nodeRegistry,
            FileMap = fileMap,
            ImportedDeclarations = importedDecls
        };

        return (result, diag, filePath);
    }

    private (List<PreparsedFile> Files, Dictionary<SymID, ImportedDecl> Decls) PostResolveImports(
        IRScript hir, string importerPath,
        PackageContext pkg, TypeTable types, DiagnosticsBag diag, Config effectiveConfig)
    {
        var importedFiles = new List<PreparsedFile>();
        var importedDecls = new Dictionary<SymID, ImportedDecl>();

        foreach (var stmt in hir.Body)
        {
            if (stmt is not ImportStmt import) continue;

            var moduleName = import.Module.Name;
            if (moduleName.EndsWith(".neb"))
                moduleName = moduleName[..^4];

            var resolvedPath = ResolveImportPath(moduleName, importerPath);

            if (resolvedPath == null)
            {
                diag.Report(import.Module.Span, NebraDiagnosticCode.ErrModuleNotFound, moduleName);
                continue;
            }

            var importAnalysis = AnalyzeImportedFile(resolvedPath, effectiveConfig);
            if (importAnalysis == null) continue;

            importedFiles.Add(importAnalysis.File);

            var exports = CollectExports(importAnalysis, moduleName);
            var allTopLevel = CollectAllTopLevel(importAnalysis, moduleName);

            switch (import.Kind)
            {
                case ImportKind.Named:
                    foreach (var spec in import.Specifiers)
                    {
                        var memberName = spec.Name.Name;
                        if (exports.TryGetValue(memberName, out var exportInfo))
                        {
                            var importName = spec.Alias ?? spec.Name;
                            var symId = SetImportedType(pkg, types, importName.Name, exportInfo);
                            if (symId != SymID.Invalid && exportInfo.DeclNode != null)
                                importedDecls[symId] = new ImportedDecl(resolvedPath, exportInfo.DeclNode.Span, exportInfo.DeclNode);
                        }
                        else if (allTopLevel.ContainsKey(memberName))
                        {
                            diag.Report(spec.Name.Span, NebraDiagnosticCode.ErrSymbolNotExported, memberName, moduleName);
                        }
                        else
                        {
                            diag.Report(spec.Name.Span, NebraDiagnosticCode.ErrSymbolNotFound, memberName, moduleName);
                        }
                    }
                    break;

                case ImportKind.Namespace:
                    if (import.Alias != null)
                    {
                        var fields = exports.Select(kvp =>
                        {
                            var fieldType = ImportType(types, kvp.Value.Type, importAnalysis.Types);
                            return new StructType.Field(
                                new NameRef(kvp.Key, TextSpan.Empty), fieldType);
                        });
                        var structType = new StructType(fields);
                        var declared = types.DeclareType(structType);
                        SetImportedSymbolType(pkg, import.Alias.Name, declared.ID);
                    }
                    break;
            }
        }

        return (importedFiles, importedDecls);
    }

    private AnalysisResult? AnalyzeImportedFile(string filePath, Config baseConfig)
    {
        var fullPath = Path.GetFullPath(filePath);

        string source;
        var openDoc = _openDocuments.FirstOrDefault(kv =>
        {
            var docPath = DocumentUri.GetFileSystemPath(DocumentUri.Parse(kv.Key));
            return docPath != null && string.Equals(Path.GetFullPath(docPath), fullPath,
                StringComparison.OrdinalIgnoreCase);
        });

        string stamp;
        if (openDoc.Value != null)
        {
            source = openDoc.Value;
            // Open-document hash so cache invalidates as the user types.
            stamp = "open:" + source.Length + ":" + source.GetHashCode();
        }
        else
        {
            try
            {
                var fi = new FileInfo(filePath);
                stamp = $"file:{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";
                source = File.ReadAllText(filePath);
            }
            catch { return null; }
        }

        if (_importCache.TryGetValue(fullPath, out var cached) && cached.Stamp == stamp)
            return cached.Result;

        var diag = new DiagnosticsBag();
        var nodeAlloc = new IDAlloc<NodeID>();
        var symAlloc = new IDAlloc<SymID>();
        var scopeAlloc = new IDAlloc<ScopeID>();
        var typesLocal = new TypeTable(new IDAlloc<TypID>());
        var names = new NameMap();

        var config = baseConfig.Clone();
        var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (fileDir != null) config.Source = fileDir;

        IRScript? hir;
        CommonTokenStream tokenStream;
        try
        {
            var inputStream = new AntlrInputStream(source);
            var lexer = new NebraLexer(inputStream);
            lexer.RemoveErrorListeners();
            tokenStream = new CommonTokenStream(lexer);
            var parser = new NebraParser(tokenStream);
            parser.RemoveErrorListeners();
            var visitor = new IRVisitor(filePath, nodeAlloc, diag, config);
            hir = visitor.Visit(parser.script()) as IRScript;
        }
        catch { return null; }

        if (hir == null) return null;

        var scopes = new ScopeGraph(diag, scopeAlloc);
        var pkg = new PackageContext(filePath, new SymbolArena(symAlloc), scopes, typesLocal, scopes.Root);
        var file = new PreparsedFile(filePath, source) { Hir = hir };
        pkg.Files.Add(file);
        Nebra.Doc.DocBinder.Bind(hir, source);

        try
        {
            var pm = new PassManager();
            pm.BuildOrder(PassManager.SingleFilePipeline);
            pm.Run(diag, [pkg], typesLocal, symAlloc, scopeAlloc, nodeAlloc, names, new Dictionary<string, object>(), config);
        }
        catch { }

        var nodeRegistry = NodeFinder.BuildNodeRegistry(hir);

        var result = new AnalysisResult
        {
            Uri = DocumentUri.FromFileSystemPath(filePath).ToString(),
            FilePath = filePath,
            SourceText = source,
            File = file,
            Package = pkg,
            Diagnostics = diag,
            TokenStream = tokenStream,
            NodeRegistry = nodeRegistry,
            FileMap = nodeRegistry.ToDictionary(kv => kv.Key, _ => filePath)
        };

        _importCache[fullPath] = (stamp, result);
        return result;
    }

    public record struct ExportInfo(IR.Type Type, IR.SymbolKind SymKind, SymID Sym, Node? DeclNode);

    private static Dictionary<string, ExportInfo> CollectExports(AnalysisResult result, string? targetModuleName = null)
    {
        var exports = new Dictionary<string, ExportInfo>();
        foreach (var stmt in result.Hir.Body)
        {
            switch (stmt)
            {
                case ExportStmt export:
                    CollectExportStmtMembers(result, export, exports);
                    break;
                case DeclareModuleDecl dmd when targetModuleName != null
                                                && dmd.ModuleName.Name == targetModuleName:
                    CollectDeclareModuleMembers(result, dmd, exports);
                    break;
            }
        }
        return exports;
    }

    private static void CollectExportStmtMembers(AnalysisResult result, ExportStmt export, Dictionary<string, ExportInfo> exports)
    {
        foreach (var (name, symId) in GetDeclaredNames(export.Declaration))
        {
            if (result.Scopes.Lookup(result.Package.Root, name, out var scopeSym))
            {
                if (result.Syms.GetByID(scopeSym, out var sym) && result.Types.GetByID(sym.Type, out var typ))
                {
                    Node? declNode = sym.DeclaringNode != NodeID.Invalid
                        ? result.NodeRegistry.GetValueOrDefault(sym.DeclaringNode)
                        : null;
                    exports[name] = new ExportInfo(typ, sym.Kind, scopeSym, declNode);
                }
            }
            else if (symId != SymID.Invalid && result.Syms.GetByID(symId, out var directSym) &&
                     result.Types.GetByID(directSym.Type, out var directTyp))
            {
                Node? declNode = directSym.DeclaringNode != NodeID.Invalid
                    ? result.NodeRegistry.GetValueOrDefault(directSym.DeclaringNode)
                    : null;
                exports[name] = new ExportInfo(directTyp, directSym.Kind, symId, declNode);
            }
        }
    }

    /// <summary>
    /// Walks the members of a <c>declare module "X" ... end</c> block and exposes
    /// them as named exports. Used so <c>import { lerp } from "lua-math"</c>
    /// resolves against an ambient declaration file (e.g. <c>lua-math/init.d.neb</c>)
    /// that uses the <c>declare module</c> style rather than per-symbol <c>export</c>s.
    /// </summary>
    private static void CollectDeclareModuleMembers(AnalysisResult result, DeclareModuleDecl declModule, Dictionary<string, ExportInfo> exports)
    {
        if (!result.Scopes.EnclosingScope(declModule.ID, out var moduleScope)) return;

        foreach (var member in declModule.Members)
        {
            foreach (var (name, memberSymId) in GetDeclareMemberNames(member))
            {
                SymID symId = SymID.Invalid;
                if (result.Scopes.LookupOnlyCurrent(moduleScope, name, out var scopeSym))
                    symId = scopeSym;
                else if (memberSymId != SymID.Invalid)
                    symId = memberSymId;

                if (symId == SymID.Invalid) continue;
                if (!result.Syms.GetByID(symId, out var sym)) continue;
                if (!result.Types.GetByID(sym.Type, out var typ)) continue;

                Node? declNode = sym.DeclaringNode != NodeID.Invalid
                    ? result.NodeRegistry.GetValueOrDefault(sym.DeclaringNode)
                    : member;
                exports[name] = new ExportInfo(typ, sym.Kind, symId, declNode);
            }
        }
    }

    private static Dictionary<string, SymID> CollectAllTopLevel(AnalysisResult result, string? targetModuleName = null)
    {
        var all = new Dictionary<string, SymID>();
        foreach (var stmt in result.Hir.Body)
        {
            foreach (var (name, sym) in GetDeclaredNames(stmt))
                all.TryAdd(name, sym);
            if (stmt is ExportStmt exp)
                foreach (var (name, sym) in GetDeclaredNames(exp.Declaration))
                    all.TryAdd(name, sym);
            if (stmt is DeclareModuleDecl dmd
                && (targetModuleName == null || dmd.ModuleName.Name == targetModuleName))
            {
                foreach (var member in dmd.Members)
                    foreach (var (name, sym) in GetDeclareMemberNames(member))
                        all.TryAdd(name, sym);
            }
        }
        return all;
    }

    private static List<(string Name, SymID Sym)> GetDeclaredNames(Stmt stmt)
    {
        return stmt switch
        {
            FunctionDecl { NamePath.Count: > 0 } fd => [(fd.NamePath[0].Name, fd.NamePath[0].Sym)],
            LocalFunctionDecl lfd => [(lfd.Name.Name, lfd.Name.Sym)],
            LocalDecl ld => ld.Variables.Select(v => (v.Name.Name, v.Name.Sym)).ToList(),
            EnumDecl ed => [(ed.Name.Name, ed.Name.Sym)],
            ClassDecl cd => [(cd.Name.Name, cd.Name.Sym)],
            InterfaceDecl id => [(id.Name.Name, id.Name.Sym)],
            _ => []
        };
    }

    private static List<(string Name, SymID Sym)> GetDeclareMemberNames(Decl decl)
    {
        return decl switch
        {
            DeclareFunctionDecl { NamePath.Count: > 0 } df => [(df.NamePath[0].Name, df.NamePath[0].Sym)],
            DeclareVariableDecl dv => [(dv.Name.Name, dv.Name.Sym)],
            EnumDecl ed => [(ed.Name.Name, ed.Name.Sym)],
            ClassDecl cd => [(cd.Name.Name, cd.Name.Sym)],
            InterfaceDecl idecl => [(idecl.Name.Name, idecl.Name.Sym)],
            _ => []
        };
    }

    private SymID SetImportedType(PackageContext pkg, TypeTable types, string name, ExportInfo exportInfo)
    {
        var importedType = ImportType(types, exportInfo.Type, null);
        if (pkg.Scopes.Lookup(pkg.Root, name, out var symId) && pkg.Syms.GetByID(symId, out var sym))
        {
            sym.Type = importedType.ID;
            return symId;
        }
        return SymID.Invalid;
    }

    private static void SetImportedSymbolType(PackageContext pkg, string name, TypID typeId)
    {
        if (pkg.Scopes.Lookup(pkg.Root, name, out var symId) && pkg.Syms.GetByID(symId, out var sym))
        {
            sym.Type = typeId;
        }
    }

    /// <summary>
    /// Bridges a type from a different <see cref="TypeTable"/> into
    /// <paramref name="dstTypes"/>. Classes and interfaces need their members
    /// copied across explicitly; without this, hovering on
    /// <c>obj:method()</c> where <c>obj</c> comes from an imported class
    /// reads an empty <see cref="ClassType.Methods"/> and falls through to
    /// <c>any</c>. The walk is memoised so cyclic class graphs (A.method
    /// returns B which references A) terminate.
    /// </summary>
    private static IR.Type ImportType(TypeTable dstTypes, IR.Type srcType, TypeTable? srcTypes,
        Dictionary<IR.Type, IR.Type>? memo = null)
    {
        memo ??= new Dictionary<IR.Type, IR.Type>(ReferenceEqualityComparer.Instance);
        if (memo.TryGetValue(srcType, out var existing)) return existing;

        switch (srcType)
        {
            case FunctionType ft:
            {
                var imported = dstTypes.DeclareType(new FunctionType(
                    ft.ParamTypes.Select(p => ImportType(dstTypes, p, srcTypes, memo)),
                    ft.ParamNames,
                    ImportType(dstTypes, ft.ReturnType, srcTypes, memo),
                    ft.IsVararg,
                    ft.VarargType != null ? ImportType(dstTypes, ft.VarargType, srcTypes, memo) : null,
                    ft.DefaultParams.Count > 0 ? [..ft.DefaultParams] : null));
                memo[srcType] = imported;
                return imported;
            }
            case UnionType ut:
            {
                var imported = dstTypes.DeclareType(new UnionType(
                    ut.Types.Select(t => ImportType(dstTypes, t, srcTypes, memo))));
                memo[srcType] = imported;
                return imported;
            }
            case TableArrayType ta:
            {
                var imported = dstTypes.DeclareType(new TableArrayType(
                    ImportType(dstTypes, ta.ElementType, srcTypes, memo)));
                memo[srcType] = imported;
                return imported;
            }
            case TableMapType tm:
            {
                var imported = dstTypes.DeclareType(new TableMapType(
                    ImportType(dstTypes, tm.KeyType, srcTypes, memo),
                    ImportType(dstTypes, tm.ValueType, srcTypes, memo)));
                memo[srcType] = imported;
                return imported;
            }
            case StructType st:
            {
                var imported = dstTypes.DeclareType(new StructType(
                    st.Fields.Select(f => new StructType.Field(f.Name, ImportType(dstTypes, f.Type, srcTypes, memo), f.IsMeta))));
                memo[srcType] = imported;
                return imported;
            }
            case TupleType tt:
            {
                var imported = dstTypes.DeclareType(new TupleType(
                    tt.Fields.Select(f => new TupleType.Field(f.Name, ImportType(dstTypes, f.Type, srcTypes, memo)))));
                memo[srcType] = imported;
                return imported;
            }
            case EnumType et:
            {
                var imported = dstTypes.DeclareType(new EnumType(
                    et.Name, et.Members, ImportType(dstTypes, et.BaseType, srcTypes, memo)));
                memo[srcType] = imported;
                return imported;
            }
            case ClassType ct:
            {
                // Pre-register the bridge in memo BEFORE recursing into
                // members so a method whose signature references the same
                // class doesn't loop and doesn't get a half-built second
                // copy.
                var bridge = new ClassType(ct.Name, null, [], ct.IsAbstract);
                var declared = dstTypes.DeclareType(bridge);
                memo[srcType] = declared;
                // Re-fetch in case DeclareType deduplicated to an existing
                // entry — its Methods dict may already be populated.
                if (declared is not ClassType target) return declared;
                if (target.Methods.Count > 0) return declared;

                target.BaseClass = ct.BaseClass != null
                    ? ImportType(dstTypes, ct.BaseClass, srcTypes, memo) as ClassType
                    : null;
                foreach (var iface in ct.Interfaces)
                {
                    if (ImportType(dstTypes, iface, srcTypes, memo) is InterfaceType bridged)
                        target.Interfaces.Add(bridged);
                }
                foreach (var (n, f) in ct.InstanceFields)
                    target.InstanceFields[n] = new StructType.Field(f.Name,
                        ImportType(dstTypes, f.Type, srcTypes, memo), f.IsMeta);
                foreach (var (n, m) in ct.Methods)
                    if (ImportType(dstTypes, m, srcTypes, memo) is FunctionType bm) target.Methods[n] = bm;
                foreach (var (n, declaration) in ct.MemberDeclarations)
                    target.MemberDeclarations[n] = declaration;
                foreach (var (n, m) in ct.StaticMethods)
                    if (ImportType(dstTypes, m, srcTypes, memo) is FunctionType bm) target.StaticMethods[n] = bm;
                foreach (var (n, list) in ct.MethodOverloads)
                {
                    var bridgedList = new List<FunctionType>();
                    foreach (var fn in list)
                        if (ImportType(dstTypes, fn, srcTypes, memo) is FunctionType bfn) bridgedList.Add(bfn);
                    target.MethodOverloads[n] = bridgedList;
                }
                foreach (var (n, list) in ct.StaticMethodOverloads)
                {
                    var bridgedList = new List<FunctionType>();
                    foreach (var fn in list)
                        if (ImportType(dstTypes, fn, srcTypes, memo) is FunctionType bfn) bridgedList.Add(bfn);
                    target.StaticMethodOverloads[n] = bridgedList;
                }
                foreach (var (n, list) in ct.MethodOverloadSides) target.MethodOverloadSides[n] = [..list];
                foreach (var (n, list) in ct.StaticMethodOverloadSides) target.StaticMethodOverloadSides[n] = [..list];
                foreach (var (n, g) in ct.Getters)
                    if (ImportType(dstTypes, g, srcTypes, memo) is FunctionType bg) target.Getters[n] = bg;
                foreach (var (n, s) in ct.Setters)
                    if (ImportType(dstTypes, s, srcTypes, memo) is FunctionType bs) target.Setters[n] = bs;
                if (ct.ConstructorType != null)
                    target.ConstructorType = ImportType(dstTypes, ct.ConstructorType, srcTypes, memo) as FunctionType;
                foreach (var n in ct.AbstractMethods) target.AbstractMethods.Add(n);
                foreach (var n in ct.ProtectedMembers) target.ProtectedMembers.Add(n);
                target.CtorTemplate = ct.CtorTemplate;
                target.ConstructorSide = ct.ConstructorSide;
                foreach (var (n, s) in ct.FieldSides) target.FieldSides[n] = s;
                foreach (var (n, s) in ct.MethodSides) target.MethodSides[n] = s;
                foreach (var (n, s) in ct.StaticMethodSides) target.StaticMethodSides[n] = s;
                foreach (var (n, s) in ct.GetterSides) target.GetterSides[n] = s;
                foreach (var (n, s) in ct.SetterSides) target.SetterSides[n] = s;
                return declared;
            }
            case InterfaceType it:
            {
                var bridge = new InterfaceType(it.Name, []);
                var declared = dstTypes.DeclareType(bridge);
                memo[srcType] = declared;
                if (declared is not InterfaceType target) return declared;
                if (target.Methods.Count > 0) return declared;

                foreach (var b in it.BaseInterfaces)
                    if (ImportType(dstTypes, b, srcTypes, memo) is InterfaceType bb) target.BaseInterfaces.Add(bb);
                foreach (var (n, f) in it.Fields)
                    target.Fields[n] = new StructType.Field(f.Name,
                        ImportType(dstTypes, f.Type, srcTypes, memo), f.IsMeta);
                foreach (var (n, m) in it.Methods)
                    if (ImportType(dstTypes, m, srcTypes, memo) is FunctionType bm) target.Methods[n] = bm;
                foreach (var (n, declaration) in it.MemberDeclarations)
                    target.MemberDeclarations[n] = declaration;
                foreach (var (n, list) in it.MethodOverloads)
                {
                    var bridgedList = new List<FunctionType>();
                    foreach (var fn in list)
                        if (ImportType(dstTypes, fn, srcTypes, memo) is FunctionType bfn) bridgedList.Add(bfn);
                    target.MethodOverloads[n] = bridgedList;
                }
                foreach (var (n, list) in it.MethodOverloadSides) target.MethodOverloadSides[n] = [..list];
                foreach (var (n, s) in it.FieldSides) target.FieldSides[n] = s;
                foreach (var (n, s) in it.MethodSides) target.MethodSides[n] = s;
                return declared;
            }
            default:
            {
                var imported = dstTypes.DeclareType(new IR.Type(srcType.Kind));
                memo[srcType] = imported;
                return imported;
            }
        }
    }

    private void PublishDiagnostics(string uri, string filePath, DiagnosticsBag bag)
    {
        var fullPath = Path.GetFullPath(filePath);
        var lspDiags = bag.Diagnostics
            .Where(d => d.Span != TextSpan.Empty)
            .Where(d => d.Span.File == null ||
                        string.Equals(Path.GetFullPath(d.Span.File), fullPath,
                            StringComparison.OrdinalIgnoreCase))
            .Select(ToLspDiagnostic)
            .ToList();

        _server?.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = DocumentUri.Parse(uri),
            Diagnostics = new Container<LspDiagnostic>(lspDiags)
        });
    }

    private static LspDiagnostic ToLspDiagnostic(NebraDiagnostic d)
    {
        return new LspDiagnostic
        {
            Range = SpanToRange(d.Span),
            Severity = d.Level switch
            {
                DiagnosticLevel.Error => DiagnosticSeverity.Error,
                DiagnosticLevel.Warning => DiagnosticSeverity.Warning,
                DiagnosticLevel.Info => DiagnosticSeverity.Information,
                _ => DiagnosticSeverity.Hint
            },
            Source = "nebra",
            Message = d.Message,
            Code = d.Code.ToString()
        };
    }

    public static OmniSharp.Extensions.LanguageServer.Protocol.Models.Range SpanToRange(TextSpan span)
    {
        return new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
            new Position(Math.Max(0, span.StartLn - 1), Math.Max(0, span.StartCol - 1)),
            new Position(Math.Max(0, span.EndLn - 1), Math.Max(0, span.EndCol))
        );
    }

    public string FormatType(TypeTable types, TypID typId)
    {
        if (typId == TypID.Invalid) return "any";
        if (!types.GetByID(typId, out var typ)) return "unknown";
        return FormatType(types, typ);
    }

    /// <summary>
    /// Recursive type pretty-printer. Walks composite types (functions, unions,
    /// arrays, maps, tuples, structs) so that inner class/interface/enum types
    /// render under their short name (e.g. <c>Vec2</c>) instead of the raw
    /// <see cref="TypeKey"/> (<c>interface&lt;Vec2&gt;</c>). Falls back to
    /// <see cref="PrettifyTypeKey"/> for primitive and unrecognised types.
    /// </summary>
    public string FormatType(TypeTable types, IR.Type typ)
    {
        switch (typ)
        {
            case EnumType et: return et.Name;
            case ClassType ct: return ct.Name;
            case InterfaceType it: return it.Name;
            case FunctionType ft: return FormatFunctionType(types, ft);
            case UnionType ut: return string.Join(" | ", ut.Types.Select(t => FormatType(types, t)));
            case TableArrayType ta: return FormatType(types, ta.ElementType) + "[]";
            case TableMapType tm: return $"{{ [{FormatType(types, tm.KeyType)}]: {FormatType(types, tm.ValueType)} }}";
            case TupleType tt: return "(" + string.Join(", ", tt.Fields.Select(f =>
                f.Name == null ? FormatType(types, f.Type) : $"{f.Name.Name}: {FormatType(types, f.Type)}")) + ")";
            case StructType st: return "{ " + string.Join(", ", st.Fields.Select(f =>
                $"{f.Name.Name}: {FormatType(types, f.Type)}")) + " }";
            case VariadicType variadic: return "..." + FormatType(types, variadic.ElementType);
            default: return PrettifyTypeKey(typ.Key);
        }
    }

    private string FormatFunctionType(TypeTable types, FunctionType ft)
    {
        var parts = new List<string>();
        for (var i = 0; i < ft.ParamTypes.Count; i++)
        {
            var pName = i < ft.ParamNames.Count ? ft.ParamNames[i] : $"arg{i}";
            var pType = FormatType(types, ft.ParamTypes[i]);
            var part = $"{pName}: {pType}";
            if (ft.DefaultParams.Contains(i)) part += " = ...";
            parts.Add(part);
        }
        if (ft.IsVararg)
        {
            var vaType = ft.VarargType != null ? FormatType(types, ft.VarargType) : "any";
            parts.Add($"...: {vaType}");
        }
        var prefix = ft.IsAsync ? "async " : "";
        var ret = ft.Predicate != null
            ? $"{ft.Predicate.ParamName} is {FormatType(types, ft.Predicate.TargetType)}"
            : FormatType(types, ft.ReturnType);
        return $"{prefix}({string.Join(", ", parts)}) -> {ret}";
    }

    /// <summary>
    /// Renders the body of a named type (interface / class / enum) as the
    /// multi-line block a user would see in source: <c>interface Vec2 ... end</c>,
    /// fields and methods on indented lines. Intended for embedding inside a
    /// fenced <c>```nebra ... ```</c> hover so the user sees the shape of the
    /// type, not just its name. Falls back to <see cref="FormatType"/> for
    /// non-body types.
    /// </summary>
    public string FormatTypeBody(AnalysisResult result, IR.Type typ)
    {
        return typ switch
        {
            InterfaceType it => FormatInterfaceBody(result, it),
            ClassType ct => FormatClassBody(result, ct),
            EnumType et => FormatEnumBody(et),
            _ => FormatType(result.Types, typ)
        };
    }

    private string FormatInterfaceBody(AnalysisResult result, InterfaceType it)
    {
        var sb = new StringBuilder();
        sb.Append("interface ").Append(it.Name);
        if (it.BaseInterfaces.Count > 0)
            sb.Append(" extends ").Append(string.Join(", ", it.BaseInterfaces.Select(b => b.Name)));
        sb.AppendLine();

        foreach (var (name, field) in it.Fields)
            sb.Append("    ").Append(name).Append(": ")
                .AppendLine(FormatType(result.Types, field.Type));

        foreach (var (name, method) in it.Methods)
            sb.Append("    function ").Append(name).Append('(')
                .Append(FormatFunctionParamList(result, method))
                .Append("): ")
                .AppendLine(FormatType(result.Types, method.ReturnType));

        sb.Append("end");
        return sb.ToString();
    }

    private string FormatClassBody(AnalysisResult result, ClassType ct)
    {
        var sb = new StringBuilder();
        if (ct.IsAbstract) sb.Append("abstract ");
        sb.Append("class ").Append(ct.Name);
        if (ct.BaseClass != null) sb.Append(" extends ").Append(ct.BaseClass.Name);
        if (ct.Interfaces.Count > 0)
            sb.Append(" implements ").Append(string.Join(", ", ct.Interfaces.Select(i => i.Name)));
        sb.AppendLine();

        foreach (var (name, field) in ct.InstanceFields)
            sb.Append("    ").Append(name).Append(": ")
                .AppendLine(FormatType(result.Types, field.Type));

        if (ct.ConstructorType != null)
            sb.Append("    constructor(")
                .Append(FormatFunctionParamList(result, ct.ConstructorType))
                .AppendLine(")");

        foreach (var (name, method) in ct.StaticMethods)
            sb.Append("    static function ").Append(name).Append('(')
                .Append(FormatFunctionParamList(result, method))
                .Append("): ")
                .AppendLine(FormatType(result.Types, method.ReturnType));

        foreach (var (name, method) in ct.Methods)
            sb.Append("    function ").Append(name).Append('(')
                .Append(FormatFunctionParamList(result, method))
                .Append("): ")
                .AppendLine(FormatType(result.Types, method.ReturnType));

        foreach (var (name, getter) in ct.Getters)
            sb.Append("    get ").Append(name).Append("(): ")
                .AppendLine(FormatType(result.Types, getter.ReturnType));

        foreach (var (name, setter) in ct.Setters)
            sb.Append("    set ").Append(name).Append('(')
                .Append(FormatFunctionParamList(result, setter))
                .AppendLine(")");

        sb.Append("end");
        return sb.ToString();
    }

    private static string FormatEnumBody(EnumType et)
    {
        var sb = new StringBuilder();
        sb.Append("enum ").AppendLine(et.Name);
        foreach (var m in et.Members)
            sb.Append("    ").AppendLine(m.Name);
        sb.Append("end");
        return sb.ToString();
    }

    private string FormatFunctionParamList(AnalysisResult result, FunctionType ft)
    {
        var parts = new List<string>();
        for (var i = 0; i < ft.ParamTypes.Count; i++)
        {
            var pName = i < ft.ParamNames.Count ? ft.ParamNames[i] : $"arg{i}";
            var pType = FormatType(result.Types, ft.ParamTypes[i]);
            var part = $"{pName}: {pType}";
            if (ft.DefaultParams.Contains(i)) part += " = ...";
            parts.Add(part);
        }
        if (ft.IsVararg)
        {
            var vaType = ft.VarargType != null ? FormatType(result.Types, ft.VarargType) : "any";
            parts.Add($"...: {vaType}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Returns a one-line markdown fragment of the form
    /// <c>Types: [Vec2](uri) · [Color](uri)</c> listing every named (class /
    /// interface / enum) type referenced anywhere inside <paramref name="typ"/>,
    /// each with a deep link to its declaration. Returns an empty string when
    /// nothing in the type is clickable. Designed to sit *under* a fenced code
    /// block so VSCode renders the links clickably.
    /// </summary>
    public string FormatTypeReferencesLine(AnalysisResult result, TypID typId)
    {
        if (typId == TypID.Invalid) return string.Empty;
        if (!result.Types.GetByID(typId, out var typ)) return string.Empty;
        return FormatTypeReferencesLine(result, typ);
    }

    public string FormatTypeReferencesLine(AnalysisResult result, IR.Type typ)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var refs = new List<(string Name, string Link)>();
        CollectTypeReferences(result, typ, seen, refs);
        if (refs.Count == 0) return string.Empty;
        return "**Types:** " + string.Join(" · ", refs.Select(r => $"[{r.Name}]({r.Link})"));
    }

    private void CollectTypeReferences(AnalysisResult result, IR.Type typ, HashSet<string> seen, List<(string Name, string Link)> refs)
    {
        switch (typ)
        {
            case EnumType et: AddNamedRef(result, et.Name, seen, refs); break;
            case ClassType ct:
                if (AddNamedRef(result, ct.Name, seen, refs))
                {
                    foreach (var (_, f) in ct.InstanceFields) CollectTypeReferences(result, f.Type, seen, refs);
                    foreach (var (_, m) in ct.Methods) CollectTypeReferences(result, m, seen, refs);
                    foreach (var (_, m) in ct.StaticMethods) CollectTypeReferences(result, m, seen, refs);
                }
                break;
            case InterfaceType it:
                if (AddNamedRef(result, it.Name, seen, refs))
                {
                    foreach (var (_, f) in it.Fields) CollectTypeReferences(result, f.Type, seen, refs);
                    foreach (var (_, m) in it.Methods) CollectTypeReferences(result, m, seen, refs);
                }
                break;
            case FunctionType ft:
                CollectTypeReferences(result, ft.ReturnType, seen, refs);
                foreach (var pt in ft.ParamTypes) CollectTypeReferences(result, pt, seen, refs);
                if (ft.VarargType != null) CollectTypeReferences(result, ft.VarargType, seen, refs);
                break;
            case UnionType ut:
                foreach (var t in ut.Types) CollectTypeReferences(result, t, seen, refs);
                break;
            case TableArrayType ta: CollectTypeReferences(result, ta.ElementType, seen, refs); break;
            case TableMapType tm:
                CollectTypeReferences(result, tm.KeyType, seen, refs);
                CollectTypeReferences(result, tm.ValueType, seen, refs);
                break;
            case StructType st:
                foreach (var f in st.Fields) CollectTypeReferences(result, f.Type, seen, refs);
                break;
            case TupleType tt:
                foreach (var f in tt.Fields) CollectTypeReferences(result, f.Type, seen, refs);
                break;
        }
    }

    private bool AddNamedRef(AnalysisResult result, string name, HashSet<string> seen, List<(string Name, string Link)> refs)
    {
        if (!seen.Add(name)) return false;
        var loc = FindTypeDeclLocation(result, name);
        if (loc == null) return true;
        var uri = DocumentUri.FromFileSystemPath(loc.FilePath);
        var line = Math.Max(1, loc.Span.StartLn);
        var col = Math.Max(1, loc.Span.StartCol);
        refs.Add((name, $"{uri}#L{line},{col}"));
        return true;
    }

    private static string PrettifyTypeKey(string key)
    {
        return key
            .Replace("<invalid>", "any")
            .Replace("PrimitiveNumber", "number")
            .Replace("PrimitiveBool", "boolean")
            .Replace("PrimitiveString", "string")
            .Replace("PrimitiveFunction", "function")
            .Replace("PrimitiveThread", "thread")
            .Replace("PrimitiveUserdata", "userdata")
            .Replace("PrimitiveNil", "nil")
            .Replace("PrimitiveNever", "never")
            .Replace("PrimitiveAny", "any");
    }

    public sealed record TypeDeclLocation(string FilePath, TextSpan Span);

    /// <summary>
    /// Best-effort lookup of where a named type (class/interface/enum) is
    /// declared. Tries, in order: the consumer's own scope (covers locally
    /// declared types), the consumer's <see cref="AnalysisResult.ImportedDeclarations"/>
    /// (covers types directly imported via <c>import { X } from</c>), the
    /// HIRs of currently-open documents, and finally a workspace-wide scan
    /// of <c>.neb</c> / <c>.d.neb</c> files. Returns <c>null</c> when none
    /// match — the caller should render the type name without a link.
    /// </summary>
    public TypeDeclLocation? FindTypeDeclLocation(AnalysisResult result, string typeName)
    {
        if (result.Scopes.Lookup(result.Package.Root, typeName, out var symId) &&
            result.Syms.GetByID(symId, out var sym))
        {
            if (result.ImportedDeclarations.TryGetValue(symId, out var imp))
                return new TypeDeclLocation(imp.FilePath, imp.Span);

            if (sym.DeclaringNode != NodeID.Invalid &&
                result.NodeRegistry.TryGetValue(sym.DeclaringNode, out var declNode))
            {
                var path = result.FileMap.TryGetValue(sym.DeclaringNode, out var declFile)
                    ? declFile : result.FilePath;
                return new TypeDeclLocation(path, declNode.Span);
            }
        }

        foreach (var (_, other) in _results)
        {
            var found = FindTypeDeclInScript(other.Hir, typeName, other.FilePath);
            if (found != null) return found;
        }

        if (_rootPath != null)
        {
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new List<string> { _rootPath };
            var modulesDir = Path.Combine(_rootPath, "nebra_modules");
            if (Directory.Exists(modulesDir)) roots.Add(modulesDir);

            foreach (var root in roots)
            {
                var rootFull = Path.GetFullPath(root);
                if (!seenRoots.Add(rootFull)) continue;
                if (!Directory.Exists(rootFull)) continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(rootFull, "*.neb", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var file in files)
                {
                    var loc = TryFindTypeDeclByText(file, typeName);
                    if (loc != null) return loc;
                }
            }
        }

        return null;
    }

    private static TypeDeclLocation? FindTypeDeclInScript(IRScript hir, string typeName, string filePath)
    {
        foreach (var stmt in hir.Body)
        {
            var match = MatchTypeDecl(stmt, typeName);
            if (match != null) return new TypeDeclLocation(filePath, match);

            if (stmt is DeclareModuleDecl dmd)
            {
                foreach (var member in dmd.Members)
                {
                    var memMatch = MatchTypeDecl(member, typeName);
                    if (memMatch != null) return new TypeDeclLocation(filePath, memMatch);
                }
            }
            if (stmt is ExportStmt ex)
            {
                var exMatch = MatchTypeDecl(ex.Declaration, typeName);
                if (exMatch != null) return new TypeDeclLocation(filePath, exMatch);
            }
        }
        return null;
    }

    private static TextSpan? MatchTypeDecl(Stmt stmt, string typeName)
    {
        return stmt switch
        {
            InterfaceDecl id when id.Name.Name == typeName => id.Name.Span,
            ClassDecl cd when cd.Name.Name == typeName => cd.Name.Span,
            EnumDecl ed when ed.Name.Name == typeName => ed.Name.Span,
            _ => null
        };
    }

    /// <summary>
    /// Cheap regex-style scan for a type declaration keyword followed by
    /// <paramref name="typeName"/>. Used as a last-resort fallback for type
    /// names that don't appear in any open analysis; computes only the line
    /// and column rather than running a full parse.
    /// </summary>
    private static TypeDeclLocation? TryFindTypeDeclByText(string filePath, string typeName)
    {
        string content;
        try { content = File.ReadAllText(filePath); }
        catch { return null; }

        string[] patterns = [
            $"interface {typeName}",
            $"class {typeName}",
            $"enum {typeName}",
            $"declare interface {typeName}",
            $"declare class {typeName}",
            $"declare enum {typeName}",
            $"abstract class {typeName}"
        ];

        var earliest = -1;
        string? hitPattern = null;
        foreach (var p in patterns)
        {
            var idx = content.IndexOf(p, StringComparison.Ordinal);
            if (idx < 0) continue;
            var after = idx + p.Length;
            if (after < content.Length && (char.IsLetterOrDigit(content[after]) || content[after] == '_'))
                continue;
            if (earliest == -1 || idx < earliest)
            {
                earliest = idx;
                hitPattern = p;
            }
        }

        if (earliest < 0 || hitPattern == null) return null;

        var line = 1;
        var col = 1;
        for (var i = 0; i < earliest; i++)
        {
            if (content[i] == '\n') { line++; col = 1; }
            else col++;
        }

        var nameOffset = hitPattern.LastIndexOf(typeName, StringComparison.Ordinal);
        col += nameOffset;
        var span = new TextSpan(filePath, line, col, line, col + typeName.Length);
        return new TypeDeclLocation(filePath, span);
    }

    public List<Location> FindUsages(SymID targetSym, AnalysisResult originResult)
    {
        var locations = new List<Location>();

        foreach (var (uri, res) in _results)
        {
            var allRefs = NodeFinder.CollectAllNameRefs(res.Hir);
            foreach (var nr in allRefs)
            {
                if (nr.Sym != targetSym) continue;
                locations.Add(new Location
                {
                    Uri = DocumentUri.Parse(uri),
                    Range = SpanToRange(nr.Span)
                });
            }
        }

        return locations;
    }

    public Dictionary<string, ExportInfo>? CollectExportsFromModule(AnalysisResult result, string moduleName)
    {
        if (moduleName.EndsWith(".neb")) moduleName = moduleName[..^4];

        var resolvedPath = ResolveImportPath(moduleName, result.FilePath);
        if (resolvedPath == null) return null;

        var dir = Path.GetDirectoryName(result.FilePath);
        var effectiveConfig = _config.Clone();
        if (dir != null) effectiveConfig.Source = dir;
        var imported = AnalyzeImportedFile(resolvedPath, effectiveConfig);
        if (imported == null) return null;

        return CollectExports(imported, moduleName);
    }

    /// <summary>
    /// Mirrors <see cref="Nebra.Compiler.ModuleResolver"/>'s search-path logic so the LSP
    /// resolves the same module specifiers as a CLI build. Looks in the importer's
    /// directory, the project source root, and <c>nebra_modules/</c>, trying both
    /// <c>&lt;name&gt;.(d.)nebra</c> and <c>&lt;name&gt;/init.(d.)nebra</c>.
    /// </summary>
    private string? ResolveImportPath(string moduleName, string importerPath)
    {
        var importerDir = Path.GetDirectoryName(Path.GetFullPath(importerPath));
        var cacheKey = $"{importerDir}|{moduleName}";
        if (_resolveCache.TryGetValue(cacheKey, out var cachedPath))
            return cachedPath;

        var resolved = ResolveImportPathUncached(moduleName, importerDir);
        _resolveCache[cacheKey] = resolved;
        return resolved;
    }

    private string? ResolveImportPathUncached(string moduleName, string? importerDir)
    {
        var searchDirs = new List<string>();
        if (importerDir != null) searchDirs.Add(importerDir);

        if (_rootPath != null)
        {
            var sourceRoot = Path.IsPathRooted(_config.Source)
                ? _config.Source
                : Path.Combine(_rootPath, _config.Source);
            if (Directory.Exists(sourceRoot))
                searchDirs.Add(Path.GetFullPath(sourceRoot));

            var modulesDir = Path.Combine(_rootPath, "nebra_modules");
            if (Directory.Exists(modulesDir))
                searchDirs.Add(Path.GetFullPath(modulesDir));
        }

        foreach (var dir in searchDirs)
        {
            var dnebra = Path.GetFullPath(Path.Combine(dir, moduleName + ".d.neb"));
            if (File.Exists(dnebra)) return dnebra;

            var nebra = Path.GetFullPath(Path.Combine(dir, moduleName + ".neb"));
            if (File.Exists(nebra)) return nebra;

            var dnebraIdx = Path.GetFullPath(Path.Combine(dir, moduleName, "init.d.neb"));
            if (File.Exists(dnebraIdx)) return dnebraIdx;

            var nebraIdx = Path.GetFullPath(Path.Combine(dir, moduleName, "init.neb"));
            if (File.Exists(nebraIdx)) return nebraIdx;
        }

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(dir, "*.d.neb", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in candidates)
            {
                if (FileDeclaresModule(file, moduleName))
                    return Path.GetFullPath(file);
            }
        }

        return null;
    }

    /// <summary>
    /// Cheap textual check: does <paramref name="filePath"/> contain a
    /// <c>declare module "&lt;name&gt;"</c> header? Used by the recursive
    /// <c>nebra_modules/</c> sweep so consumers can import a module whose
    /// declaration lives in a file whose path doesn't match the module name
    /// (e.g. several modules declared in a single <c>types.d.neb</c>).
    /// </summary>
    private static bool FileDeclaresModule(string filePath, string moduleName)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            return content.Contains($"declare module \"{moduleName}\"", StringComparison.Ordinal)
                   || content.Contains($"declare module '{moduleName}'", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private void ReanalyzeImporters(string changedUri)
    {
        var changedPath = DocumentUri.GetFileSystemPath(DocumentUri.Parse(changedUri));
        if (changedPath == null) return;
        var changedFull = Path.GetFullPath(changedPath);

        foreach (var (otherUri, otherText) in _openDocuments)
        {
            if (string.Equals(otherUri, changedUri, StringComparison.OrdinalIgnoreCase)) continue;
            if (!_results.TryGetValue(otherUri, out var otherResult)) continue;

            var imports = otherResult.Hir.Body.OfType<ImportStmt>().Any(import =>
            {
                var modName = import.Module.Name;
                if (modName.EndsWith(".neb")) modName = modName[..^4];
                var resolved = ResolveImportPath(modName, otherResult.FilePath);
                return resolved != null
                       && string.Equals(resolved, changedFull, StringComparison.OrdinalIgnoreCase);
            });

            if (imports)
                AnalyzeDocument(otherUri, otherText);
        }
    }

    /// <summary>
    /// Walks every annotated declaration in <paramref name="hir"/> and reports
    /// any annotation usage diagnostics (unknown name, target mismatch,
    /// unknown / missing / type-mismatched arguments). Mirrors the validations
    /// the compiler's <c>ApplyAnnotationsPass.BuildArgs</c> performs &mdash;
    /// without actually running <c>apply</c> &mdash; so the editor surfaces the
    /// same errors as <c>nebra build</c>.
    /// </summary>
    private void ValidateAnnotations(IRScript hir, DiagnosticsBag diag)
    {
        var metas = GetAnnotationMetas();
        if (metas.Count == 0) return;
        var byName = metas.ToDictionary(m => m.Name, m => m, StringComparer.Ordinal);

        foreach (var stmt in hir.Body)
            ValidateAnnotationsOnStmt(stmt, byName, diag);
    }

    private static void ValidateAnnotationsOnStmt(Stmt stmt, Dictionary<string, Nebra.Compiler.Annotations.AnnotationMeta> metas, DiagnosticsBag diag)
    {
        var decl = stmt switch
        {
            ExportStmt ex => ex.Declaration,
            Decl d => d,
            _ => null
        };
        if (decl == null) return;

        var anns = decl switch
        {
            FunctionDecl fd => fd.Annotations,
            LocalFunctionDecl lfd => lfd.Annotations,
            LocalDecl ld => ld.Annotations,
            ClassDecl cd => cd.Annotations,
            EnumDecl ed => ed.Annotations,
            InterfaceDecl id => id.Annotations,
            _ => []
        };

        var targetKind = decl switch
        {
            FunctionDecl => Nebra.Compiler.Annotations.AnnotationTargetKind.Function,
            LocalFunctionDecl => Nebra.Compiler.Annotations.AnnotationTargetKind.LocalFunction,
            LocalDecl => Nebra.Compiler.Annotations.AnnotationTargetKind.Variable,
            ClassDecl => Nebra.Compiler.Annotations.AnnotationTargetKind.Class,
            EnumDecl => Nebra.Compiler.Annotations.AnnotationTargetKind.Enum,
            InterfaceDecl => Nebra.Compiler.Annotations.AnnotationTargetKind.Interface,
            _ => Nebra.Compiler.Annotations.AnnotationTargetKind.Function
        };

        foreach (var ann in anns)
            ValidateAnnotation(ann, targetKind, metas, diag);

        if (decl is ClassDecl c)
        {
            foreach (var f in c.Fields) foreach (var a in f.Annotations)
                ValidateAnnotation(a, Nebra.Compiler.Annotations.AnnotationTargetKind.ClassField, metas, diag);
            foreach (var m in c.Methods) foreach (var a in m.Annotations)
                ValidateAnnotation(a, Nebra.Compiler.Annotations.AnnotationTargetKind.ClassMethod, metas, diag);
        }
        if (decl is InterfaceDecl i)
        {
            foreach (var f in i.Fields) foreach (var a in f.Annotations)
                ValidateAnnotation(a, Nebra.Compiler.Annotations.AnnotationTargetKind.InterfaceField, metas, diag);
            foreach (var m in i.Methods) foreach (var a in m.Annotations)
                ValidateAnnotation(a, Nebra.Compiler.Annotations.AnnotationTargetKind.InterfaceMethod, metas, diag);
        }
        if (decl is EnumDecl e)
            foreach (var m in e.Members) foreach (var a in m.Annotations)
                ValidateAnnotation(a, Nebra.Compiler.Annotations.AnnotationTargetKind.EnumMember, metas, diag);
    }

    private static void ValidateAnnotation(Annotation ann, Nebra.Compiler.Annotations.AnnotationTargetKind targetKind,
        Dictionary<string, Nebra.Compiler.Annotations.AnnotationMeta> metas, DiagnosticsBag diag)
    {
        if (!metas.TryGetValue(ann.Name.Name, out var meta))
        {
            diag.Report(ann.Span, NebraDiagnosticCode.ErrUnknownAnnotation, ann.Name.Name);
            return;
        }

        if (!meta.Targets.Contains(targetKind))
        {
            diag.Report(ann.Span, NebraDiagnosticCode.ErrAnnotationTargetMismatch, ann.Name.Name, targetKind.ToString());
            return;
        }

        var specByName = meta.Parameters.ToDictionary(p => p.Name);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var positionalIdx = 0;

        foreach (var arg in ann.Args)
        {
            string name;
            if (arg.Name != null)
            {
                name = arg.Name;
                if (!specByName.ContainsKey(name))
                {
                    diag.Report(arg.Span, NebraDiagnosticCode.ErrAnnotationArgUnknown, ann.Name.Name, name);
                    continue;
                }
            }
            else
            {
                if (positionalIdx >= meta.Parameters.Count)
                {
                    diag.Report(arg.Span, NebraDiagnosticCode.ErrAnnotationArgUnknown, ann.Name.Name,
                        $"<positional #{positionalIdx + 1}>");
                    continue;
                }
                name = meta.Parameters[positionalIdx].Name;
                positionalIdx++;
            }
            seen.Add(name);

            if (!Nebra.Compiler.Passes.ApplyAnnotationsPass.TryFoldLiteral(arg.Value, out var folded))
            {
                diag.Report(arg.Span, NebraDiagnosticCode.ErrAnnotationArgNotLiteral, ann.Name.Name, name);
                continue;
            }

            if (!Nebra.Compiler.Passes.ApplyAnnotationsPass.TypeMatchesSpec(specByName[name].TypeName, folded, out var actualLabel))
            {
                diag.Report(arg.Span, NebraDiagnosticCode.ErrAnnotationArgTypeMismatch,
                    ann.Name.Name, name, specByName[name].TypeName, actualLabel);
            }
        }

        foreach (var spec in meta.Parameters)
            if (spec.Required && !seen.Contains(spec.Name))
                diag.Report(ann.Span, NebraDiagnosticCode.ErrAnnotationArgMissing, ann.Name.Name, spec.Name);
    }

    private readonly Dictionary<string, (Nebra.Compiler.Annotations.AnnotationMeta meta, DateTime mtime)> _annotationMetaCache = new();

    /// <summary>
    /// Returns the lightweight meta (target + params) for every annotation
    /// definition discovered in <c>Config.Annotations</c>. Caches each entry
    /// by file path + mtime so repeated lookups during a typing session don't
    /// re-parse unchanged annotation files.
    /// </summary>
    public List<Nebra.Compiler.Annotations.AnnotationMeta> GetAnnotationMetas()
    {
        var result = new List<Nebra.Compiler.Annotations.AnnotationMeta>();
        if (_config.Annotations.Count == 0 || _rootPath == null) return result;

        foreach (var entry in _config.Annotations)
        {
            var fullPath = Path.IsPathRooted(entry) ? entry : Path.Combine(_rootPath, entry);
            if (Directory.Exists(fullPath))
            {
                foreach (var file in Directory.EnumerateFiles(fullPath, "*.neb", SearchOption.AllDirectories))
                {
                    var meta = LoadAnnotationMetaCached(file);
                    if (meta != null) result.Add(meta);
                }
            }
            else if (File.Exists(fullPath))
            {
                var meta = LoadAnnotationMetaCached(fullPath);
                if (meta != null) result.Add(meta);
            }
        }
        return result;
    }

    public Nebra.Compiler.Annotations.AnnotationMeta? GetAnnotationMeta(string annotationName)
    {
        foreach (var meta in GetAnnotationMetas())
            if (meta.Name == annotationName) return meta;
        return null;
    }

    private Nebra.Compiler.Annotations.AnnotationMeta? LoadAnnotationMetaCached(string filePath)
    {
        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(filePath); }
        catch { return null; }

        if (_annotationMetaCache.TryGetValue(filePath, out var entry) && entry.mtime == mtime)
            return entry.meta;

        var diag = new DiagnosticsBag();
        var alloc = new IDAlloc<NodeID>();
        var meta = Nebra.Compiler.Passes.ResolveAnnotationsPass.LoadMetaFromFile(filePath, _config, alloc, diag);
        if (meta != null) _annotationMetaCache[filePath] = (meta, mtime);
        return meta;
    }

    /// <summary>
    /// Returns a human-readable description of the annotation for hover info.
    /// Renders the meta as a fenced code block with target + parameter signature.
    /// </summary>
    public string? GetAnnotationInfo(string annotationName)
    {
        var meta = GetAnnotationMeta(annotationName);
        if (meta == null) return null;
        return FormatAnnotationSignature(meta);
    }

    public static string FormatAnnotationSignature(Nebra.Compiler.Annotations.AnnotationMeta meta)
    {
        var parts = meta.Parameters.Select(p =>
        {
            var label = $"{p.Name}: {p.TypeName}";
            if (!p.Required) label += " = " + (p.DefaultValue?.ToString() ?? "nil");
            return label;
        });
        var sig = $"@{meta.Name}({string.Join(", ", parts)})";
        return $"(annotation) {sig}\n-- target: {string.Join(" | ", meta.Targets)}\n-- source: {Path.GetFileName(meta.SourcePath)}";
    }

    public List<string> DiscoverAnnotationNames()
    {
        return GetAnnotationMetas().Select(m => m.Name).ToList();
    }

    /// <summary>
    /// Searches the workspace root for <c>.neb</c> / <c>.d.neb</c> files that export
    /// a top-level symbol with the given name and returns each as a tuple of
    /// (absolute path, module specifier suitable for <c>import { X } from "..."</c>).
    /// </summary>
    /// <summary>
    /// Analyses every project file whose text mentions <paramref name="symbolName"/> and returns the
    /// results, reusing the cached analysis for documents that are already open. Files that do not
    /// mention the name are skipped without parsing, which keeps a workspace-wide request from
    /// paying for the whole project.
    /// </summary>
    public List<AnalysisResult> AnalyzeFilesMentioning(string symbolName)
    {
        var results = new List<AnalysisResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cached in _results.Values)
        {
            if (!string.IsNullOrEmpty(cached.FilePath)) seen.Add(Path.GetFullPath(cached.FilePath));
            results.Add(cached);
        }

        if (_rootPath == null || !Directory.Exists(_rootPath)) return results;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_rootPath, "*.neb", SearchOption.AllDirectories); }
        catch { return results; }

        foreach (var file in files)
        {
            var fullPath = Path.GetFullPath(file);
            if (seen.Contains(fullPath)) continue;
            if (fullPath.Contains(Path.DirectorySeparatorChar + "nebra_modules" + Path.DirectorySeparatorChar)) continue;
            if (fullPath.Contains(Path.DirectorySeparatorChar + "out" + Path.DirectorySeparatorChar)) continue;

            string source;
            try { source = File.ReadAllText(fullPath); }
            catch { continue; }

            if (!source.Contains(symbolName, StringComparison.Ordinal)) continue;

            var uri = DocumentUri.FromFileSystemPath(fullPath).ToString();
            var (analysed, _, _) = BuildAnalysis(uri, source);
            if (analysed != null) results.Add(analysed);
        }

        return results;
    }

    public List<(string AbsPath, string ModulePath)> FindExportingFiles(string symbolName, string requesterFilePath)
    {
        var matches = new List<(string AbsPath, string ModulePath)>();
        if (_rootPath == null || !Directory.Exists(_rootPath)) return matches;

        var requesterFull = Path.GetFullPath(requesterFilePath);
        var requesterDir = Path.GetDirectoryName(requesterFull);
        if (requesterDir == null) return matches;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_rootPath, "*.neb", SearchOption.AllDirectories); }
        catch { return matches; }

        foreach (var file in files)
        {
            var fullPath = Path.GetFullPath(file);
            if (string.Equals(fullPath, requesterFull, StringComparison.OrdinalIgnoreCase)) continue;
            if (fullPath.Contains(Path.DirectorySeparatorChar + "nebra_modules" + Path.DirectorySeparatorChar)) continue;
            if (fullPath.Contains(Path.DirectorySeparatorChar + "out" + Path.DirectorySeparatorChar)) continue;

            string source;
            try { source = File.ReadAllText(file); }
            catch { continue; }

            if (!QuickHasExport(source, symbolName)) continue;

            var rel = Path.GetRelativePath(requesterDir, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase))
                rel = rel[..^6];
            else if (rel.EndsWith(".neb", StringComparison.OrdinalIgnoreCase))
                rel = rel[..^4];
            if (!rel.StartsWith("./") && !rel.StartsWith("../"))
                rel = "./" + rel;

            matches.Add((fullPath, rel));
        }

        return matches;
    }

    /// <summary>
    /// Cheap textual check: does the source contain a top-level <c>export</c> for
    /// <paramref name="name"/>? Avoids paying for a full parse during code-action
    /// resolution. False positives are tolerated — a wrong suggestion only wastes
    /// a click — but false negatives would hide a legitimate import path.
    /// </summary>
    private static bool QuickHasExport(string source, string name)
    {
        var patterns = new[]
        {
            $"export function {name}",
            $"export async function {name}",
            $"export class {name}",
            $"export interface {name}",
            $"export enum {name}",
            $"export local {name}",
            $"export const {name}",
            $"export mut {name}",
        };
        foreach (var p in patterns)
            if (source.Contains(p, StringComparison.Ordinal))
                return true;

        var idx = source.IndexOf("export local", StringComparison.Ordinal);
        while (idx >= 0)
        {
            var slice = source[idx..];
            var nl = slice.IndexOf('\n');
            if (nl < 0) nl = slice.Length;
            var lineSlice = slice[..nl];
            if (lineSlice.Contains(name, StringComparison.Ordinal)) return true;
            idx = source.IndexOf("export local", idx + 1, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// Compiles a single file using the standard compiler pipeline so that the
    /// 'Compile this file' code action reports the same diagnostics a CLI build
    /// would produce. Returns true on success along with a human-readable summary.
    /// </summary>
    public bool CompileFile(string filePath, out string message)
    {
        try
        {
            var compiler = new NebraCompiler { Config = _config.Clone() };
            compiler.AddSource(filePath);
            var ok = compiler.Compile();
            if (!ok)
            {
                var errs = compiler.Diagnostics.Diagnostics.Count(d => d.Level == DiagnosticLevel.Error);
                message = $"Compile failed ({errs} error{(errs == 1 ? "" : "s")}). See Problems panel.";
                return false;
            }
            message = $"Compiled '{Path.GetFileName(filePath)}'.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Compile failed: {ex.Message}";
            return false;
        }
    }

    public void ShowMessage(MessageType type, string message)
    {
        _server?.Window.ShowMessage(new ShowMessageParams { Type = type, Message = message });
    }

    public List<Symbol> CollectVisibleSymbols(AnalysisResult result, ScopeID scopeId)
    {
        var symbols = new List<Symbol>();
        var seen = new HashSet<string>();
        var currentScope = scopeId;

        while (currentScope != ScopeID.Invalid)
        {
            foreach (var (id, sym) in result.Syms.ByID)
            {
                if (sym.Owner == currentScope && seen.Add(sym.Name))
                    symbols.Add(sym);
            }

            if (!result.Scopes.ParentScope(currentScope, out var parent))
                break;
            currentScope = parent;
        }

        return symbols;
    }
}
