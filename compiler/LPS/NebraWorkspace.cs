using Nebra.Compiler.Annotations;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;

using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nebra.LPS;

/// <summary>
/// Everything the editor has opened: one <see cref="NebraProject"/> per workspace folder and per
/// <c>nebra.toml</c> found beneath one, plus a project for files outside all of them. A file
/// belongs to the deepest project containing it, and every request about a file goes to that
/// project. Creating or deleting a <c>nebra.toml</c> re-discovers the projects and moves open
/// documents to the project now responsible for them.
/// </summary>
public sealed class NebraWorkspace
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly object _gate = new();
    private readonly List<string> _folders = [];
    private Dictionary<string, NebraProject> _projects = new(PathComparer);
    private NebraProject? _looseFiles;
    private ILanguageServer? _server;
    private bool _started;
    private ServerSettings _settings = new();

    /// <summary>The settings the editor sent last; defaults until it sends any.</summary>
    public ServerSettings Settings => Volatile.Read(ref _settings);

    public void UpdateSettings(ServerSettings settings)
    {
        Volatile.Write(ref _settings, settings);
    }

    /// <summary>Starts with <paramref name="folders"/>, or with <paramref name="rootPath"/> when the editor sent no folders.</summary>
    public void Initialize(string? rootPath, IEnumerable<string>? folders = null)
    {
        var roots = (folders ?? []).Select(Path.GetFullPath).ToList();
        if (roots.Count == 0 && rootPath != null) roots.Add(Path.GetFullPath(rootPath));

        lock (_gate)
        {
            _folders.Clear();
            _folders.AddRange(roots.Distinct(PathComparer));
        }

        Rediscover();
    }

    public void SetServer(ILanguageServer server)
    {
        _server = server;
        foreach (var project in AllProjects()) project.SetServer(server);
    }

    public void Start()
    {
        _started = true;
        foreach (var project in AllProjects()) project.Start();
    }

    public void AddFolder(string path)
    {
        lock (_gate)
        {
            if (!_folders.Contains(path, PathComparer)) _folders.Add(Path.GetFullPath(path));
        }

        Rediscover();
    }

    public void RemoveFolder(string path)
    {
        lock (_gate)
        {
            _folders.RemoveAll(folder => PathComparer.Equals(folder, Path.GetFullPath(path)));
        }

        Rediscover();
    }

    /// <summary>Every project, including the one for files outside all of them once it exists.</summary>
    public IReadOnlyList<NebraProject> AllProjects()
    {
        lock (_gate)
        {
            return _projects.Values.Concat(_looseFiles == null ? [] : [_looseFiles]).ToList();
        }
    }

    /// <summary>The project the file at <paramref name="uriOrPath"/> belongs to.</summary>
    public NebraProject ProjectFor(string uriOrPath)
    {
        var path = ToPath(uriOrPath);

        lock (_gate)
        {
            var owner = path == null
                ? null
                : _projects.Values
                    .Where(project => IsBeneath(path, project.RootPath!))
                    .OrderByDescending(project => project.RootPath!.Length)
                    .FirstOrDefault();

            return owner ?? LooseFiles();
        }
    }

    public Task<AnalysisResult?> GetResultAsync(string uri, CancellationToken cancellationToken = default)
        => ProjectFor(uri).GetResultAsync(uri, cancellationToken);

    public Task<AnalysisResult?> GetWorkspaceResultAsync(string uri, CancellationToken cancellationToken = default)
        => ProjectFor(uri).GetWorkspaceResultAsync(uri, cancellationToken);

    public Task<WorkspaceSnapshot> GetSnapshotAsync(string uri, CancellationToken cancellationToken = default)
        => ProjectFor(uri).GetSnapshotAsync(cancellationToken);

    public WorkspaceSnapshot? LatestSnapshotFor(string path) => ProjectFor(path).LatestSnapshot;

    public void OnDocumentOpened(string uri, string text) => ProjectFor(uri).OnDocumentOpened(uri, text);

    public void OnDocumentChanged(string uri, string text) => ProjectFor(uri).OnDocumentChanged(uri, text);

    public void OnDocumentClosed(string uri) => ProjectFor(uri).OnDocumentClosed(uri);

    /// <summary>
    /// Hands files changed outside the editor to their projects, after re-discovering the projects
    /// when a <c>nebra.toml</c> appeared or disappeared.
    /// </summary>
    public void OnFilesChanged(IEnumerable<(string Uri, bool Exists)> changes)
    {
        var changed = changes.ToList();
        var configs = changed.Where(change => Path.GetFileName(ToPath(change.Uri) ?? "").Equals("nebra.toml", StringComparison.OrdinalIgnoreCase));
        if (configs.Any(change => change.Exists != IsKnownRoot(Path.GetDirectoryName(ToPath(change.Uri))!)))
        {
            Rediscover();
        }

        foreach (var group in changed.GroupBy(change => ProjectFor(change.Uri)))
        {
            group.Key.OnFilesChanged(group.Select(change => change.Uri));
        }
    }

    public string FormatType(TypeTable types, TypID typId) => NebraProject.FormatType(types, typId);

    public string FormatType(TypeTable types, IR.Type typ) => NebraProject.FormatType(types, typ);

    public string FormatTypeBody(AnalysisResult result, IR.Type typ) => ProjectFor(result.FilePath).FormatTypeBody(result, typ);

    public string FormatTypeReferencesLine(AnalysisResult result, TypID typId) => ProjectFor(result.FilePath).FormatTypeReferencesLine(result, typId);

    public string FormatTypeReferencesLine(AnalysisResult result, IR.Type typ) => ProjectFor(result.FilePath).FormatTypeReferencesLine(result, typ);

    public NebraProject.TypeDeclLocation? FindTypeDeclLocation(AnalysisResult result, string typeName)
        => ProjectFor(result.FilePath).FindTypeDeclLocation(result, typeName);

    public List<Location> FindUsages(SymID targetSym, AnalysisResult originResult)
        => ProjectFor(originResult.FilePath).FindUsages(targetSym, originResult);

    public Dictionary<string, NebraProject.ExportInfo>? CollectExportsFromModule(AnalysisResult result, string moduleName)
        => ProjectFor(result.FilePath).CollectExportsFromModule(result, moduleName);

    public bool CompileFile(string filePath, out string message) => ProjectFor(filePath).CompileFile(filePath, out message);

    public List<Symbol> CollectVisibleSymbols(AnalysisResult result, ScopeID scopeId)
        => ProjectFor(result.FilePath).CollectVisibleSymbols(result, scopeId);

    public void ShowMessage(MessageType type, string message)
    {
        _server?.Window.ShowMessage(new ShowMessageParams { Type = type, Message = message });
    }

    public static Range SpanToRange(Diagnostics.TextSpan span) => NebraProject.SpanToRange(span);

    public static Dictionary<string, NebraProject.ExportInfo> CollectExports(AnalysisResult result, string? targetModuleName = null)
        => NebraProject.CollectExports(result, targetModuleName);

    public static List<AnalysisResult> FilesMentioning(AnalysisResult origin, string symbolName)
        => NebraProject.FilesMentioning(origin, symbolName);

    /// <summary>
    /// Finds the project roots - every folder, and every directory beneath one holding a
    /// <c>nebra.toml</c> - and reconciles the projects with them: new roots get a project, gone
    /// ones retire theirs, and every open document is handed to the project now owning it.
    /// </summary>
    private void Rediscover()
    {
        List<string> folders;
        lock (_gate) folders = [.._folders];

        var roots = folders.Concat(folders.SelectMany(FindNestedRoots)).Distinct(PathComparer).ToList();
        List<NebraProject> retired;
        List<(NebraProject Owner, string Uri, string Text)> open;

        lock (_gate)
        {
            open = _projects.Values.Concat(_looseFiles == null ? [] : [_looseFiles])
                .SelectMany(project => project.OpenDocuments.Select(document => (project, document.Uri, document.Text)))
                .ToList();
            retired = _projects.Where(entry => !roots.Contains(entry.Key, PathComparer)).Select(entry => entry.Value).ToList();

            var next = new Dictionary<string, NebraProject>(PathComparer);
            foreach (var root in roots)
            {
                next[root] = _projects.TryGetValue(root, out var existing) ? existing : CreateProject(root);
            }

            foreach (var project in next.Values)
            {
                project.NestedRoots = roots.Where(root => !PathComparer.Equals(root, project.RootPath) && IsBeneath(root, project.RootPath!)).ToList();
            }

            _projects = next;
        }

        foreach (var project in retired) _ = project.RetireAsync();

        foreach (var (owner, uri, text) in open)
        {
            var target = ProjectFor(uri);
            if (ReferenceEquals(target, owner)) continue;

            if (!retired.Contains(owner)) owner.OnDocumentClosed(uri);
            target.OnDocumentOpened(uri, text);
        }
    }

    private NebraProject CreateProject(string? root)
    {
        var project = new NebraProject(() => Settings);
        project.Initialize(root);
        if (_server != null) project.SetServer(_server);
        if (_started) project.Start();
        return project;
    }

    private NebraProject LooseFiles()
    {
        return _looseFiles ??= CreateProject(null);
    }

    private bool IsKnownRoot(string directory)
    {
        lock (_gate) return _projects.ContainsKey(Path.GetFullPath(directory));
    }

    private static IEnumerable<string> FindNestedRoots(string folder)
    {
        return ProjectFiles.Directories(folder)
            .Where(directory => !PathComparer.Equals(directory, folder) && File.Exists(Path.Combine(directory, "nebra.toml")))
            .Select(Path.GetFullPath);
    }

    private static string? ToPath(string uriOrPath)
    {
        if (!uriOrPath.Contains("://")) return Path.GetFullPath(uriOrPath);

        var path = DocumentUri.GetFileSystemPath(DocumentUri.Parse(uriOrPath));
        return path == null ? null : Path.GetFullPath(path);
    }

    private static bool IsBeneath(string path, string root)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return PathComparer.Equals(path, root) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
