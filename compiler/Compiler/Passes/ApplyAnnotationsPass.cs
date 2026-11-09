using Nebra.Compiler.Annotations;
using Nebra.Diagnostics;
using Nebra.IR;
using Nebra.Runtime;

namespace Nebra.Compiler.Passes;

/// <summary>
/// Per-file pass that walks the HIR, finds every declaration carrying compile-time annotations,
/// and executes them via <see cref="NebraRuntime"/>. Each annotation receives the target declaration
/// as a serialized Lua table plus the validated argument dictionary, and may return either a
/// single replacement node or a list of statements to splice into the enclosing block.
///
/// Runs after parsing but before <see cref="BindDeclarePass"/>, so the resulting IR is still
/// untyped / unbound and will flow through the rest of the pipeline as if the annotation output
/// had been hand-written by the user.
/// </summary>
public sealed class ApplyAnnotationsPass()
    : Pass(PassName, PassScope.PerFile, noErrors: false, ResolveAnnotationsPass.PassName)
{
    public const string PassName = "ApplyAnnotations";

    public override bool Run(PassContext context)
    {
        if (context.File == null || context.Config.AnnotationFile) return true;

        // Walk even when the project defines no annotations of its own: an annotation that is
        // neither builtin nor registered is a typo that would otherwise do nothing at all, and
        // whether it is reported must not depend on unrelated configuration.
        var registry = context.Cache.TryGetValue(AnnotationRegistry.CacheKey, out var reg)
                       && reg is AnnotationRegistry found
            ? found
            : new AnnotationRegistry();

        RewriteStmtList(context, registry, context.File.Hir.Body);
        return true;
    }

    /// <summary>
    /// Walks a statement list in place, expanding each annotated declaration into the result of
    /// its apply function. Also recurses into nested block-bearing statements so annotations on
    /// inner decls still execute, and processes member-level annotations inside class/interface/enum
    /// decls and parameter annotations on function signatures. A declaration's own annotations run
    /// before those of its members, so a class annotation sees its fields as they are written; the
    /// member annotations then run on what it returned.
    /// </summary>
    private static void RewriteStmtList(PassContext ctx, AnnotationRegistry registry, List<Stmt> stmts)
    {
        for (var i = 0; i < stmts.Count; i++)
        {
            var stmt = stmts[i];
            RecurseInto(ctx, registry, stmt);

            var annotated = ExtractAnnotatedDecl(stmt);
            var replacement = annotated.decl != null && annotated.annotations.Count > 0
                ? ApplyAnnotationsToDecl(ctx, registry, annotated.decl, annotated.annotations, stmt)
                : null;
            if (replacement == null)
            {
                ProcessMemberAnnotations(ctx, registry, stmt);
                continue;
            }

            foreach (var produced in replacement)
                ProcessMemberAnnotations(ctx, registry, produced);

            stmts.RemoveAt(i);
            stmts.InsertRange(i, replacement);
            i += replacement.Count - 1;
        }
    }

    private static (Decl? decl, List<Annotation> annotations) ExtractAnnotatedDecl(Stmt stmt)
    {
        var decl = stmt switch
        {
            ExportStmt ex => ex.Declaration,
            Decl d => d,
            _ => null,
        };

        if (decl == null) return (null, []);

        var anns = decl switch
        {
            FunctionDecl fd => fd.Annotations,
            LocalFunctionDecl lfd => lfd.Annotations,
            LocalDecl ld => ld.Annotations,
            ClassDecl cd => cd.Annotations,
            EnumDecl ed => ed.Annotations,
            InterfaceDecl id => id.Annotations,
            ExtendDecl ed2 => ed2.Annotations,
            _ => [],
        };

        return (decl, anns);
    }

    private static void RecurseInto(PassContext ctx, AnnotationRegistry registry, Stmt stmt)
    {
        switch (stmt)
        {
            case DoBlockStmt d: RewriteStmtList(ctx, registry, d.Body); break;
            case TryStmt t:
                foreach (var block in t.Blocks) RewriteStmtList(ctx, registry, block);
                break;
            case WhileStmt w: RewriteStmtList(ctx, registry, w.Body); break;
            case RepeatStmt r: RewriteStmtList(ctx, registry, r.Body); break;
            case IfStmt ifs:
                RewriteStmtList(ctx, registry, ifs.Body);
                foreach (var clause in ifs.ElseIfs) RewriteStmtList(ctx, registry, clause.Body);
                if (ifs.ElseBody != null) RewriteStmtList(ctx, registry, ifs.ElseBody);
                break;
            case NumericForStmt nf: RewriteStmtList(ctx, registry, nf.Body); break;
            case GenericForStmt gf: RewriteStmtList(ctx, registry, gf.Body); break;
            case MatchStmt ms:
                foreach (var arm in ms.Arms) RewriteStmtList(ctx, registry, arm.Body);
                break;
            case FunctionDecl fd: RewriteStmtList(ctx, registry, fd.Body); break;
            case LocalFunctionDecl lfd: RewriteStmtList(ctx, registry, lfd.Body); break;
            case ExportStmt { Declaration: FunctionDecl efd }:
                RewriteStmtList(ctx, registry, efd.Body); break;
            case ExportStmt { Declaration: LocalFunctionDecl elfd }:
                RewriteStmtList(ctx, registry, elfd.Body); break;
        }
    }

    /// <summary>
    /// Runs all annotations on a single declaration in source order. Each annotation may replace the
    /// target with a single node or splice a list of stmts into the enclosing block.
    /// </summary>
    private static List<Stmt>? ApplyAnnotationsToDecl(
        PassContext ctx, AnnotationRegistry registry,
        Decl decl, List<Annotation> annotations, Stmt originalStmt)
    {
        var targetKind = TargetKindForDecl(decl);
        List<Stmt>? produced = null;
        var current = decl;
        var written = annotations.Where(annotation => !BuiltinAnnotations.IsBuiltin(annotation.Name.Name)).Select(annotation => annotation.ID).ToHashSet();

        foreach (var ann in annotations.ToList())
        {
            if (BuiltinAnnotations.IsBuiltin(ann.Name.Name)) continue;
            if (!registry.TryGet(ann.Name.Name, out var def))
            {
                ctx.Diag.Report(ann.Span, DiagnosticCode.ErrUnknownAnnotation, ann.Name.Name);
                continue;
            }

            if (!def.Targets.Contains(targetKind))
            {
                ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationTargetMismatch, ann.Name.Name,
                    targetKind.ToString());
                continue;
            }

            if (!BuildArgs(ctx, ann, def, out var args)) continue;

            var codec = new IRLuaCodec();
            var encoded = codec.Encode(current);

            using var runtime = NebraRuntime.CreateSandboxed();
            var loadErr = LoadDefinition(runtime, def);
            if (loadErr != null)
            {
                ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationRuntimeError, ann.Name.Name, loadErr);
                continue;
            }

            var result = runtime.CallGlobalFunction("apply", [encoded, args], out var callErr);
            if (callErr != null)
            {
                ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationRuntimeError, ann.Name.Name, callErr);
                continue;
            }

            try
            {
                if (IRLuaCodec.IsList(result))
                {
                    var stmtList = codec.DecodeStmtList(result, ctx.NodeAlloc, ann.Span);
                    RecordExpansion(ctx, ann, current, stmtList);
                    produced = Splice(produced, current, stmtList);
                    var next = stmtList.OfType<Decl>().FirstOrDefault(d => TargetKindForDecl(d) == targetKind);
                    if (next == null && annotations.Count > 1)
                    {
                        ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationMalformedResult, ann.Name.Name,
                            "chained annotations require the returned list to still contain the target kind");
                        break;
                    }

                    current = next ?? current;
                }
                else
                {
                    var node = codec.Decode(result, ctx.NodeAlloc, ann.Span);
                    if (node is not Decl newDecl)
                    {
                        ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationMalformedResult, ann.Name.Name,
                            $"expected a declaration node, got {node.GetType().Name}");
                        break;
                    }

                    RecordExpansion(ctx, ann, current, [newDecl]);
                    produced = Splice(produced, current, [newDecl]);
                    current = newDecl;
                }
            }
            catch (Exception ex)
            {
                ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationMalformedResult, ann.Name.Name, ex.Message);
                break;
            }
        }

        if (produced == null)
        {
            ClearAnnotationsOn(decl);
            return null;
        }

        foreach (var produce in produced.OfType<Decl>())
            RemoveAnnotations(produce, annotation => written.Contains(annotation.ID));

        if (originalStmt is ExportStmt)
        {
            for (var i = 0; i < produced.Count; i++)
            {
                if (produced[i] is Decl d && TargetKindForDecl(d) == targetKind)
                {
                    produced[i] = new ExportStmt(ctx.NodeAlloc.Next(), d.Span, d);
                    break;
                }
            }
        }

        return produced;
    }

    /// <summary>
    /// Puts what an annotation returned in place of the declaration it ran on, among what the
    /// annotations before it produced, so the statements each of them adds are kept.
    /// </summary>
    private static List<Stmt> Splice(List<Stmt>? produced, Decl current, List<Stmt> replacement)
    {
        if (produced == null) return [..replacement];

        var index = produced.FindIndex(stmt => ReferenceEquals(stmt, current));
        if (index < 0) return [..produced, ..replacement];

        produced.RemoveAt(index);
        produced.InsertRange(index, replacement);
        return produced;
    }

    /// <summary>
    /// Removes the annotations written on a declaration once all of them ran, so each one sees the
    /// others on its target. Annotations they add themselves, such as markers for reflection, stay.
    /// </summary>
    private static void RemoveAnnotations(Decl decl, Predicate<Annotation> processed)
    {
        switch (decl)
        {
            case FunctionDecl fd: fd.Annotations = fd.Annotations.Where(a => !processed(a)).ToList(); break;
            case LocalFunctionDecl lfd: lfd.Annotations = lfd.Annotations.Where(a => !processed(a)).ToList(); break;
            case LocalDecl ld: ld.Annotations = ld.Annotations.Where(a => !processed(a)).ToList(); break;
            case ClassDecl cd: cd.Annotations = cd.Annotations.Where(a => !processed(a)).ToList(); break;
            case EnumDecl ed: ed.Annotations = ed.Annotations.Where(a => !processed(a)).ToList(); break;
            case InterfaceDecl id: id.Annotations = id.Annotations.Where(a => !processed(a)).ToList(); break;
        }
    }

    private static void RecordExpansion(PassContext ctx, Annotation annotation, Decl target, List<Stmt> result)
    {
        if (!ctx.Cache.TryGetValue(AnnotationExpansion.CacheKey, out var existing) || existing is not List<AnnotationExpansion> expansions)
        {
            expansions = [];
            ctx.Cache[AnnotationExpansion.CacheKey] = expansions;
        }

        lock (expansions)
        {
            expansions.Add(new AnnotationExpansion(annotation, target, [..result]));
        }
    }

    /// <summary>
    /// Processes annotations on members inside class, interface, enum decls and on function parameters.
    /// </summary>
    private static void ProcessMemberAnnotations(PassContext ctx, AnnotationRegistry registry, Stmt stmt)
    {
        var decl = stmt switch
        {
            ExportStmt ex => ex.Declaration,
            Decl d => d,
            _ => null,
        };
        if (decl == null) return;

        switch (decl)
        {
            case ClassDecl cd:
                ProcessAnnotatedList(ctx, registry, cd.Fields, f => f.Annotations, AnnotationTargetKind.ClassField);
                ProcessAnnotatedList(ctx, registry, cd.Methods, m => m.Annotations, AnnotationTargetKind.ClassMethod);
                ProcessAnnotatedList(ctx, registry, cd.Accessors, a => a.Annotations,
                    AnnotationTargetKind.ClassAccessor);
                foreach (var classConstructor in cd.Constructors.Where(classConstructor => classConstructor.Annotations.Count > 0))
                    ApplyAnnotationsToMember(ctx, registry, classConstructor, classConstructor.Annotations,
                        AnnotationTargetKind.ClassConstructor);
                foreach (var m in cd.Methods) ProcessParameterAnnotations(ctx, registry, m.Parameters);
                foreach (var classConstructor in cd.Constructors) ProcessParameterAnnotations(ctx, registry, classConstructor.Parameters);
                foreach (var a in cd.Accessors) ProcessParameterAnnotations(ctx, registry, a.Parameters);
                break;
            case InterfaceDecl id:
                if (!id.IsContract)
                {
                    ProcessAnnotatedList(ctx, registry, id.Fields, f => f.Annotations, AnnotationTargetKind.InterfaceField);
                }

                ProcessAnnotatedList(ctx, registry, id.Methods, m => m.Annotations,
                    AnnotationTargetKind.InterfaceMethod);
                foreach (var m in id.Methods) ProcessParameterAnnotations(ctx, registry, m.Parameters);
                break;
            case EnumDecl ed:
                ProcessAnnotatedList(ctx, registry, ed.Members, m => m.Annotations, AnnotationTargetKind.EnumMember);
                break;
            case ExtendDecl ed2:
                ProcessAnnotatedList(ctx, registry, ed2.Methods, m => m.Annotations,
                    AnnotationTargetKind.ExtensionMethod);
                foreach (var m in ed2.Methods) ProcessParameterAnnotations(ctx, registry, m.Parameters);
                break;
            case FunctionDecl fd:
                ProcessParameterAnnotations(ctx, registry, fd.Parameters);
                break;
            case LocalFunctionDecl lfd:
                ProcessParameterAnnotations(ctx, registry, lfd.Parameters);
                break;
        }
    }

    /// <summary>
    /// Iterates a list of members, finds annotated ones and applies their annotations in place.
    /// Supports splice (list return) by expanding the list.
    /// </summary>
    private static void ProcessAnnotatedList<T>(PassContext ctx, AnnotationRegistry registry,
        List<T> members, Func<T, List<Annotation>> getAnnotations, AnnotationTargetKind targetKind) where T : class
    {
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var anns = getAnnotations(member);
            if (anns.Count == 0) continue;

            var annsCopy = new List<Annotation>(anns);
            anns.Clear();

            var current = (object)member;
            List<object>? produced = null;

            foreach (var ann in annsCopy)
            {
                if (BuiltinAnnotations.IsBuiltin(ann.Name.Name))
                {
                    anns.Add(ann);
                    continue;
                }
                if (!registry.TryGet(ann.Name.Name, out var def))
                {
                    ctx.Diag.Report(ann.Span, DiagnosticCode.ErrUnknownAnnotation, ann.Name.Name);
                    continue;
                }

                if (!def.Targets.Contains(targetKind))
                {
                    ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationTargetMismatch, ann.Name.Name,
                        targetKind.ToString());
                    continue;
                }

                if (!BuildArgs(ctx, ann, def, out var args)) continue;

                var codec = new IRLuaCodec();
                object? encoded;
                try
                {
                    encoded = codec.EncodeAny(current);
                }
                catch
                {
                    continue;
                }

                if (encoded == null) continue;

                using var runtime = NebraRuntime.CreateSandboxed();
                var loadErr = LoadDefinition(runtime, def);
                if (loadErr != null)
                {
                    ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationRuntimeError, ann.Name.Name, loadErr);
                    continue;
                }

                var result = runtime.CallGlobalFunction("apply", [encoded, args], out var callErr);
                if (callErr != null)
                {
                    ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationRuntimeError, ann.Name.Name, callErr);
                    continue;
                }

                try
                {
                    if (IRLuaCodec.IsList(result))
                    {
                        var nodes = DecodeListAs<T>(codec, result, ctx, ann);
                        if (nodes == null) break;
                        produced = nodes.Cast<object>().ToList();
                        current = produced.FirstOrDefault(o => o is T) ?? current;
                    }
                    else
                    {
                        var node = codec.DecodeAny(result, typeof(T), ctx.NodeAlloc, ann.Span);
                        if (node == null) break;
                        current = node;
                        produced = [node];
                    }
                }
                catch (Exception ex)
                {
                    ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationMalformedResult, ann.Name.Name, ex.Message);
                    break;
                }
            }

            if (produced == null) continue;

            var typed = produced.OfType<T>().ToList();
            if (typed.Count == 0) continue;

            members.RemoveAt(i);
            members.InsertRange(i, typed);
            i += typed.Count - 1;
        }
    }

    private static List<T>? DecodeListAs<T>(IRLuaCodec codec, object? result, PassContext ctx, Annotation ann)
        where T : class
    {
        try
        {
            if (result is not List<object?> list) return null;
            var nodes = new List<T>();
            foreach (var item in list)
            {
                if (item is not Dictionary<string, object?> itemDict) continue;
                var node = codec.DecodeAny(itemDict, typeof(T), ctx.NodeAlloc, ann.Span);
                if (node is T t) nodes.Add(t);
            }

            return nodes;
        }
        catch (Exception ex)
        {
            ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationMalformedResult, ann.Name.Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Applies a single member's annotations (for non-list targets like constructor).
    /// Replaces properties in-place via the annotation result.
    /// </summary>
    private static void ApplyAnnotationsToMember<T>(PassContext ctx, AnnotationRegistry registry,
        T member, List<Annotation> annotations, AnnotationTargetKind targetKind) where T : class
    {
        var annsCopy = new List<Annotation>(annotations);
        annotations.Clear();

        foreach (var ann in annsCopy)
        {
            if (BuiltinAnnotations.IsBuiltin(ann.Name.Name))
            {
                annotations.Add(ann);
                continue;
            }
            if (!registry.TryGet(ann.Name.Name, out var def))
            {
                ctx.Diag.Report(ann.Span, DiagnosticCode.ErrUnknownAnnotation, ann.Name.Name);
                continue;
            }

            if (!def.Targets.Contains(targetKind))
            {
                ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationTargetMismatch, ann.Name.Name,
                    targetKind.ToString());
                continue;
            }

            if (!BuildArgs(ctx, ann, def, out var args)) continue;

            if (member is not Node node) continue;

            var codec = new IRLuaCodec();
            var encoded = codec.Encode(node);

            using var runtime = NebraRuntime.CreateSandboxed();
            var loadErr = LoadDefinition(runtime, def);
            if (loadErr != null)
            {
                ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationRuntimeError, ann.Name.Name, loadErr);
                continue;
            }

            runtime.CallGlobalFunction("apply", [encoded, args], out var callErr);
            if (callErr != null)
            {
                ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationRuntimeError, ann.Name.Name, callErr);
            }
        }
    }

    private static void ProcessParameterAnnotations(PassContext ctx, AnnotationRegistry registry,
        List<Parameter> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];
            if (param.Annotations.Count == 0) continue;

            var annsCopy = new List<Annotation>(param.Annotations);
            param.Annotations.Clear();
            Node current = param;

            foreach (var ann in annsCopy)
            {
                if (BuiltinAnnotations.IsBuiltin(ann.Name.Name))
                {
                    param.Annotations.Add(ann);
                    continue;
                }
                if (!registry.TryGet(ann.Name.Name, out var def))
                {
                    ctx.Diag.Report(ann.Span, DiagnosticCode.ErrUnknownAnnotation, ann.Name.Name);
                    continue;
                }

                if (!def.Targets.Contains(AnnotationTargetKind.Parameter))
                {
                    ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationTargetMismatch, ann.Name.Name, "Parameter");
                    continue;
                }

                if (!BuildArgs(ctx, ann, def, out var args)) continue;

                var codec = new IRLuaCodec();
                var encoded = codec.Encode(current);

                using var runtime = NebraRuntime.CreateSandboxed();
                var loadErr = LoadDefinition(runtime, def);
                if (loadErr != null)
                {
                    ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationRuntimeError, ann.Name.Name, loadErr);
                    continue;
                }

                var result = runtime.CallGlobalFunction("apply", [encoded, args], out var callErr);
                if (callErr != null)
                {
                    ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationRuntimeError, ann.Name.Name, callErr);
                    continue;
                }

                try
                {
                    var node = codec.Decode(result, ctx.NodeAlloc, ann.Span);
                    if (node is Parameter p)
                    {
                        parameters[i] = p;
                        current = p;
                    }
                    else
                    {
                        ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationMalformedResult, ann.Name.Name,
                            $"expected Parameter, got {node.GetType().Name}");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationMalformedResult, ann.Name.Name, ex.Message);
                    break;
                }
            }
        }
    }

    private static void ClearAnnotationsOn(Decl decl)
    {
        switch (decl)
        {
            case FunctionDecl fd: fd.Annotations = BuiltinAnnotations.KeepBuiltins(fd.Annotations); break;
            case LocalFunctionDecl lfd: lfd.Annotations = BuiltinAnnotations.KeepBuiltins(lfd.Annotations); break;
            case LocalDecl ld: ld.Annotations = BuiltinAnnotations.KeepBuiltins(ld.Annotations); break;
            case ClassDecl cd: cd.Annotations = BuiltinAnnotations.KeepBuiltins(cd.Annotations); break;
            case EnumDecl ed: ed.Annotations = BuiltinAnnotations.KeepBuiltins(ed.Annotations); break;
            case InterfaceDecl id: id.Annotations = BuiltinAnnotations.KeepBuiltins(id.Annotations); break;
        }
    }

    private static AnnotationTargetKind TargetKindForDecl(Decl decl) => decl switch
    {
        FunctionDecl => AnnotationTargetKind.Function,
        LocalFunctionDecl => AnnotationTargetKind.LocalFunction,
        LocalDecl => AnnotationTargetKind.Variable,
        ClassDecl => AnnotationTargetKind.Class,
        EnumDecl => AnnotationTargetKind.Enum,
        InterfaceDecl => AnnotationTargetKind.Interface,
        ExtendDecl => AnnotationTargetKind.Extend,
        _ => AnnotationTargetKind.Function,
    };

    /// <summary>
    /// Validates the annotation's arguments against the declared parameter spec and produces the
    /// <c>args</c> table passed to <c>apply(target, args)</c>. Only constant literals of the types
    /// declared in <c>meta.params</c> are accepted.
    /// </summary>
    private static bool BuildArgs(PassContext ctx, Annotation ann, AnnotationDefinition def,
        out Dictionary<string, object?> args)
    {
        args = new Dictionary<string, object?>();
        var specByName = def.Parameters.ToDictionary(p => p.Name);
        var specList = def.Parameters;

        var positionalIdx = 0;
        foreach (var arg in ann.Args)
        {
            string name;
            if (arg.Name != null)
            {
                name = arg.Name;
                if (!specByName.ContainsKey(name))
                {
                    ctx.Diag.Report(arg.Span, DiagnosticCode.ErrAnnotationArgUnknown, ann.Name.Name, name);
                    return false;
                }
            }
            else
            {
                if (positionalIdx >= specList.Count)
                {
                    ctx.Diag.Report(arg.Span, DiagnosticCode.ErrAnnotationArgUnknown, ann.Name.Name,
                        $"<positional #{positionalIdx + 1}>");
                    return false;
                }

                name = specList[positionalIdx].Name;
                positionalIdx++;
            }

            if (!TryFoldLiteral(arg.Value, out var folded))
            {
                ctx.Diag.Report(arg.Span, DiagnosticCode.ErrAnnotationArgNotLiteral, ann.Name.Name, name);
                return false;
            }

            if (!TypeMatchesSpec(specByName[name].TypeName, folded, out var actualLabel))
            {
                ctx.Diag.Report(arg.Span, DiagnosticCode.ErrAnnotationArgTypeMismatch,
                    ann.Name.Name, name, specByName[name].TypeName, actualLabel);
                return false;
            }

            args[name] = folded;
        }

        foreach (var spec in specList)
        {
            if (args.ContainsKey(spec.Name)) continue;
            if (spec.Required)
            {
                ctx.Diag.Report(ann.Span, DiagnosticCode.ErrAnnotationArgMissing, ann.Name.Name, spec.Name);
                return false;
            }

            args[spec.Name] = spec.DefaultValue;
        }

        return true;
    }

    /// <summary>
    /// Validates a folded annotation argument against the type name declared in
    /// the annotation's <c>meta.params[name].type</c> field. Recognised type
    /// names: <c>any</c>, <c>string</c>, <c>number</c>, <c>boolean</c>,
    /// <c>table</c>, <c>nil</c>. Unknown type names (e.g. user-defined enums)
    /// fall through as acceptable so authors can declare custom param types
    /// without the validator blocking them. Always allows <c>nil</c> for
    /// optional parameters so <c>@log</c> without a message stays valid.
    /// </summary>
    public static bool TypeMatchesSpec(string typeName, object? value, out string actualLabel)
    {
        actualLabel = value switch
        {
            null => "nil",
            string => "string",
            bool => "boolean",
            long or double or int or float => "number",
            System.Collections.IDictionary => "table",
            System.Collections.IList => "table",
            _ => value.GetType().Name
        };

        if (value == null) return true;

        return typeName.ToLowerInvariant() switch
        {
            "any" or "" => true,
            "string" => value is string,
            "number" => value is long or double or int or float,
            "boolean" or "bool" => value is bool,
            "table" => value is System.Collections.IDictionary or System.Collections.IList,
            "nil" => value == null,
            _ => true
        };
    }

    public static bool TryFoldLiteral(Expr expr, out object? value)
    {
        value = null;
        switch (expr)
        {
            case NilLiteralExpr:
                value = null;
                return true;
            case BoolLiteralExpr b:
                value = b.Value;
                return true;
            case StringLiteralExpr s:
                value = s.Value;
                return true;
            case NumberLiteralExpr n:
                if (long.TryParse(n.Raw, out var l))
                {
                    value = l;
                    return true;
                }

                if (double.TryParse(n.Raw, System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    value = d;
                    return true;
                }

                return false;
            case UnaryExpr { Op: UnaryOp.Negate, Operand: NumberLiteralExpr nn }:
                if (long.TryParse(nn.Raw, out var nl))
                {
                    value = -nl;
                    return true;
                }

                if (double.TryParse(nn.Raw, System.Globalization.CultureInfo.InvariantCulture, out var nd))
                {
                    value = -nd;
                    return true;
                }

                return false;
            case NameExpr ne:
                value = ne.Name.Name;
                return true;
            case DotAccessExpr { Object: NameExpr } dot:
                value = dot.FieldName.Name;
                return true;
            case TableConstructorExpr t:
            {
                var hasNamed = t.Fields.Any(f => f.Name != null);
                if (hasNamed)
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var f in t.Fields)
                    {
                        if (f.Name == null) return false;
                        if (!TryFoldLiteral(f.Value, out var fv)) return false;
                        dict[f.Name.Name] = fv;
                    }

                    value = dict;
                    return true;
                }

                var list = new List<object?>();
                foreach (var f in t.Fields)
                {
                    if (f.Name != null) return false;
                    if (!TryFoldLiteral(f.Value, out var fv)) return false;
                    list.Add(fv);
                }

                value = list;
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// Loads an annotation definition into a fresh runtime, together with the modules it imports.
    /// </summary>
    private static string? LoadDefinition(NebraRuntime runtime, AnnotationDefinition def)
    {
        return runtime.InstallModules(def.SourcePath, def.Modules, def.Imports) ?? runtime.LoadAndRun(def.CompiledLua, def.Name);
    }
}