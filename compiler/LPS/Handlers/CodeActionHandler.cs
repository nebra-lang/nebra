using Nebra.IR;
using MediatR;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Provides quick fixes and refactor actions: import suggestions for unresolved
/// symbols, "implement missing members" stubs for classes that fail interface or
/// abstract checks, and a 'Compile this file' command pointing at the server-side
/// <see cref="ExecuteCompileCommandHandler"/>.
/// </summary>
public sealed class CodeActionHandler(NebraWorkspace workspace) : CodeActionHandlerBase
{
    public override async Task<CommandOrCodeActionContainer?> Handle(CodeActionParams request, CancellationToken ct)
    {
        var actions = new List<CommandOrCodeAction>();

        var uri = request.TextDocument.Uri.ToString();
        var result = await workspace.GetResultAsync(uri, ct);

        if (result != null)
        {
            CollectImportFixes(result, await workspace.ProjectFor(result.FilePath).GetSnapshotAsync(ct), request, actions);
            CollectImplementMembersFixes(result, request, actions);
            CollectOrganizeImports(result, actions);
            CollectRefactorings(result, request, actions);

            foreach (var diagnostic in request.Context.Diagnostics ?? [])
                actions.AddRange(QuickFixes.For(result, workspace.LatestSnapshotFor(result.FilePath), diagnostic).Select(fix => new CommandOrCodeAction(fix)));
        }

        actions.Add(new CommandOrCodeAction(new CodeAction
        {
            Title = "Compile this file",
            Kind = CodeActionKind.Source,
            Command = new Command
            {
                Title = "Compile this file",
                Name = "nebra.compileFile",
                Arguments = new JArray(uri)
            }
        }));

        return new CommandOrCodeActionContainer(actions);
    }

    public override Task<CodeAction> Handle(CodeAction request, CancellationToken ct)
        => Task.FromResult(request);

    private void CollectRefactorings(AnalysisResult result, CodeActionParams request, List<CommandOrCodeAction> actions)
    {
        CodeAction?[] refactorings =
        [
            ExtractVariable.Offer(result, request.Range),
            ExtractFunction.Offer(workspace, result, request.Range),
            InlineVariable.Offer(result, request.Range.Start)
        ];

        actions.AddRange(refactorings.OfType<CodeAction>().Select(action => new CommandOrCodeAction(action)));
    }

    private static void CollectOrganizeImports(AnalysisResult result, List<CommandOrCodeAction> actions)
    {
        var edits = OrganizeImports.Compute(result);
        if (edits.Count == 0) return;

        actions.Add(new CommandOrCodeAction(new CodeAction
        {
            Title = "Organize imports",
            Kind = CodeActionKind.SourceOrganizeImports,
            Edit = SingleFileEdit(result, edits)
        }));
    }

    private static WorkspaceEdit SingleFileEdit(AnalysisResult result, IEnumerable<TextEdit> edits)
    {
        return new WorkspaceEdit
        {
            Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
            {
                [DocumentUri.Parse(result.Uri)] = edits.ToList()
            }
        };
    }

    /// <summary>
    /// For each <c>ErrUndeclaredSymbol</c> diagnostic in the requested range, search
    /// the workspace for files that export a matching symbol and propose an import
    /// edit that adds <c>import { Name } from "&lt;path&gt;"</c> at the top of the file.
    /// </summary>
    private static void CollectImportFixes(AnalysisResult result, WorkspaceSnapshot snapshot, CodeActionParams request, List<CommandOrCodeAction> actions)
    {
        if (request.Context.Diagnostics == null) return;

        foreach (var diag in request.Context.Diagnostics)
        {
            if (diag.Code?.String != nameof(Diagnostics.DiagnosticCode.ErrUndeclaredSymbol)) continue;

            var symbolName = ExtractQuotedName(diag.Message);
            if (symbolName == null) continue;

            var fixes = ProjectImports(result, snapshot, symbolName).Concat(DependencyImports(result, symbolName)).ToList();
            foreach (var (module, importEdit) in fixes)
            {
                var edit = new WorkspaceEdit
                {
                    Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                    {
                        [DocumentUri.Parse(result.Uri)] = [importEdit]
                    }
                };

                actions.Add(new CommandOrCodeAction(new CodeAction
                {
                    Title = $"Import '{symbolName}' from '{module}'",
                    Kind = CodeActionKind.QuickFix,
                    Diagnostics = new Container<Diagnostic>(diag),
                    Edit = edit,
                    IsPreferred = fixes.Count == 1
                }));
            }
        }
    }

