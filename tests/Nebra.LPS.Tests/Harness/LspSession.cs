using System.IO.Pipelines;
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Server;

namespace Nebra.LPS.Tests.Harness;

/// <summary>A position in a project file, addressed by its path relative to the project root.</summary>
public sealed record SourceLocation(string Path, int Line, int Character);

/// <summary>
/// A Nebra language server and an LSP client wired together over in-memory pipes, serving a
/// project written to a temporary directory. Requests travel through the real protocol stack, so
/// a test observes exactly what an editor would, without spawning a process.
/// </summary>
public sealed class LspSession : IAsyncDisposable
{
    private const string DefaultConfig =
        "name = \"lps-test\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nsource = \"src\"\noutput = \"out\"\n";

    private static readonly TimeSpan DiagnosticsTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The first analysis in a process pays for JIT compilation and for building the parser's
    /// prediction cache, which takes seconds. Paying it once up front keeps sessions that start
    /// in parallel from all doing so at the same time and running into the timeout.
    /// </summary>
    private static readonly Task Warmup = Task.Run(WarmUpAnalysis);

    private readonly Dictionary<string, MarkedSource> _files;
    private readonly DiagnosticsCollector _diagnostics;
    private readonly ILanguageServer _server;

    public string Root { get; }
    public ILanguageClient Client { get; }

    private LspSession(string root, Dictionary<string, MarkedSource> files, ILanguageServer server,
        ILanguageClient client, DiagnosticsCollector diagnostics)
    {
        Root = root;
        _files = files;
        _server = server;
        Client = client;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Writes <paramref name="files"/> (paths relative to the project root, marker syntax allowed)
    /// to a fresh project directory and starts a server over it. A <c>nebra.toml</c> is added
    /// unless one is supplied.
    /// </summary>
    public static async Task<LspSession> StartAsync(params (string Path, string Text)[] files)
    {
        await Warmup;

        var root = Directory.CreateTempSubdirectory("nebra-lps-").FullName;
        var parsed = new Dictionary<string, MarkedSource>(StringComparer.Ordinal);

        foreach (var (path, text) in files)
        {
            var source = MarkedSource.Parse(text);
            parsed[path] = source;
            WriteFile(root, path, source.Text);
        }

        if (!parsed.ContainsKey("nebra.toml"))
        {
            WriteFile(root, "nebra.toml", DefaultConfig);
        }

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var diagnostics = new DiagnosticsCollector();

        var serverTask = LanguageServer.From(options =>
        {
            NebraLanguageServer.Configure(options, new NebraWorkspace());
            options
                .WithInput(clientToServer.Reader)
                .WithOutput(serverToClient.Writer);
        });

        var clientTask = LanguageClient.From(options =>
        {
            options
                .WithInput(serverToClient.Reader)
                .WithOutput(clientToServer.Writer)
                .WithRootUri(DocumentUri.FromFileSystemPath(root))
                .OnPublishDiagnostics(diagnostics.Record);
        });

        await Task.WhenAll(serverTask, clientTask);
        return new LspSession(root, parsed, serverTask.Result, clientTask.Result, diagnostics);
    }

    public DocumentUri Uri(string path) => DocumentUri.FromFileSystemPath(FullPath(path));

    public string FullPath(string path) => System.IO.Path.Combine(Root, path);

    /// <summary>Finds the marker <paramref name="name"/> in whichever file declares it.</summary>
    public (string Path, Position Position) Marker(string name)
    {
        foreach (var (path, source) in _files)
        {
            if (source.Markers.TryGetValue(name, out var position))
            {
                return (path, position);
            }
        }

        throw new KeyNotFoundException($"No file declares the marker '{name}'.");
    }

    public SourceLocation Location(string marker)
    {
        var (path, position) = Marker(marker);
        return new SourceLocation(path, position.Line, position.Character);
    }

    public TextDocumentPositionParams At(string marker)
    {
        var (path, position) = Marker(marker);
        return new TextDocumentPositionParams
        {
            TextDocument = new TextDocumentIdentifier(Uri(path)),
            Position = position
        };
    }

    /// <summary>Converts a location the server returned back into a project-relative one.</summary>
    public SourceLocation ToSourceLocation(DocumentUri uri, Position position)
    {
        var full = System.IO.Path.GetFullPath(uri.GetFileSystemPath());
        var relative = System.IO.Path.GetRelativePath(Root, full).Replace('\\', '/');
        return new SourceLocation(relative, position.Line, position.Character);
    }

    /// <summary>
    /// Opens <paramref name="path"/> in the server and waits for the diagnostics it publishes
    /// in response.
    /// </summary>
    public async Task<IReadOnlyList<Diagnostic>> OpenAsync(string path)
    {
        var uri = Uri(path);
        var published = _diagnostics.WaitForNext(uri);

        Client.TextDocument.DidOpenTextDocument(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "nebra",
                Version = 1,
                Text = _files[path].Text
            }
        });

