using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
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
using OmniSharp.Extensions.LanguageServer.Protocol.Server.WorkDone;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;

using NebraDiagnostic = Nebra.Diagnostics.Diagnostic;
using NebraDiagnosticCode = Nebra.Diagnostics.DiagnosticCode;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;

namespace Nebra.LPS;

/// <summary>
/// One Nebra project - a directory with its own <c>nebra.toml</c>, or a workspace folder without
/// one - with its open documents, its snapshots and the diagnostics it publishes.
/// <see cref="NebraWorkspace"/> routes every request to the project a file belongs to.
/// </summary>
public sealed class NebraProject(Func<ServerSettings>? settings = null)
{
    private readonly Func<ServerSettings> _settings = settings ?? (() => new ServerSettings());

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private sealed record OpenDocument(string Uri, string Text);

    private readonly ConcurrentDictionary<string, OpenDocument> _openDocuments = new(PathComparer);
    private readonly ConcurrentDictionary<string, string> _lastParsed = new(PathComparer);
    private readonly ConcurrentDictionary<string, byte> _touched = new(PathComparer);
    private readonly Dictionary<string, string> _published = new(PathComparer);
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly Dictionary<string, (long Version, Task<WorkspaceSnapshot> Build)> _documentSnapshots = new(PathComparer);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private CancellationTokenSource? _pendingDiagnostics;
    private CancellationTokenSource _superseded = new();
    private WorkspaceSnapshot? _snapshot;
    private bool _retired;
    private long _version;

    private ILanguageServer? _server;
    private Config _config = new();

    private string? _rootPath;

    /// <summary>The directory this project lives in; null for files that belong to no project.</summary>
    public string? RootPath => _rootPath;

    /// <summary>The documents the editor has open in this project, by URI and text.</summary>
    public IReadOnlyList<(string Uri, string Text)> OpenDocuments =>
        _openDocuments.Values.Select(document => (document.Uri, document.Text)).ToList();