    private static IEnumerable<(string Module, TextEdit Edit)> ProjectImports(AnalysisResult result, WorkspaceSnapshot snapshot, string name)
    {
        var sourceRoot = ImportEdits.SourceRoot(result);
        foreach (var exporter in snapshot.SourceResults())
        {
            if (string.Equals(exporter.FilePath, result.FilePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!NebraWorkspace.CollectExports(exporter).ContainsKey(name)) continue;

            var edit = ImportEdits.AddImport(result, name, exporter.FilePath);
            if (edit != null) yield return (ImportEdits.ModuleSpecifier(result.FilePath, exporter.FilePath, sourceRoot), edit);
        }
    }

    private static IEnumerable<(string Module, TextEdit Edit)> DependencyImports(AnalysisResult result, string name)
    {
        foreach (var export in DependencyExports.Of(result.Snapshot.Config).Where(export => export.Name == name))
        {
            var edit = ImportEdits.AddImport(result, export);
            if (edit != null) yield return (export.Module, edit);
        }
    }

    /// <summary>
    /// Computes the missing interface and abstract members for a class containing
    /// the cursor and emits a single quick fix that appends stub method/field
    /// declarations before the closing <c>end</c>.
    /// </summary>
    private void CollectImplementMembersFixes(AnalysisResult result, CodeActionParams request, List<CommandOrCodeAction> actions)
    {
        var line = request.Range.Start.Line + 1;
        var col = request.Range.Start.Character + 1;
        var classDecl = FindContainingClass(result.Hir, line, col);
        if (classDecl == null) return;
        if (classDecl.IsDeclare) return;
        if (classDecl.Name.Sym == SymID.Invalid) return;
        if (!result.Syms.GetByID(classDecl.Name.Sym, out var classSym)) return;
        if (!result.Types.GetByID(classSym.Type, out var rawType) || rawType is not ClassType classType) return;

        var existingNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in classDecl.Fields) existingNames.Add(f.Name.Name);
        foreach (var m in classDecl.Methods) existingNames.Add(m.Name.Name);
        foreach (var a in classDecl.Accessors) existingNames.Add(a.Name.Name);

        var stubs = new List<string>();

        foreach (var iface in classType.Interfaces)
            CollectMissingFromInterface(result, iface, existingNames, stubs);

        if (classType.BaseClass != null)
            CollectMissingAbstract(result, classType.BaseClass, existingNames, stubs);

        if (stubs.Count == 0) return;

        var indent = DetectIndent(result.SourceText, classDecl);
        var insertText = string.Join("\n", stubs.Select(s => indent + s)) + "\n";
        var insertRange = ComputeInsertBeforeEndRange(result.SourceText, classDecl);

        var edit = new WorkspaceEdit
        {
            Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
            {
                [DocumentUri.Parse(result.Uri)] =
                [
                    new TextEdit { Range = insertRange, NewText = insertText }
                ]
            }
        };

        actions.Add(new CommandOrCodeAction(new CodeAction
        {
            Title = $"Implement missing members on '{classType.Name}'",
            Kind = CodeActionKind.QuickFix,
            Edit = edit,
            IsPreferred = true
        }));
    }

    private void CollectMissingFromInterface(AnalysisResult result, InterfaceType iface, HashSet<string> existing, List<string> stubs)
    {
        foreach (var (name, field) in iface.Fields)
        {
            if (!existing.Add(name)) continue;
            stubs.Add($"{name}: {workspace.FormatType(result.Types, field.Type.ID)}");
        }
        foreach (var (name, method) in iface.Methods)
        {
            if (!existing.Add(name)) continue;
            // Default methods carry an implementation; they are inherited, not required.
            if (iface.DefaultMethods.Contains(name)) continue;
            stubs.Add(BuildMethodStub(name, method, result));
        }
        foreach (var b in iface.BaseInterfaces)
            CollectMissingFromInterface(result, b, existing, stubs);
    }

    private void CollectMissingAbstract(AnalysisResult result, ClassType baseClass, HashSet<string> existing, List<string> stubs)
    {
        foreach (var name in baseClass.AbstractMethods)
        {
            if (!existing.Add(name)) continue;
            if (!baseClass.Methods.TryGetValue(name, out var method)) continue;
            stubs.Add("override " + BuildMethodStub(name, method, result));
        }
        if (baseClass.BaseClass != null)
            CollectMissingAbstract(result, baseClass.BaseClass, existing, stubs);
    }