        return await published.WaitAsync(DiagnosticsTimeout);
    }

    /// <summary>
    /// Returns a task that completes with the next diagnostics the server publishes for
    /// <paramref name="path"/>. Call it before the action expected to trigger them.
    /// </summary>
    public Task<IReadOnlyList<Diagnostic>> NextDiagnostics(string path)
    {
        return _diagnostics.WaitForNext(Uri(path)).WaitAsync(DiagnosticsTimeout);
    }

    /// <summary>
    /// Replaces the content of an open document and waits for the diagnostics published for it.
    /// </summary>
    public async Task<IReadOnlyList<Diagnostic>> ChangeAsync(string path, string markedText, int version)
    {
        var source = MarkedSource.Parse(markedText);
        _files[path] = source;

        var uri = Uri(path);
        var published = NextDiagnostics(path);

        Client.TextDocument.DidChangeTextDocument(new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(
                new TextDocumentContentChangeEvent { Text = source.Text })
        });

        return await published;
    }

    public async Task<string?> HoverAsync(string marker)
    {
        var at = At(marker);
        var hover = await Client.TextDocument.RequestHover(new HoverParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return hover?.Contents.MarkupContent?.Value;
    }

    public async Task<IReadOnlyList<SourceLocation>> DefinitionAsync(string marker)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestDefinition(new DefinitionParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return result
            .Select(link => link.IsLocation
                ? ToSourceLocation(link.Location!.Uri, link.Location.Range.Start)
                : ToSourceLocation(link.LocationLink!.TargetUri, link.LocationLink.TargetSelectionRange.Start))
            .ToList();
    }

    public async Task<IReadOnlyList<SourceLocation>> ReferencesAsync(string marker, bool includeDeclaration)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestReferences(new ReferenceParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position,
            Context = new ReferenceContext { IncludeDeclaration = includeDeclaration }
        });

        return result
            .Select(location => ToSourceLocation(location.Uri, location.Range.Start))
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Character)
            .ToList();
    }

    /// <summary>
    /// Requests a rename and returns the start of every edited range, grouped by file and sorted,
    /// or an empty list when the server declined the rename.
    /// </summary>
    public async Task<IReadOnlyList<SourceLocation>> RenameAsync(string marker, string newName)
    {
        var at = At(marker);
        var edit = await Client.TextDocument.RequestRename(new RenameParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position,
            NewName = newName
        });

        if (edit?.Changes == null)
        {
            return [];
        }

        return edit.Changes
            .SelectMany(change => change.Value.Select(text => ToSourceLocation(change.Key, text.Range.Start)))
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Character)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> CompletionLabelsAsync(string marker)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestCompletion(new CompletionParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return result.Items.Select(item => item.Label).ToList();
    }

    public async Task<SignatureHelp?> SignatureHelpAsync(string marker)
    {
        var at = At(marker);
        return await Client.TextDocument.RequestSignatureHelp(new SignatureHelpParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });
    }

    public async Task<IReadOnlyList<DocumentSymbol>> DocumentSymbolsAsync(string path)
    {
        var result = await Client.TextDocument.RequestDocumentSymbol(new DocumentSymbolParams
        {
            TextDocument = new TextDocumentIdentifier(Uri(path))
        });

        return result
            .Where(symbol => symbol.IsDocumentSymbol)
            .Select(symbol => symbol.DocumentSymbol!)
            .ToList();
    }

    public async Task<SemanticTokens?> SemanticTokensAsync(string path)
    {
        return await Client.TextDocument.RequestSemanticTokensFull(new SemanticTokensParams
        {
            TextDocument = new TextDocumentIdentifier(Uri(path))
        });
    }

    public async Task<IReadOnlyList<CodeAction>> CodeActionsAsync(string path, IReadOnlyList<Diagnostic> diagnostics)
    {
        var range = diagnostics.Count > 0
            ? diagnostics[0].Range
            : new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 0, 0);

        var result = await Client.TextDocument.RequestCodeAction(new CodeActionParams
        {
            TextDocument = new TextDocumentIdentifier(Uri(path)),
            Range = range,
            Context = new CodeActionContext { Diagnostics = new Container<Diagnostic>(diagnostics) }
        });

        return result
            .Where(item => item.IsCodeAction)
            .Select(item => item.CodeAction!)
            .ToList();
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        _server.Dispose();

        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }

        return ValueTask.CompletedTask;
    }

    private static void WarmUpAnalysis()
    {
        var root = Directory.CreateTempSubdirectory("nebra-lps-warmup-").FullName;
        WriteFile(root, "nebra.toml", DefaultConfig);
        const string text = "local value: number = 1\nprint(value)\n";
        WriteFile(root, "src/main.neb", text);

        var workspace = new NebraWorkspace();
        workspace.Initialize(root);
        workspace.AnalyzeDocument(DocumentUri.FromFileSystemPath(System.IO.Path.Combine(root, "src/main.neb")).ToString(), text);

        Directory.Delete(root, true);
    }

    private static void WriteFile(string root, string path, string text)
    {
        var full = System.IO.Path.Combine(root, path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }
}
