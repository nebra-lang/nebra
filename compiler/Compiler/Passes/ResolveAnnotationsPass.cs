using Nebra.Compiler.Annotations;
using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;
using Nebra.PackageManager;

namespace Nebra.Compiler.Passes;

/// <summary>
/// Build-scoped pass that discovers annotation definition files (<c>.neb</c>) from
/// <c>Config.Annotations</c>, sub-compiles each to Lua, extracts their <c>meta</c>
/// declaration and registers them in an <see cref="AnnotationRegistry"/> stored on
/// <c>PassContext.Cache</c>. Consumed by <see cref="ApplyAnnotationsPass"/>.
/// </summary>
public sealed class ResolveAnnotationsPass() : Pass(PassName, PassScope.PerBuild)
{
    public const string PassName = "ResolveAnnotations";

    public override bool Run(PassContext context)
    {
        var registry = new AnnotationRegistry();
        context.Cache[AnnotationRegistry.CacheKey] = registry;

        var baseDir = context.Config.ProjectRoot;
        foreach (var entry in context.Config.Annotations)
        {
            var fullPath = Path.IsPathRooted(entry) ? entry : Path.Combine(baseDir, entry);
            if (Directory.Exists(fullPath))
            {
                foreach (var file in Directory.EnumerateFiles(fullPath, "*.neb", SearchOption.AllDirectories))
                    LoadAnnotationFile(context, registry, file);
            }
            else if (File.Exists(fullPath))
            {
                LoadAnnotationFile(context, registry, fullPath);
            }
            else
            {
                context.Diag.Report(TextSpan.Empty, DiagnosticCode.ErrAnnotationPathNotFound, entry);
            }
        }

        foreach (var pkg in GetInstalledPackages(context))
        {
            var roots = pkg.AnnotationRoots.Count > 0
                ? pkg.AnnotationRoots
                : [pkg.RootPath];
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in InstalledPackages.EnumerateFilesSafely(root, "*.neb"))
                {
                    if (file.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!LooksLikeAnnotationFile(file)) continue;
                    LoadAnnotationFile(context, registry, file);
                }
            }
        }

        return true;
    }

    private static IReadOnlyList<InstalledPackage> GetInstalledPackages(PassContext context)
    {
        if (context.Cache.TryGetValue(InstalledPackages.CacheKey, out var cached)
            && cached is IReadOnlyList<InstalledPackage> list)
            return list;
        return InstalledPackages.Discover(context.Config.ProjectRoot);
    }

    /// <summary>
    /// Cheap textual heuristic to avoid sub-compiling every <c>.neb</c> file in a package. A
    /// valid annotation file must export an <c>annotation</c> table and an <c>apply</c> function.
    /// </summary>
    private static bool LooksLikeAnnotationFile(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch { return false; }
        return text.Contains("export local annotation", StringComparison.Ordinal)
            || text.Contains("export function apply", StringComparison.Ordinal)
            || text.Contains("export local function apply", StringComparison.Ordinal);
    }

    private static void LoadAnnotationFile(PassContext ctx, AnnotationRegistry registry, string path)
    {
        var annotationName = Path.GetFileNameWithoutExtension(path);

        var subConfig = ctx.Config.Clone();
        subConfig.Code = new CodeSection
        {
            IndexBase = 1,
            ConcatOperator = ctx.Config.Code.ConcatOperator,
            StringInterpolation = ctx.Config.Code.StringInterpolation,
            AltBooleanOperators = ctx.Config.Code.AltBooleanOperators,
            Semicolons = ctx.Config.Code.Semicolons,
            ImportStatement = ctx.Config.Code.ImportStatement,
            StripUnused = ctx.Config.Code.StripUnused,
            Libs = [..ctx.Config.Code.Libs],
        };

        var subCompiler = new NebraCompiler { Config = subConfig };
        subCompiler.AddSource(path);

        var pm = new PassManager();
        pm.BuildOrder(PassManager.AnnotationFilePipeline);
        var ok = pm.Run(subCompiler.Diagnostics, subCompiler.Packages.Values.ToList(),
            subCompiler.TypeUniverse, subCompiler.SymAlloc, subCompiler.ScopeAlloc,
            subCompiler.NodeAlloc, subCompiler.Names, subCompiler.Cache, subCompiler.Config);

        if (!ok || subCompiler.Diagnostics.HasErrors)
        {
            ctx.Diag.Report(TextSpan.Empty, DiagnosticCode.ErrAnnotationCompileFailed, annotationName);
            return;
        }

        PreparsedFile? file = null;
        foreach (var pkg in subCompiler.Packages.Values)
        {
            foreach (var f in pkg.Files)
            {
                file = f;
                break;
            }
            if (file != null) break;
        }
        if (file == null || file.GeneratedLua == null)
        {
            ctx.Diag.Report(TextSpan.Empty, DiagnosticCode.ErrAnnotationCompileFailed, annotationName);
            return;
        }

        if (ContainsAnnotations(file.Hir.Body))
        {
            ctx.Diag.Report(TextSpan.Empty, DiagnosticCode.ErrAnnotationInAnnotationFile);
        }

        if (!TryExtractMeta(file.Hir.Body, out var targets, out var parameters, out var metaError))
        {
            ctx.Diag.Report(TextSpan.Empty, DiagnosticCode.ErrAnnotationMetaInvalid, annotationName, metaError ?? "unknown");
            return;
        }

        if (!HasApplyFunction(file.Hir.Body))
        {
            ctx.Diag.Report(TextSpan.Empty, DiagnosticCode.ErrAnnotationMissingApply, annotationName);
            return;
        }

        var def = new AnnotationDefinition(annotationName, targets, parameters, file.GeneratedLua, path);
        if (!registry.TryAdd(def))
        {
            ctx.Diag.Report(TextSpan.Empty, DiagnosticCode.ErrAnnotationDuplicateName, annotationName);
        }
    }

    /// <summary>
    /// Parses just the <c>annotation = { … }</c> meta block of an annotation file
    /// from disk and returns the lightweight <see cref="AnnotationMeta"/> the
    /// LSP needs for IntelliSense (completion, signature help, hover, arg
    /// validation). Does <b>not</b> sub-compile the file or invoke <c>apply</c>;
    /// designed to be cheap enough to call on every workspace open.
    /// </summary>
    public static AnnotationMeta? LoadMetaFromFile(string filePath, Configuration.Config config, IDAlloc<NodeID> nodeAlloc, DiagnosticsBag diag)
    {
        string source;
        try { source = File.ReadAllText(filePath); }
        catch { return null; }

        Antlr4.Runtime.AntlrInputStream stream;
        try { stream = new Antlr4.Runtime.AntlrInputStream(source); }
        catch { return null; }

        var lexer = new NebraLexer(stream);
        lexer.RemoveErrorListeners();
        var tokens = new Antlr4.Runtime.CommonTokenStream(lexer);
        var parser = new NebraParser(tokens);
        parser.RemoveErrorListeners();
        var visitor = new IRVisitor(filePath, nodeAlloc, diag, config);
        if (visitor.Visit(parser.script()) is not IRScript script) return null;

        if (!TryExtractMeta(script.Body, out var targets, out var parameters, out _)) return null;

        var name = Path.GetFileNameWithoutExtension(filePath);
        return new AnnotationMeta(name, targets, parameters, filePath);
    }

    public static bool TryExtractMeta(List<Stmt> body, out HashSet<AnnotationTargetKind> targets, out List<AnnotationParamSpec> parameters, out string? error)
    {
        targets = [AnnotationTargetKind.Function];
        parameters = [];
        error = null;

        TableConstructorExpr? metaTable = null;
        foreach (var stmt in body)
        {
            if (stmt is ExportStmt { Declaration: LocalDecl ld }
                && ld.Variables.Count == 1
                && ld.Variables[0].Name.Name == "annotation"
                && ld.Values.Count == 1
                && ld.Values[0] is TableConstructorExpr tc)
            {
                metaTable = tc;
                break;
            }
        }

        if (metaTable == null)
        {
            error = "missing `export local annotation = { ... }` declaration";
            return false;
        }

        foreach (var field in metaTable.Fields)
        {
            if (field.Name == null) continue;
            switch (field.Name.Name)
            {
                case "target":
                case "targets":
                    if (!TryParseTargets(field.Value, out var parsedTargets, out var targetErr))
                    {
                        error = targetErr;
                        return false;
                    }
                    targets = parsedTargets;
                    break;
                case "params":
                    if (field.Value is TableConstructorExpr paramsTable)
                    {
                        foreach (var paramField in paramsTable.Fields)
                        {
                            if (paramField.Name == null) continue;
                            if (paramField.Value is not TableConstructorExpr paramSpec) continue;
                            var (typeName, defaultValue, required) = ParseParamSpec(paramSpec);
                            parameters.Add(new AnnotationParamSpec(paramField.Name.Name, typeName, defaultValue, required));
                        }
                    }
                    break;
            }
        }

        return true;
    }

    private static bool TryParseTargets(Expr expr, out HashSet<AnnotationTargetKind> targets, out string? error)
    {
        targets = [];
        error = null;

        if (expr is TableConstructorExpr listExpr)
        {
            foreach (var f in listExpr.Fields)
            {
                if (f.Name != null)
                {
                    error = "`meta.target` list must contain only positional entries";
                    return false;
                }
                if (!TryParseSingleTarget(f.Value, out var single, out var singleErr))
                {
                    error = singleErr;
                    return false;
                }
                targets.Add(single);
            }
            if (targets.Count == 0)
            {
                error = "`meta.target` list must contain at least one AnnotationTarget";
                return false;
            }
            return true;
        }

        if (!TryParseSingleTarget(expr, out var parsed, out var err))
        {
            error = err;
            return false;
        }
        targets.Add(parsed);
        return true;
    }

    private static bool TryParseSingleTarget(Expr expr, out AnnotationTargetKind target, out string? error)
    {
        target = AnnotationTargetKind.Function;
        error = null;

        string? targetName = null;
        if (expr is DotAccessExpr dot && dot.Object is NameExpr ne && ne.Name.Name == "AnnotationTarget")
            targetName = dot.FieldName.Name;
        else if (expr is StringLiteralExpr sl)
            targetName = sl.Value;
        else if (expr is NameExpr bare)
            targetName = bare.Name.Name;

        if (targetName == null)
        {
            error = "`meta.target` entries must be AnnotationTarget enum values";
            return false;
        }

        if (!Enum.TryParse<AnnotationTargetKind>(targetName, ignoreCase: true, out target))
        {
            error = $"unknown AnnotationTarget '{targetName}'";
            return false;
        }
        return true;
    }

    private static (string typeName, object? defaultValue, bool required) ParseParamSpec(TableConstructorExpr spec)
    {
        var typeName = "any";
        object? defaultValue = null;
        var required = true;
        var hasDefault = false;

        foreach (var field in spec.Fields)
        {
            if (field.Name == null) continue;
            switch (field.Name.Name)
            {
                case "type":
                    if (field.Value is StringLiteralExpr s) typeName = s.Value;
                    break;
                case "default":
                    defaultValue = ConstFoldLiteral(field.Value);
                    hasDefault = true;
                    break;
                case "required":
                    if (field.Value is BoolLiteralExpr b) required = b.Value;
                    break;
            }
        }
        if (hasDefault) required = false;
        return (typeName, defaultValue, required);
    }

    /// <summary>
    /// Folds a literal expression into a plain C# value. Returns null for unsupported shapes.
    /// </summary>
    private static object? ConstFoldLiteral(Expr expr)
    {
        return expr switch
        {
            NilLiteralExpr => null,
            BoolLiteralExpr b => b.Value,
            StringLiteralExpr s => s.Value,
            NumberLiteralExpr n => ParseNumber(n.Raw),
            UnaryExpr { Op: UnaryOp.Negate, Operand: NumberLiteralExpr nn } => NegateNumber(ParseNumber(nn.Raw)),
            TableConstructorExpr t => FoldTable(t),
            _ => null,
        };
    }

    private static object ParseNumber(string raw)
    {
        if (long.TryParse(raw, out var l)) return l;
        if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return 0L;
    }

    private static object NegateNumber(object n) => n switch
    {
        long l => -l,
        double d => -d,
        _ => n,
    };

    private static object? FoldTable(TableConstructorExpr t)
    {
        var hasNamed = t.Fields.Any(f => f.Name != null);
        if (hasNamed)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var f in t.Fields)
                if (f.Name != null) dict[f.Name.Name] = ConstFoldLiteral(f.Value);
            return dict;
        }
        var list = new List<object?>();
        foreach (var f in t.Fields) list.Add(ConstFoldLiteral(f.Value));
        return list;
    }

    /// <summary>
    /// Recursively checks whether any declaration in the body carries <c>@</c>-annotations.
    /// Annotation definition files must not use annotations themselves to prevent recursion.
    /// </summary>
    private static bool ContainsAnnotations(List<Stmt> body)
    {
        foreach (var stmt in body)
        {
            var decl = stmt switch
            {
                ExportStmt ex => ex.Declaration,
                Decl d => d,
                _ => null,
            };
            if (decl == null) continue;

            switch (decl)
            {
                case FunctionDecl fd when fd.Annotations.Count > 0: return true;
                case LocalFunctionDecl lfd when lfd.Annotations.Count > 0: return true;
                case LocalDecl ld when ld.Annotations.Count > 0: return true;
                case ClassDecl cd when cd.Annotations.Count > 0: return true;
                case EnumDecl ed when ed.Annotations.Count > 0: return true;
                case InterfaceDecl id when id.Annotations.Count > 0: return true;
                case ClassDecl cd2:
                    if (cd2.Fields.Any(f => f.Annotations.Count > 0)) return true;
                    if (cd2.Methods.Any(m => m.Annotations.Count > 0)) return true;
                    break;
                case EnumDecl ed2:
                    if (ed2.Members.Any(m => m.Annotations.Count > 0)) return true;
                    break;
            }
        }
        return false;
    }

    private static bool HasApplyFunction(List<Stmt> body)
    {
        foreach (var stmt in body)
        {
            if (stmt is ExportStmt ex)
            {
                if (ex.Declaration is FunctionDecl fd && fd.NamePath.Count == 1 && fd.NamePath[0].Name == "apply")
                    return true;
                if (ex.Declaration is LocalFunctionDecl lfd && lfd.Name.Name == "apply")
                    return true;
            }
        }
        return false;
    }
}