    /// <summary>
    /// The project roots nested inside this one, whose files belong to them instead. Set by the
    /// workspace whenever it discovers projects.
    /// </summary>
    public IReadOnlyList<string> NestedRoots { get; set; } = [];

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
            _config = loaded ?? new Config { ProjectRoot = Path.GetFullPath(rootPath) };
        }
    }

    public void SetServer(ILanguageServer server)
    {
        _server = server;
    }

    /// <summary>
    /// Publishes the first round of diagnostics. Called once the client has completed the
    /// initialization handshake: a notification sent before that may be dropped, and a file
    /// recorded as published would not be sent again until its diagnostics change.
    /// </summary>
    public void Start()
    {
        ScheduleDiagnostics();
    }

    /// <summary>
    /// The most recent workspace snapshot, which may lag behind the latest edits; null before the
    /// first one is built. For features that can do with a slightly stale view and must not wait.
    /// </summary>
    public WorkspaceSnapshot? LatestSnapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Returns the snapshot reflecting every change received so far, compiling it if the latest
    /// one is out of date. Concurrent callers share one compilation. A change arriving while it
    /// runs cancels it and the build starts over with that change, except for the first build,
    /// which always completes so that features working from the latest snapshot have one.
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
            while (true)
            {
                var superseded = Volatile.Read(ref _superseded).Token;
                current = Volatile.Read(ref _snapshot);
                var version = Interlocked.Read(ref _version);
                if (current != null && current.Version == version)
                {
                    return current;
                }

                try
                {
                    var built = current == null
                        ? await BuildFirstSnapshotAsync(version).ConfigureAwait(false)
                        : await Task.Run(() => BuildSnapshot(version, superseded), CancellationToken.None).ConfigureAwait(false);
                    Volatile.Write(ref _snapshot, built);
                    return built;
                }
                catch (OperationCanceledException) when (superseded.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        finally
        {
            _buildGate.Release();
        }
    }

    /// <summary>
    /// Builds the project's first snapshot, which loads every library and compiles every file, and
    /// shows the editor how far it got.
    /// </summary>
    private async Task<WorkspaceSnapshot> BuildFirstSnapshotAsync(long version)
    {
        using var progress = await BeginProgressAsync($"Loading {ProjectName}").ConfigureAwait(false);

        return await Task.Run(() => BuildSnapshot(version, CancellationToken.None, (pass, index, count) =>
        {
            progress?.OnNext(new WorkDoneProgressReport
            {
                Message = $"Loading {ProjectName}: {Describe(pass)}",
                Percentage = index * 100 / count
            });
        }), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IWorkDoneObserver?> BeginProgressAsync(string message)
    {
        if (_server == null || !_server.WorkDoneManager.IsSupported) return null;

        try
        {
            return await _server.WorkDoneManager.Create(new WorkDoneProgressBegin
            {
                Title = "Nebra",
                Message = message,
                Percentage = 0
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _server.Window.LogWarning($"nebra: the editor refused a progress report: {exception.Message}");
            return null;
        }
    }

    private string ProjectName
    {
        get
        {
            if (_rootPath == null) return "files";
            return _config.Name ?? Path.GetFileName(_rootPath.TrimEnd(Path.DirectorySeparatorChar));
        }
    }

    /// <summary>Turns a pass name such as <c>ResolveLibs</c> into <c>resolve libs</c>.</summary>
    private static string Describe(string pass)
    {
        return Regex.Replace(pass, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
    }

    /// <summary>
    /// The view of the document at <paramref name="uri"/> in the current snapshot, or null when it
    /// is not part of the compilation.
    /// </summary>
    public async Task<AnalysisResult?> GetWorkspaceResultAsync(string uri, CancellationToken cancellationToken = default)
    {
        var path = PathOf(uri);
        if (path == null) return null;

        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.GetResult(path);
    }

    /// <summary>
    /// The view of the document at <paramref name="uri"/> for a request about a position in it.
    /// Taken from the workspace snapshot when that is current, and otherwise from a compilation of
    /// just this document and what it imports, which costs a fraction of the whole workspace.
    /// Features that reach across files use <see cref="GetWorkspaceResultAsync"/> instead.
    /// </summary>
    public async Task<AnalysisResult?> GetResultAsync(string uri, CancellationToken cancellationToken = default)
    {
        var path = PathOf(uri);
        if (path == null) return null;
        if (!_openDocuments.ContainsKey(path) && !File.Exists(path)) return null;

        var version = Interlocked.Read(ref _version);
        var workspace = Volatile.Read(ref _snapshot);
        var fromWorkspace = workspace != null && workspace.Version == version ? workspace.GetResult(path) : null;
        if (fromWorkspace != null) return fromWorkspace;

        var snapshot = await GetDocumentSnapshotAsync(path, version).WaitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.GetResult(path);
    }

    /// <summary>
    /// The single-document snapshot of <paramref name="path"/> at <paramref name="version"/>,
    /// shared by every request that asks for it before the next edit.
    /// </summary>
    private Task<WorkspaceSnapshot> GetDocumentSnapshotAsync(string path, long version)
    {
        lock (_documentSnapshots)
        {
            if (_documentSnapshots.TryGetValue(path, out var entry) && entry.Version == version)
            {
                return entry.Build;
            }

            var build = Task.Run(() => BuildDocumentSnapshot(path, version));
            _documentSnapshots[path] = (version, build);
            return build;
        }
    }

    private WorkspaceSnapshot BuildDocumentSnapshot(string path, long version)
    {
        var config = CreateBuildConfig();
        config.AnnotationFile = IsAnnotationFile(path);
        var document = _openDocuments.TryGetValue(path, out var open)
            ? new SourceDocument(path, open.Text, open.Uri)
            : new SourceDocument(path, File.ReadAllText(path));

        var snapshot = WorkspaceSnapshot.Build(version, config, [document], _lastParsed, isWorkspace: false);
        RememberParsed(snapshot, [document]);
        return snapshot;
    }

    /// <summary>
    /// A copy of the project configuration whose source overrides carry every open document, so a
    /// build reads the editor's unsaved text even for files it only loads through an import.
    /// </summary>
    private Config CreateBuildConfig()
    {
        var config = _config.Clone();
        config.SourceOverrides = _openDocuments.ToDictionary(entry => entry.Key, entry => entry.Value.Text, PathComparer);
        return config;
    }

    private void RememberParsed(WorkspaceSnapshot snapshot, IEnumerable<SourceDocument> documents)
    {
        foreach (var document in documents)
        {
            if (!snapshot.IsStale(document.Path) && snapshot.GetResult(document.Path) != null)
            {
                _lastParsed[document.Path] = document.Text;
            }
        }
    }

    public void OnDocumentOpened(string uri, string text) => UpdateDocument(uri, text);

    public void OnDocumentChanged(string uri, string text) => UpdateDocument(uri, text);

    /// <summary>
    /// Handles files changed outside the editor. A changed <c>nebra.toml</c> reloads the
    /// configuration; any other change only invalidates the snapshot, since every build reads the
    /// files it does not have open from disk anyway.
    /// </summary>
    public void OnFilesChanged(IEnumerable<string> uris)
    {
        var paths = uris.Select(PathOf).OfType<string>().ToList();
        if (paths.Count == 0) return;

        if (paths.Any(path => Path.GetFileName(path).Equals("nebra.toml", StringComparison.OrdinalIgnoreCase)))
        {
            Initialize(_rootPath);
        }

        foreach (var path in paths.Where(path => !_openDocuments.ContainsKey(path)))
        {
            _lastParsed.TryRemove(path, out _);
            _touched[path] = 0;
        }

        Advance();
        _resolveCache.Clear();
        ScheduleDiagnostics();
    }

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

    /// <summary>
    /// Forgets single-document snapshots a workspace snapshot has caught up with; requests at that
    /// version or later are answered from the workspace.
    /// </summary>
    private void DropDocumentSnapshotsBefore(long version)
    {
        lock (_documentSnapshots)
        {
            foreach (var path in _documentSnapshots.Where(entry => entry.Value.Version <= version).Select(entry => entry.Key).ToList())
            {
                _documentSnapshots.Remove(path);
            }
        }
    }

    /// <summary>Moves to the next version and cancels the build of the one before it.</summary>
    private void Advance()
    {
        Interlocked.Increment(ref _version);
        Interlocked.Exchange(ref _superseded, new CancellationTokenSource()).Cancel();
    }

    private void Invalidate(string path)
    {
        Advance();
        _resolveCache.Clear();
        _touched[path] = 0;
        ScheduleDiagnostics();
    }

    private static string? PathOf(string uri)
    {
        var path = DocumentUri.GetFileSystemPath(DocumentUri.Parse(uri));
        return path == null ? null : Path.GetFullPath(path);
    }

    private WorkspaceSnapshot BuildSnapshot(long version, CancellationToken cancellationToken,
        Action<string, int, int>? passStarting = null)
    {
        var config = CreateBuildConfig();
        var documents = new Dictionary<string, SourceDocument>(PathComparer);

        foreach (var path in EnumerateProjectSources(config))
        {
            documents[path] = new SourceDocument(path, File.ReadAllText(path));
        }

        foreach (var (path, open) in _openDocuments)
        {
            if (IsAnnotationFile(path)) continue;
            if (!IsCompiledAsSource(path, config) && documents.Count > 0 && !IsOutsideProject(path)) continue;
            documents[path] = new SourceDocument(path, open.Text, open.Uri);
        }

        var snapshot = WorkspaceSnapshot.Build(version, config, documents.Values.ToList(), _lastParsed,
            cancellationToken: cancellationToken, passStarting: passStarting);
        RememberParsed(snapshot, documents.Values);
        DropDocumentSnapshotsBefore(version);

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
            if (IsCompiledAsSource(full, config) && !IsBuildOutput(full, config) && !IsInNestedProject(full))
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

    private bool IsInNestedProject(string path)
    {
        return NestedRoots.Any(root => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Stops publishing and withdraws every diagnostic this project published, for when it stops
    /// existing and its files move to another project.
    /// </summary>
    public async Task RetireAsync()
    {
        Interlocked.Exchange(ref _pendingDiagnostics, null)?.Cancel();
        if (_server == null) return;

        await _publishGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _retired = true;
            foreach (var path in _published.Keys.ToList())
            {
                _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
                {
                    Uri = DocumentUri.FromFileSystemPath(path),
                    Diagnostics = new Container<LspDiagnostic>()
                });
            }

            _published.Clear();
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is an annotation definition file: one beneath an
    /// annotation directory of this project, or beneath one of a dependency that reads like an
    /// annotation file. Such a file is checked on its own, in the annotation environment, and
    /// never compiled as a source of the project.
    /// </summary>
    public bool IsAnnotationFile(string path)
    {
        if (_rootPath == null) return false;
        if (!path.EndsWith(".neb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase)) return false;

        var ownRoots = _config.Annotations.Select(entry => Path.GetFullPath(Path.IsPathRooted(entry) ? entry : Path.Combine(_rootPath, entry)));
        if (ownRoots.Any(root => PathComparer.Equals(root, path) || IsBeneathDirectory(path, root))) return true;

        var dependencyRoots = InstalledPackages.Discover(_rootPath)
            .SelectMany(package => package.AnnotationRoots.Count > 0 ? package.AnnotationRoots : [package.RootPath]);
        if (!dependencyRoots.Any(root => IsBeneathDirectory(path, Path.GetFullPath(root)))) return false;

        var text = _openDocuments.TryGetValue(path, out var open) ? open.Text : File.Exists(path) ? File.ReadAllText(path) : "";
        return Nebra.Compiler.Passes.ResolveAnnotationsPass.LooksLikeAnnotationSource(text);
    }

    private static bool IsBeneathDirectory(string path, string directory)
    {
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsOutsideProject(string path)
    {
        if (_rootPath == null) return true;
        var root = Path.GetFullPath(_rootPath) + Path.DirectorySeparatorChar;
        return !path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Publishes diagnostics once edits have paused for the configured delay; a newer
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
            await Task.Delay(_settings().DiagnosticsDelay, cancellationToken).ConfigureAwait(false);
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
            if (_retired) return;

            var touched = _touched.Keys.ToList();
            foreach (var path in touched) _touched.TryRemove(path, out _);

            var paths = new HashSet<string>(snapshot.SourcePaths, PathComparer);
            paths.UnionWith(_openDocuments.Keys);
            paths.UnionWith(_published.Keys);
            paths.UnionWith(touched);

            foreach (var path in paths)
            {
                if (IsInNestedProject(path))
                {
                    _published.Remove(path);
                    continue;
                }

                var inWorkspace = snapshot.SourcePaths.Contains(path, PathComparer) || _openDocuments.ContainsKey(path);
                var source = inWorkspace && IsAnnotationFile(path)
                    ? await GetDocumentSnapshotAsync(path, snapshot.Version).ConfigureAwait(false)
                    : snapshot;
                var diagnostics = inWorkspace ? source.DiagnosticsFor(path).Select(ToLspDiagnostic).ToList() : [];
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

    public static Dictionary<string, ExportInfo> CollectExports(AnalysisResult result, string? targetModuleName = null)
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
            new Position(Math.Max(0, span.EndLn - 1), Math.Max(0, span.EndCol - 1))
        );
    }

    public static string FormatType(TypeTable types, TypID typId)
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
    public static string FormatType(TypeTable types, IR.Type typ)
    {
        switch (typ)
        {
            case EnumType et: return et.Name;
            case ClassType ct: return IR.Type.WithTypeArguments(ct.Name, ct.ShownArguments, t => FormatType(types, t));
            case InterfaceType it: return IR.Type.WithTypeArguments(it.Name, it.ShownArguments, t => FormatType(types, t));
            case FunctionType ft: return FormatFunctionType(types, ft);
            case UnionType ut: return string.Join(" | ", ut.Types.Select(t => IR.Type.Grouped(t, FormatType(types, t))));
            case TableArrayType ta: return IR.Type.Grouped(ta.ElementType, FormatType(types, ta.ElementType)) + "[]";
            case TableMapType tm: return $"{{ [{FormatType(types, tm.KeyType)}]: {FormatType(types, tm.ValueType)} }}";
            case TupleType tt: return "(" + string.Join(", ", tt.Fields.Select(f =>
                f.Name == null ? FormatType(types, f.Type) : $"{f.Name.Name}: {FormatType(types, f.Type)}")) + ")";
            case StructType st: return "{ " + string.Join(", ", st.Fields.Select(f =>
                $"{IR.Type.FieldLabel(f.Name.Name)}: {FormatType(types, f.Type)}")) + " }";
            case TypeParameterType typeParameter: return typeParameter.Name;
            case LiteralType or KeyOfType or IndexedAccessType or UtilityType: return IR.Type.FormatComputed(typ, t => FormatType(types, t))!;
            case VariadicType variadic: return "..." + IR.Type.Grouped(variadic.ElementType, FormatType(types, variadic.ElementType));
            default: return PrettifyTypeKey(typ.Key);
        }
    }

    private static string FormatFunctionType(TypeTable types, FunctionType ft)
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
            sb.Append("    ").Append(KeywordOf(it, name)).Append(' ').Append(name).Append('(')
                .Append(FormatFunctionParamList(result, method))
                .Append("): ")
                .AppendLine(FormatType(result.Types, method.ReturnType));

        sb.Append("end");
        return sb.ToString();
    }

    private static string KeywordOf(InterfaceType it, string member)
    {
        return it.MethodNodes.TryGetValue(member, out var node) && node.IsMethod ? "method" : "function";
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
                return result.FileMap.TryGetValue(sym.DeclaringNode, out var declFile)
                    ? new TypeDeclLocation(declFile, declNode.Span)
                    : null;
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

                var files = PathComparer.Equals(rootFull, Path.GetFullPath(modulesDir))
                    ? ProjectFiles.PackageFiles(rootFull, "*.neb")
                    : ProjectFiles.Files(rootFull, "*.neb");

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
    /// in other files through an import of it. <paramref name="originResult"/> has to come from
    /// <see cref="GetWorkspaceResultAsync"/>; a single-document snapshot only knows its own file.
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
            var candidates = Path.GetFileName(dir) == ProjectFiles.ModulesDirectory
                ? ProjectFiles.PackageFiles(dir, "*.d.neb").ToArray()
                : ProjectFiles.Files(dir, "*.d.neb").ToArray();

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

    /// <summary>
    /// Searches the workspace root for <c>.neb</c> / <c>.d.neb</c> files that export
    /// a top-level symbol with the given name and returns each as a tuple of
    /// (absolute path, module specifier suitable for <c>import { X } from "..."</c>).
    /// </summary>
    /// <summary>
    /// The source files of <paramref name="origin"/>'s snapshot whose text mentions
    /// <paramref name="symbolName"/>, which narrows a workspace-wide edit to the files it can touch.
    /// <paramref name="origin"/> has to come from <see cref="GetWorkspaceResultAsync"/>.
    /// </summary>
    public static List<AnalysisResult> FilesMentioning(AnalysisResult origin, string symbolName)
    {
        return origin.Snapshot.SourceResults()
            .Where(result => result.SourceText.Contains(symbolName, StringComparison.Ordinal))
            .ToList();
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

    /// <summary>
    /// Whether <paramref name="symbol"/> is declared in <paramref name="scope"/>; the package's
    /// root scope also holds what the shared libraries declare at their top level.
    /// </summary>
    private static bool IsDeclaredIn(AnalysisResult result, Symbol symbol, ScopeID scope)
    {
        if (symbol.Owner == scope) return true;
        return scope == result.Package.Root && result.Scopes.Library is { } library && symbol.Owner == library.Root;
    }

    public List<Symbol> CollectVisibleSymbols(AnalysisResult result, ScopeID scopeId)
    {
        var symbols = new List<Symbol>();
        var seen = new HashSet<string>();
        var currentScope = scopeId;

        while (currentScope != ScopeID.Invalid)
        {
            foreach (var (id, sym) in result.Syms.Visible)
            {
                if (IsDeclaredIn(result, sym, currentScope) && seen.Add(sym.Name))
                    symbols.Add(sym);
            }

            if (!result.Scopes.ParentScope(currentScope, out var parent))
                break;
            currentScope = parent;
        }

        return symbols;
    }
}