    private string BuildMethodStub(string name, FunctionType method, AnalysisResult result)
    {
        var paramText = string.Join(", ",
            method.ParamTypes.Zip(method.ParamNames, (t, n) => $"{n}: {workspace.FormatType(result.Types, t.ID)}"));
        var ret = workspace.FormatType(result.Types, method.ReturnType.ID);
        var returnsNil = string.Equals(ret, "nil", StringComparison.Ordinal);
        var retSuffix = returnsNil ? "" : $": {ret}";

        // A stub that declares a return type has to produce one, or applying the fix trades the
        // missing-member error for a missing-return error. `error` is declared `never`, so it
        // satisfies any return type while still making an unimplemented member obvious at runtime.
        var body = returnsNil
            ? "    -- TODO: implement"
            : $"    error(\"{name} is not implemented\")";

        return $"function {name}({paramText}){retSuffix}\n{body}\nend";
    }

    private static OmniSharp.Extensions.LanguageServer.Protocol.Models.Range ComputeInsertBeforeEndRange(string source, ClassDecl cd)
    {
        var lines = source.Split('\n');
        var endLine = Math.Max(0, cd.Span.EndLn - 1);
        if (endLine >= lines.Length) endLine = lines.Length - 1;

        for (var i = endLine; i >= 0; i--)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("end"))
            {
                return new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(i, 0), new Position(i, 0));
            }
        }
        return new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
            new Position(endLine, 0), new Position(endLine, 0));
    }

    private static string DetectIndent(string source, ClassDecl cd)
    {
        var lines = source.Split('\n');
        for (var i = cd.Span.StartLn; i < lines.Length && i < cd.Span.EndLn; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var lead = line.Length - line.TrimStart().Length;
            if (lead > 0) return new string(' ', lead);
        }
        return "    ";
    }

    private static string? ExtractQuotedName(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var start = message.IndexOf('\'');
        if (start < 0) return null;
        var end = message.IndexOf('\'', start + 1);
        if (end <= start) return null;
        return message.Substring(start + 1, end - start - 1);
    }

    private static ClassDecl? FindContainingClass(IRScript hir, int line, int col)
    {
        foreach (var stmt in hir.Body)
        {
            ClassDecl? cd = stmt switch
            {
                ClassDecl c => c,
                ExportStmt e when e.Declaration is ClassDecl ce => ce,
                _ => null
            };
            if (cd == null) continue;
            var afterStart = cd.Span.StartLn < line || (cd.Span.StartLn == line && cd.Span.StartCol <= col);
            var beforeEnd = cd.Span.EndLn > line || (cd.Span.EndLn == line && cd.Span.EndCol >= col);
            if (afterStart && beforeEnd) return cd;
        }
        return null;
    }

    protected override CodeActionRegistrationOptions CreateRegistrationOptions(CodeActionCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new CodeActionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra"),
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix, CodeActionKind.Source,
                CodeActionKind.SourceOrganizeImports, CodeActionKind.RefactorExtract, CodeActionKind.RefactorInline),
            ResolveProvider = false
        };
    }
}

/// <summary>
/// Server-side handler for <c>workspace/executeCommand</c> with command name
/// <c>nebra.compileFile</c>. Compiles the URI passed as the first argument and
/// reports the outcome through the language server's window/showMessage channel.
/// </summary>
public sealed class ExecuteCompileCommandHandler(NebraWorkspace workspace) : ExecuteCommandHandlerBase
{
    public override Task<Unit> Handle(ExecuteCommandParams request, CancellationToken ct)
    {
        if (request.Arguments == null || request.Arguments.Count == 0)
            return Unit.Task;

        var uri = request.Arguments[0]?.ToString();
        if (string.IsNullOrEmpty(uri)) return Unit.Task;

        var filePath = DocumentUri.GetFileSystemPath(DocumentUri.Parse(uri));
        if (filePath == null) return Unit.Task;

        try
        {
            var ok = workspace.CompileFile(filePath, out var message);
            workspace.ShowMessage(ok ? MessageType.Info : MessageType.Error, message);
        }
        catch (Exception ex)
        {
            workspace.ShowMessage(MessageType.Error, $"Compile failed: {ex.Message}");
        }

        return Unit.Task;
    }

    protected override ExecuteCommandRegistrationOptions CreateRegistrationOptions(ExecuteCommandCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new ExecuteCommandRegistrationOptions
        {
            Commands = new Container<string>("nebra.compileFile")
        };
    }
}
