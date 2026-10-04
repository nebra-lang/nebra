using System.Collections.Concurrent;
using System.Text;
using Antlr4.Runtime;
using Nebra.Compiler;
using Nebra.Compiler.Passes;
using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;
using Nebra.PackageManager;
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
    /// <summary>
    /// How long diagnostics wait for typing to pause before the workspace is recompiled for them.
    /// Requests do not wait: they compile the latest state on demand.
    /// </summary>
    private static readonly TimeSpan DiagnosticsDelay = TimeSpan.FromMilliseconds(120);

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private sealed record OpenDocument(string Uri, string Text);

    private readonly ConcurrentDictionary<string, OpenDocument> _openDocuments = new(PathComparer);
    private readonly ConcurrentDictionary<string, string> _lastParsed = new(PathComparer);
    private readonly ConcurrentDictionary<string, byte> _touched = new(PathComparer);
    private readonly Dictionary<string, string> _published = new(PathComparer);
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private CancellationTokenSource? _pendingDiagnostics;
    private WorkspaceSnapshot? _snapshot;
    private long _version;

    private ILanguageServerFacade? _server;
    private Config _config = new();

    private string? _rootPath;

    /// <summary>
    /// Cache for <see cref="ResolveImportPath"/>. Module resolution itself
    /// can do a recursive directory walk (looking for matching
    /// <c>declare module</c> headers), which is too expensive to repeat on
    /// every keystroke. Keyed by <c>importerDir|moduleName</c>; cleared
    /// whenever a document changes.
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
            else _config.ProjectRoot = Path.GetFullPath(rootPath);
        }
    }

    public void SetServer(ILanguageServerFacade server)
    {
        _server = server;
        ScheduleDiagnostics();
    }

    /// <summary>
    /// Returns the snapshot reflecting every change received so far, compiling it if the latest
    /// one is out of date. Concurrent callers share one compilation.
    /// </summary>
    public async Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var current = Volatile.Read(ref _snapshot);
        if (current != null && current.Version == Interlocked.Read(ref _version))
        {
            return current;
        }

        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = Volatile.Read(ref _snapshot);
            var version = Interlocked.Read(ref _version);
            if (current != null && current.Version == version)
            {
                return current;
            }

            var built = await Task.Run(() => BuildSnapshot(version), CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, built);
            return built;
        }
        finally
        {
            _buildGate.Release();
        }
    }

    /// <summary>
    /// The view of the document at <paramref name="uri"/> in the current snapshot, or null when it
    /// is not part of the compilation.
    /// </summary>
    public async Task<AnalysisResult?> GetResultAsync(string uri, CancellationToken cancellationToken = default)
    {
        var path = PathOf(uri);
        if (path == null) return null;

        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.GetResult(path);
    }

    public void OnDocumentOpened(string uri, string text) => UpdateDocument(uri, text);

    public void OnDocumentChanged(string uri, string text) => UpdateDocument(uri, text);

    public void OnDocumentClosed(string uri)
    {
        var path = PathOf(uri);
        if (path == null) return;

        _openDocuments.TryRemove(path, out _);
        Invalidate(path);
    }

    private void UpdateDocument(string uri, string text)
    {
        var path = PathOf(uri);
        if (path == null) return;

        _openDocuments[path] = new OpenDocument(uri, text);
        Invalidate(path);
    }

    private void Invalidate(string path)
    {
        Interlocked.Increment(ref _version);
        _resolveCache.Clear();
        _touched[path] = 0;
        ScheduleDiagnostics();
    }

    private static string? PathOf(string uri)
    {
        var path = DocumentUri.GetFileSystemPath(DocumentUri.Parse(uri));
        return path == null ? null : Path.GetFullPath(path);
    }

    private WorkspaceSnapshot BuildSnapshot(long version)
    {
        var config = _config.Clone();
        var documents = new Dictionary<string, SourceDocument>(PathComparer);

        foreach (var path in EnumerateProjectSources(config))
        {
            documents[path] = new SourceDocument(path, File.ReadAllText(path));
        }

        foreach (var (path, open) in _openDocuments)
        {
            if (!IsCompiledAsSource(path, config) && documents.Count > 0 && !IsOutsideProject(path)) continue;
            documents[path] = new SourceDocument(path, open.Text, open.Uri);
        }

        var snapshot = WorkspaceSnapshot.Build(version, config, documents.Values.ToList(), _lastParsed);

        foreach (var (path, document) in documents)
        {
            if (!snapshot.IsStale(path) && snapshot.GetResult(path) != null)
            {
                _lastParsed[path] = document.Text;
            }
        }

        if (snapshot.Failure != null)
        {
            _server?.Window.LogError($"nebra: analysis stopped early: {snapshot.Failure}");
        }

        return snapshot;
    }

    /// <summary>
    /// The project's own source files, exactly the set <c>nebra build</c> compiles: every
    /// <c>.neb</c> file under the source directory, declaration files only for a types-only
    /// project, and nothing inside <c>nebra_modules/</c> or the output directory.
    /// </summary>
    private IEnumerable<string> EnumerateProjectSources(Config config)
    {
        if (_rootPath == null) yield break;

        var sourceRoot = Path.GetFullPath(Path.Combine(config.ProjectRoot, config.Source));
        if (!Directory.Exists(sourceRoot)) yield break;

        foreach (var file in InstalledPackages.EnumerateFilesSafely(sourceRoot, "*.neb"))
        {
            var full = Path.GetFullPath(file);
            if (IsCompiledAsSource(full, config) && !IsBuildOutput(full, config))
            {
                yield return full;
            }
        }
    }

    private static bool IsCompiledAsSource(string path, Config config)
    {
        return config.TypesOnly || !path.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBuildOutput(string path, Config config)
    {
        var output = Path.GetFullPath(Path.Combine(config.ProjectRoot, config.Output)) + Path.DirectorySeparatorChar;
        return path.StartsWith(output, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsOutsideProject(string path)
    {
        if (_rootPath == null) return true;
        var root = Path.GetFullPath(_rootPath) + Path.DirectorySeparatorChar;
        return !path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Publishes diagnostics once edits have paused for <see cref="DiagnosticsDelay"/>; a newer
    /// edit cancels the wait so a burst of keystrokes compiles once.
    /// </summary>
    private void ScheduleDiagnostics()
    {
        var pending = new CancellationTokenSource();
        Interlocked.Exchange(ref _pendingDiagnostics, pending)?.Cancel();
        _ = PublishAfterDelayAsync(pending.Token);
    }

    private async Task PublishAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DiagnosticsDelay, cancellationToken).ConfigureAwait(false);
            var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await PublishDiagnosticsAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _server?.Window.LogError($"nebra: publishing diagnostics failed: {exception}");
        }
    }

    /// <summary>
    /// Sends diagnostics for every source file and open document whose diagnostics changed, and
    /// for every document edited since the last round even if they did not, since an editor
    /// waits for a response to its own change. Files that dropped out of the workspace are cleared.
    /// </summary>
    private async Task PublishDiagnosticsAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_server == null) return;

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var touched = _touched.Keys.ToList();
            foreach (var path in touched) _touched.TryRemove(path, out _);

            var paths = new HashSet<string>(snapshot.SourcePaths, PathComparer);
            paths.UnionWith(_openDocuments.Keys);
            paths.UnionWith(_published.Keys);
            paths.UnionWith(touched);

            foreach (var path in paths)
            {
                var inWorkspace = snapshot.SourcePaths.Contains(path, PathComparer) || _openDocuments.ContainsKey(path);
                var diagnostics = inWorkspace ? snapshot.DiagnosticsFor(path).Select(ToLspDiagnostic).ToList() : [];
                var fingerprint = string.Join("\n", diagnostics.Select(d => $"{d.Range}|{d.Code}|{d.Message}"));

                var changed = !_published.TryGetValue(path, out var previous) || previous != fingerprint;
                if (!changed && !touched.Contains(path, PathComparer)) continue;

                if (inWorkspace) _published[path] = fingerprint;
                else _published.Remove(path);

                var uri = _openDocuments.TryGetValue(path, out var open) ? open.Uri : DocumentUri.FromFileSystemPath(path).ToString();
                _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
                {
                    Uri = DocumentUri.Parse(uri),
                    Diagnostics = new Container<LspDiagnostic>(diagnostics)
                });
            }
        }
        finally
        {
            _publishGate.Release();
        }
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

        foreach (var other in result.Snapshot.SourceResults())
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

    /// <summary>
    /// Every use of the symbol <paramref name="targetSym"/> across the workspace, including uses
    /// in other files through an import of it.
    /// </summary>
    public List<Location> FindUsages(SymID targetSym, AnalysisResult originResult)
    {
        var snapshot = originResult.Snapshot;
        var origin = snapshot.Origin(targetSym);
        var locations = new List<Location>();

        foreach (var other in snapshot.SourceResults())
        {
            foreach (var nameRef in NodeFinder.CollectAllNameRefs(other.Hir))
            {
                if (nameRef.Sym == SymID.Invalid || snapshot.Origin(nameRef.Sym) != origin) continue;
                locations.Add(new Location
                {
                    Uri = DocumentUri.Parse(other.Uri),
                    Range = SpanToRange(nameRef.Span)
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

        var imported = result.Snapshot.GetResult(resolvedPath);
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
    /// The source files of <paramref name="origin"/>'s snapshot whose text mentions
    /// <paramref name="symbolName"/>, which narrows a workspace-wide edit to the files it can touch.
    /// </summary>
    public static List<AnalysisResult> FilesMentioning(AnalysisResult origin, string symbolName)
    {
        return origin.Snapshot.SourceResults()
            .Where(result => result.SourceText.Contains(symbolName, StringComparison.Ordinal))
            .ToList();
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
