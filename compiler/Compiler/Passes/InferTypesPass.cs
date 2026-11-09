using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

/// <summary>
/// The infer types pass is responsible for inferring the types of expressions in the source code. It takes care of
/// inferring the types of variables, function return types, and other expressions based on their usage and context.
/// </summary>
public sealed partial class InferTypesPass() : Pass(PassName, PassScope.PerBuild)
{
    public const string PassName = "InferTypes";
    private int _asyncDepth;

    /// <summary>
    /// The <c>super(...)</c> calls written as statements of the constructor being resolved, the
    /// only place one may appear; <see cref="CheckSuperCall"/> checks where among them it stands.
    /// </summary>
    private HashSet<SuperCallExpr> _constructorSuperCalls = [];

    /// <summary>The target of the assignment being typed, which may be a property with only a setter.</summary>
    private Expr? _assignmentTarget;

    /// <summary>The interface whose default each class took for a method it does not declare itself.</summary>
    private readonly Dictionary<(ClassType, string), InterfaceType> _defaultSources = [];

    /// <summary>
    /// Tracks class/interface decl nodes we've already resolved so the
    /// pre-phase pass and the regular per-file walk don't duplicate work
    /// (and don't double-report diagnostics on method bodies).
    /// </summary>
    private readonly HashSet<NodeID> _resolvedClassDecls = [];
    private readonly Dictionary<NodeID, List<(ClassMethodNode Method, Type ReturnType)>> _pendingClassBodies = [];
    private readonly HashSet<ClassMethodNode> _methodsInferringReturn = [];
    private readonly HashSet<NodeID> _resolvedInterfaceDecls = [];
    private readonly HashSet<NodeID> _resolvedExtendDecls = [];

    /// <summary>
    /// The class whose body is currently being resolved, or <c>null</c> outside any class. Access
    /// to a protected member is legal from here and from anything deriving from the class that
    /// declared it, so the check needs to know where it stands.
    /// </summary>
    private ClassType? _currentClass;

    /// <summary>
    /// How many loops enclose the statement being resolved, within the current function. A
    /// <c>break</c> carrying a depth may not ask for more levels than this. The count is reset
    /// when a function body is entered, since a loop outside that function is not one the
    /// <c>break</c> can leave.
    /// </summary>
    private int _loopDepth;
    private readonly HashSet<NodeID> _registeredExtendDecls = [];

    /// <summary>
    /// Identifies a value location for flow-narrowing. Either a plain symbol (NameExpr) or a chain of
    /// non-optional dot accesses rooted at a symbol.
    /// </summary>
    private abstract record AccessPath;

    private sealed record SymPath(SymID Sym) : AccessPath;

    private sealed record FieldPath(AccessPath Base, string Field) : AccessPath;

    /// <summary>
    /// A single case in an exhaustive-match chain. Either a type test (`x is T`) or an enum member
    /// equality test (`x == Enum.Member`).
    /// </summary>
    private abstract record MatchCase;

    private sealed record TypeMatchCase(TypID TargetType) : MatchCase;

    private sealed record EnumMemberMatchCase(TypID EnumTypeId, string Member) : MatchCase;

    private readonly Dictionary<AccessPath, TypID> _narrowed = new();

    /// <summary>
    /// Runs in three phases so that every check sees a fully-populated type universe regardless of
    /// the order packages and files happen to be walked in.
    /// <para>
    /// Phase 0 types the signatures that follow from annotations alone — <c>declare</c>d globals
    /// and any function with an explicit return type — and pushes them onto the import bindings
    /// that reference them. A body checked later needs those signatures: a <c>never</c> return
    /// decides whether a call diverges, which drives the unreachable-code and never-completion
    /// checks.
    /// </para>
    /// <para>
    /// Phase 1 resolves class, interface and extend declarations across the whole build so that
    /// expression-level checks in phase 2 see complete member tables. Without it an installed
    /// types-only package whose <c>.d.neb</c> is processed after a consumer's source file would
    /// leave <c>Entity.Methods</c> empty when <c>Entity.Subscribe(...)</c> is checked, and the call
    /// would fall through to <c>any</c>. It runs in two sweeps: interfaces first across every
    /// file, then the rest. A class is checked against the interfaces it implements as it is
    /// resolved, so an interface declared in another file (or further down the same one) would
    /// otherwise still be empty at that point, and every check against it would silently pass.
    /// </para>
    /// <para>
    /// Phase 2 is the full per-file walk, ordered so importers run after their import targets.
    /// Earlier passes propagate source-side symbol types onto import bindings, but value-level
    /// declarations only get their types here, so re-propagating after each package pushes the
    /// freshly inferred types onto every importer.
    /// </para>
    /// </summary>
    public override bool Run(PassContext context)
    {
        // Filling a class's members is what makes it usable from another module, and the loops
        // below do it one package at a time. Walking them in discovery order - filesystem order
        // for the project, import order for a dependency's modules - would type-check a user of
        // a class before the class itself was populated, so every stage runs in import order.
        var ordered = TopoSortPackages(context);

        foreach (var pkg in ordered)
        {
            foreach (var file in pkg.Files)
            {
                ResolveSignatures(MakeFileContext(context, pkg, file), file.Hir.Body);
            }
        }

        foreach (var pkg in ordered)
            foreach (var file in pkg.Files)
                ResolveImportsPass.PropagateImportTypes(context, pkg, file);

        foreach (var pkg in ordered)
            foreach (var file in pkg.Files)
                ResolveClassSignatures(MakeFileContext(context, pkg, file), file.Hir.Body);

        foreach (var pkg in ordered)
        {
            foreach (var file in pkg.Files)
            {
                var fileCtx = MakeFileContext(context, pkg, file);
                _narrowed.Clear();
                ResolveInterfaceUniverseDecls(fileCtx, file.Hir.Body);
            }
        }

        foreach (var pkg in ordered)
        {
            foreach (var file in pkg.Files)
            {
                var fileCtx = MakeFileContext(context, pkg, file);
                _narrowed.Clear();
                TypeAnnotatedTopLevelLocals(fileCtx, pkg, file);
                foreach (var defer in DeferStmt.In(file.Hir.Body))
                    fileCtx.Diag.Report(defer.Span, DiagnosticCode.ErrDeferOutsideFunction);
            }
        }

        foreach (var pkg in ordered)
        {
            foreach (var file in pkg.Files)
            {
                var fileCtx = MakeFileContext(context, pkg, file);
                _narrowed.Clear();
                foreach (var cd in ClassDeclsIn(file.Hir.Body))
                    DeclareClassMembers(fileCtx, cd);
            }
        }

        foreach (var pkg in ordered)
        {
            foreach (var file in pkg.Files)
            {
                var fileCtx = MakeFileContext(context, pkg, file);
                _narrowed.Clear();
                ResolveTypeUniverseDecls(fileCtx, file.Hir.Body);
            }
        }

        // Every extension in the build is registered before any body is resolved, so a use may
        // precede the `extend` block that declares it, and a use in a module that does not import
        // the declaring one is still recognised and reported.
        foreach (var pkg in ordered)
        {
            foreach (var file in pkg.Files)
            {
                var registerCtx = MakeFileContext(context, pkg, file);
                foreach (var (ed, module) in CollectExtendDecls(file.Hir.Body))
                    RegisterExtensionSignatures(registerCtx, ed, module);
            }
        }

        foreach (var pkg in ordered)
        {
            foreach (var file in pkg.Files)
            {
                var fileCtx = MakeFileContext(context, pkg, file);
                _narrowed.Clear();
                ResolveStmts(fileCtx, file.Hir.Body, file.Hir.Return);
            }

            foreach (var importerPkg in context.Pkgs)
                foreach (var importerFile in importerPkg.Files)
                    ResolveImportsPass.PropagateImportTypes(context, importerPkg, importerFile);
        }

        ReportUnresolvedNames();
        return true;
    }

    /// <summary>
    /// Whether <paramref name="iface"/> or an interface it extends declares <paramref name="name"/>
    /// with <c>method</c>, making it an instance method that has to be called with <c>:</c>.
    /// </summary>
    private static bool DeclaresInstanceMethod(InterfaceType iface, string name)
    {
        return new[] { iface }.Concat(Type.BaseInterfacesOf(iface))
            .Any(candidate => candidate.MethodNodes.TryGetValue(name, out var node) && node.IsMethod);
    }

    /// <summary>
    /// The type of <paramref name="value"/> where a value of type <paramref name="expected"/> is
    /// wanted. An empty table literal has no fields to tell its shape from, so it takes the list or
    /// map type expected of it instead of being a list of anything. A table type with fields it
    /// does not have is still rejected.
    /// </summary>
    private TypID SynthesizeFor(PassContext pc, Expr value, TypID expected)
    {
        if (value is FunctionDefExpr literal && TakesSignatureFromContext(literal))
            return SynthesizeWithSignature(pc, literal, ExpectedSignature(pc, Evaluate(pc, expected)));

        if (ContextualTypeFor(pc, value, expected) is { } literalType)
        {
            SynthesizeExpr(pc, value);
            return literalType;
        }

        var synthesized = value is TableConstructorExpr table
            ? SynthesizeTableFor(pc, table, Evaluate(pc, expected))
            : SynthesizeExpr(pc, value);
        return IsEmptyTable(value) && TakesEmptyTable(pc, expected) ? expected : synthesized;
    }

    private static bool IsEmptyTable(Expr value) => value is TableConstructorExpr { Fields.Count: 0 };

    /// <summary>
    /// Whether an empty table literal can be a value of <paramref name="expected"/>: a list or map
    /// type, or a struct type, such as <c>Partial&lt;User&gt;</c>, whose fields may all be left out.
    /// </summary>
    private bool TakesEmptyTable(PassContext pc, TypID expected)
    {
        if (IsTableShape(pc, expected)) return true;
        return pc.Pkg!.Types.GetByID(Evaluate(pc, StripNil(pc, expected)), out var shape)
               && shape is StructType structType
               && structType.Fields.All(field => field.IsMeta || IsNullable(pc, field.Type.ID));
    }

    /// <summary>Whether <paramref name="type"/> is a list or map type, or a union holding one.</summary>
    private static bool IsTableShape(PassContext pc, TypID type)
    {
        if (!pc.Types.GetByID(type, out var resolved)) return false;
        return resolved switch
        {
            TableArrayType or TableMapType => true,
            UnionType union => union.Types.Any(member => member is TableArrayType or TableMapType),
            _ => false
        };
    }

    /// <summary>
    /// Gives the symbol a static member of <paramref name="cd"/> is reached by inside the class its
    /// type; <paramref name="overload"/> picks among the overloads of one name, in declaration order.
    /// </summary>
    private static void SetStaticMemberType(PassContext pc, ClassDecl cd, string name, int overload, Type type)
    {
        if (!pc.Pkg!.Scopes.EnclosingScope(cd.ID, out var classScope)) return;

        var symbols = pc.Pkg.Scopes.LookupAll(classScope, name)
            .Where(id => pc.Pkg.Syms.GetByID(id, out var symbol) && symbol.Flags.HasFlag(SymbolFlags.Static) && symbol.Owner == classScope)
            .ToList();
        if (overload < symbols.Count) pc.Pkg.Syms.SetType(symbols[overload], type.ID);
    }

    /// <summary>
    /// Gives every top-level local written with a type annotation that type before any class is
    /// resolved. A class's methods are checked before the statements around the class are, so a
    /// method reading such a local would otherwise see it untyped.
    /// </summary>
    private static void TypeAnnotatedTopLevelLocals(PassContext pc, PackageContext pkg, PreparsedFile file)
    {
        foreach (var statement in file.Hir.Body)
        {
            if ((statement is ExportStmt export ? export.Declaration : statement) is not LocalDecl local) continue;

            foreach (var variable in local.Variables)
            {
                var annotated = variable.TypeAnnotation?.ResolvedType ?? TypID.Invalid;
                if (annotated == TypID.Invalid || !pkg.Syms.GetByID(variable.Name.Sym, out var symbol) || symbol.Type != TypID.Invalid) continue;
                pkg.Syms.SetType(variable.Name.Sym, annotated);
            }
        }
    }

    /// <summary>
    /// Types the default value of a class field and checks it against the field's annotation the
    /// way a local's value is checked against its own, so <c>name: string = nil</c> is rejected
    /// under <c>strict_nil</c> and a default of the wrong type always is.
    /// </summary>
    private void CheckFieldDefault(PassContext pc, ClassFieldNode field)
    {
        var annotated = field.TypeAnnotation?.ResolvedType ?? TypID.Invalid;
        var defaultType = SynthesizeFor(pc, field.DefaultValue!, annotated);
        if (annotated == TypID.Invalid || defaultType == TypID.Invalid) return;

        if (!IsTypeAssignable(pc, annotated, defaultType))
        {
            pc.Diag.Report(field.DefaultValue!.Span, DiagnosticCode.ErrTypeMismatch, TypeName(pc, annotated), TypeName(pc, defaultType));
        }
    }

    /// <summary>
    /// Adds <paramref name="item"/> unless <paramref name="list"/> holds it already. Classes and
    /// interfaces are shared by name between packages while every package resolves its own copy
    /// of a library's declarations, so the same relation is resolved once per package; adding it
    /// each time would grow the list with the package count and every lookup walking it with it.
    /// </summary>
    private static void AddOnce<T>(List<T> list, T item)
    {
        if (!list.Contains(item)) list.Add(item);
    }

    /// <summary>
    /// Orders packages so that an importer always follows its targets. Uses
    /// the <c>import_resolved:&lt;file&gt;:&lt;module&gt;</c> cache entries
    /// laid down by <see cref="ResolveImportsPass"/> to discover edges. Cycles
    /// are tolerated (the offending package is emitted at its discovery
    /// point); the cost of a wrong order inside a cycle is at worst the same
    /// "untyped callee" problem we had before, so this is no regression.
    /// </summary>
    private static List<PackageContext> TopoSortPackages(PassContext context)
    {
        var fileToPkg = new Dictionary<PreparsedFile, PackageContext>();
        foreach (var pkg in context.Pkgs)
            foreach (var file in pkg.Files)
                fileToPkg[file] = pkg;

        var deps = new Dictionary<PackageContext, HashSet<PackageContext>>();
        foreach (var pkg in context.Pkgs) deps[pkg] = [];

        foreach (var pkg in context.Pkgs)
        {
            foreach (var file in pkg.Files)
            {
                foreach (var stmt in file.Hir.Body)
                {
                    if (stmt is not ImportStmt import) continue;
                    var key = $"import_resolved:{file.Filename}:{import.Module.Name}";
                    if (!context.Cache.TryGetValue(key, out var obj) || obj is not ResolvedModule resolved) continue;
                    if (resolved.File == null) continue;
                    if (!fileToPkg.TryGetValue(resolved.File, out var depPkg)) continue;
                    if (depPkg == pkg) continue;
                    deps[pkg].Add(depPkg);
                }
            }
        }

        var ordered = new List<PackageContext>();
        var visited = new HashSet<PackageContext>();
        var onStack = new HashSet<PackageContext>();

        foreach (var pkg in context.Pkgs) Visit(pkg);
        return ordered;

        void Visit(PackageContext pkg)
        {
            if (visited.Contains(pkg)) return;
            if (!onStack.Add(pkg)) return;
            foreach (var dep in deps[pkg]) Visit(dep);
            onStack.Remove(pkg);
            visited.Add(pkg);
            ordered.Add(pkg);
        }
    }

    private static PassContext MakeFileContext(PassContext parent, PackageContext pkg, PreparsedFile file)
        => new(parent.Diag, parent.Pkgs, pkg, file, parent.Types, parent.SymAlloc,
               parent.ScopeAlloc, parent.NodeAlloc, parent.Names, parent.Cache, parent.Config);

    /// <summary>
    /// Types the function signatures that follow from annotations alone, without walking any body:
    /// <c>declare</c>d functions (including module members) and functions carrying an explicit
    /// return type. Phase 2 recomputes the same signatures from the same annotations, so running
    /// this early only fills them in sooner.
    /// <para>
    /// <c>declare</c>d <em>variables</em> are deliberately excluded. Their annotation is often a
    /// named interface whose member table is not populated until phase 1, and typing the variable
    /// before then would let a phase-1 body see an interface that still looks empty and report a
    /// missing member that does exist.
    /// </para>
    /// </summary>
    private void ResolveSignatures(PassContext pc, List<Stmt> stmts)
    {
        foreach (var stmt in stmts)
        {
            ResolveSignature(pc, stmt is ExportStmt es ? es.Declaration : stmt);
        }
    }

    private void ResolveSignature(PassContext pc, Stmt stmt)
    {
        switch (stmt)
        {
            case DeclareFunctionDecl dfd:
                ResolveDecl(pc, dfd);
                break;
            case DeclareModuleDecl dmd:
                foreach (var member in dmd.Members) ResolveSignature(pc, member);
                break;
            case FunctionDecl fd:
                ResolveAnnotatedSignature(pc, fd.Parameters, fd.ReturnType,
                    fd.NamePath.Count == 1 && fd.MethodName == null ? fd.NamePath[0] : null, fd.IsAsync);
                break;
            case LocalFunctionDecl lfd:
                ResolveAnnotatedSignature(pc, lfd.Parameters, lfd.ReturnType, lfd.Name, lfd.IsAsync);
                break;
        }
    }

    /// <summary>
    /// Builds a function's type from its parameter and return annotations and stamps it on the
    /// function symbol. Mirrors the signature half of <see cref="ResolveFunctionLike"/>; functions
    /// without a declared return type are skipped because theirs is inferred from the body.
    /// </summary>
    private void ResolveAnnotatedSignature(PassContext pc, List<Parameter> parameters, TypeRef? returnTypeRef,
        NameRef? funcName, bool isAsync)
    {
        if (returnTypeRef == null || returnTypeRef.ResolvedType == TypID.Invalid) return;
        if (funcName is not { Sym: var funcSym } || funcSym == SymID.Invalid) return;

        var paramTypes = new List<Tuple<string, Type>>();
        var isVararg = false;
        Type? varargType = null;
        var defaultIndices = new List<int>();

        for (var i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];
            var t = ResolveParamType(pc, param);
            if (param.IsVararg)
            {
                isVararg = true;
                varargType = t.Kind == TypeKind.PrimitiveAny ? null : t;
                continue;
            }

            paramTypes.Add(new Tuple<string, Type>(param.Name.Name, t));
            if (param.DefaultValue != null) defaultIndices.Add(i);
        }

        var funcTyp = pc.Types.FuncOf(paramTypes, GetType(pc, returnTypeRef.ResolvedType), isVararg, varargType,
            defaultIndices.Count > 0 ? defaultIndices : null, isAsync,
            predicate: BuildPredicate(pc, returnTypeRef, parameters));
        pc.Pkg!.Syms.SetType(funcSym, funcTyp);
    }

    /// <summary>
    /// Resolves only the interface declarations in a statement list. Runs over every file before
    /// <see cref="ResolveTypeUniverseDecls"/>, so a class is always checked against a complete
    /// interface member table regardless of declaration order. Re-entry is guarded by
    /// <see cref="_resolvedInterfaceDecls"/>, so the main sweep skips whatever this one covered.
    /// </summary>
    private void ResolveInterfaceUniverseDecls(PassContext pc, List<Stmt> stmts)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case InterfaceDecl id:
                    ResolveInterfaceDecl(pc, id);
                    break;
                case ExportStmt { Declaration: InterfaceDecl eid }:
                    ResolveInterfaceDecl(pc, eid);
                    break;
                case DeclareModuleDecl dmd:
                    foreach (var member in dmd.Members)
                    {
                        if (member is InterfaceDecl mid) ResolveInterfaceDecl(pc, mid);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Pre-phase: visit every declaration that contributes type info to the
    /// symbol table (class/interface members, declare-variable types,
    /// declare-function signatures, declare-module members). Without this,
    /// e.g. `declare Steam: SteamStatic` in a freshly-loaded .d.neb file
    /// would not have its symbol typed until that file's normal walk —
    /// which might happen AFTER a consumer file uses `Steam.X` and falls
    /// through to `any`. Body resolution on classes/interfaces happens here
    /// too; the re-entry guard skips it in Phase 2.
    /// </summary>
    private void ResolveTypeUniverseDecls(PassContext pc, List<Stmt> stmts)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case ClassDecl cd:
                    ResolveClassDecl(pc, cd);
                    break;
                case InterfaceDecl id:
                    ResolveInterfaceDecl(pc, id);
                    break;
                case ExtendDecl ed:
                    ResolveExtendDecl(pc, ed);
                    break;
                case DeclareVariableDecl dvd:
                    ResolveDecl(pc, dvd);
                    break;
                case DeclareFunctionDecl dfd:
                    ResolveDecl(pc, dfd);
                    break;
                case ExportStmt es:
                    switch (es.Declaration)
                    {
                        case ClassDecl ecd: ResolveClassDecl(pc, ecd); break;
                        case InterfaceDecl eid: ResolveInterfaceDecl(pc, eid); break;
                        case DeclareVariableDecl edvd: ResolveDecl(pc, edvd); break;
                        case DeclareFunctionDecl edfd: ResolveDecl(pc, edfd); break;
                    }
                    break;
                case DeclareModuleDecl dmd:
                    foreach (var member in dmd.Members)
                    {
                        switch (member)
                        {
                            case ClassDecl mcd: ResolveClassDecl(pc, mcd); break;
                            case InterfaceDecl mid: ResolveInterfaceDecl(pc, mid); break;
                            case DeclareVariableDecl mdvd: ResolveDecl(pc, mdvd); break;
                            case DeclareFunctionDecl mdfd: ResolveDecl(pc, mdfd); break;
                            case ExtendDecl med: ResolveExtendDecl(pc, med); break;
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>Types a returned value, a lone table literal by the shape the function declares it returns.</summary>
    private void SynthesizeReturned(PassContext pc, Expr value, int returnedCount)
    {
        if (value is TableConstructorExpr table && returnedCount == 1 && _declaredReturn != TypID.Invalid)
        {
            SynthesizeTableFor(pc, table, Evaluate(pc, _declaredReturn));
            return;
        }

        SynthesizeExpr(pc, value);
    }

    /// <summary>
    /// Resolves a function body: the same walk as <see cref="ResolveStmts"/>, but with the
    /// enclosing-loop count reset for its duration. A loop around the function is not one a
    /// <c>break</c> inside it can leave, so it must not count towards the depth a break may ask
    /// for.
    /// </summary>
    private void ResolveFunctionBody(PassContext pc, List<Stmt> stmts, Stmt? tail = null, TypID? declaredReturn = null)
    {
        var enclosingLoops = _loopDepth;
        var enclosingReturn = _declaredReturn;
        var enclosingTry = (_tryDepth, _finallyDepth);
        _loopDepth = 0;
        _declaredReturn = declaredReturn ?? TypID.Invalid;
        (_tryDepth, _finallyDepth) = (0, 0);
        try
        {
            ResolveStmts(pc, stmts, tail);
        }
        finally
        {
            _loopDepth = enclosingLoops;
            _declaredReturn = enclosingReturn;
            (_tryDepth, _finallyDepth) = enclosingTry;
        }
    }

    /// <summary>
    /// Resolves a block statement by statement, followed by its optional trailing statement (the
    /// tail <c>return</c>, which the HIR keeps outside the body list). Two flow-sensitive effects
    /// are applied while walking it: statements that follow one which can never fall through are
    /// reported as unreachable, and an <c>if</c> whose only branch always exits (<c>return</c>,
    /// <c>break</c>, or a call returning <c>never</c>) narrows its condition's else-side over the
    /// rest of the block — the guard-clause shape.
    /// </summary>
    private void ResolveStmts(PassContext pc, List<Stmt> stmts, Stmt? tail = null)
    {
        var flowNarrows = new List<(AccessPath path, TypID prev, bool hadPrev)>();
        var reportedUnreachable = false;

        for (var i = 0; i < stmts.Count; i++)
        {
            var stmt = stmts[i];
            var carried = ResolveStmt(pc, stmt);
            if (carried is { Count: > 0 })
            {
                flowNarrows.AddRange(PushAllNarrows(carried));
            }

            var next = i + 1 < stmts.Count ? stmts[i + 1] : tail;
            if (reportedUnreachable || next == null) continue;
            reportedUnreachable = ReportIfUnreachable(pc, stmt, next);
        }

        if (tail != null) ResolveStmt(pc, tail);

        PopAllNarrows(flowNarrows);
    }

    /// <summary>
    /// Reports <paramref name="next"/> as dead code when <paramref name="stmt"/> can never fall
    /// through to it, and returns whether it did. A terminator (<c>return</c>, <c>break</c>, ...)
    /// is a warning for parity with plain Lua; a call whose declared return type is <c>never</c>
    /// is an error, since the signature states outright that control does not come back.
    /// </summary>
    private bool ReportIfUnreachable(PassContext pc, Stmt stmt, Stmt next)
    {
        if (IsTerminator(stmt))
        {
            pc.Diag.Report(next.Span, DiagnosticCode.WrnUnreachableCode);
            return true;
        }

        if (stmt is ExprStmt es && IsDivergingCall(pc, es.Expression))
        {
            pc.Diag.Report(next.Span, DiagnosticCode.ErrUnreachableAfterNever,
                DescribeCallee(es.Expression));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a single statement. Returns the narrowings that stay in effect for the statements
    /// that follow it in the same block (a guard-clause <c>if</c> whose body always exits), or
    /// <c>null</c> when the statement carries nothing forward.
    /// </summary>
    private List<(AccessPath path, TypID typ)>? ResolveStmt(PassContext pc, Stmt stmt)
    {
        if (stmt == null) return null;
        switch (stmt)
        {
            case Decl decl:
                ResolveDecl(pc, decl);
                break;
            case AssignStmt assignStmt:
                ResolveAssignStmt(pc, assignStmt);
                break;
            case ExprStmt exprStmt:
                SynthesizeExpr(pc, exprStmt.Expression);
                break;
            case BreakStmt breakStmt:
                if (_tryDepth > 0 && breakStmt.Depth > _loopDepth)
                {
                    pc.Diag.Report(breakStmt.Span, DiagnosticCode.ErrJumpOutOfTry, "break");
                }
                else if (breakStmt.Depth < 1 || breakStmt.Depth > _loopDepth)
                {
                    pc.Diag.Report(breakStmt.Span, DiagnosticCode.ErrInvalidControlFlowDepth, "break");
                }
                else if (breakStmt.Depth > 1 && !Codegen.LuaFeatureSet.For(pc.Config.Target).HasGoto)
                {
                    pc.Diag.Report(breakStmt.Span, DiagnosticCode.ErrMultiLevelBreakUnsupported,
                        breakStmt.Depth, pc.Config.Target);
                }
                break;
            case LabelStmt:
                break;
            case GotoStmt gotoStmt:
                if (_tryDepth > 0) pc.Diag.Report(gotoStmt.Span, DiagnosticCode.ErrJumpOutOfTry, "goto");
                break;
            case TryStmt tryStmt:
                ResolveTry(pc, tryStmt);
                break;
            case DoBlockStmt doBlockStmt:
                ResolveStmts(pc, doBlockStmt.Body);
                break;
            case WhileStmt whileStmt:
            {
                SynthesizeExpr(pc, whileStmt.Condition);
                var (whileTrue, _) = AnalyzeCondition(pc, whileStmt.Condition);
                var whileSaved = PushAllNarrows(whileTrue);
                _loopDepth++;
                ResolveStmts(pc, whileStmt.Body);
                _loopDepth--;
                PopAllNarrows(whileSaved);
                break;
            }
            case RepeatStmt repeatStmt:
                _loopDepth++;
                ResolveStmts(pc, repeatStmt.Body);
                _loopDepth--;
                SynthesizeExpr(pc, repeatStmt.Condition);
                break;
            case IfStmt ifStmt:
            {
                // Error recovery can hand us an `if` with no condition (`if then end`). The syntax
                // error is already reported, so the branch bodies are still walked for whatever
                // else they contain, but nothing is inferred from the missing condition.
                if (ifStmt.Condition == null)
                {
                    ResolveStmts(pc, ifStmt.Body);
                    foreach (var elseIf in ifStmt.ElseIfs) ResolveStmts(pc, elseIf.Body);
                    if (ifStmt.ElseBody != null) ResolveStmts(pc, ifStmt.ElseBody);
                    break;
                }

                SynthesizeExpr(pc, ifStmt.Condition);

                var (thenNarrows, elseNarrows) = AnalyzeCondition(pc, ifStmt.Condition);
                var thenSaved = PushAllNarrows(thenNarrows);
                ResolveStmts(pc, ifStmt.Body);
                PopAllNarrows(thenSaved);

                var earlierFalse = PushAllNarrows(elseNarrows);
                foreach (var elseIf in ifStmt.ElseIfs)
                {
                    SynthesizeExpr(pc, elseIf.Condition);
                    var (eiThen, eiElse) = AnalyzeCondition(pc, elseIf.Condition);
                    var eiSaved = PushAllNarrows(eiThen);
                    ResolveStmts(pc, elseIf.Body);
                    PopAllNarrows(eiSaved);
                    earlierFalse.AddRange(PushAllNarrows(eiElse));
                }

                if (ifStmt.ElseBody != null)
                    ResolveStmts(pc, ifStmt.ElseBody);
                PopAllNarrows(earlierFalse);

                CheckExhaustiveMatch(pc, ifStmt);

                if (ifStmt.ElseBody == null && ifStmt.ElseIfs.Count == 0
                    && elseNarrows.Count > 0 && BlockAlwaysExits(pc, ifStmt.Body))
                {
                    return elseNarrows;
                }

                break;
            }
            case NumericForStmt nf:
            {
                var ts = SynthesizeExpr(pc, nf.Start);
                EnsureAssignable(pc, nf.Start.Span, pc.Types.PrimNumber.ID, ts);
                var tl = SynthesizeExpr(pc, nf.Limit);
                EnsureAssignable(pc, nf.Limit.Span, pc.Types.PrimNumber.ID, tl);
                if (nf.Step != null)
                {
                    var tStep = SynthesizeExpr(pc, nf.Step);
                    EnsureAssignable(pc, nf.Step.Span, pc.Types.PrimNumber.ID, tStep);
                }

                pc.Pkg!.Syms.SetType(nf.VarName.Sym, pc.Types.PrimNumber.ID);
                _loopDepth++;
                ResolveStmts(pc, nf.Body);
                _loopDepth--;
                break;
            }
            case GenericForStmt gf:
            {
                var iterTypes = new List<TypID>();
                foreach (var iter in gf.Iterators)
                {
                    iterTypes.Add(SynthesizeExpr(pc, iter));
                }

                if (gf is { Iterators: [BinaryExpr { IsRange: true }], VarNames: [var counter] })
                {
                    gf.RunsOverRange = true;
                    if (counter.Sym != SymID.Invalid) pc.Pkg!.Syms.SetType(counter.Sym, pc.Types.PrimNumber.ID);
                }
                else if (IteratorMethodOf(pc, gf, iterTypes) is { } iteratorMethod)
                {
                    gf.CallsIteratorMethod = true;
                    InferIteratedVarTypes(pc, gf, iteratorMethod);
                }
                else
                {
                    gf.ChecksIteratorAtRuntime = gf.Iterators.Count == 1 && iterTypes[0] == pc.Types.PrimAny.ID
                        && gf.Iterators[0] is not (FunctionCallExpr or MethodCallExpr or VarargExpr);
                    InferGenericForVarTypes(pc, gf, iterTypes);
                }

                _loopDepth++;
                ResolveStmts(pc, gf.Body);
                _loopDepth--;
                break;
            }
            case ReturnStmt returnStmt:
                if (_finallyDepth > 0) pc.Diag.Report(returnStmt.Span, DiagnosticCode.ErrReturnInFinally);
                foreach (var value in returnStmt.Values)
                {
                    SynthesizeReturned(pc, value, returnStmt.Values.Count);
                }

                AdoptReturnedLiterals(pc, returnStmt.Values);
                break;
            case ImportStmt:
                break;
            case ExportStmt exportStmt:
                ResolveDecl(pc, exportStmt.Declaration);
                break;
            case MatchStmt matchStmt:
            {
                var scrutType = SynthesizeExpr(pc, matchStmt.Scrutinee);
                foreach (var arm in matchStmt.Arms)
                {
                    if (arm.Pattern.ValueExpr != null)
                    {
                        var patType = EnumMemberFor(pc, arm.Pattern.ValueExpr, scrutType) ?? SynthesizeExpr(pc, arm.Pattern.ValueExpr);
                        CheckMatchPatternType(pc, scrutType, patType, arm.Pattern.ValueExpr.Span);
                    }
                    if (arm.Pattern.Kind == MatchPatternKind.TypeBinding && arm.Pattern.TypeRef != null)
                        CheckMatchPatternTypeBinding(pc, scrutType, arm.Pattern.TypeRef, arm.Pattern.Span);
                    if (arm.Guard != null) SynthesizeExpr(pc, arm.Guard);
                    ResolveStmts(pc, arm.Body);
                }
                CheckExhaustiveMatch(pc, matchStmt);
                break;
            }
            case ContinueStmt continueStmt:
                if (_tryDepth > 0 && _loopDepth == 0) pc.Diag.Report(continueStmt.Span, DiagnosticCode.ErrJumpOutOfTry, "continue");
                break;
            case DeferStmt ds:
                if (_tryDepth > 0) pc.Diag.Report(ds.Span, DiagnosticCode.ErrDeferInTry);
                if (_asyncDepth > 0 && pc.Config.Target == Configuration.LuaVersion.Lua51)
                    pc.Diag.Report(ds.Span, DiagnosticCode.ErrDeferInAsyncOnLua51, "5.1");
                if (ds.Call != null) SynthesizeExpr(pc, ds.Call);
                if (ds.Block != null) ResolveStmts(pc, ds.Block);
                break;
            case GuardStmt gs:
                SynthesizeExpr(pc, gs.Condition);
                if (gs.ElseExpr != null) SynthesizeExpr(pc, gs.ElseExpr);
                break;
            default:
                throw new InvalidOperationException($"Unknown statement kind: {stmt.GetType().Name}");
        }

        return null;
    }

    /// <summary>
    /// Reports the function <paramref name="name"/> declares when an overload declared before it in
    /// the same scope has the same parameter types and is reachable from the same side.
    /// </summary>
    private void ReportIndistinguishableFunction(PassContext pc, NameRef name)
    {
        if (name.Overloads is not { Count: > 1 } overloads) return;
        if (!pc.Pkg!.Syms.GetByID(name.Sym, out var symbol)) return;
        if (!pc.Types.GetByID(symbol.Type, out var type) || type is not FunctionType function) return;

        var suffix = OverloadSignature.Suffix(function, false);
        foreach (var other in overloads.TakeWhile(other => other != name.Sym))
        {
            if (!pc.Pkg.Syms.GetByID(other, out var earlier) || (earlier.Side & symbol.Side) == 0) continue;
            if (!pc.Types.GetByID(earlier.Type, out var earlierType) || earlierType is not FunctionType earlierFunction) continue;
            if (OverloadSignature.Suffix(earlierFunction, false) != suffix) continue;

            pc.Diag.Report(name.Span, DiagnosticCode.ErrIndistinguishableOverload, name.Name, ParameterList(pc, function, false));
            return;
        }
    }

    private void ResolveDecl(PassContext pc, Decl decl)
    {
        switch (decl)
        {
            case FunctionDecl fd:
                ReportClassMemberReplacement(pc, fd);
                TypeMethodSelf(pc, fd);
                if (fd.IsAsync) _asyncDepth++;
                using (FixTypeParams(fd.TypeParams))
                    ResolveFunctionLike(pc, fd.Parameters, fd.ReturnType, fd.Body, fd.ReturnStmt,
                        fd.NamePath.Count == 1 && fd.MethodName == null ? fd.NamePath[0] : null, fd.IsAsync, ExpectedSignatureOfField(pc, fd));
                if (fd.IsAsync) _asyncDepth--;
                if (fd.NamePath.Count == 1 && fd.MethodName == null) ReportIndistinguishableFunction(pc, fd.NamePath[0]);
                break;
            case LocalFunctionDecl lfd:
                if (lfd.IsAsync) _asyncDepth++;
                using (FixTypeParams(lfd.TypeParams))
                    ResolveFunctionLike(pc, lfd.Parameters, lfd.ReturnType, lfd.Body, lfd.ReturnStmt, lfd.Name, lfd.IsAsync);
                if (lfd.IsAsync) _asyncDepth--;
                ReportIndistinguishableFunction(pc, lfd.Name);
                break;
            case LocalDecl ld:
                ResolveLocalDecl(pc, ld);
                break;
            case DeclareFunctionDecl dfd:
            {
                var paramTypes = new List<Tuple<string, Type>>();
                var dfdIsVararg = false;
                Type? dfdVarargType = null;
                var dfdDefaults = new List<int>();
                for (var i = 0; i < dfd.Parameters.Count; i++)
                {
                    var param = dfd.Parameters[i];
                    var t = ResolveParamType(pc, param);
                    if (param.IsVararg)
                    {
                        dfdIsVararg = true;
                        dfdVarargType = t.Kind == TypeKind.PrimitiveAny ? null : t;
                    }
                    else
                    {
                        paramTypes.Add(new Tuple<string, Type>(param.Name.Name, t));
                    }
                    if (param.Name.Sym != SymID.Invalid)
                    {
                        pc.Pkg!.Syms.SetType(param.Name.Sym, t.ID);
                    }
                    if (param.DefaultValue != null && !param.IsVararg)
                    {
                        dfdDefaults.Add(i);
                    }
                }

                var ret = dfd.ReturnType != null && dfd.ReturnType.ResolvedType != TypID.Invalid
                    ? GetType(pc, dfd.ReturnType.ResolvedType)
                    : pc.Types.PrimNil;
                var funcTyp = pc.Types.FuncOf(paramTypes, ret, dfdIsVararg, dfdVarargType,
                    dfdDefaults.Count > 0 ? dfdDefaults : null, dfd.IsAsync,
                    predicate: BuildPredicate(pc, dfd.ReturnType, dfd.Parameters));
                if (dfd.NamePath.Count == 1 && dfd.MethodName == null)
                {
                    pc.Pkg!.Syms.SetType(dfd.NamePath[0].Sym, funcTyp);
                }

                break;
            }
            case DeclareVariableDecl dvd:
            {
                var t = dvd.TypeAnnotation.ResolvedType;
                if (t != TypID.Invalid)
                {
                    pc.Pkg!.Syms.SetType(dvd.Name.Sym, t);
                }

                break;
            }
            case DeclareModuleDecl dmd:
                foreach (var member in dmd.Members)
                {
                    ResolveDecl(pc, member);
                }

                break;
            case EnumDecl ed:
                break;
            case ClassDecl cd:
                ResolveClassDecl(pc, cd);
                break;
            case InterfaceDecl id:
                ResolveInterfaceDecl(pc, id);
                break;
            case ExtendDecl ed:
                ResolveExtendDecl(pc, ed);
                break;
            default:
                throw new InvalidOperationException($"Unknown declaration kind: {decl.GetType().Name}");
        }
    }

    /// <summary>
    /// Resolves a class declaration: registers its members on the <see cref="ClassType"/> and
    /// type-checks each body. A <c>declare class</c> carries signatures only, so its methods are
    /// bodyless by construction and are exempt from body resolution and the all-paths-return
    /// check, the same way <c>abstract</c> methods are.
    /// </summary>
    private void ResolveClassDecl(PassContext pc, ClassDecl cd)
    {
        if (!_resolvedClassDecls.Add(cd.ID)) return;

        if (cd.Name.Sym == SymID.Invalid) return;
        if (!pc.Pkg!.Syms.GetByID(cd.Name.Sym, out var classSym)) return;
        if (!pc.Types.GetByID(classSym.Type, out var rawType) || rawType is not ClassType classType) return;

        var enclosingClass = _currentClass;
        _currentClass = classType;
        try
        {
            var methodBodies = _pendingClassBodies.Remove(cd.ID, out var pending) ? pending : ResolveClassBody(pc, cd, classType);
            ResolveClassBodies(pc, cd, classType, methodBodies);
        }
        finally
        {
            _currentClass = enclosingClass;
        }
    }

    /// <summary>
    /// Types the members of a class ahead of its bodies, so a body anywhere in the build can call
    /// a method of a class declared further down. <see cref="ResolveClassDecl"/> resolves the
    /// bodies later.
    /// </summary>
    private void DeclareClassMembers(PassContext pc, ClassDecl cd)
    {
        if (_resolvedClassDecls.Contains(cd.ID) || _pendingClassBodies.ContainsKey(cd.ID)) return;
        if (!pc.Pkg!.Syms.GetByID(cd.Name.Sym, out var classSym)) return;
        if (!pc.Types.GetByID(classSym.Type, out var rawType) || rawType is not ClassType classType) return;

        var enclosingClass = _currentClass;
        _currentClass = classType;
        try
        {
            _pendingClassBodies[cd.ID] = ResolveClassBody(pc, cd, classType);
        }
        finally
        {
            _currentClass = enclosingClass;
        }
    }

    /// <summary>
    /// The body of <see cref="ResolveClassDecl"/>, split out so the enclosing-class state it runs
    /// under is restored on every exit path.
    /// </summary>
    private List<(ClassMethodNode Method, Type ReturnType)> ResolveClassBody(PassContext pc, ClassDecl cd, ClassType classType)
    {
        using var classTypeParams = FixTypeParams(cd.TypeParams);
        foreach (var (id, sym) in pc.Pkg!.Syms.ByID)
            if (sym.Name == "self" && sym.DeclaringNode == cd.ID && sym.Type == TypID.Invalid)
                pc.Pkg.Syms.SetType(id, classType.ID);

        var ctorTemplate = Nebra.Compiler.Annotations.BuiltinAnnotations.ExtractOverrideCtor(cd.Annotations);
        if (ctorTemplate != null) classType.CtorTemplate = ctorTemplate;

        var inheritTemplate = Nebra.Compiler.Annotations.BuiltinAnnotations.ExtractOverrideInherit(cd.Annotations);
        if (inheritTemplate != null) classType.InheritTemplate = inheritTemplate;

        classType.IndexHook = Nebra.Compiler.Annotations.BuiltinAnnotations.ExtractInheritHook(cd.Annotations, "index");
        classType.NewIndexHook = Nebra.Compiler.Annotations.BuiltinAnnotations.ExtractInheritHook(cd.Annotations, "newindex");
        classType.IsTemplate = Nebra.Compiler.Annotations.BuiltinAnnotations.ExtractOverrideIs(cd.Annotations);

        LinkBaseClass(pc, cd, classType, report: true);
        if (cd.BaseClass != null && classType.BaseClass != null)
        {
            var baseDefinition = classType.BaseClass.Definition;
            ReportBaseTypeArguments(pc, cd.BaseClass, baseDefinition.TypeParams.Count, cd.BaseClassTypeArgs);
            if (baseDefinition.IsSealed)
                pc.Diag.Report(cd.BaseClass.Span, DiagnosticCode.ErrSealedBase, baseDefinition.Name);
            else if (baseDefinition.HasLocalConstructor)
                pc.Diag.Report(cd.BaseClass.Span, DiagnosticCode.ErrLocalConstructor, baseDefinition.Name);
        }

        for (var index = 0; index < cd.Interfaces.Count; index++)
        {
            var iface = cd.Interfaces[index];
            if (iface.Sym == SymID.Invalid) continue;
            var ifaceTyp = LookupSymbolType(pc, iface.Sym);
            if (ifaceTyp != TypID.Invalid && pc.Types.GetByID(ifaceTyp, out var it) && it is InterfaceType ifaceType)
            {
                if (ifaceType.IsContract)
                {
                    pc.Diag.Report(iface.Span, DiagnosticCode.ErrImplementsContract, iface.Name);
                }

                var interfaceArgs = index < cd.InterfaceTypeArgs.Count ? cd.InterfaceTypeArgs[index] : [];
                AddOnce(classType.Interfaces, (InterfaceType)ResolveTypeRefsPass.Instantiation(pc.Types, ifaceType, interfaceArgs));
                ReportBaseTypeArguments(pc, iface, ifaceType.TypeParams.Count, interfaceArgs);
            }
            else if (ifaceTyp != TypID.Invalid)
                pc.Diag.Report(iface.Span, Diagnostics.DiagnosticCode.ErrImplementsNonInterface, iface.Name);
        }

        CheckDuplicateMembers(pc, cd);
        ReportReservedMemberNames(pc, cd);

        foreach (var field in cd.Fields)
        {
            if (field.DefaultValue != null) CheckFieldDefault(pc, field);
            var fieldType = field.TypeAnnotation != null && field.TypeAnnotation.ResolvedType != TypID.Invalid
                ? GetType(pc, field.TypeAnnotation.ResolvedType)
                : (field.DefaultValue != null ? GetType(pc, field.DefaultValue.Type) : pc.Types.PrimAny);
            classType.InstanceFields[field.Name.Name] = new StructType.Field(field.Name, fieldType);
            classType.MemberDeclarations.TryAdd(field.Name.Name, field.Name);
            if (field.IsStatic)
                SetStaticMemberType(pc, cd, field.Name.Name, 0, fieldType);
            if (field.IsProtected)
                classType.ProtectedMembers.Add(field.Name.Name);
            if (field.IsLocal)
                classType.PrivateMembers.Add(field.Name.Name);
            StampMemberSide(pc, field.Annotations, classType.FieldSides, field.Name.Name);
        }

        foreach (var parameter in cd.Constructors.SelectMany(constructor => constructor.Parameters).Where(parameter => parameter.DefaultValue != null))
            SynthesizeExpr(pc, parameter.DefaultValue!);
        RegisterConstructors(pc, cd, classType);
        ReportDuplicateConstructors(pc, cd, classType);
        if (cd.Constructor != null)
        {
            var ctorSide = Nebra.Compiler.Annotations.BuiltinAnnotations.ExtractSide(cd.Constructor.Annotations,
                (ann, badName) => ReportBadSide(pc, ann, badName));
            classType.ConstructorSide = ctorSide;
        }

        var methodBodies = new List<(ClassMethodNode Method, Type ReturnType)>();
        var staticOverloads = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var method in cd.Methods)
        {
            if (method.IsAbstract && !cd.IsAbstract)
                pc.Diag.Report(method.Span, Diagnostics.DiagnosticCode.ErrAbstractInNonAbstractClass, method.Name.Name);

            if (method.IsAsync) _asyncDepth++;
            AdoptInterfaceParamDefaults(pc, cd, classType, method);
            var methodParams = new List<Tuple<string, Type>>();

            // Implicit "self" parameter for instance methods
            if (!method.IsStatic)
            {
                methodParams.Add(new Tuple<string, Type>("self", classType));
            }
            var isVararg = false;
            Type? varargType = null;
            var defaultIndices = new List<int>();
            for (var i = 0; i < method.Parameters.Count; i++)
            {
                var p = method.Parameters[i];
                var t = ResolveParamType(pc, p);
                if (p.IsVararg) { isVararg = true; varargType = t.Kind == TypeKind.PrimitiveAny ? null : t; }
                else { methodParams.Add(new Tuple<string, Type>(p.Name.Name, t)); }
                if (p.Name.Sym != SymID.Invalid) pc.Pkg!.Syms.SetType(p.Name.Sym, t.ID);
                if (p.DefaultValue != null) { SynthesizeExpr(pc, p.DefaultValue); defaultIndices.Add(method.IsStatic ? i : i + 1); }
            }
            Type retType;
            if (method.ReturnType != null && method.ReturnType.ResolvedType != TypID.Invalid)
                retType = GetType(pc, method.ReturnType.ResolvedType);
            else if (!method.IsStatic && TryGetInheritedReturnType(classType, method.Name.Name, out var inheritedRet))
                // A method that omits its return type inherits the signature of the
                // method it overrides (base class or implemented interface), rather than
                // silently defaulting to `nil`.
                retType = inheritedRet;
            else
                retType = pc.Types.PrimNil;

            if (InfersReturnType(pc, cd, method, retType))
            {
                retType = pc.Types.PrimAny;
                _methodsInferringReturn.Add(method);
            }

            if (method.IsOperator && !cd.IsDeclare) ReportOperatorReturns(pc, method.Name.Name, method.ReturnType, retType);
            var funcTypId = pc.Types.FuncOf(methodParams, retType, isVararg, varargType, defaultIndices.Count > 0 ? defaultIndices : null, method.IsAsync,
                predicate: BuildPredicate(pc, method.ReturnType, method.Parameters));
            var ft = (FunctionType)GetType(pc, funcTypId);
            var methodSide = Nebra.Compiler.Annotations.BuiltinAnnotations.ExtractSide(method.Annotations,
                (ann, badName) => ReportBadSide(pc, ann, badName));
            if (method.IsStatic)
            {
                AppendOverload(classType.StaticMethods, classType.StaticMethodOverloads,
                    classType.StaticMethodOverloadSides, method.Name.Name, ft, methodSide);
                var overload = staticOverloads.GetValueOrDefault(method.Name.Name);
                staticOverloads[method.Name.Name] = overload + 1;
                SetStaticMemberType(pc, cd, method.Name.Name, overload, ft);
            }
            else
                AppendOverload(classType.Methods, classType.MethodOverloads,
                    classType.MethodOverloadSides, method.Name.Name, ft, methodSide);
            StampMemberSide(pc, method.Annotations,
                method.IsStatic ? classType.StaticMethodSides : classType.MethodSides,
                method.Name.Name);
            classType.MemberDeclarations.TryAdd(method.Name.Name, method.Name);

            if (method.IsAbstract)
                classType.AbstractMethods.Add(method.Name.Name);

            if (method.IsProtected)
                classType.ProtectedMembers.Add(method.Name.Name);

            if (method.IsLocal)
                classType.PrivateMembers.Add(method.Name.Name);

            if (method.IsOverride)
            {
                // `override` is valid against a base class method or an implemented interface
                // method (including a default), since overriding an interface default is an override.
                var hasParent = (classType.BaseClass != null && ParentHasMethod(classType.BaseClass, method.Name.Name))
                    || InterfaceHasMethod(classType, method.Name.Name);
                if (!hasParent)
                    pc.Diag.Report(method.Span, Diagnostics.DiagnosticCode.ErrOverrideNoParent, method.Name.Name);
            }

            if (!method.IsOperator && !method.IsOverride && !method.IsAbstract && classType.BaseClass != null
                && ParentHasMethod(classType.BaseClass, method.Name.Name)
                && (method.IsStatic || ParentHasOverload(classType.BaseClass, method.Name.Name, ft)))
            {
                pc.Diag.Report(method.Span, Diagnostics.DiagnosticCode.WarnMissingShadowOverride, method.Name.Name, classType.BaseClass.Name);
            }

            if (!method.IsAbstract && !cd.IsDeclare)
                methodBodies.Add((method, retType));
            if (method.IsAsync) _asyncDepth--;
        }

        classType.IsCompiledFromSource = !cd.IsDeclare;

        foreach (var accessor in cd.Accessors)
        {
            ReportAccessorParameters(pc, accessor.Kind, accessor.Name, accessor.Parameters);
            var accParams = new List<Tuple<string, Type>>();
            foreach (var p in accessor.Parameters)
            {
                var t = ResolveParamType(pc, p);
                if (p.Name.Sym != SymID.Invalid) pc.Pkg!.Syms.SetType(p.Name.Sym, t.ID);
                accParams.Add(new Tuple<string, Type>(p.Name.Name, t));
            }
            var accRetType = accessor.ReturnType != null && accessor.ReturnType.ResolvedType != TypID.Invalid
                ? GetType(pc, accessor.ReturnType.ResolvedType) : pc.Types.PrimNil;
            var accFuncTyp = (FunctionType)GetType(pc, pc.Types.FuncOf(accParams, accRetType));
            if (accessor.Kind == AccessorKind.Getter)
                classType.Getters[accessor.Name.Name] = accFuncTyp;
            else
                classType.Setters[accessor.Name.Name] = accFuncTyp;
            StampMemberSide(pc, accessor.Annotations,
                accessor.Kind == AccessorKind.Getter ? classType.GetterSides : classType.SetterSides,
                accessor.Name.Name);
            classType.MemberDeclarations.TryAdd(accessor.Name.Name, accessor.Name);

            if (accessor.IsOverride && classType.BaseClass != null)
            {
                var hasParent = accessor.Kind == AccessorKind.Getter
                    ? ParentHasGetter(classType.BaseClass, accessor.Name.Name)
                    : ParentHasSetter(classType.BaseClass, accessor.Name.Name);
                if (!hasParent)
                    pc.Diag.Report(accessor.Span, Diagnostics.DiagnosticCode.ErrOverrideNoParent, accessor.Name.Name);
            }
            else if (accessor.IsOverride && classType.BaseClass == null)
            {
                pc.Diag.Report(accessor.Span, Diagnostics.DiagnosticCode.ErrOverrideNoParent, accessor.Name.Name);
            }
        }

        pc.Types.CompleteMembers(classType);
        return methodBodies;
    }

    /// <summary>
    /// Resolves the constructor, method and accessor bodies of a class whose members are typed,
    /// then checks what the class promises to its interfaces and abstract bases.
    /// </summary>
    private void ResolveClassBodies(PassContext pc, ClassDecl cd, ClassType classType,
        List<(ClassMethodNode Method, Type ReturnType)> methodBodies)
    {
        using var classTypeParams = FixTypeParams(cd.TypeParams);
        foreach (var constructor in cd.Constructors)
        {
            foreach (var defer in DeferStmt.In(constructor.Body))
                pc.Diag.Report(defer.Span, DiagnosticCode.ErrDeferInConstructor);
            if (!cd.IsDeclare) CheckSuperCall(pc, cd, classType, constructor);
            var outerConstructorSuperCalls = _constructorSuperCalls;
            _constructorSuperCalls = constructor.Body
                .Select(stmt => stmt is ExprStmt { Expression: SuperCallExpr superCall } ? superCall : null)
                .OfType<SuperCallExpr>()
                .ToHashSet();
            ResolveFunctionBody(pc, constructor.Body, constructor.ReturnStmt);
            _constructorSuperCalls = outerConstructorSuperCalls;
        }

        foreach (var (method, returnType) in methodBodies)
        {
            using (FixTypeParams(method.TypeParams))
            {
                if (_methodsInferringReturn.Remove(method))
                {
                    ResolveMethodInferringReturn(pc, cd, classType, method);
                    continue;
                }

                ResolveMethodBody(pc, method.Body, method.ReturnStmt, method.IsAsync, returnType, method.ReturnType?.Span ?? method.Name.Span);
            }
        }

        foreach (var accessor in cd.Accessors)
            ResolveFunctionBody(pc, accessor.Body, accessor.ReturnStmt, accessor.ReturnType?.ResolvedType);

        ReportUninitializedFields(pc, cd, classType);

        _openTypeParamDepth++;
        try
        {
            CheckInterfaceImplementation(pc, cd, classType);
            CheckAbstractImplementation(pc, cd, classType);
            CheckHostInheritedMembers(pc, cd, classType);
        }
        finally
        {
            _openTypeParamDepth--;
        }

        pc.Types.CompleteMembers(classType);
    }

    /// <summary>
    /// Reports members a class declares more than once. Fields and accessors collide on their
    /// name alone; methods do not, because a class may carry several overloads of one name. Two
    /// methods only collide when they agree on both their signature and their <c>@side</c>, which
    /// is what makes one of them unreachable at every call site.
    /// </summary>
    private void CheckDuplicateMembers(PassContext pc, ClassDecl cd)
    {
        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in cd.Fields)
        {
            if (fieldNames.Add(field.Name.Name)) continue;

            pc.Diag.Report(field.Name.Span, Diagnostics.DiagnosticCode.ErrDuplicateClassMember, field.Name.Name);
        }

        var accessorKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var accessor in cd.Accessors)
        {
            if (accessorKeys.Add(accessor.Kind + " " + accessor.Name.Name)) continue;

            pc.Diag.Report(accessor.Name.Span, Diagnostics.DiagnosticCode.ErrDuplicateClassMember, accessor.Name.Name);
        }

        var methodKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in cd.Methods)
        {
            var side = Nebra.Compiler.Annotations.BuiltinAnnotations.ExtractSide(method.Annotations, (_, _) => { });
            var key = $"{(method.IsStatic ? "static " : "")}{method.Name.Name}({MethodParamKey(pc, method)}) @{side}";
            if (methodKeys.Add(key)) continue;

            pc.Diag.Report(method.Name.Span, Diagnostics.DiagnosticCode.ErrDuplicateClassMember, method.Name.Name);
        }
    }

    /// <summary>
    /// Renders a method's declared parameter types as a comparable key. Two methods sharing it are
    /// the same overload, not two of them.
    /// </summary>
    private string MethodParamKey(PassContext pc, ClassMethodNode method)
    {
        var parts = new List<string>();
        foreach (var p in method.Parameters)
        {
            var typ = p.TypeAnnotation != null && p.TypeAnnotation.ResolvedType != TypID.Invalid
                ? TypeName(pc, p.TypeAnnotation.ResolvedType)
                : "any";
            parts.Add(p.IsVararg ? "..." + typ : typ);
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Gives every class of <paramref name="stmts"/> its base class and constructor before any class
    /// body is resolved. A body may construct a class declared further down, or extend one, and
    /// either has to see that class's constructor to check the arguments it is given.
    /// </summary>
    private void ResolveClassSignatures(PassContext pc, List<Stmt> stmts)
    {
        foreach (var cd in ClassDeclsIn(stmts))
        {
            if (!pc.Pkg!.Syms.GetByID(cd.Name.Sym, out var classSym)) continue;
            if (!pc.Types.GetByID(classSym.Type, out var rawType) || rawType is not ClassType classType) continue;

            LinkBaseClass(pc, cd, classType, report: false);
            RegisterConstructors(pc, cd, classType);
        }
    }

    private static IEnumerable<ClassDecl> ClassDeclsIn(List<Stmt> stmts)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case ClassDecl cd:
                    yield return cd;
                    break;
                case ExportStmt { Declaration: ClassDecl exported }:
                    yield return exported;
                    break;
                case DeclareModuleDecl module:
                    foreach (var member in module.Members.OfType<ClassDecl>())
                        yield return member;
                    break;
            }
        }
    }

    /// <summary>
    /// Sets the base class <paramref name="cd"/> extends. The cycle is rejected before it reaches
    /// the type graph: every walk over the base chain assumes it ends, so letting one in makes the
    /// compiler spin instead of reporting anything. Problems are only reported when
    /// <paramref name="report"/> is set, so linking early and again later reports them once.
    /// </summary>
    private void LinkBaseClass(PassContext pc, ClassDecl cd, ClassType classType, bool report)
    {
        if (cd.BaseClass == null || cd.BaseClass.Sym == SymID.Invalid) return;

        var baseTyp = LookupSymbolType(pc, cd.BaseClass.Sym);
        if (baseTyp == TypID.Invalid) return;

        if (!pc.Types.GetByID(baseTyp, out var bt) || bt is not ClassType baseCls)
        {
            if (report)
                pc.Diag.Report(cd.BaseClass.Span, Diagnostics.DiagnosticCode.ErrExtendsNonClass, cd.BaseClass.Name);
            return;
        }

        var linked = (ClassType)ResolveTypeRefsPass.Instantiation(pc.Types, baseCls, cd.BaseClassTypeArgs);
        if (classType.BaseClass?.ID == linked.ID) return;

        if (ReachesThroughBaseClasses(baseCls, classType))
        {
            if (report)
                pc.Diag.Report(cd.BaseClass.Span, Diagnostics.DiagnosticCode.ErrCyclicInheritance, cd.Name.Name, cd.BaseClass.Name);
            return;
        }

        classType.BaseClass = linked;
    }

    /// <summary>
    /// The type of a class constructor, typing its parameters on the way.
    /// </summary>
    private FunctionType ConstructorTypeOf(PassContext pc, ClassConstructorNode ctor, ClassType classType)
    {
        var parameters = new List<Tuple<string, Type>>();
        var defaults = new List<int>();
        var isVararg = false;
        Type? varargType = null;
        for (var i = 0; i < ctor.Parameters.Count; i++)
        {
            var parameter = ctor.Parameters[i];
            var type = ResolveParamType(pc, parameter);
            if (parameter.Name.Sym != SymID.Invalid) pc.Pkg!.Syms.SetType(parameter.Name.Sym, type.ID);
            if (parameter.IsVararg)
            {
                isVararg = true;
                varargType = type.Kind == TypeKind.PrimitiveAny ? null : type;
                continue;
            }

            parameters.Add(new Tuple<string, Type>(parameter.Name.Name, type));
            if (parameter.DefaultValue != null) defaults.Add(i);
        }

        return (FunctionType)GetType(pc, pc.Types.FuncOf(parameters, classType, isVararg, varargType,
            defaults.Count > 0 ? defaults : null));
    }

    /// <summary>
    /// Checks the arguments of a <c>super(...)</c> against the constructor of the base class it
    /// runs, the nearest one up the chain.
    /// </summary>
    private void CheckSuperArguments(PassContext pc, SuperCallExpr superCall, List<TypID> argTypes)
    {
        var baseClass = _currentClass?.BaseClass;
        if (baseClass == null) return;

        var constructors = ResolveConstructorOverloads(baseClass);
        if (constructors == null)
        {
            if (superCall.Arguments.Count > 0)
                pc.Diag.Report(superCall.Span, DiagnosticCode.ErrNoConstructor, baseClass.Name);
            return;
        }

        var (owner, overloads) = constructors.Value;
        var ctorType = PickConstructor(pc, superCall.Span, baseClass.Name, owner, overloads, superCall.Arguments, argTypes, out var overloadName);
        if (ctorType == null) return;

        if (owner.Definition == baseClass.Definition) superCall.OverloadName = overloadName;
        CheckConstructorArguments(pc, superCall.Span, baseClass.Name, ctorType, superCall.Arguments, argTypes);
    }

    private void CheckConstructorArguments(PassContext pc, TextSpan span, string className, FunctionType ctorType, List<Expr> arguments, List<TypID> argTypes)
    {
        var argCount = arguments.Count;
        AdoptArguments(pc, ctorType, arguments, argTypes, 0);
        var paramCount = ctorType.ParamTypes.Count;
        var minParams = ctorType.MinParamCount;
        if (argCount >= minParams && (argCount <= paramCount || ctorType.IsVararg))
        {
            CheckCallArguments(pc, span, ctorType, argTypes);
            return;
        }

        var expected = ctorType.IsVararg ? $"at least {minParams}"
            : minParams == paramCount ? paramCount.ToString()
            : $"{minParams}..{paramCount}";
        pc.Diag.Report(span, DiagnosticCode.ErrConstructorParamMismatch, className, expected, argCount.ToString());
    }

    /// <summary>
    /// Checks the <c>super()</c> rules for a class that declares its own constructor. Codegen
    /// falls back to an argument-less <c>Base.new()</c> when no super call is present, which is
    /// only correct while the inherited constructor needs no arguments; anything else leaves the
    /// base half of the object unset. A super call that is not the first statement is equally
    /// broken, since everything before it runs before <c>self</c> exists.
    /// </summary>
    private void CheckSuperCall(PassContext pc, ClassDecl cd, ClassType classType, ClassConstructorNode ctor)
    {
        var superIndex = ctor.Body.FindIndex(s => s is ExprStmt { Expression: SuperCallExpr });

        if (classType.BaseClass == null)
        {
            if (superIndex >= 0)
                pc.Diag.Report(ctor.Body[superIndex].Span, Diagnostics.DiagnosticCode.ErrSuperOutsideConstructor);
            return;
        }

        if (superIndex > 0)
        {
            pc.Diag.Report(ctor.Body[superIndex].Span, Diagnostics.DiagnosticCode.ErrSuperNotFirst);
            return;
        }

        if (superIndex == 0 || HasArgumentlessConstructor(classType.BaseClass)) return;

        pc.Diag.Report(ctor.Span, Diagnostics.DiagnosticCode.ErrMissingSuperCall, cd.Name.Name);
    }

    /// <summary>
    /// Reports whether <paramref name="target"/> sits on <paramref name="start"/>'s chain of base
    /// classes, <paramref name="start"/> itself included. Carries its own visited set so it stays
    /// terminating even if a cycle has already been recorded elsewhere.
    /// </summary>
    private static bool ReachesThroughBaseClasses(ClassType start, ClassType target)
    {
        var seen = new HashSet<TypID>();
        for (var cur = start; cur != null; cur = cur.BaseClass)
        {
            if (cur.ID == target.ID) return true;
            if (!seen.Add(cur.ID)) return true;
        }

        return false;
    }

    /// <summary>
    /// The interface counterpart of <see cref="ReachesThroughBaseClasses"/>: whether
    /// <paramref name="target"/> is reachable from <paramref name="start"/> through extends edges,
    /// <paramref name="start"/> included.
    /// </summary>
    private static bool ReachesThroughBaseInterfaces(InterfaceType start, InterfaceType target)
    {
        var seen = new HashSet<TypID>();
        var stack = new Stack<InterfaceType>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur.ID == target.ID) return true;
            if (!seen.Add(cur.ID)) continue;
            foreach (var b in cur.BaseInterfaces) stack.Push(b);
        }

        return false;
    }

    private static bool ParentHasMethod(ClassType cls, string name)
    {
        var cur = cls;
        while (cur != null)
        {
            if (cur.Methods.ContainsKey(name) || cur.AbstractMethods.Contains(name)) return true;
            cur = cur.BaseClass;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="cls"/> or a class it extends declares <paramref name="name"/> with
    /// the parameter types of <paramref name="method"/>; a method of other parameter types adds an
    /// overload instead of hiding the inherited one.
    /// </summary>
    private static bool ParentHasOverload(ClassType cls, string name, FunctionType method)
    {
        var suffix = OverloadSignature.Suffix(method, true);
        return OverloadSignature.InstanceOverloads(cls, name)
            .Any(overload => OverloadSignature.Suffix(overload.Function, true) == suffix);
    }

    private string ParameterList(PassContext pc, FunctionType function, bool hasSelf) =>
        string.Join(", ", function.ParamTypes.Skip(hasSelf ? 1 : 0).Select(type => TypeName(pc, type.ID)));

    private static bool ParentHasGetter(ClassType cls, string name)
    {
        var cur = cls;
        while (cur != null)
        {
            if (cur.Getters.ContainsKey(name)) return true;
            cur = cur.BaseClass;
        }
        return false;
    }

    private static bool ParentHasSetter(ClassType cls, string name)
    {
        var cur = cls;
        while (cur != null)
        {
            if (cur.Setters.ContainsKey(name)) return true;
            cur = cur.BaseClass;
        }
        return false;
    }

    /// <summary>
    /// Reports every interface member <paramref name="classType"/> is required to declare but
    /// does not, and materialises the interface defaults it inherits (including those declared
    /// on transitively-extended interfaces). A method carrying a default is never a missing
    /// requirement, since the class inherits its body.
    /// <para>
    /// A <c>declare class</c> is exempt from the requirement: it describes an externally
    /// implemented type, so the members it takes from an interface are provided by that
    /// implementation and need not be restated. Member lookup reaches them through
    /// <see cref="ResolveInterfaceMethodOnClass"/> instead.
    /// </para>
    /// </summary>
    private void CheckInterfaceImplementation(PassContext pc, ClassDecl cd, ClassType classType)
    {
        foreach (var ifaceType in classType.Interfaces)
        {
            foreach (var (name, ft) in ifaceType.Methods)
            {
                var implemented = ResolveClassChainMethod(classType, name);
                var takenFromDefault = _defaultSources.ContainsKey((classType, name));
                if (implemented != null && (!takenFromDefault || !ifaceType.DefaultMethods.Contains(name)))
                {
                    CheckImplementedMethodSignature(pc, cd, ifaceType, name, ft, implemented);
                    continue;
                }

                if (ifaceType.DefaultMethods.Contains(name))
                {
                    InjectDefault(pc, classType, ifaceType, name, ft);
                    continue;
                }

                if (cd.IsDeclare) continue;

                pc.Diag.Report(cd.Span, Diagnostics.DiagnosticCode.ErrMissingInterfaceMember, cd.Name.Name, name, ifaceType.Name);
            }

            foreach (var (name, ifaceField) in ifaceType.Fields)
            {
                var classField = ResolveClassChainField(classType, name);
                if (classField != null)
                {
                    CheckImplementedFieldType(pc, cd, ifaceType, name, ifaceField, classField);
                    continue;
                }

                if (cd.IsDeclare) continue;

                pc.Diag.Report(cd.Span, Diagnostics.DiagnosticCode.ErrMissingInterfaceMember, cd.Name.Name, name, ifaceType.Name);
            }

            foreach (var baseIface in Type.BaseInterfacesOf(ifaceType))
            {
                foreach (var name in baseIface.DefaultMethods)
                {
                    if (classType.Methods.ContainsKey(name) && !_defaultSources.ContainsKey((classType, name))) continue;
                    if (!baseIface.Methods.TryGetValue(name, out var bft)) continue;

                    InjectDefault(pc, classType, baseIface, name, bft);
                }
            }
        }
    }

    /// <summary>
    /// Materialises an interface default into <paramref name="classType"/>: registers the
    /// method type (with a synthetic <c>self</c> so instance calls type-check) and records the
    /// body node for codegen — unless a base class already supplies the method.
    /// </summary>
    private void InjectDefault(PassContext pc, ClassType classType, InterfaceType iface, string name, FunctionType ft)
    {
        if (_defaultSources.TryGetValue((classType, name), out var chosen))
        {
            ChooseBetweenDefaults(pc, classType, chosen, iface, name, ft);
            return;
        }

        if (classType.Methods.ContainsKey(name)) return;
        if (classType.BaseClass != null && ParentHasMethod(classType.BaseClass, name)) return;

        classType.Methods[name] = WithSelfParam(pc, classType, ft);
        _defaultSources[(classType, name)] = iface;

        if (iface.DefaultMethodNodes.TryGetValue(name, out var node))
            classType.DefaultsToEmit[name] = node;
    }

    /// <summary>
    /// Settles a default <paramref name="name"/> that <paramref name="candidate"/> offers after
    /// <paramref name="chosen"/> already supplied one. An interface extending the other is the more
    /// specific and wins, the same body reached twice is no conflict, and two unrelated bodies are
    /// reported, since nothing says which one the class runs.
    /// </summary>
    private void ChooseBetweenDefaults(PassContext pc, ClassType classType, InterfaceType chosen, InterfaceType candidate,
        string name, FunctionType ft)
    {
        if (chosen == candidate || InterfaceExtendsInterface(chosen, candidate)) return;

        var chosenBody = chosen.DefaultMethodNodes.GetValueOrDefault(name);
        var candidateBody = candidate.DefaultMethodNodes.GetValueOrDefault(name);
        if (chosenBody == candidateBody) return;

        if (InterfaceExtendsInterface(candidate, chosen))
        {
            classType.Methods[name] = WithSelfParam(pc, classType, ft);
            _defaultSources[(classType, name)] = candidate;
            if (candidateBody != null) classType.DefaultsToEmit[name] = candidateBody;
            return;
        }

        if (classType.IsCompiledFromSource && classType.DeclaredAt is { } declaration)
            pc.Diag.Report(declaration.Span, Diagnostics.DiagnosticCode.ErrAmbiguousDefaultMethod,
                classType.Name, name, chosen.Name, candidate.Name);
    }

    /// <summary>
    /// Finds the method that satisfies an interface requirement: the one the class declares, or
    /// else the nearest one it inherits from a base class. A class implements an interface with
    /// everything it has, not only with what it restates. Deliberately does not fall back to the
    /// implemented interfaces the way <see cref="ResolveMethodOnType"/> does, since an interface
    /// requirement cannot be satisfied by the requirement itself.
    /// </summary>
    private static FunctionType? ResolveClassChainMethod(ClassType classType, string name)
    {
        for (var cur = classType; cur != null; cur = cur.BaseClass)
        {
            if (cur.Methods.TryGetValue(name, out var ft)) return ft;
        }

        return null;
    }

    /// <summary>
    /// The field counterpart of <see cref="ResolveClassChainMethod"/>: an inherited field
    /// satisfies an interface field just as a declared one does.
    /// </summary>
    private static StructType.Field? ResolveClassChainField(ClassType classType, string name)
    {
        for (var cur = classType; cur != null; cur = cur.BaseClass)
        {
            if (cur.InstanceFields.TryGetValue(name, out var field)) return field;
        }

        return null;
    }

    /// <summary>
    /// Copies the parameter defaults an implemented interface declares onto a class method that
    /// omits them, so the promise the interface makes to its callers holds for this
    /// implementation too. Codegen reads the very same field, so the class method emits the guard
    /// the interface's own default method would have emitted. A default the class spells out
    /// itself always wins, and the first interface declaring the method decides.
    /// </summary>
    private void AdoptInterfaceParamDefaults(PassContext pc, ClassDecl cd, ClassType classType, ClassMethodNode method)
    {
        if (method.IsStatic) return;

        foreach (var iface in Type.ImplementedInterfaces(classType))
        {
            if (!iface.MethodNodes.TryGetValue(method.Name.Name, out var ifaceMethod)) continue;

            var shared = Math.Min(method.Parameters.Count, ifaceMethod.Parameters.Count);
            for (var i = 0; i < shared; i++)
            {
                var target = method.Parameters[i];
                var source = ifaceMethod.Parameters[i];
                if (target.IsVararg || source.IsVararg) continue;
                if (target.DefaultValue != null || source.DefaultValue == null) continue;

                if (!IsSelfContainedDefault(source.DefaultValue))
                {
                    pc.Diag.Report(target.Name.Span, Diagnostics.DiagnosticCode.ErrInterfaceDefaultNotInheritable,
                        target.Name.Name, method.Name.Name, cd.Name.Name, iface.Name);
                    continue;
                }

                target.DefaultValue = source.DefaultValue;
            }

            return;
        }
    }

    /// <summary>
    /// Reports whether a default value can be re-emitted in another declaration's context. An
    /// adopted default is written into the implementing class's output, which may sit in a
    /// different file or package than the interface, so anything resolved through a name (a
    /// local, an import, a call) would be looked up in the wrong scope there and silently come
    /// back nil. Literals and compositions of literals carry no such reference.
    /// </summary>
    private static bool IsSelfContainedDefault(Expr expr)
    {
        switch (expr)
        {
            case NilLiteralExpr:
            case BoolLiteralExpr:
            case NumberLiteralExpr:
            case StringLiteralExpr:
                return true;
            case ParenExpr paren:
                return IsSelfContainedDefault(paren.Inner);
            case UnaryExpr unary:
                return IsSelfContainedDefault(unary.Operand);
            case BinaryExpr binary:
                return IsSelfContainedDefault(binary.Left) && IsSelfContainedDefault(binary.Right);
            case TableConstructorExpr table:
                foreach (var field in table.Fields)
                {
                    if (field.Key != null && !IsSelfContainedDefault(field.Key)) return false;
                    if (!IsSelfContainedDefault(field.Value)) return false;
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Compares the method that satisfies an interface requirement against the signature the
    /// interface declares. Parameters are contravariant and the return type covariant, which is
    /// what keeps a call routed through the interface sound: the caller passes the interface's
    /// parameter types and expects its return type back. The stored class signature carries a
    /// synthetic <c>self</c> the interface signature does not, so the comparison skips it.
    /// A member inherited from a base class has no declaration in this class, so the mismatch is
    /// reported on the class header, where the <c>implements</c> promise was made.
    /// </summary>
    private void CheckImplementedMethodSignature(PassContext pc, ClassDecl cd, InterfaceType ifaceType,
        string name, FunctionType ifaceFt, FunctionType classFt)
    {
        var method = cd.Methods.FirstOrDefault(m => !m.IsStatic && !m.IsLocal && m.Name.Name == name);
        var span = method?.Name.Span ?? cd.Name.Span;
        classFt = WithTypeParametersOf(pc, classFt, ifaceFt);
        var offset = StartsWithSelfParam(classFt) ? 1 : 0;
        var classParamCount = classFt.ParamTypes.Count - offset;
        var ifaceParamCount = ifaceFt.ParamTypes.Count;

        if (classParamCount < ifaceParamCount || HasRequiredParamsBeyond(classFt, offset, ifaceParamCount))
        {
            pc.Diag.Report(span, Diagnostics.DiagnosticCode.ErrInterfaceMethodArity,
                name, cd.Name.Name, classParamCount, ifaceType.Name, ifaceParamCount);
            return;
        }

        for (var i = 0; i < ifaceParamCount; i++)
        {
            var ifaceParam = ifaceFt.ParamTypes[i];
            var classParam = classFt.ParamTypes[i + offset];
            if (IsTypeAssignable(pc, classParam.ID, ifaceParam.ID)) continue;

            pc.Diag.Report(span, Diagnostics.DiagnosticCode.ErrInterfaceMethodParamType,
                classFt.ParamNames[i + offset], name, cd.Name.Name, TypeName(pc, classParam.ID),
                ifaceType.Name, TypeName(pc, ifaceParam.ID));
        }

        if (ifaceFt.IsVararg && !classFt.IsVararg)
        {
            pc.Diag.Report(span, Diagnostics.DiagnosticCode.ErrInterfaceMethodVararg,
                name, cd.Name.Name, ifaceType.Name);
        }

        var promisesNoValue = ifaceFt.ReturnType.ID == pc.Types.PrimNil.ID && method is { ReturnType: null };
        if (!promisesNoValue && !IsTypeAssignable(pc, ifaceFt.ReturnType.ID, classFt.ReturnType.ID))
        {
            pc.Diag.Report(span, Diagnostics.DiagnosticCode.ErrInterfaceMethodReturnType,
                name, cd.Name.Name, TypeName(pc, classFt.ReturnType.ID),
                ifaceType.Name, TypeName(pc, ifaceFt.ReturnType.ID));
        }
    }

    /// <summary>
    /// Reports whether the class signature demands an argument the interface knows nothing about,
    /// which a call routed through the interface could never supply. Extra parameters are fine as
    /// long as every one of them is optional.
    /// </summary>
    private static bool HasRequiredParamsBeyond(FunctionType classFt, int offset, int ifaceParamCount)
    {
        for (var i = offset + ifaceParamCount; i < classFt.ParamTypes.Count; i++)
        {
            if (!classFt.DefaultParams.Contains(i)) return true;
        }

        return false;
    }

    /// <summary>
    /// Compares a field the class declares against the interface field it implements. Fields are
    /// read and written through the interface, so the types have to match in both directions.
    /// </summary>
    private void CheckImplementedFieldType(PassContext pc, ClassDecl cd, InterfaceType ifaceType,
        string name, StructType.Field ifaceField, StructType.Field classField)
    {
        if (IsTypeAssignable(pc, ifaceField.Type.ID, classField.Type.ID)
            && IsTypeAssignable(pc, classField.Type.ID, ifaceField.Type.ID)) return;

        var field = cd.Fields.FirstOrDefault(f => !f.IsLocal && f.Name.Name == name);
        pc.Diag.Report(field?.Name.Span ?? cd.Span, Diagnostics.DiagnosticCode.ErrInterfaceFieldType,
            name, cd.Name.Name, TypeName(pc, classField.Type.ID),
            ifaceType.Name, TypeName(pc, ifaceField.Type.ID));
    }

    private static bool InterfaceHasMethod(ClassType classType, string name)
    {
        foreach (var iface in Type.ImplementedInterfaces(classType))
        {
            if (iface.Methods.ContainsKey(name)) return true;
        }
        return false;
    }

    /// <summary>
    /// Reports the members a class created by its host cannot have: accessors and the call operator
    /// need a metatable of their own, which the host keeps, and the index operators need a method
    /// of the host to be called through.
    /// </summary>
    private static void CheckHostInheritedMembers(PassContext pc, ClassDecl cd, ClassType classType)
    {
        if (cd.IsDeclare || cd.BaseClass == null || classType.EffectiveInheritTemplate == null) return;

        foreach (var accessor in cd.Accessors)
        {
            pc.Diag.Report(accessor.Span, Diagnostics.DiagnosticCode.ErrHostInheritedMember, cd.Name.Name, "accessors");
        }

        var unsupported = new HashSet<string> { "__op_call" };
        if (classType.NearestIndexHook == null) unsupported.Add("__op_index");
        if (classType.NearestNewIndexHook == null) unsupported.Add("__op_newindex");

        foreach (var method in cd.Methods.Where(method => unsupported.Contains(method.Name.Name)))
        {
            pc.Diag.Report(method.Span, Diagnostics.DiagnosticCode.ErrHostInheritedMember, cd.Name.Name, "this operator");
        }
    }

    private void CheckAbstractImplementation(PassContext pc, ClassDecl cd, ClassType classType)
    {
        if (cd.IsAbstract || classType.BaseClass == null) return;

        var visited = new HashSet<ClassType>();
        var cur = classType.BaseClass;
        while (cur != null && visited.Add(cur))
        {
            foreach (var abstractMethod in cur.AbstractMethods)
            {
                if (!classType.Methods.ContainsKey(abstractMethod))
                    pc.Diag.Report(cd.Span, Diagnostics.DiagnosticCode.ErrMissingAbstractMember, cd.Name.Name, abstractMethod, cur.Name);
            }
            cur = cur.BaseClass;
        }
    }

    private void ResolveInterfaceDecl(PassContext pc, InterfaceDecl id)
    {
        if (!_resolvedInterfaceDecls.Add(id.ID)) return;
        ReportReservedMemberNames(pc, id);
        if (id.Name.Sym == SymID.Invalid) return;
        if (!pc.Pkg!.Syms.GetByID(id.Name.Sym, out var ifaceSym)) return;
        if (!pc.Types.GetByID(ifaceSym.Type, out var rawType) || rawType is not InterfaceType ifaceType) return;

        for (var index = 0; index < id.BaseInterfaces.Count; index++)
        {
            var baseIface = id.BaseInterfaces[index];
            if (baseIface.Sym == SymID.Invalid) continue;
            var bt = LookupSymbolType(pc, baseIface.Sym);
            if (bt != TypID.Invalid && pc.Types.GetByID(bt, out var bit) && bit is InterfaceType baseIfaceType)
            {
                var baseArgs = index < id.BaseInterfaceTypeArgs.Count ? id.BaseInterfaceTypeArgs[index] : [];
                ReportBaseTypeArguments(pc, baseIface, baseIfaceType.TypeParams.Count, baseArgs);
                baseIfaceType = (InterfaceType)ResolveTypeRefsPass.Instantiation(pc.Types, baseIfaceType, baseArgs);
                // Same reasoning as for base classes: a cycle here makes the walks over the
                // extends graph non-terminating, so it is rejected rather than recorded.
                if (ReachesThroughBaseInterfaces(baseIfaceType, ifaceType))
                    pc.Diag.Report(baseIface.Span, Diagnostics.DiagnosticCode.ErrCyclicInheritance,
                        id.Name.Name, baseIface.Name);
                else
                    AddOnce(ifaceType.BaseInterfaces, baseIfaceType);
            }
        }

        // Type `self` inside default-method bodies as the interface itself.
        foreach (var (selfId, selfSym) in pc.Pkg.Syms.ByID)
            if (selfSym.Name == "self" && selfSym.DeclaringNode == id.ID && selfSym.Type == TypID.Invalid)
                pc.Pkg.Syms.SetType(selfId, ifaceType.ID);

        foreach (var field in id.Fields)
        {
            var fType = field.TypeAnnotation != null && field.TypeAnnotation.ResolvedType != TypID.Invalid
                ? GetType(pc, field.TypeAnnotation.ResolvedType) : pc.Types.PrimAny;
            ifaceType.Fields[field.Name.Name] = new StructType.Field(field.Name, fType);
            ifaceType.MemberDeclarations.TryAdd(field.Name.Name, field.Name);
            StampMemberSide(pc, field.Annotations, ifaceType.FieldSides, field.Name.Name);
            if (id.IsContract) ReadContractRules(pc, ifaceType, field, fType);
        }

        var defaultBodies = new List<(InterfaceMethodNode Method, Type ReturnType)>();
        foreach (var method in id.Methods)
        {
            var methodParams = new List<Tuple<string, Type>>();
            var ifaceIsVararg = false;
            Type? ifaceVarargType = null;
            var defaultIndices = new List<int>();
            for (var i = 0; i < method.Parameters.Count; i++)
            {
                var p = method.Parameters[i];
                var t = ResolveParamType(pc, p);
                if (p.IsVararg)
                {
                    ifaceIsVararg = true;
                    ifaceVarargType = t.Kind == TypeKind.PrimitiveAny ? null : t;
                }
                else
                {
                    methodParams.Add(new Tuple<string, Type>(p.Name.Name, t));
                }
                if (p.Name.Sym != SymID.Invalid) pc.Pkg!.Syms.SetType(p.Name.Sym, t.ID);
                if (p.DefaultValue != null)
                {
                    SynthesizeExpr(pc, p.DefaultValue);
                    defaultIndices.Add(i);
                }
            }
            var retType = method.ReturnType != null && method.ReturnType.ResolvedType != TypID.Invalid
                ? GetType(pc, method.ReturnType.ResolvedType) : UnannotatedReturnType(pc, method.Body, method.ReturnStmt);
            var ifaceFt = (FunctionType)GetType(pc,
                pc.Types.FuncOf(methodParams, retType, ifaceIsVararg, ifaceVarargType,
                    defaultIndices.Count > 0 ? defaultIndices : null, method.IsAsync,
                    predicate: BuildPredicate(pc, method.ReturnType, method.Parameters)));
            var methodSide = Nebra.Compiler.Annotations.BuiltinAnnotations.ExtractSide(method.Annotations,
                (ann, badName) => ReportBadSide(pc, ann, badName));
            AppendOverload(ifaceType.Methods, ifaceType.MethodOverloads,
                ifaceType.MethodOverloadSides, method.Name.Name, ifaceFt, methodSide);
            ifaceType.MethodNodes[method.Name.Name] = method;
            ifaceType.MemberDeclarations.TryAdd(method.Name.Name, method.Name);
            StampMemberSide(pc, method.Annotations, ifaceType.MethodSides, method.Name.Name);

            // A default method carries a body: record it (so implementing classes inherit it)
            // and type-check the body with `self` bound to the interface.
            if (method.IsDefault)
            {
                ifaceType.DefaultMethods.Add(method.Name.Name);
                ifaceType.DefaultMethodNodes[method.Name.Name] = method;
                defaultBodies.Add((method, retType));
            }
        }

        pc.Types.CompleteMembers(ifaceType);
        using var interfaceTypeParams = FixTypeParams(id.TypeParams);
        foreach (var (method, returnType) in defaultBodies)
        {
            using (FixTypeParams(method.TypeParams))
                ResolveMethodBody(pc, method.Body!, method.ReturnStmt, method.IsAsync, returnType, method.ReturnType?.Span ?? method.Name.Span);
        }
    }

    /// <summary>
    /// Type-checks a method body and its return paths against <paramref name="returnType"/>. Runs
    /// after every member of the owning type has been registered, so the body may call members
    /// declared after it.
    /// </summary>
    /// <summary>
    /// Whether a method written without a return type takes it from its body: it returns a value,
    /// and what it overrides, if anything, does not promise a value of its own.
    /// </summary>
    private static bool InfersReturnType(PassContext pc, ClassDecl cd, ClassMethodNode method, Type inherited)
    {
        if (cd.IsDeclare || method.IsAbstract || method.ReturnType != null) return false;

        return inherited.ID == pc.Types.PrimNil.ID && ReturnsValue(method.Body, method.ReturnStmt);
    }

    /// <summary>The return type of a signature written without one, whose body may return a value.</summary>
    private static Type UnannotatedReturnType(PassContext pc, List<Stmt>? body, ReturnStmt? returnStmt)
    {
        return ReturnsValue(body ?? [], returnStmt) ? pc.Types.PrimAny : pc.Types.PrimNil;
    }

    private static bool ReturnsValue(List<Stmt> body, ReturnStmt? returnStmt)
    {
        if (returnStmt is { Values.Count: > 0 }) return true;

        return body.SelectMany(ReturnStatements).Any(statement => statement.Values.Count > 0);
    }

    /// <summary>Resolves the body of a method without a return type and gives its signature the type the body returns.</summary>
    private void ResolveMethodInferringReturn(PassContext pc, ClassDecl cd, ClassType classType, ClassMethodNode method)
    {
        if (method.IsAsync) _asyncDepth++;
        ResolveFunctionBody(pc, method.Body, method.ReturnStmt);

        var collected = CollectReturnTypes(pc, method.Body);
        if (method.ReturnStmt != null)
        {
            collected.Add((ComputeReturnType(pc, method.ReturnStmt.Values), method.ReturnStmt.Span));
        }

        if (method.IsAsync) _asyncDepth--;
        AdoptReturnType(pc, cd, classType, method, InferReturnType(pc, collected));
    }

    private void AdoptReturnType(PassContext pc, ClassDecl cd, ClassType classType, ClassMethodNode method, Type returnType)
    {
        var name = method.Name.Name;
        var primary = method.IsStatic ? classType.StaticMethods : classType.Methods;
        var overloads = (method.IsStatic ? classType.StaticMethodOverloads : classType.MethodOverloads)[name];
        var index = cd.Methods.Where(candidate => candidate.IsStatic == method.IsStatic && candidate.Name.Name == name).ToList().IndexOf(method);

        var updated = WithReturnType(pc, overloads[index], returnType);
        overloads[index] = updated;
        if (index == overloads.Count - 1)
        {
            primary[name] = updated;
        }

        if (method.IsStatic)
        {
            SetStaticMemberType(pc, cd, name, index, updated);
        }
    }

    private FunctionType WithReturnType(PassContext pc, FunctionType function, Type returnType)
    {
        var parameters = function.ParamNames.Zip(function.ParamTypes, (name, type) => new Tuple<string, Type>(name, type)).ToList();
        var defaults = function.DefaultParams.Count > 0 ? function.DefaultParams : null;
        return (FunctionType)GetType(pc, pc.Types.FuncOf(parameters, returnType, function.IsVararg, function.VarargType, defaults, function.IsAsync, function.Predicate));
    }

    /// <summary>The type shared by everything a body returns: the widest of them, or <c>any</c> when they have nothing in common.</summary>
    private Type InferReturnType(PassContext pc, List<(TypID typ, TextSpan span)> collected)
    {
        if (collected.Count == 0) return pc.Types.PrimNil;

        var inferred = collected[0].typ;
        foreach (var (returned, _) in collected.Skip(1))
        {
            if (returned == inferred || IsTypeAssignable(pc, inferred, returned)) continue;
            if (!IsTypeAssignable(pc, returned, inferred)) return pc.Types.PrimAny;

            inferred = returned;
        }

        return GetType(pc, inferred);
    }

    private void ResolveMethodBody(PassContext pc, List<Stmt> body, ReturnStmt? returnStmt, bool isAsync, Type returnType,
        Diagnostics.TextSpan returnSpan)
    {
        if (isAsync) _asyncDepth++;
        ResolveFunctionBody(pc, body, returnStmt, returnType.ID);

        var collected = CollectReturnTypes(pc, body);
        if (returnStmt != null)
            collected.Add((ComputeReturnType(pc, returnStmt.Values), returnStmt.Span));
        CheckReturnFlow(pc, returnType.ID, collected, body, returnStmt, returnSpan);
        if (isAsync) _asyncDepth--;
    }

    private Type? ResolveExtendTarget(PassContext pc, ExtendDecl ed)
    {
        // Null target (parse error) or an unresolved/`any` target is not extendable — skip it
        // rather than crashing or polluting `any` with methods that would resolve everywhere.
        if (ed.TargetType == null || ed.TargetType.ResolvedType == TypID.Invalid) return null;
        var target = GetType(pc, ed.TargetType.ResolvedType);
        return target.ID == pc.Types.PrimAny.ID ? null : target;
    }

    private void ResolveExtendDecl(PassContext pc, ExtendDecl ed)
    {
        if (!_resolvedExtendDecls.Add(ed.ID)) return;
        var target = ResolveExtendTarget(pc, ed);
        if (target == null) return;

        foreach (var (selfId, selfSym) in pc.Pkg!.Syms.ByID)
            if (selfSym.Name == "self" && selfSym.DeclaringNode == ed.ID && selfSym.Type == TypID.Invalid)
                pc.Pkg.Syms.SetType(selfId, target.ID);

        foreach (var method in ed.Methods)
        {
            foreach (var p in method.Parameters)
            {
                var t = ResolveParamType(pc, p);
                if (p.Name.Sym != SymID.Invalid) pc.Pkg!.Syms.SetType(p.Name.Sym, t.ID);
                if (p.DefaultValue != null) SynthesizeExpr(pc, p.DefaultValue);
            }
            var retType = method.ReturnType != null && method.ReturnType.ResolvedType != TypID.Invalid
                ? GetType(pc, method.ReturnType.ResolvedType) : UnannotatedReturnType(pc, method.Body, method.ReturnStmt);

            if (ed.IsDeclare) continue;

            if (method.IsAsync) _asyncDepth++;
            using var extensionTypeParams = FixTypeParams(method.TypeParams);
            ResolveFunctionBody(pc, method.Body, method.ReturnStmt, retType.ID);

            var collected = CollectReturnTypes(pc, method.Body);
            if (method.ReturnStmt != null)
                collected.Add((ComputeReturnType(pc, method.ReturnStmt.Values), method.ReturnStmt.Span));
            CheckReturnFlow(pc, retType.ID, collected, method.Body, method.ReturnStmt,
                method.ReturnType?.Span ?? method.Name.Span);
            if (method.IsAsync) _asyncDepth--;
        }
    }

    /// <summary>
    /// Finds an extension method for <paramref name="name"/> on <paramref name="objType"/>, its
    /// base classes, or its implemented/extended interfaces. Returns the (self-prefixed)
    /// signature and the type the extension was declared on.
    /// </summary>
    private static (FunctionType?, Type?) ResolveExtensionMethod(PassContext pc, Type objType, string name)
    {
        return Type.ResolveExtension(objType, name, pc.Types.PrimFunction);
    }

    /// <summary>
    /// Records an overload for a class/interface method. <paramref name="primary"/>
    /// stays "last write wins" (so existing code paths that look up the single
    /// method type by name continue to work for the common no-overload case);
    /// <paramref name="overloads"/> accumulates every declared variant, with
    /// <paramref name="overloadSides"/> tracking each variant's side. Resolution
    /// uses the overload list when the call site has multiple candidates with
    /// the same name.
    /// </summary>
    private static void AppendOverload(Dictionary<string, FunctionType> primary,
        Dictionary<string, List<FunctionType>> overloads,
        Dictionary<string, List<Side>> overloadSides,
        string name, FunctionType ft, Side side)
    {
        primary[name] = ft;
        if (!overloads.TryGetValue(name, out var list))
        {
            list = [];
            overloads[name] = list;
        }
        list.Add(ft);
        if (!overloadSides.TryGetValue(name, out var sides))
        {
            sides = [];
            overloadSides[name] = sides;
        }
        sides.Add(side);
    }

    private static void StampMemberSide(PassContext pc, List<Annotation> annotations, Dictionary<string, Side> sides, string memberName)
    {
        if (annotations == null || annotations.Count == 0) return;
        var side = Nebra.Compiler.Annotations.BuiltinAnnotations.ExtractSide(annotations,
            (ann, badName) => ReportBadSide(pc, ann, badName));
        if (side != Side.All) sides[memberName] = side;
    }

    private static void ReportBadSide(PassContext pc, Annotation ann, string badName)
    {
        if (!string.IsNullOrEmpty(badName))
            pc.Diag.Report(ann.Span, Diagnostics.DiagnosticCode.ErrUnknownSideName, badName);
    }

    /// <summary>
    /// Builds the type-predicate marker from a <c>param is Type</c> return annotation, validating
    /// that the named parameter exists. Returns null for a plain return type.
    /// </summary>
    /// <summary>
    /// Narrows <paramref name="path"/> to <paramref name="targetType"/> in the then-branch and to
    /// the complement in the else-branch — the same shape as an <c>is</c> test.
    /// </summary>
    private void NarrowPredicate(PassContext pc, AccessPath? path, TypID targetType,
        List<(AccessPath, TypID)> thenN, List<(AccessPath, TypID)> elseN)
    {
        if (path == null || targetType == TypID.Invalid) return;
        var current = ResolveAccessPathType(pc, path);
        thenN.Add((path, targetType));
        var subtracted = SubtractType(pc, current, targetType);
        if (subtracted != TypID.Invalid) elseN.Add((path, subtracted));
    }

    private TypePredicate? BuildPredicate(PassContext pc, TypeRef? returnTypeRef, List<Parameter> parameters)
    {
        if (returnTypeRef is not TypePredicateRef tpr) return null;
        var pname = tpr.ParamName.Name;
        if (parameters.All(p => p.Name.Name != pname))
        {
            pc.Diag.Report(tpr.ParamName.Span, DiagnosticCode.ErrUnknownPredicateParam, pname);
            return null;
        }
        var target = tpr.TargetType.ResolvedType != TypID.Invalid
            ? GetType(pc, tpr.TargetType.ResolvedType) : pc.Types.PrimAny;
        return new TypePredicate(pname, target);
    }

    private void ResolveFunctionLike(PassContext pc, List<Parameter> parameters, TypeRef? returnTypeRef,
        List<Stmt> body, ReturnStmt? returnStmt, NameRef? funcName, bool isAsync = false, FunctionType? expected = null)
    {
        var paramTypes = new List<Tuple<string, Type>>();
        var isVararg = false;
        Type? varargType = null;
        var defaultIndices = new List<int>();

        for (var i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];
            var t = ParamTypeFrom(expected, param, i) ?? ResolveParamType(pc, param);

            if (param.IsVararg)
            {
                isVararg = true;
                varargType = t.Kind == TypeKind.PrimitiveAny ? null : t;
                if (param.Name.Sym != SymID.Invalid)
                {
                    var arrTyp = varargType != null
                        ? pc.Pkg!.Types.ArrayOf(varargType)
                        : pc.Pkg!.Types.ArrayOf(pc.Types.PrimAny);
                    pc.Pkg!.Syms.SetType(param.Name.Sym, arrTyp);
                }
            }
            else
            {
                paramTypes.Add(new Tuple<string, Type>(param.Name.Name, t));
                if (param.Name.Sym != SymID.Invalid)
                {
                    pc.Pkg!.Syms.SetType(param.Name.Sym, t.ID);
                }
                if (param.DefaultValue != null)
                {
                    var dvt = SynthesizeExpr(pc, param.DefaultValue);
                    EnsureAssignable(pc, param.DefaultValue.Span, t.ID, dvt);
                    defaultIndices.Add(i);
                }
            }
        }

        ResolveFunctionBody(pc, body, returnStmt, returnTypeRef?.ResolvedType);

        Type returnType;
        if (returnTypeRef != null && returnTypeRef.ResolvedType != TypID.Invalid)
        {
            returnType = GetType(pc, returnTypeRef.ResolvedType);
            var collected = CollectReturnTypes(pc, body);
            if (returnStmt != null)
            {
                collected.Add((ComputeReturnType(pc, returnStmt.Values), returnStmt.Span));
            }

            CheckReturnFlow(pc, returnType.ID, collected, body, returnStmt, returnTypeRef.Span);
        }
        else
        {
            var collected = CollectReturnTypes(pc, body);
            if (returnStmt != null)
            {
                collected.Add((ComputeReturnType(pc, returnStmt.Values), returnStmt.Span));
            }

            returnType = InferReturnType(pc, collected);
        }

        var funcTyp = pc.Types.FuncOf(paramTypes, returnType, isVararg, varargType,
            defaultIndices.Count > 0 ? defaultIndices : null, isAsync,
            predicate: BuildPredicate(pc, returnTypeRef, parameters));
        if (funcName is { Sym: { } funcSym } && funcSym != SymID.Invalid)
        {
            pc.Pkg!.Syms.SetType(funcSym, funcTyp);
        }
    }

    private void ResolveLocalDecl(PassContext pc, LocalDecl ld)
    {
        var valueTypes = new List<TypID>();
        for (var i = 0; i < ld.Values.Count; i++)
        {
            var expected = i < ld.Variables.Count ? ld.Variables[i].TypeAnnotation?.ResolvedType ?? TypID.Invalid : TypID.Invalid;
            valueTypes.Add(SynthesizeFor(pc, ld.Values[i], expected));
        }

        valueTypes = ExpandTrailingTuple(pc, ld.Values, valueTypes, ld.Variables.Count);

        for (var i = 0; i < ld.Variables.Count; i++)
        {
            var variable = ld.Variables[i];
            var annotated = variable.TypeAnnotation?.ResolvedType ?? TypID.Invalid;
            var inferred = i < valueTypes.Count ? FirstValue(pc, valueTypes[i]) : pc.Types.PrimAny.ID;

            TypID finalType;
            if (annotated != TypID.Invalid)
            {
                if (i < valueTypes.Count && !IsTypeAssignable(pc, annotated, inferred))
                {
                    pc.Diag.Report(variable.Span, DiagnosticCode.ErrTypeMismatch,
                        TypeName(pc, annotated), TypeName(pc, inferred));
                }

                finalType = annotated;
            }
            else
            {
                finalType = inferred != TypID.Invalid ? inferred : pc.Types.PrimAny.ID;
            }

            if (variable.Name.Sym != SymID.Invalid)
            {
                pc.Pkg!.Syms.SetType(variable.Name.Sym, finalType);
            }
        }
    }

    /// <summary>
    /// Resolves an assignment. Writing through a narrowed access path ends that narrowing unless
    /// the new value fits the narrowed type as well: the value only has to fit the declared type,
    /// and reads after the assignment see the declared type again.
    /// </summary>
    private void ResolveAssignStmt(PassContext pc, AssignStmt stmt)
    {
        var valueTypes = new List<TypID>();
        foreach (var value in stmt.Values)
        {
            valueTypes.Add(SynthesizeArgument(pc, value));
        }

        valueTypes = ExpandTrailingTuple(pc, stmt.Values, valueTypes, stmt.Targets.Count);

        for (var i = 0; i < stmt.Targets.Count; i++)
        {
            var target = stmt.Targets[i];
            var narrowedPath = GetAccessPath(target);
            var assigned = i < valueTypes.Count ? FirstValue(pc, valueTypes[i]) : pc.Types.PrimNil.ID;
            if (narrowedPath != null && !KeepsNarrowing(pc, narrowedPath, assigned)) _narrowed.Remove(narrowedPath);

            CheckAssignableTarget(pc, target);
            ReportClassReplacement(pc, target);
            if (target is IndexAccessExpr indexTarget && TryNewIndexOperator(pc, indexTarget, assigned)) continue;

            _assignmentTarget = target;
            var targetType = SynthesizeExpr(pc, target);
            _assignmentTarget = null;
            if (i < stmt.Values.Count && stmt.Values[i] is FunctionDefExpr literal && _pendingLiterals.Contains(literal))
                valueTypes[i] = SynthesizeWithSignature(pc, literal, ExpectedSignature(pc, Evaluate(pc, targetType)));
            if (i < stmt.Values.Count && stmt.Values[i] is TableConstructorExpr table && _pendingTables.Contains(table))
                valueTypes[i] = SynthesizeTableFor(pc, table, Evaluate(pc, targetType));
            if (i < stmt.Values.Count && ContextualTypeFor(pc, stmt.Values[i], targetType) is { } literalValue)
                valueTypes[i] = literalValue;
            if (i < valueTypes.Count && targetType != TypID.Invalid && targetType != pc.Types.PrimAny.ID)
            {
                var valueType = i < stmt.Values.Count && IsEmptyTable(stmt.Values[i]) && TakesEmptyTable(pc, targetType)
                    ? targetType
                    : FirstValue(pc, valueTypes[i]);
                if (!IsTypeAssignable(pc, targetType, valueType) && !DeletesMapEntry(pc, target, valueType))
                {
                    pc.Diag.Report(target.Span, DiagnosticCode.ErrTypeMismatch,
                        TypeName(pc, targetType), TypeName(pc, valueType));
                }
            }
        }

        FlushPendingLiterals(pc, stmt.Values);
    }

    /// <summary>
    /// Whether an assignment removes an entry of a map, as <c>set[key] = nil</c> does in Lua. A map
    /// holds its values, not <c>nil</c>, so its value type does not have to allow it.
    /// </summary>
    private bool DeletesMapEntry(PassContext pc, Expr target, TypID value)
    {
        if (value != pc.Types.PrimNil.ID || target is not IndexAccessExpr index) return false;
        return pc.Pkg!.Types.GetByID(StripNil(pc, SynthesizeExpr(pc, index.Object)), out var container) && container is TableMapType;
    }

    private bool KeepsNarrowing(PassContext pc, AccessPath path, TypID assigned)
    {
        if (!_narrowed.TryGetValue(path, out var narrowed)) return false;
        if (IsNullable(pc, assigned) && !IsNullable(pc, narrowed)) return false;
        return IsTypeAssignable(pc, narrowed, assigned);
    }

    /// <summary>
    /// Rejects an assignment to a property that only declares a getter. The write would be
    /// dropped: codegen routes the read through <c>__get_name</c> and has no setter to route the
    /// write to. A property declaring both an accessor and a plain field of the same name is left
    /// alone, since the field carries the value.
    /// </summary>
    private void CheckAssignableTarget(PassContext pc, Expr target)
    {
        if (target is not DotAccessExpr dot) return;

        var objTyp = SynthesizeExpr(pc, dot.Object);
        if (!pc.Pkg!.Types.GetByID(objTyp, out var objType)) return;
        if (objType is not ClassType classType) return;

        var name = dot.FieldName.Name;
        for (var cur = classType; cur != null; cur = cur.BaseClass)
        {
            if (cur.InstanceFields.ContainsKey(name) || cur.Setters.ContainsKey(name)) return;
            if (!cur.Getters.ContainsKey(name)) continue;

            pc.Diag.Report(dot.FieldName.Span, DiagnosticCode.ErrWriteToReadonly, name);
            return;
        }
    }

    /// <summary>
    /// Lua-style multi-assignment unpacks the LAST expression's multi-returns
    /// across any extra LHS slots. A function returning <c>(string, number,
    /// boolean)</c> consumed by <c>local a, b, c = f()</c> binds each element
    /// to one LHS instead of putting the whole tuple in <c>a</c>. Only the
    /// trailing expression is expanded — earlier ones are always single-value
    /// in Lua semantics. No-op when LHS doesn't exceed RHS or the trailing
    /// type isn't a tuple.
    /// </summary>
    private List<TypID> ExpandTrailingTuple(PassContext pc, List<Expr> values, List<TypID> valueTypes, int targetCount)
    {
        if (valueTypes.Count == 0 || valueTypes.Count >= targetCount) return valueTypes;
        if (!IsMultiReturnExpr(values[^1])) return valueTypes;

        var lastTyp = valueTypes[^1];
        if (!pc.Pkg!.Types.GetByID(lastTyp, out var t)) return valueTypes;

        var expanded = new List<TypID>(valueTypes.Take(valueTypes.Count - 1));
        switch (t)
        {
            case TupleType tuple:
            {
                // A tuple return may end in a variadic tail, e.g. `(number, ...string)`:
                // the fixed fields fill their slots, then the tail element repeats to cover
                // any remaining LHS slots.
                var fixedCount = tuple.Fields.Count;
                VariadicType? tail = null;
                if (fixedCount > 0 && tuple.Fields[^1].Type is VariadicType vt)
                {
                    tail = vt;
                    fixedCount--;
                }
                for (var i = 0; i < fixedCount; i++) expanded.Add(tuple.Fields[i].Type.ID);
                if (tail != null)
                    while (expanded.Count < targetCount) expanded.Add(tail.ElementType.ID);
                break;
            }
            case VariadicType variadic:
                // `...T` fills every remaining LHS slot with T.
                while (expanded.Count < targetCount) expanded.Add(variadic.ElementType.ID);
                break;
            default:
                return valueTypes;
        }

        return expanded;
    }

    /// <summary>
    /// A variadic type is a multi-value marker; when it lands in a single-value slot
    /// (e.g. <c>local a = variadicReturningCall()</c>) it collapses to its element type.
    /// </summary>
    private TypID CollapseVariadic(PassContext pc, TypID id)
    {
        if (pc.Pkg!.Types.GetByID(id, out var t) && t is VariadicType v) return v.ElementType.ID;
        return id;
    }

    /// <summary>
    /// The type a single slot receives from a value: Lua keeps only the first of several values,
    /// so <c>local year = civil(days)</c> with a call returning <c>(number, number, number)</c>
    /// is a <c>number</c>, and a call returning nothing gives <c>nil</c>.
    /// </summary>
    private TypID FirstValue(PassContext pc, TypID id)
    {
        if (!pc.Pkg!.Types.GetByID(id, out var type) || type is not TupleType tuple) return CollapseVariadic(pc, id);
        return tuple.Fields.Count == 0 ? pc.Types.PrimNil.ID : FirstValue(pc, tuple.Fields[0].Type.ID);
    }

    /// <summary>
    /// Calls and varargs are the only expressions that Lua allows to spread
    /// into multiple values at the end of an assignment list. Everything else
    /// — parenthesised expressions included — collapses to a single value.
    /// </summary>
    private static bool IsMultiReturnExpr(Expr e)
    {
        return e is FunctionCallExpr or MethodCallExpr or VarargExpr;
    }

    private Type ResolveParamType(PassContext pc, Parameter param)
    {
        if (param.TypeAnnotation != null && param.TypeAnnotation.ResolvedType != TypID.Invalid)
        {
            return GetType(pc, param.TypeAnnotation.ResolvedType);
        }

        return pc.Types.PrimAny;
    }

    /// <summary>
    /// The <c>iterator()</c> method a loop over a single value calls: one of the value's class or
    /// interface that takes no arguments.
    /// </summary>
    private FunctionType? IteratorMethodOf(PassContext pc, GenericForStmt gf, List<TypID> iterTypes)
    {
        if (gf.Iterators.Count != 1 || !pc.Pkg!.Types.GetByID(iterTypes[0], out var iterated)) return null;
        if (iterated is not (ClassType or InterfaceType) && iterated.Kind != TypeKind.Parameterized) return null;

        var method = ResolveMethodOnType(pc, iterated, "iterator");
        var receiverCount = method != null && method.ParamNames.Count > 0 && method.ParamNames[0] == "self" ? 1 : 0;
        return method != null && method.MinParamCount <= receiverCount ? method : null;
    }

    /// <summary>
    /// Types the variables of a loop over <c>iterator()</c>: what its iterator function returns,
    /// when the method declares one, and <c>any</c> otherwise.
    /// </summary>
    private void InferIteratedVarTypes(PassContext pc, GenericForStmt gf, FunctionType iteratorMethod)
    {
        var returned = iteratorMethod.ReturnType is TupleType tuple && tuple.Fields.Count > 0 ? tuple.Fields[0].Type : iteratorMethod.ReturnType;
        var values = returned is FunctionType step
            ? step.ReturnType is TupleType results ? results.Fields.Select(field => field.Type.ID).ToList() : [step.ReturnType.ID]
            : [];

        for (var i = 0; i < gf.VarNames.Count; i++)
        {
            var type = i < values.Count ? CollapseVariadic(pc, values[i]) : pc.Types.PrimAny.ID;
            pc.Pkg!.Syms.SetType(gf.VarNames[i].Sym, i == 0 ? StripNil(pc, type) : type);
        }
    }

    private void InferGenericForVarTypes(PassContext pc, GenericForStmt gf, List<TypID> iterTypes)
    {
        var tt = pc.Types;
        var varCount = gf.VarNames.Count;
        var inferred = new TypID[varCount];
        for (var i = 0; i < varCount; i++) inferred[i] = tt.PrimAny.ID;

        if (IsNextOverTable(pc, gf, iterTypes, out var iteratedTable) || IsIteratorCallOverTable(pc, gf, out iteratedTable))
        {
            iterTypes = [iteratedTable];
        }

        if (iterTypes.Count >= 1 && tt.GetByID(iterTypes[0], out var firstIterType))
        {
            switch (firstIterType)
            {
                case FunctionType ft:
                {
                    var produced = ft.ReturnType is TupleType tuple ? tuple.Fields.Select(field => field.Type).ToList() : [ft.ReturnType];
                    for (var i = 0; i < varCount && i < produced.Count; i++)
                        inferred[i] = produced[i].ID;
                    if (varCount >= 1) inferred[0] = StripNil(pc, inferred[0]);
                    break;
                }
                case TableArrayType arr:
                    if (varCount >= 1) inferred[0] = tt.PrimNumber.ID;
                    if (varCount >= 2) inferred[1] = arr.ElementType.ID;
                    break;
                case TableMapType map:
                    if (varCount >= 1) inferred[0] = map.KeyType.ID;
                    if (varCount >= 2) inferred[1] = map.ValueType.ID;
                    break;
                case EnumType enumType:
                    if (varCount >= 1) inferred[0] = tt.PrimString.ID;
                    if (varCount >= 2) inferred[1] = enumType.BaseType.ID;
                    break;
            }
        }

        for (var i = 0; i < varCount; i++)
        {
            pc.Pkg!.Syms.SetType(gf.VarNames[i].Sym, inferred[i]);
        }
    }

    /// <summary>
    /// Whether the loop is written <c>for i, v in ipairs(t)</c> or <c>for k, v in pairs(t)</c> with
    /// the functions of the standard library over a table whose type is known, so its variables
    /// take the table's types rather than the declared <c>any</c> of those functions.
    /// </summary>
    private bool IsIteratorCallOverTable(PassContext pc, GenericForStmt gf, out TypID table)
    {
        table = TypID.Invalid;
        if (gf.Iterators is not [FunctionCallExpr { Callee: NameExpr callee, Arguments: [var argument] }]) return false;
        if (callee.Name.Name is not ("ipairs" or "pairs")) return false;
        if (!pc.Pkg!.Syms.GetByID(callee.Name.Sym, out var symbol) || !symbol.Flags.HasFlag(SymbolFlags.External)) return false;

        table = StripNil(pc, argument.Type);
        var isList = pc.Types.GetByID(table, out var type) && type is TableArrayType;
        return callee.Name.Name == "pairs" ? isList || type is TableMapType : isList;
    }

    /// <summary>
    /// Whether the loop is written <c>for k, v in next, t</c> over a table whose type is known,
    /// which iterates exactly like <c>pairs(t)</c>, so its variables take the table's key and value
    /// types rather than the declared <c>any</c> of <c>next</c>.
    /// </summary>
    private bool IsNextOverTable(PassContext pc, GenericForStmt gf, List<TypID> iterTypes, out TypID table)
    {
        table = TypID.Invalid;
        if (gf.Iterators is not [NameExpr { Name.Name: "next" } next, _, ..] || iterTypes.Count < 2) return false;
        if (!pc.Pkg!.Syms.GetByID(next.Name.Sym, out var symbol) || !symbol.Flags.HasFlag(SymbolFlags.External)) return false;
        if (!pc.Types.GetByID(StripNil(pc, iterTypes[1]), out var iterated) || iterated is not (TableArrayType or TableMapType)) return false;

        table = iterated.ID;
        return true;
    }

    private TypID SynthesizeExpr(PassContext pc, Expr expr)
    {
        var tt = pc.Types;
        // Same defensive guard the earlier passes use — parser-recovery may
        // have left a nested expression slot null; return the any-type so
        // downstream consumers don't crash on a missing inferred type.
        if (expr == null) return tt.PrimAny.ID;
        TypID result;

        switch (expr)
        {
            case NilLiteralExpr:
                result = tt.PrimNil.ID;
                break;
            case BoolLiteralExpr:
                result = tt.PrimBool.ID;
                break;
            case NumberLiteralExpr:
                result = tt.PrimNumber.ID;
                break;
            case StringLiteralExpr:
                result = tt.PrimString.ID;
                break;
            case InterpolatedStringExpr interp:
                foreach (var part in interp.Parts)
                {
                    if (part is InterpExprPart ep)
                        SynthesizeExpr(pc, ep.Expression);
                }

                if (!pc.Config.Code.StringInterpolation)
                {
                    pc.Diag.Report(interp.Span, DiagnosticCode.ErrStringInterpolationDisabled);
                }

                result = tt.PrimString.ID;
                break;
            case VarargExpr:
                result = tt.PrimAny.ID;
                break;
            case NameExpr { IsUnresolved: true } unresolved:
                result = SynthesizeUnresolvedName(pc, unresolved);
                break;
            case NameExpr nameExpr:
                result = LookupSymbolType(pc, nameExpr.Name.Sym);
                break;
            case ParenExpr paren:
                result = FirstValue(pc, SynthesizeExpr(pc, paren.Inner));
                break;
            case BinaryExpr bin:
                result = InferBinary(pc, bin);
                break;
            case UnaryExpr un:
                result = InferUnary(pc, un);
                break;
            case NonNilAssertExpr nna:
                result = StripNil(pc, SynthesizeExpr(pc, nna.Inner));
                break;
            case IncDecExpr incDec:
                result = InferIncDec(pc, incDec);
                break;
            case TypeCheckExpr tchk:
                SynthesizeExpr(pc, tchk.Inner);
                NoteGenericTypeCheck(pc, tchk.TargetType, tchk.Span);
                result = tt.PrimBool.ID;
                break;
            case TypeCastExpr tcast:
                SynthesizeExpr(pc, tcast.Inner);
                result = tcast.TargetType.ResolvedType != TypID.Invalid
                    ? tcast.TargetType.ResolvedType
                    : tt.PrimAny.ID;
                break;
            case TypeOfExpr tof:
                SynthesizeExpr(pc, tof.Inner);
                result = tt.PrimString.ID;
                break;
            case InstanceOfExpr iof:
                SynthesizeExpr(pc, iof.Inner);
                if (iof.TargetType is GenericTypeRef) NoteGenericTypeCheck(pc, iof.TargetType, iof.Span);
                result = tt.PrimBool.ID;
                break;
            case FunctionDefExpr fde:
                result = InferFunctionDef(pc, fde);
                break;
            case DotAccessExpr dot:
                result = InferDotAccess(pc, dot);
                break;
            case IndexAccessExpr idx:
                result = InferIndexAccess(pc, idx);
                break;
            case FunctionCallExpr call:
                result = InferFunctionCall(pc, call);
                FlushPendingLiterals(pc, call.Arguments);
                break;
            case MethodCallExpr mc:
                result = InferMethodCall(pc, mc);
                FlushPendingLiterals(pc, mc.Arguments);
                break;
            case TableConstructorExpr tc:
                result = InferTableConstructor(pc, tc);
                break;
            case MatchExpr me:
                result = InferMatchExpr(pc, me);
                break;
            case AwaitExpr awaitExpr:
                result = InferAwaitExpr(pc, awaitExpr);
                break;
            case NewExpr newExpr:
                result = InferNewExpr(pc, newExpr);
                FlushPendingLiterals(pc, newExpr.Arguments);
                break;
            case SuperCallExpr superCall:
                if (!_constructorSuperCalls.Contains(superCall))
                    pc.Diag.Report(superCall.Span, DiagnosticCode.ErrSuperOutsideConstructor);
                CheckSuperArguments(pc, superCall, superCall.Arguments.Select(arg => SynthesizeArgument(pc, arg)).ToList());
                FlushPendingLiterals(pc, superCall.Arguments);
                result = tt.PrimAny.ID;
                break;
            default:
                result = tt.PrimAny.ID;
                break;
        }

        result = Evaluate(pc, result);
        expr.Type = result;
        return result;
    }

    /// <summary>
    /// Infers the type of a binary expression. For the short-circuiting operators a <c>never</c>
    /// operand is special: a diverging left side takes the whole expression with it, while a
    /// diverging right side contributes no value, so the result is the surviving operand — and for
    /// <c>or</c>, reaching the result means the left side was truthy, which rules its nil out.
    /// </summary>
    /// <summary>
    /// Types the right side of <c>and</c> or <c>or</c>, which only runs when the left side was
    /// truthy or falsy respectively. In <c>x == nil or x &lt; 1</c> the comparison sees <c>x</c>
    /// as not nil.
    /// </summary>
    private TypID SynthesizeShortCircuitRight(PassContext pc, BinaryExpr bin)
    {
        var (whenTruthy, whenFalsy) = AnalyzeCondition(pc, bin.Left);
        var saved = PushAllNarrows(bin.Op == BinaryOp.And ? whenTruthy : whenFalsy);
        try
        {
            return SynthesizeExpr(pc, bin.Right);
        }
        finally
        {
            PopAllNarrows(saved);
        }
    }

    private TypID InferBinary(PassContext pc, BinaryExpr bin)
    {
        var tt = pc.Types;
        var l = FirstValue(pc, SynthesizeExpr(pc, bin.Left));
        var r = FirstValue(pc, bin.Op is BinaryOp.And or BinaryOp.Or ? SynthesizeShortCircuitRight(pc, bin) : SynthesizeExpr(pc, bin.Right));
        AdoptComparedEnumMember(pc, bin, l, r);
        l = bin.Left.Type != TypID.Invalid ? FirstValue(pc, bin.Left.Type) : l;
        r = bin.Right.Type != TypID.Invalid ? FirstValue(pc, bin.Right.Type) : r;
        if (bin.Op == BinaryOp.Concat && l == tt.PrimNumber.ID && r == tt.PrimNumber.ID)
        {
            bin.IsRange = true;
            return tt.ArrayOf(tt.PrimNumber);
        }

        if (TryExtensionOperator(pc, bin, l, r, out var extensionResult)) return extensionResult;

        var metaName = BinaryOpToMetamethod(bin.Op);
        if (metaName != null && TryClassBinaryOperator(pc, bin, metaName, l, r, out var classResult)) return classResult;

        if (IsConfiguredConcatOp(pc, bin.Op)
            && (l == tt.PrimString.ID || r == tt.PrimString.ID))
        {
            EnsureConcatable(pc, bin.Left.Span, l);
            EnsureConcatable(pc, bin.Right.Span, r);
            return tt.PrimString.ID;
        }

        if (metaName != null)
        {
            var metaReturn = TryGetMetamethodReturn(pc, l, metaName) ?? TryGetMetamethodReturn(pc, r, metaName);
            if (metaReturn != null) return metaReturn.Value;
        }

        switch (bin.Op)
        {
            case BinaryOp.Add:
            case BinaryOp.Sub:
            case BinaryOp.Mul:
            case BinaryOp.Div:
            case BinaryOp.FloorDiv:
            case BinaryOp.Mod:
            case BinaryOp.Pow:
                EnsureAssignable(pc, bin.Left.Span, tt.PrimNumber.ID, l);
                EnsureAssignable(pc, bin.Right.Span, tt.PrimNumber.ID, r);
                return tt.PrimNumber.ID;
            case BinaryOp.Concat:
                EnsureConcatable(pc, bin.Left.Span, l);
                EnsureConcatable(pc, bin.Right.Span, r);
                return tt.PrimString.ID;
            case BinaryOp.BitwiseAnd:
            case BinaryOp.BitwiseOr:
            case BinaryOp.BitwiseXor:
            case BinaryOp.LShift:
            case BinaryOp.RShift:
            {
                EnsureAssignable(pc, bin.Left.Span, tt.PrimNumber.ID, l);
                EnsureAssignable(pc, bin.Right.Span, tt.PrimNumber.ID, r);

                // Codegen has no lowering for these unless the target has the operators or the
                // LuaJIT bit library; without one it used to emit nothing at all, leaving the
                // surrounding statement syntactically broken.
                var features = Codegen.LuaFeatureSet.For(pc.Config.Target);
                if (!features.HasBitwise && features.BitwiseStyle != Codegen.BitwiseStyle.BitLib)
                {
                    pc.Diag.Report(bin.Span, DiagnosticCode.ErrBitwiseUnsupported, pc.Config.Target);
                }

                return tt.PrimNumber.ID;
            }
            case BinaryOp.Eq:
            case BinaryOp.Neq:
                return tt.PrimBool.ID;
            case BinaryOp.Lt:
            case BinaryOp.Gt:
            case BinaryOp.Lte:
            case BinaryOp.Gte:
                if (!IsNumberLike(pc, l) && !IsStringLike(pc, l))
                {
                    pc.Diag.Report(bin.Left.Span, DiagnosticCode.ErrTypeMismatch, "number or string", TypeName(pc, l));
                }

                if (!IsNumberLike(pc, r) && !IsStringLike(pc, r))
                {
                    pc.Diag.Report(bin.Right.Span, DiagnosticCode.ErrTypeMismatch, "number or string", TypeName(pc, r));
                }

                return tt.PrimBool.ID;
            case BinaryOp.And:
            case BinaryOp.Or:
                if (l == tt.PrimNever.ID) return tt.PrimNever.ID;
                if (r == tt.PrimNever.ID) return bin.Op == BinaryOp.Or ? StripNil(pc, l) : l;
                if (l == tt.PrimAny.ID || r == tt.PrimAny.ID) return tt.PrimAny.ID;
                return bin.Op == BinaryOp.And ? InferAnd(pc, l, r) : InferOr(pc, bin, l, r);
            case BinaryOp.NilCoalesce:
            {
                if (l == tt.PrimNever.ID) return tt.PrimNever.ID;
                var stripped = StripNil(pc, l);
                if (stripped == r) return stripped;
                if (l == tt.PrimAny.ID || r == tt.PrimAny.ID) return tt.PrimAny.ID;
                if (IsTypeAssignable(pc, stripped, r)) return stripped;
                return pc.Types.UnionOf([GetType(pc, stripped), GetType(pc, r)]);
            }
            default:
                pc.Diag.Report(bin.Span, DiagnosticCode.ErrInvalidOperator, bin.Op.ToString());
                return tt.PrimAny.ID;
        }
    }

    /// <summary>
    /// The type of <c>l and r</c>: <paramref name="l"/> when it is falsy, else <paramref name="r"/>.
    /// A left side that can never be falsy always gives the right one, and one that can only be
    /// falsy by being nil adds just <c>nil</c>.
    /// </summary>
    private TypID InferAnd(PassContext pc, TypID l, TypID r)
    {
        if (!CanBeFalsy(pc, l)) return r;
        if (l == r) return l;
        if (!CanBeFalse(pc, l)) return MakeNullable(pc, r);
        return pc.Types.UnionOf([GetType(pc, l), GetType(pc, r)]);
    }

    /// <summary>
    /// The type of <c>l or r</c>: <paramref name="l"/> when it is truthy, else <paramref name="r"/>,
    /// so the nil of <paramref name="l"/> never reaches the result. In the conditional idiom
    /// <c>cond and a or b</c> the result is <c>a</c> or <c>b</c>, whatever <c>cond</c> is.
    /// </summary>
    private TypID InferOr(PassContext pc, BinaryExpr bin, TypID l, TypID r)
    {
        if (!CanBeFalsy(pc, l)) return l;

        var truthy = bin.Left is BinaryExpr { Op: BinaryOp.And } conditional && conditional.Right.Type != TypID.Invalid
            ? StripNil(pc, conditional.Right.Type)
            : StripNil(pc, l);
        if (truthy == pc.Types.PrimAny.ID || truthy == r) return r == pc.Types.PrimAny.ID ? truthy : r;
        if (IsTypeAssignable(pc, truthy, r)) return truthy;
        return pc.Types.UnionOf([GetType(pc, truthy), GetType(pc, r)]);
    }

    private bool CanBeFalsy(PassContext pc, TypID id)
    {
        return MembersOf(pc, id).Any(member => MayBeFalse(member) || MayBeNil(member));
    }

    private bool CanBeFalse(PassContext pc, TypID id)
    {
        return MembersOf(pc, id).Any(MayBeFalse);
    }

    private IEnumerable<Type> MembersOf(PassContext pc, TypID id)
    {
        if (!pc.Pkg!.Types.GetByID(id, out var type)) return [pc.Types.PrimAny];
        return type is UnionType union ? union.Types : [type];
    }

    private static bool MayBeFalse(Type type)
    {
        return type.Kind is TypeKind.PrimitiveAny or TypeKind.PrimitiveBool or TypeKind.Predicate
            or TypeKind.TypeParameter or TypeKind.Tuple or TypeKind.Variadic;
    }

    private static bool MayBeNil(Type type)
    {
        return type.Kind is TypeKind.PrimitiveNil or TypeKind.PrimitiveAny
            or TypeKind.TypeParameter or TypeKind.Tuple or TypeKind.Variadic;
    }

    private TypID InferUnary(PassContext pc, UnaryExpr un)
    {
        var tt = pc.Types;
        var t = FirstValue(pc, SynthesizeExpr(pc, un.Operand));
        if (TryExtensionOperator(pc, un, t, out var extensionResult)) return extensionResult;

        var metaName = UnaryOpToMetamethod(un.Op);
        if (metaName != null)
        {
            var metaReturn = TryGetMetamethodReturn(pc, t, metaName);
            if (metaReturn != null) return metaReturn.Value;
        }

        switch (un.Op)
        {
            case UnaryOp.Negate:
                EnsureAssignable(pc, un.Operand.Span, tt.PrimNumber.ID, t);
                return tt.PrimNumber.ID;
            case UnaryOp.LogicalNot:
                return tt.PrimBool.ID;
            case UnaryOp.Length:
                if (!IsStringLike(pc, t) && !IsTableLike(pc, t) && t != tt.PrimAny.ID)
                {
                    pc.Diag.Report(un.Operand.Span, DiagnosticCode.ErrTypeMismatch, "string or table", TypeName(pc, t));
                }

                return tt.PrimNumber.ID;
            case UnaryOp.BitwiseNot:
                EnsureAssignable(pc, un.Operand.Span, tt.PrimNumber.ID, t);
                return tt.PrimNumber.ID;
            default:
                return tt.PrimAny.ID;
        }
    }

    private TypID InferIncDec(PassContext pc, IncDecExpr incDec)
    {
        var tt = pc.Types;
        var t = SynthesizeExpr(pc, incDec.Target);

        if (incDec.Target is not (NameExpr or DotAccessExpr or IndexAccessExpr))
        {
            pc.Diag.Report(incDec.Target.Span, DiagnosticCode.ErrInvalidAssignTarget);
            return tt.PrimNumber.ID;
        }

        EnsureAssignable(pc, incDec.Target.Span, tt.PrimNumber.ID, t);
        return tt.PrimNumber.ID;
    }

    private TypID InferMatchExpr(PassContext pc, MatchExpr me)
    {
        var tt = pc.Types;
        var scrutType = SynthesizeExpr(pc, me.Scrutinee);

        TypID? unified = null;
        foreach (var arm in me.Arms)
        {
            if (arm.Pattern.ValueExpr != null)
            {
                var patType = EnumMemberFor(pc, arm.Pattern.ValueExpr, scrutType) ?? SynthesizeExpr(pc, arm.Pattern.ValueExpr);
                CheckMatchPatternType(pc, scrutType, patType, arm.Pattern.ValueExpr.Span);
            }
            if (arm.Pattern.Kind == MatchPatternKind.TypeBinding && arm.Pattern.TypeRef != null)
                CheckMatchPatternTypeBinding(pc, scrutType, arm.Pattern.TypeRef, arm.Pattern.Span);
            if (arm.Guard != null) SynthesizeExpr(pc, arm.Guard);
            var armType = SynthesizeExpr(pc, arm.Value);
            if (unified == null)
                unified = armType;
            else if (unified.Value != armType)
            {
                if (unified.Value == tt.PrimAny.ID || armType == tt.PrimAny.ID)
                    unified = tt.PrimAny.ID;
                else
                    unified = pc.Types.UnionOf([GetType(pc, unified.Value), GetType(pc, armType)]);
            }
        }

        CheckExhaustivePatterns(pc, scrutType, me.Span, me.Arms.Select(a => a.Pattern).ToList());

        return unified ?? tt.PrimAny.ID;
    }

    private TypID InferAwaitExpr(PassContext pc, AwaitExpr awaitExpr)
    {
        if (_tryDepth > 0 && !Codegen.LuaFeatureSet.For(pc.Config.Target).CanYieldAcrossPcall)
            pc.Diag.Report(awaitExpr.Span, DiagnosticCode.ErrAwaitInTryOnLua51, "5.1");
        if (_asyncDepth <= 0)
        {
            pc.Diag.Report(awaitExpr.Span, DiagnosticCode.ErrAwaitOutsideAsync);
        }

        var inner = awaitExpr.Expression;
        if (inner is not FunctionCallExpr call && inner is not MethodCallExpr)
        {
            SynthesizeExpr(pc, inner);
            pc.Diag.Report(awaitExpr.Span, DiagnosticCode.ErrAwaitNonCallable);
            return pc.Types.PrimAny.ID;
        }

        FunctionType? fnType = null;
        List<TypID> argTypes;
        var untypedTarget = false;

        if (inner is FunctionCallExpr fc)
        {
            var calleeTyp = SynthesizeExpr(pc, fc.Callee);
            argTypes = fc.Arguments.Select(a => SynthesizeExpr(pc, a)).ToList();
            calleeTyp = StripNil(pc, calleeTyp);
            if (pc.Pkg!.Types.GetByID(calleeTyp, out var t))
            {
                if (t is FunctionType ft) fnType = ft;
                else untypedTarget = t.Kind == TypeKind.PrimitiveAny;
            }
        }
        else
        {
            var mc = (MethodCallExpr)inner;
            var recvTyp = StripNil(pc, SynthesizeExpr(pc, mc.Object));
            argTypes = mc.Arguments.Select(a => SynthesizeExpr(pc, a)).ToList();
            InferMethodCall(pc, mc, continuationSupplied: true);
            if (pc.Pkg!.Types.GetByID(recvTyp, out var recvType))
            {
                fnType = ResolveMethodOnType(pc, recvType, mc.MethodName.Name)
                         ?? ResolveExtensionMethod(pc, recvType, mc.MethodName.Name).Item1;
                untypedTarget = recvType.Kind == TypeKind.PrimitiveAny;
            }
        }

        if (fnType == null)
        {
            if (untypedTarget) return pc.Types.PrimAny.ID;
            pc.Diag.Report(awaitExpr.Span, DiagnosticCode.ErrAwaitNonCallable);
            return pc.Types.PrimAny.ID;
        }

        if (fnType.IsAsync)
            return fnType.ReturnType.ID;

        var lastIdx = fnType.ParamTypes.Count - 1;
        if (lastIdx >= 0 && pc.Pkg!.Types.GetByID(StripNil(pc, fnType.ParamTypes[lastIdx].ID), out var lastParam)
            && lastParam is FunctionType cbType)
        {
            var takesSelf = inner is MethodCallExpr && fnType.ParamNames.Count > 0 && fnType.ParamNames[0] == "self";
            awaitExpr.CallbackPosition = takesSelf ? lastIdx - 1 : lastIdx;
            if (cbType.ParamTypes.Count == 0)
                return pc.Types.PrimNil.ID;
            if (cbType.ParamTypes.Count == 1)
                return cbType.ParamTypes[0].ID;
            return pc.Types.PrimAny.ID;
        }

        pc.Diag.Report(awaitExpr.Span, DiagnosticCode.ErrAwaitNonAsync, DescribeCallee(inner));
        return pc.Types.PrimAny.ID;
    }

    /// <summary>
    /// Renders a readable name for the thing being <c>await</c>ed so diagnostics can
    /// point at the actual call (e.g. <c>http.get</c>) instead of the generic
    /// placeholder "function". Falls back to "function" only when the callee is an
    /// anonymous/computed expression with no obvious name.
    /// </summary>
    private static string DescribeCallee(Expr expr)
    {
        switch (expr)
        {
            case FunctionCallExpr fc:
                return DescribeCallee(fc.Callee);
            case MethodCallExpr mc:
                return $"{DescribeCallee(mc.Object)}:{mc.MethodName.Name}";
            case NameExpr ne:
                return ne.Name.Name;
            case DotAccessExpr da:
                return $"{DescribeCallee(da.Object)}.{da.FieldName.Name}";
            case IndexAccessExpr ia:
                return $"{DescribeCallee(ia.Object)}[...]";
            case ParenExpr pe:
                return DescribeCallee(pe.Inner);
            default:
                return "function";
        }
    }

    private TypID InferNewExpr(PassContext pc, NewExpr newExpr)
    {
        var classTypId = LookupSymbolType(pc, newExpr.ClassName.Sym);
        var argTypes = new List<TypID>();
        foreach (var arg in newExpr.Arguments) argTypes.Add(SynthesizeArgument(pc, arg));

        if (classTypId == TypID.Invalid || !pc.Types.GetByID(classTypId, out var rawType) || rawType is not ClassType classType)
        {
            if (classTypId != TypID.Invalid)
                pc.Diag.Report(newExpr.Span, DiagnosticCode.ErrNewNonClass, newExpr.ClassName.Name);
            return pc.Types.PrimAny.ID;
        }

        if (classType.IsAbstract)
        {
            pc.Diag.Report(newExpr.Span, DiagnosticCode.ErrInstantiateAbstract, newExpr.ClassName.Name);
            return classTypId;
        }

        var constructors = ResolveConstructorOverloads(classType);
        if (constructors == null)
        {
            if (newExpr.Arguments.Count > 0)
                pc.Diag.Report(newExpr.Span, DiagnosticCode.ErrNoConstructor, newExpr.ClassName.Name);
            return InferredInstantiation(pc, classType, null, argTypes).ID;
        }

        var (owner, overloads) = constructors.Value;
        var ctorType = PickConstructor(pc, newExpr.Span, newExpr.ClassName.Name, owner, overloads, newExpr.Arguments, argTypes, out var overloadName);
        if (ctorType == null) return InferredInstantiation(pc, classType, null, argTypes).ID;

        if (owner.Definition == classType.Definition) newExpr.OverloadName = overloadName;
        if (owner.LocalConstructors.Contains(ctorType) && _currentClass?.Definition != owner.Definition)
            pc.Diag.Report(newExpr.Span, DiagnosticCode.ErrLocalConstructor, owner.Name);

        CheckConstructorArguments(pc, newExpr.Span, newExpr.ClassName.Name, ctorType, newExpr.Arguments, argTypes);
        return InferredInstantiation(pc, classType, ctorType, argTypes).ID;
    }

    /// <summary>
    /// The type <c>new</c> creates: for a generic class, the instantiation its constructor's
    /// arguments tell, with <c>any</c> for a type parameter they leave open.
    /// </summary>
    private ClassType InferredInstantiation(PassContext pc, ClassType classType, FunctionType? constructor, List<TypID> argTypes)
    {
        if (classType.TypeParams.Count == 0) return classType;

        var bindings = new Dictionary<TypID, Type>();
        for (var i = 0; constructor != null && i < argTypes.Count; i++)
        {
            var parameter = i < constructor.ParamTypes.Count ? constructor.ParamTypes[i] : constructor.IsVararg ? constructor.VarargType : null;
            if (parameter != null && pc.Pkg!.Types.GetByID(argTypes[i], out var argument))
                Bind(pc, parameter, argument, bindings);
        }

        var arguments = classType.TypeParams.Select(typeParam => bindings.GetValueOrDefault(typeParam.ID) ?? pc.Types.PrimAny).ToList();
        return pc.Types.Instantiate(classType, arguments);
    }

    private TypID InferFunctionDef(PassContext pc, FunctionDefExpr fde)
    {
        if (fde.IsAsync) _asyncDepth++;
        var paramTypes = new List<Tuple<string, Type>>();
        var fdeIsVararg = false;
        Type? fdeVarargType = null;
        var fdeDefaults = new List<int>();

        for (var i = 0; i < fde.Parameters.Count; i++)
        {
            var param = fde.Parameters[i];
            var t = ContextualParamType(fde, param, i) ?? ResolveParamType(pc, param);
            if (param.IsVararg)
            {
                fdeIsVararg = true;
                fdeVarargType = t.Kind == TypeKind.PrimitiveAny ? null : t;
                if (param.Name.Sym != SymID.Invalid)
                {
                    var arrTyp = fdeVarargType != null
                        ? pc.Pkg!.Types.ArrayOf(fdeVarargType)
                        : pc.Pkg!.Types.ArrayOf(pc.Types.PrimAny);
                    pc.Pkg!.Syms.SetType(param.Name.Sym, arrTyp);
                }
            }
            else
            {
                paramTypes.Add(new Tuple<string, Type>(param.Name.Name, t));
                if (param.Name.Sym != SymID.Invalid)
                {
                    pc.Pkg!.Syms.SetType(param.Name.Sym, t.ID);
                }
                if (param.DefaultValue != null)
                {
                    var dvt = SynthesizeExpr(pc, param.DefaultValue);
                    EnsureAssignable(pc, param.DefaultValue.Span, t.ID, dvt);
                    fdeDefaults.Add(i);
                }
            }
        }

        var declaredReturn = fde.ReturnType?.ResolvedType ?? (_expectedSignatures.TryGetValue(fde, out var expectedSignature) ? expectedSignature.ReturnType.ID : TypID.Invalid);
        ResolveFunctionBody(pc, fde.Body, fde.ReturnStmt, declaredReturn);

        Type returnType;
        if (fde.ReturnType != null && fde.ReturnType.ResolvedType != TypID.Invalid)
        {
            returnType = GetType(pc, fde.ReturnType.ResolvedType);
        }
        else
        {
            var collected = CollectReturnTypes(pc, fde.Body);
            if (fde.ReturnStmt != null)
            {
                collected.Add((ComputeReturnType(pc, fde.ReturnStmt.Values), fde.ReturnStmt.Span));
            }

            returnType = InferReturnType(pc, collected);
        }

        if (fde.IsAsync) _asyncDepth--;
        return pc.Types.FuncOf(paramTypes, returnType, fdeIsVararg, fdeVarargType,
            fdeDefaults.Count > 0 ? fdeDefaults : null, fde.IsAsync);
    }

    /// <summary>
    /// The type whose members a value of <paramref name="type"/> has: for a bounded type parameter
    /// its bound, the class it extends or else the first interface it implements, since every type
    /// it stands for has those members.
    /// </summary>
    private TypID MemberTypeOf(PassContext pc, TypID type)
    {
        type = ApparentType(pc, type);
        if (pc.Pkg!.Types.GetByID(type, out var shown) && LiteralBase(shown) is { } literalBase) return literalBase.ID;
        if (!pc.Pkg!.Types.GetByID(type, out var resolved) || resolved is not TypeParameterType typeParameter) return type;
        return typeParameter.ExtendsBound ?? (typeParameter.ImplementsBounds.Count > 0 ? typeParameter.ImplementsBounds[0] : type);
    }

    private TypID InferDotAccess(PassContext pc, DotAccessExpr dot)
    {
        var baseTyp = SynthesizeExpr(pc, dot.Object);

        if (!dot.IsOptional)
        {
            var path = GetAccessPath(dot);
            if (path != null && _narrowed.TryGetValue(path, out var narrowed))
            {
                return narrowed;
            }
        }

        if (dot.IsOptional)
        {
            baseTyp = StripNil(pc, baseTyp);
        }
        else
        {
            EnsureNotNil(pc, dot.Object.Span, baseTyp);
            if (IsNullable(pc, baseTyp))
            {
                baseTyp = StripNil(pc, baseTyp);
            }
        }
        baseTyp = MemberTypeOf(pc, baseTyp);

        if (!pc.Pkg!.Types.GetByID(baseTyp, out var baseType))
        {
            return dot.IsOptional ? MakeNullable(pc, pc.Types.PrimAny.ID) : pc.Types.PrimAny.ID;
        }

        if (TryExtensionGetter(pc, dot, baseType, out var getterType))
        {
            return dot.IsOptional ? MakeNullable(pc, getterType) : getterType;
        }

        var resultType = pc.Types.PrimAny.ID;
        switch (baseType)
        {
            case StructType st:
            {
                var field = st.Fields.FirstOrDefault(f => f.Name.Name == dot.FieldName.Name);
                if (field == null)
                {
                    pc.Diag.Report(dot.FieldName.Span, DiagnosticCode.ErrNoSuchKey, TypeName(pc, baseTyp), $"'{dot.FieldName.Name}'",
                        string.Join(", ", st.Fields.Where(candidate => !candidate.IsMeta).Select(candidate => $"'{candidate.Name.Name}'")));
                    return pc.Types.PrimAny.ID;
                }

                resultType = field.Type.ID;
                break;
            }
            case TableMapType mt:
                resultType = mt.ValueType.ID;
                break;
            case EnumType et:
            {
                var member = et.Members.FirstOrDefault(m => m.Name == dot.FieldName.Name);
                if (member == null)
                {
                    pc.Diag.Report(dot.FieldName.Span, DiagnosticCode.ErrNoSuchMember, et.Name, dot.FieldName.Name);
                    return pc.Types.PrimAny.ID;
                }

                resultType = baseTyp;
                break;
            }
            case InterfaceType ift:
            {
                var fname = dot.FieldName.Name;
                if (ift.Fields.TryGetValue(fname, out var ifield))
                {
                    resultType = ifield.Type.ID;
                }
                else if (ift.Methods.TryGetValue(fname, out var imethod))
                {
                    resultType = imethod.ID;
                }
                else
                {
                    var found = false;
                    var visited = new HashSet<InterfaceType>();
                    var queue = new Queue<InterfaceType>(ift.BaseInterfaces);
                    while (!found && queue.TryDequeue(out var bi))
                    {
                        if (!visited.Add(bi)) continue;
                        if (bi.Fields.TryGetValue(fname, out var bf)) { resultType = bf.Type.ID; found = true; break; }
                        if (bi.Methods.TryGetValue(fname, out var bm)) { resultType = bm.ID; found = true; break; }
                        foreach (var nbi in bi.BaseInterfaces) queue.Enqueue(nbi);
                    }
                    if (!found)
                    {
                        pc.Diag.Report(dot.FieldName.Span, DiagnosticCode.ErrNoSuchMember, ift.Name, fname);
                        return pc.Types.PrimAny.ID;
                    }
                }
                break;
            }
            case ClassType ct:
            {
                var fname = dot.FieldName.Name;
                var isClassRef = dot.Object is NameExpr cre
                    && pc.Pkg!.Syms.GetByID(cre.Name.Sym, out var cSym)
                    && cSym.Kind == SymbolKind.Class;
                if (isClassRef)
                {
                    for (var cur = ct; cur != null; cur = cur.BaseClass)
                    {
                        if (cur.StaticMethods.TryGetValue(fname, out var sm))
                        {
                            resultType = sm.ID;
                            goto classDotDone;
                        }
                    }
                }
                if (ct.InstanceFields.TryGetValue(fname, out var field))
                {
                    CheckProtectedAccess(pc, dot.FieldName.Span, ct, fname);
                    resultType = field.Type.ID;
                }
                else if (ct.Methods.TryGetValue(fname, out var method))
                {
                    CheckProtectedAccess(pc, dot.FieldName.Span, ct, fname);
                    resultType = method.ID;
                }
                else if (ct.Getters.TryGetValue(fname, out var getter))
                {
                    resultType = getter.ReturnType.ID;
                }
                else if (ct.StaticMethods.TryGetValue(fname, out var staticMethod))
                {
                    resultType = staticMethod.ID;
                }
                else
                {
                    var found = false;
                    var cur = ct.BaseClass;
                    while (cur != null && !found)
                    {
                        if (cur.InstanceFields.TryGetValue(fname, out var pf)) { CheckProtectedAccess(pc, dot.FieldName.Span, ct, fname); resultType = pf.Type.ID; found = true; }
                        else if (cur.Methods.TryGetValue(fname, out var pm)) { CheckProtectedAccess(pc, dot.FieldName.Span, ct, fname); resultType = pm.ID; found = true; }
                        else if (cur.Getters.TryGetValue(fname, out var pg)) { resultType = pg.ReturnType.ID; found = true; }
                        else if (cur.StaticMethods.TryGetValue(fname, out var psm)) { resultType = psm.ID; found = true; }
                        cur = cur.BaseClass;
                    }
                    if (!found) resultType = ResolveUndeclaredMember(pc, ct, dot);
                }
                classDotDone:
                break;
            }
            case { Kind: TypeKind.PrimitiveAny }:
                resultType = pc.Types.PrimAny.ID;
                break;
            default:
                resultType = pc.Types.PrimAny.ID;
                break;
        }

        return dot.IsOptional ? MakeNullable(pc, resultType) : resultType;
    }

    /// <summary>
    /// Reports an access to a protected member from outside the hierarchy that declares it. The
    /// member may be declared anywhere up the chain, and the access is legal from the declaring
    /// class and from any class below it.
    /// </summary>
    private void CheckProtectedAccess(PassContext pc, TextSpan span, ClassType classType, string memberName)
    {
        var privateOwner = FindPrivateOwner(classType, memberName);
        if (privateOwner != null)
        {
            if (_currentClass?.Definition != privateOwner.Definition)
                pc.Diag.Report(span, DiagnosticCode.ErrPrivateAccess, memberName, privateOwner.Name);
            return;
        }

        var owner = FindProtectedOwner(classType, memberName);
        if (owner == null) return;
        if (_currentClass != null && DerivesFrom(_currentClass, owner)) return;

        pc.Diag.Report(span, DiagnosticCode.ErrProtectedAccess, memberName, owner.Name);
    }

    /// <summary>
    /// Finds the class in <paramref name="classType"/>'s chain that declares
    /// <paramref name="memberName"/> private, or <c>null</c> when none does.
    /// </summary>
    private static ClassType? FindPrivateOwner(ClassType classType, string memberName)
    {
        for (var cur = classType; cur != null; cur = cur.BaseClass)
        {
            if (cur.PrivateMembers.Contains(memberName)) return cur;
        }

        return null;
    }

    /// <summary>
    /// Finds the class in <paramref name="classType"/>'s chain that declares
    /// <paramref name="memberName"/> protected, or <c>null</c> when none does.
    /// </summary>
    private static ClassType? FindProtectedOwner(ClassType classType, string memberName)
    {
        for (var cur = classType; cur != null; cur = cur.BaseClass)
        {
            if (cur.ProtectedMembers.Contains(memberName)) return cur;
        }

        return null;
    }

    private static bool DerivesFrom(ClassType candidate, ClassType target)
    {
        for (var cur = candidate; cur != null; cur = cur.BaseClass)
        {
            if (cur.Definition == target.Definition) return true;
        }

        return false;
    }

    private TypID InferIndexAccess(PassContext pc, IndexAccessExpr idx)
    {
        var baseTyp = SynthesizeExpr(pc, idx.Object);
        var indexTyp = SynthesizeExpr(pc, idx.Index);
        EnsureNotNil(pc, idx.Object.Span, baseTyp);
        if (IsNullable(pc, baseTyp))
        {
            baseTyp = StripNil(pc, baseTyp);
        }

        if (!pc.Pkg!.Types.GetByID(baseTyp, out var baseType))
        {
            return pc.Types.PrimAny.ID;
        }

        if (TryIndexOperator(pc, idx, baseType, indexTyp, out var operatorResult))
        {
            return operatorResult;
        }

        if (FieldByKey(pc, idx, baseType, indexTyp) is { } fieldType)
        {
            return fieldType;
        }

        switch (baseType)
        {
            case TableArrayType at:
                if (!IsTypeAssignable(pc, pc.Types.PrimNumber.ID, indexTyp) && indexTyp != pc.Types.PrimAny.ID)
                {
                    pc.Diag.Report(idx.Index.Span, DiagnosticCode.ErrTypeMismatch, "number", TypeName(pc, indexTyp));
                }

                return at.ElementType.ID;
            case TableMapType mt:
                if (!IsTypeAssignable(pc, mt.KeyType.ID, indexTyp) && indexTyp != pc.Types.PrimAny.ID)
                {
                    pc.Diag.Report(idx.Index.Span, DiagnosticCode.ErrTypeMismatch,
                        TypeName(pc, mt.KeyType.ID), TypeName(pc, indexTyp));
                }

                return mt.ValueType.ID;
            case not null when baseType.Kind == TypeKind.PrimitiveAny:
                return pc.Types.PrimAny.ID;
            case not null when baseType.Kind == TypeKind.PrimitiveString:
                return pc.Types.PrimString.ID;
            default:
                pc.Diag.Report(idx.Object.Span, DiagnosticCode.ErrTypeNotIndexable, TypeName(pc, baseTyp));
                return pc.Types.PrimAny.ID;
        }
    }

    /// <summary>
    /// Reports a call written with '.' to a member that needs its receiver passed with ':'. A
    /// receiver typed as a union, such as a nilable value or <c>a ?? b</c>, needs ':' as soon as
    /// one of its members does.
    /// </summary>
    private bool ReportsMissingColon(PassContext pc, DotAccessExpr callee)
    {
        if (!pc.Pkg!.Types.GetByID(SynthesizeExpr(pc, callee.Object), out var receiverType)) return false;

        var isClassReference = callee.Object is NameExpr className
            && pc.Pkg.Syms.GetByID(className.Name.Sym, out var classSymbol)
            && classSymbol.Kind == SymbolKind.Class;
        var name = callee.FieldName.Name;
        var receiverName = callee.Object is NameExpr receiverExpr ? receiverExpr.Name.Name : "obj";
        var candidates = receiverType is UnionType union ? union.Types : [receiverType];

        foreach (var candidate in candidates)
        {
            if (candidate is ClassType receiverClass && !isClassReference && IsInstanceMethodOf(receiverClass, name))
            {
                pc.Diag.Report(callee.Span, DiagnosticCode.ErrInstanceMethodNeedsColon, name, receiverClass.Name, receiverName);
                return true;
            }

            if (candidate is InterfaceType receiverInterface && DeclaresInstanceMethod(receiverInterface, name))
            {
                pc.Diag.Report(callee.Span, DiagnosticCode.ErrInterfaceMethodNeedsColon, name, receiverInterface.Name, receiverName);
                return true;
            }
        }

        return false;
    }

    private static bool IsInstanceMethodOf(ClassType classType, string name)
    {
        return classType.Methods.ContainsKey(name)
            && !classType.StaticMethods.ContainsKey(name)
            && !name.StartsWith("__");
    }

    private TypID InferFunctionCall(PassContext pc, FunctionCallExpr call)
    {
        if (call.Callee is DotAccessExpr dotCallee && ReportsMissingColon(pc, dotCallee))
        {
            foreach (var arg in call.Arguments) SynthesizeExpr(pc, arg);
            return call.IsOptional ? MakeNullable(pc, pc.Types.PrimAny.ID) : pc.Types.PrimAny.ID;
        }

        var argTypes = new List<TypID>();
        foreach (var arg in call.Arguments)
        {
            argTypes.Add(SynthesizeArgument(pc, arg));
        }

        // Interface/class method-overload dispatch on dot-access callees.
        // `Events.CallRemote(...)` with `@side(client) CallRemote(name, ...)`
        // and `@side(server) CallRemote(name, player, ...)` must dispatch to
        // the side-appropriate overload at the call site instead of always
        // picking whichever was inserted last in the method table.
        if (call.Callee is DotAccessExpr dotForOverload
            && pc.Pkg!.Types.GetByID(SynthesizeExpr(pc, dotForOverload.Object), out var receiverTyp))
        {
            var fname = dotForOverload.FieldName.Name;
            var isClassRef = dotForOverload.Object is NameExpr cre
                && pc.Pkg!.Syms.GetByID(cre.Name.Sym, out var cSym)
                && cSym.Kind == SymbolKind.Class;
            var (ovFns, ovSides) = CollectMethodOverloads(pc, receiverTyp, fname, staticOnly: isClassRef);
            if (ovFns.Count > 1)
            {
                // Dot-access calls don't prepend `self`; ScoreOverload should
                // compare argTypes against the function's non-self params.
                // For class instance methods we'd normally need self, but a
                // dot-access call on an INSTANCE is rejected earlier; only
                // class-ref-static or interface methods reach here.
                var picked = PickOverload(pc, ovFns, ovSides, WithLiteralTypes(pc, call.Arguments, argTypes), prefixSelf: false, out var certain);
                if (picked != null)
                {
                    if (certain && isClassRef && receiverTyp is ClassType staticOwner)
                        dotForOverload.OverloadName = StaticOverloadName(staticOwner, fname, picked);
                    AdoptArguments(pc, picked, call.Arguments, argTypes, 0);
                    CheckCallArguments(pc, call.Span, picked, argTypes);
                    var ret = SpecializedReturn(pc, picked, argTypes);
                    return call.IsOptional ? MakeNullable(pc, ret) : ret;
                }

                ReportNoMatchingOverload(pc, call.Span, fname, ovFns, argTypes, skipSelf: false);
                return pc.Types.PrimAny.ID;
            }
        }

        if (call.Callee is NameExpr ne && ne.Name.Overloads is { Count: > 1 } overloads)
        {
            var resolved = ResolveOverload(pc, call.Span, overloads, WithLiteralTypes(pc, call.Arguments, argTypes));
            if (resolved != null)
            {
                if (!IsImported(pc, ne.Name.Sym)) ne.Name.Sym = resolved.Value.sym;
                ne.CallsResolvedOverload = resolved.Value.certain;
                AdoptArguments(pc, resolved.Value.ft, call.Arguments, argTypes, 0);
                CheckCallArguments(pc, call.Span, resolved.Value.ft, argTypes);
                var ret = SpecializedReturn(pc, resolved.Value.ft, argTypes);
                return call.IsOptional ? MakeNullable(pc, ret) : ret;
            }

            return call.IsOptional ? MakeNullable(pc, pc.Types.PrimAny.ID) : pc.Types.PrimAny.ID;
        }

        var calleeTyp = SynthesizeExpr(pc, call.Callee);

        if (call.IsOptional)
        {
            calleeTyp = StripNil(pc, calleeTyp);
        }
        else
        {
            EnsureNotNil(pc, call.Callee.Span, calleeTyp);
            if (IsNullable(pc, calleeTyp))
            {
                calleeTyp = StripNil(pc, calleeTyp);
            }
        }

        calleeTyp = ApparentType(pc, calleeTyp);
        if (!pc.Pkg!.Types.GetByID(calleeTyp, out var calleeType))
        {
            return call.IsOptional ? MakeNullable(pc, pc.Types.PrimAny.ID) : pc.Types.PrimAny.ID;
        }

        if (calleeType is not FunctionType fnType)
        {
            // `any` and the `function` category are callable with an unknown signature → any result.
            if (calleeType.Kind is TypeKind.PrimitiveAny or TypeKind.PrimitiveFunction)
            {
                return call.IsOptional ? MakeNullable(pc, pc.Types.PrimAny.ID) : pc.Types.PrimAny.ID;
            }

            if (TryCallOperator(pc, call, calleeType, argTypes, out var callResult))
            {
                return call.IsOptional ? MakeNullable(pc, callResult) : callResult;
            }

            pc.Diag.Report(call.Callee.Span, DiagnosticCode.ErrTypeMismatch, "function", TypeName(pc, calleeTyp));
            return pc.Types.PrimAny.ID;
        }

        AdoptArguments(pc, fnType, call.Arguments, argTypes, 0);
        CheckCallArguments(pc, call.Span, fnType, argTypes);
        var resultType = SpecializedReturn(pc, fnType, argTypes);
        return call.IsOptional ? MakeNullable(pc, resultType) : resultType;
    }

    private (SymID sym, FunctionType ft, bool certain)? ResolveOverload(PassContext pc, TextSpan span, List<SymID> overloads, List<TypID> argTypes)
    {
        FunctionType? bestFn = null;
        SymID bestSym = SymID.Invalid;
        var bestScore = -1;
        var scores = new List<(FunctionType Function, int Score)>();

        foreach (var symId in overloads)
        {
            if (!pc.Pkg!.Syms.GetByID(symId, out var sym)) continue;
            if (!pc.Types.GetByID(sym.Type, out var typ) || typ is not FunctionType ft) continue;

            var score = ScoreOverload(pc, ft, argTypes);
            scores.Add((ft, score));
            if (score > bestScore)
            {
                bestScore = score;
                bestFn = ft;
                bestSym = symId;
            }
        }

        if (bestFn != null && bestScore >= 0)
        {
            return (bestSym, bestFn, IsCertainOverload(pc, bestFn, bestScore, scores, argTypes, false));
        }

        var functions = overloads
            .Select(symbol => pc.Pkg!.Syms.GetByID(symbol, out var overload) && pc.Types.GetByID(overload.Type, out var type) ? type as FunctionType : null)
            .OfType<FunctionType>()
            .ToList();
        var name = pc.Pkg!.Syms.GetByID(overloads[0], out var first) ? first.Name : "function";
        ReportNoMatchingOverload(pc, span, name, functions, argTypes, skipSelf: false);
        return null;
    }

    /// <summary>
    /// Reports a call no overload of <paramref name="name"/> takes, listing the parameter types of
    /// every overload, without the <c>self</c> of a method.
    /// </summary>
    private void ReportNoMatchingOverload(PassContext pc, TextSpan span, string name, List<FunctionType> overloads, List<TypID> argTypes, bool skipSelf)
    {
        var signatures = overloads
            .Select(overload => "(" + string.Join(", ", overload.ParamTypes.Skip(skipSelf ? 1 : 0).Select(type => TypeName(pc, type.ID))) + ")")
            .Distinct();
        pc.Diag.Report(span, DiagnosticCode.ErrNoMatchingOverload, name,
            string.Join(", ", argTypes.Select(type => TypeName(pc, type))), string.Join(", ", signatures));
    }

    /// <summary>
    /// Whether <paramref name="symbol"/> is bound by an import, whose overloads are all reached
    /// through the one function the module exports under that name.
    /// </summary>
    private static bool IsImported(PassContext pc, SymID symbol) =>
        pc.Pkg!.Syms.GetByID(symbol, out var bound) && bound.ImportedFrom != SymID.Invalid;

    /// <summary>
    /// Whether a call can be bound to <paramref name="picked"/> at compile time: no overload of
    /// other parameter types scores as well, and no argument is typed <c>any</c>, whose value only
    /// the runtime dispatcher can match to an overload.
    /// </summary>
    private bool IsCertainOverload(PassContext pc, FunctionType picked, int pickedScore,
        List<(FunctionType Function, int Score)> scores, List<TypID> argTypes, bool hasSelf)
    {
        var suffix = OverloadSignature.Suffix(picked, hasSelf);
        if (scores.Any(entry => entry.Score == pickedScore && OverloadSignature.Suffix(entry.Function, hasSelf) != suffix))
            return false;

        return argTypes
            .All(argument => pc.Types.GetByID(argument, out var type) && type.Kind != TypeKind.PrimitiveAny);
    }

    private int ScoreOverload(PassContext pc, FunctionType ft, List<TypID> argTypes)
    {
        _openTypeParamDepth++;
        try
        {
            return ScoreOverloadAgainst(pc, ft, argTypes);
        }
        finally
        {
            _openTypeParamDepth--;
        }
    }

    private int ScoreOverloadAgainst(PassContext pc, FunctionType ft, List<TypID> argTypes)
    {
        var paramCount = ft.ParamTypes.Count;
        var argCount = argTypes.Count;
        var minParams = ft.MinParamCount;

        if (argCount < minParams) return -1;
        if (argCount > paramCount && !ft.IsVararg)
        {
            if (ft.IsAsync && argCount == paramCount + 1)
            {
            }
            else
            {
                var lastParam = paramCount > 0 ? ft.ParamTypes[paramCount - 1] : null;
                if (lastParam is not { Kind: TypeKind.PrimitiveAny })
                    return -1;
            }
        }

        var score = 0;
        for (var i = 0; i < paramCount && i < argCount; i++)
        {
            var paramType = ft.ParamTypes[i].ID;
            var argType = argTypes[i];
            if (paramType == argType)
                score += 3;
            else if (IsTypeAssignable(pc, paramType, argType))
                score += 1;
            else
                return -1;
        }

        if (argCount == paramCount)
            score += 1;

        return score;
    }

    /// <summary>
    /// Infers the type of a <c>receiver:method(...)</c> call. The <c>:</c> form passes the receiver
    /// as an implicit <c>self</c>, so the receiver is prepended to the argument list when it is a
    /// class (where <c>self</c> is synthetic) or when the signature declares a leading <c>self</c>
    /// parameter (e.g. <c>declare interface IoFile function flush(self: any): any</c>). A plain
    /// table whose field takes neither is the footgun this warns about: Lua still passes the
    /// receiver, so every declared parameter is shifted by one.
    /// </summary>
    /// <summary>
    /// For a call made through <c>await</c>, the arguments together with the continuation the
    /// async driver appends: a callback-last function is awaited without its callback, so when
    /// exactly that trailing function parameter is missing it counts as supplied.
    /// </summary>
    private static List<TypID> WithContinuation(FunctionType function, List<TypID> arguments, bool continuationSupplied)
    {
        if (!continuationSupplied || function.IsAsync || function.ParamTypes.Count != arguments.Count + 1)
            return arguments;

        return function.ParamTypes[^1] is FunctionType continuation ? [..arguments, continuation.ID] : arguments;
    }

    private TypID InferMethodCall(PassContext pc, MethodCallExpr mc, bool continuationSupplied = false)
    {
        var objTyp = SynthesizeExpr(pc, mc.Object);
        EnsureNotNil(pc, mc.Object.Span, objTyp);
        if (IsNullable(pc, objTyp))
        {
            objTyp = StripNil(pc, objTyp);
        }
        objTyp = MemberTypeOf(pc, objTyp);
        if (mc.IsSuper)
        {
            if (_currentClass?.BaseClass is not { } baseClass)
            {
                pc.Diag.Report(mc.Span, DiagnosticCode.ErrSuperMethodWithoutBase, mc.MethodName.Name);
                foreach (var arg in mc.Arguments) SynthesizeExpr(pc, arg);
                return pc.Types.PrimAny.ID;
            }
            objTyp = baseClass.ID;
        }

        var argTypes = new List<TypID>();
        foreach (var arg in mc.Arguments)
        {
            argTypes.Add(SynthesizeArgument(pc, arg));
        }

        if (!pc.Pkg!.Types.GetByID(objTyp, out var objType))
        {
            return pc.Types.PrimAny.ID;
        }

        // Overload dispatch for `receiver:method(...)`. The args here are the
        // explicit args at the call site — `self` is implicit, so for class
        // instance methods we have to account for it when scoring (the stored
        // FunctionType includes `self` as its first param).
        var (mcFns, mcSides) = CollectMethodOverloads(pc, objType, mc.MethodName.Name, staticOnly: false);
        var reportsNoOverload = mcFns.Count > 1;
        if (mcFns.Count > 1)
        {
            var ovPrefixSelf = objType is ClassType || (mcFns.Count > 0 && StartsWithSelfParam(mcFns[0]));
            var picked = PickOverload(pc, mcFns, mcSides, WithLiteralTypes(pc, mc.Arguments, argTypes), prefixSelf: ovPrefixSelf, out var certain);
            if (picked != null)
            {
                if (certain && objType is ClassType overloadReceiver)
                    mc.OverloadName = InstanceOverloadName(overloadReceiver, mc.MethodName.Name, picked);
                var pickedArgs = ovPrefixSelf
                    ? new List<TypID>(argTypes.Count + 1) { objTyp }
                    : new List<TypID>(argTypes.Count);
                pickedArgs.AddRange(argTypes);
                AdoptArguments(pc, picked, mc.Arguments, pickedArgs, ovPrefixSelf ? 1 : 0);
                CheckCallArguments(pc, mc.Span, picked, WithContinuation(picked, pickedArgs, continuationSupplied));
                return SpecializedReturn(pc, picked, pickedArgs);
            }

            if (reportsNoOverload && !continuationSupplied)
            {
                ReportNoMatchingOverload(pc, mc.Span, mc.MethodName.Name, mcFns, argTypes, ovPrefixSelf);
                return pc.Types.PrimAny.ID;
            }
        }

        var methodFn = ResolveMethodOnType(pc, objType, mc.MethodName.Name);
        if (methodFn == null)
        {
            // Fall back to an extension method: it lowers to a plain call `fn(receiver, args)`.
            var (extFn, extTarget) = ResolveExtensionMethod(pc, objType, mc.MethodName.Name);
            if (extFn != null)
            {
                mc.ExtensionTargetType = extTarget!.ID;
                mc.Extension = extTarget.ExtensionMethodNodes.GetValueOrDefault(mc.MethodName.Name);
                if (mc.Extension != null) UseExtension(pc, mc.Extension, mc.MethodName.Span, objType);
                var extArgs = new List<TypID>(argTypes.Count + 1) { objTyp };
                extArgs.AddRange(argTypes);
                AdoptArguments(pc, extFn, mc.Arguments, extArgs, 1);
                CheckCallArguments(pc, mc.Span, extFn, WithContinuation(extFn, extArgs, continuationSupplied));
                return SpecializedReturn(pc, extFn, extArgs);
            }

            if (objType.Kind != TypeKind.PrimitiveAny)
            {
                pc.Diag.Report(mc.MethodName.Span, DiagnosticCode.ErrNoSuchMethod,
                    TypeName(pc, objTyp), mc.MethodName.Name);
            }
            return pc.Types.PrimAny.ID;
        }

        if (objType is ClassType receiverClass)
        {
            CheckProtectedAccess(pc, mc.MethodName.Span, receiverClass, mc.MethodName.Name);
        }

        var prefixSelf = objType is ClassType || StartsWithSelfParam(methodFn);
        if (!prefixSelf && objType is StructType)
        {
            pc.Diag.Report(mc.MethodName.Span, DiagnosticCode.WarnColonCallWithoutSelf, mc.MethodName.Name);
        }

        var fullArgs = prefixSelf
            ? new List<TypID>(argTypes.Count + 1) { objTyp }
            : new List<TypID>(argTypes.Count);
        fullArgs.AddRange(argTypes);
        AdoptArguments(pc, methodFn, mc.Arguments, fullArgs, prefixSelf ? 1 : 0);
        CheckCallArguments(pc, mc.Span, methodFn, WithContinuation(methodFn, fullArgs, continuationSupplied));

        return SpecializedReturn(pc, methodFn, fullArgs);
    }

    private static bool StartsWithSelfParam(FunctionType ft)
    {
        return ft.ParamNames.Count > 0 && ft.ParamNames[0] == "self";
    }

    private FunctionType? ResolveMethodOnType(PassContext pc, Type baseType, string methodName)
    {
        if (baseType is ClassType ct)
        {
            var cur = ct;
            while (cur != null)
            {
                if (cur.Methods.TryGetValue(methodName, out var m)) return m;
                cur = cur.BaseClass;
            }
            return ResolveInterfaceMethodOnClass(pc, ct, methodName);
        }
        if (baseType is InterfaceType ift)
        {
            if (ift.Methods.TryGetValue(methodName, out var direct)) return direct;
            var visited = new HashSet<InterfaceType>();
            var queue = new Queue<InterfaceType>(ift.BaseInterfaces);
            while (queue.TryDequeue(out var bi))
            {
                if (!visited.Add(bi)) continue;
                if (bi.Methods.TryGetValue(methodName, out var inherited)) return inherited;
                foreach (var nbi in bi.BaseInterfaces) queue.Enqueue(nbi);
            }
            return null;
        }
        if (baseType is StructType st)
        {
            var field = st.Fields.FirstOrDefault(f => f.Name.Name == methodName);
            return field?.Type as FunctionType;
        }
        return null;
    }

    /// <summary>
    /// Looks <paramref name="methodName"/> up on the interfaces <paramref name="classType"/>
    /// implements, transitively and including the ones inherited from its base classes. A
    /// <c>declare class</c> is not required to restate the members it takes from an interface
    /// (the implementation is external), so for such a class the signature lives on the
    /// interface alone. The result carries the synthetic <c>self</c> parameter class instance
    /// methods are stored with, which makes it indistinguishable from a declared one.
    /// </summary>
    private FunctionType? ResolveInterfaceMethodOnClass(PassContext pc, ClassType classType, string methodName)
    {
        foreach (var iface in Type.ImplementedInterfaces(classType))
        {
            if (iface.Methods.TryGetValue(methodName, out var ft))
            {
                return WithSelfParam(pc, classType, ft);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a member read (<c>receiver.member</c>) on a class receiver against the
    /// interfaces it implements, covering the members a <c>declare class</c> inherits without
    /// restating them. Returns <c>any</c> when no implemented interface declares the name.
    /// </summary>
    /// <summary>
    /// Resolves a member a class and its bases do not declare themselves: one an implemented
    /// interface declares, a setter, or an extension method. Anything else is not part of the
    /// class, and reading or writing it is reported rather than let through as <c>any</c>.
    /// </summary>
    private TypID ResolveUndeclaredMember(PassContext pc, ClassType classType, DotAccessExpr dot)
    {
        var name = dot.FieldName.Name;
        var fromInterface = ResolveInterfaceMemberOnClass(pc, classType, name);
        if (fromInterface != pc.Types.PrimAny.ID) return fromInterface;

        for (var current = classType; current != null; current = current.BaseClass)
        {
            if (current.Setters.TryGetValue(name, out var setter))
            {
                if (!ReferenceEquals(dot, _assignmentTarget))
                    pc.Diag.Report(dot.FieldName.Span, DiagnosticCode.ErrWriteOnlyProperty, name);
                return setter.ParamTypes.Count > 1 ? setter.ParamTypes[^1].ID : pc.Types.PrimAny.ID;
            }
        }

        var (extension, _) = Type.ResolveExtension(classType, name, pc.Types.PrimFunction);
        if (extension != null) return extension.ID;

        pc.Diag.Report(dot.FieldName.Span, DiagnosticCode.ErrNoSuchField, classType.Name, name);
        return pc.Types.PrimAny.ID;
    }

    private TypID ResolveInterfaceMemberOnClass(PassContext pc, ClassType classType, string memberName)
    {
        foreach (var iface in Type.ImplementedInterfaces(classType))
        {
            if (iface.Fields.TryGetValue(memberName, out var field))
            {
                return field.Type.ID;
            }

            if (iface.Methods.TryGetValue(memberName, out var method))
            {
                return WithSelfParam(pc, classType, method).ID;
            }
        }

        return pc.Types.PrimAny.ID;
    }

    /// <summary>
    /// Rebuilds <paramref name="ft"/> with a leading <c>self</c> parameter typed as
    /// <paramref name="classType"/>, the shape class instance methods are stored in. Default
    /// parameter indices shift along with the inserted parameter.
    /// </summary>
    private FunctionType WithSelfParam(PassContext pc, ClassType classType, FunctionType ft)
    {
        var parameters = new List<Tuple<string, Type>> { new("self", classType) };
        for (var i = 0; i < ft.ParamTypes.Count; i++)
        {
            parameters.Add(new Tuple<string, Type>(ft.ParamNames[i], ft.ParamTypes[i]));
        }

        var defaults = ft.DefaultParams.Count > 0
            ? ft.DefaultParams.Select(index => index + 1).ToList()
            : null;

        return (FunctionType)GetType(pc, pc.Types.FuncOf(parameters, ft.ReturnType, ft.IsVararg,
            ft.VarargType, defaults, ft.IsAsync, ft.Predicate));
    }

    /// <summary>
    /// Gathers every declared overload of <paramref name="methodName"/> on
    /// <paramref name="baseType"/>, with each overload's <see cref="Side"/>
    /// annotation in parallel. Walks the class/interface inheritance chain so
    /// inherited overloads are part of the candidate set. Empty list means the
    /// caller should fall back to the single-method <see cref="ResolveMethodOnType"/>
    /// path (which already handles the no-overload majority case).
    /// </summary>
    private (List<FunctionType> Fns, List<Side> Sides) CollectMethodOverloads(
        PassContext pc, Type baseType, string methodName, bool staticOnly)
    {
        var fns = new List<FunctionType>();
        var sides = new List<Side>();

        if (baseType is ClassType ct)
        {
            for (var cur = ct; cur != null; cur = cur.BaseClass)
            {
                var ovBag = staticOnly ? cur.StaticMethodOverloads : cur.MethodOverloads;
                var ovSides = staticOnly ? cur.StaticMethodOverloadSides : cur.MethodOverloadSides;
                if (ovBag.TryGetValue(methodName, out var list))
                {
                    fns.AddRange(list);
                    if (ovSides.TryGetValue(methodName, out var s)) sides.AddRange(s);
                    else sides.AddRange(Enumerable.Repeat(Side.All, list.Count));
                }
            }

            if (fns.Count == 0 && !staticOnly)
            {
                CollectInterfaceOverloads(pc, ct, methodName, fns, sides);
            }
        }
        else if (baseType is InterfaceType ift && !staticOnly)
        {
            void Walk(InterfaceType i, HashSet<InterfaceType> visited)
            {
                if (!visited.Add(i)) return;
                if (i.MethodOverloads.TryGetValue(methodName, out var list))
                {
                    fns.AddRange(list);
                    if (i.MethodOverloadSides.TryGetValue(methodName, out var s)) sides.AddRange(s);
                    else sides.AddRange(Enumerable.Repeat(Side.All, list.Count));
                }
                foreach (var b in i.BaseInterfaces) Walk(b, visited);
            }
            Walk(ift, []);
        }

        return (fns, sides);
    }

    /// <summary>
    /// Adds the overloads an implemented interface declares for <paramref name="methodName"/>,
    /// self-prefixed so they score the way class methods do. Only consulted when the class chain
    /// declares none, so a class that restates an interface member keeps its own candidate set.
    /// </summary>
    private void CollectInterfaceOverloads(PassContext pc, ClassType classType, string methodName,
        List<FunctionType> fns, List<Side> sides)
    {
        foreach (var iface in Type.ImplementedInterfaces(classType))
        {
            if (!iface.MethodOverloads.TryGetValue(methodName, out var list)) continue;

            foreach (var fn in list)
            {
                fns.Add(WithSelfParam(pc, classType, fn));
            }

            if (iface.MethodOverloadSides.TryGetValue(methodName, out var ifaceSides))
            {
                sides.AddRange(ifaceSides);
            }
            else
            {
                sides.AddRange(Enumerable.Repeat(Side.All, list.Count));
            }

            return;
        }
    }

    /// <summary>
    /// Picks the best matching overload from a candidate set, filtering by
    /// side first (overloads not reachable from the file's side mask are
    /// dropped) and then scoring remaining candidates against the actual
    /// argument types. Returns null when no candidate fits — the caller
    /// should then fall back to the primary <see cref="ResolveMethodOnType"/>
    /// result so existing diagnostics still fire.
    /// </summary>
    private FunctionType? PickOverload(PassContext pc, List<FunctionType> fns,
        List<Side> sides, List<TypID> argTypes, bool prefixSelf) =>
        PickOverload(pc, fns, sides, argTypes, prefixSelf, out _);

    private FunctionType? PickOverload(PassContext pc, List<FunctionType> fns,
        List<Side> sides, List<TypID> argTypes, bool prefixSelf, out bool certain)
    {
        certain = false;
        if (fns.Count == 0) return null;
        var fileMask = ResolveFileSideMask(pc);

        FunctionType? best = null;
        var bestScore = -1;
        var scores = new List<(FunctionType Function, int Score)>();

        for (var i = 0; i < fns.Count; i++)
        {
            var fn = fns[i];
            var side = i < sides.Count ? sides[i] : Side.All;
            // A symbol with side X is reachable from a file with mask F when
            // every bit of X is in F (see SideExtensions.IsAccessibleFrom).
            if (!side.IsAccessibleFrom(fileMask)) continue;

            // Method overloads include the synthetic `self` parameter for
            // instance methods on classes (added in ResolveClassDecl). At a
            // dot-access call site we haven't prepended self; account for
            // that by trimming the param list for scoring.
            var effective = prefixSelf
                ? new List<TypID>(argTypes.Count + 1) { fn.ParamTypes.Count > 0 ? fn.ParamTypes[0].ID : pc.Types.PrimAny.ID }
                : new List<TypID>(argTypes.Count);
            effective.AddRange(argTypes);

            var score = ScoreOverload(pc, fn, effective);
            scores.Add((fn, score));
            if (score > bestScore) { bestScore = score; best = fn; }
        }

        if (bestScore < 0) return null;
        certain = IsCertainOverload(pc, best!, bestScore, scores, argTypes, prefixSelf);
        return best;
    }

    /// <summary>
    /// The name of the Lua function holding the overload <paramref name="picked"/> of the instance
    /// method <paramref name="name"/> called on a <paramref name="receiver"/>, or <c>null</c> when
    /// the class declaring it keeps the overload under the method's plain name. On an
    /// instantiation such as <c>Box&lt;number&gt;</c> the name comes from the generic definition's
    /// signature, which is the one the class is emitted with.
    /// </summary>
    private static string? InstanceOverloadName(ClassType receiver, string name, FunctionType picked)
    {
        for (var current = receiver; current != null; current = current.BaseClass)
        {
            if (!current.MethodOverloads.TryGetValue(name, out var overloads)) continue;

            var index = overloads.IndexOf(picked);
            if (index < 0) continue;

            var owner = current.Definition;
            if (!OverloadSignature.IsSplit(owner, name, false) || !owner.MethodOverloads.TryGetValue(name, out var declared) || index >= declared.Count) return null;
            return OverloadSignature.VariantName(name, declared[index], true);
        }

        return null;
    }

    /// <summary>
    /// The name of the Lua function holding the overload <paramref name="picked"/> of the static
    /// method <paramref name="name"/> reached through <paramref name="receiver"/>, or <c>null</c>
    /// when the class declaring it keeps the overload under the method's plain name.
    /// </summary>
    private static string? StaticOverloadName(ClassType receiver, string name, FunctionType picked)
    {
        for (var owner = receiver; owner != null; owner = owner.BaseClass)
        {
            if (!owner.StaticMethodOverloads.TryGetValue(name, out var overloads) || !overloads.Contains(picked)) continue;
            return OverloadSignature.IsSplit(owner, name, true) ? OverloadSignature.VariantName(name, picked, false) : null;
        }

        return null;
    }

    private Side ResolveFileSideMask(PassContext pc)
    {
        if (pc.File == null || pc.Config.Sides.Count == 0) return Side.All;
        return SidesResolver.ResolveFileSide(pc.Config.Sides,
            pc.File.Filename ?? "", pc.Config.ProjectRoot);
    }

    private TypID InferTableConstructor(PassContext pc, TableConstructorExpr tc)
    {
        var tt = pc.Types;

        if (tc.Fields.Count == 0)
        {
            return tt.ArrayOf(tt.PrimAny);
        }

        var allPositional = tc.Fields.All(f => f.Kind == TableFieldKind.Positional);
        var shape = _shapedTables.GetValueOrDefault(tc);
        var allNamed = tc.Fields.All(f => f.Kind == TableFieldKind.Named || shape != null && f.Key is StringLiteralExpr);

        if (allPositional)
        {
            var elemType = SynthesizeExpr(pc, tc.Fields[0].Value);
            for (var i = 1; i < tc.Fields.Count; i++)
            {
                var et = SynthesizeExpr(pc, tc.Fields[i].Value);
                if (!IsTypeAssignable(pc, elemType, et) && !IsTypeAssignable(pc, et, elemType))
                {
                    elemType = tt.PrimAny.ID;
                }
            }

            return tt.ArrayOf(GetType(pc, elemType));
        }

        if (allNamed)
        {
            var fields = new List<StructType.Field>();
            foreach (var field in tc.Fields)
            {
                if (field.Key != null) SynthesizeExpr(pc, field.Key);
                var fieldName = field.Name ?? new NameRef(TableFieldName(field)!, field.Key!.Span);
                var expectedField = shape != null ? Type.FieldTypeOf(shape, fieldName.Name) : null;
                var fValType = expectedField != null ? SynthesizeFor(pc, field.Value, expectedField.ID) : SynthesizeExpr(pc, field.Value);
                fields.Add(new StructType.Field(fieldName, GetType(pc, fValType)));
            }

            return tt.StructOf(fields);
        }

        var keyType = tt.PrimAny.ID;
        var valueType = TypID.Invalid;
        foreach (var field in tc.Fields)
        {
            TypID kt;
            if (field.Key != null)
            {
                kt = SynthesizeExpr(pc, field.Key);
            }
            else if (field.Name != null)
            {
                kt = tt.PrimString.ID;
            }
            else
            {
                kt = tt.PrimNumber.ID;
            }

            var vt = SynthesizeExpr(pc, field.Value);
            if (valueType == TypID.Invalid)
            {
                keyType = kt;
                valueType = vt;
            }
            else
            {
                if (!IsTypeAssignable(pc, keyType, kt) && !IsTypeAssignable(pc, kt, keyType))
                {
                    keyType = tt.PrimAny.ID;
                }

                if (!IsTypeAssignable(pc, valueType, vt) && !IsTypeAssignable(pc, vt, valueType))
                {
                    valueType = tt.PrimAny.ID;
                }
            }
        }

        return tt.MapOf(GetType(pc, keyType), GetType(pc, valueType));
    }

    /// <summary>
    /// The return type of a call with the function's type parameters filled in from the arguments:
    /// <c>first(items, "x")</c> with <c>first&lt;T&gt;(items: { [string]: T }, key: string): T?</c>
    /// and items a map of <c>Item</c> returns an <c>Item?</c>. A type parameter no argument tells
    /// stays as it is.
    /// </summary>
    private TypID SpecializedReturn(PassContext pc, FunctionType fn, List<TypID> argTypes)
    {
        if (!ContainsTypeParameter(fn.ReturnType)) return fn.ReturnType.ID;

        var bindings = new Dictionary<TypID, Type>();
        for (var i = 0; i < argTypes.Count; i++)
        {
            var parameter = i < fn.ParamTypes.Count ? fn.ParamTypes[i] : fn.IsVararg ? fn.VarargType : null;
            if (parameter != null && pc.Pkg!.Types.GetByID(argTypes[i], out var argument))
                Bind(pc, parameter, argument, bindings);
        }

        return bindings.Count == 0 ? fn.ReturnType.ID : pc.Pkg!.Types.Substitute(fn.ReturnType, bindings).ID;
    }

    /// <summary>
    /// Matches a parameter type against the type of its argument and records what each type
    /// parameter in it stands for: through <c>T?</c> and other unions, lists, maps, tuples and the
    /// signature of a callback.
    /// </summary>
    private void Bind(PassContext pc, Type parameter, Type argument, Dictionary<TypID, Type> bindings)
    {
        switch (parameter)
        {
            case TypeParameterType typeParameter:
                Widen(pc, bindings, typeParameter, argument);
                break;
            case UnionType union:
            {
                var generic = union.Types.Where(ContainsTypeParameter).ToList();
                if (generic.Count != 1) break;

                var arguments = argument is UnionType argumentUnion ? argumentUnion.Types : [argument];
                var rest = arguments.Where(candidate => union.Types.All(member => member.ID != candidate.ID)).ToList();
                if (rest.Count > 0)
                    Bind(pc, generic[0], rest.Count == 1 ? rest[0] : GetType(pc, pc.Types.UnionOf(rest)), bindings);
                break;
            }
            case TableArrayType list when argument is TableArrayType argumentList:
                Bind(pc, list.ElementType, argumentList.ElementType, bindings);
                break;
            case TableMapType map when argument is TableMapType argumentMap:
                Bind(pc, map.KeyType, argumentMap.KeyType, bindings);
                Bind(pc, map.ValueType, argumentMap.ValueType, bindings);
                break;
            case TupleType tuple when argument is TupleType argumentTuple:
                foreach (var (field, argumentField) in tuple.Fields.Zip(argumentTuple.Fields))
                    Bind(pc, field.Type, argumentField.Type, bindings);
                break;
            case FunctionType function when argument is FunctionType argumentFunction:
                foreach (var (functionParameter, argumentParameter) in function.ParamTypes.Zip(argumentFunction.ParamTypes))
                    Bind(pc, functionParameter, argumentParameter, bindings);
                Bind(pc, function.ReturnType, argumentFunction.ReturnType, bindings);
                break;
            case ClassType { GenericDefinition: { } definition } instance
                when InstantiationOf(argument, definition) is { } argumentInstance:
                foreach (var (typeArgument, argumentTypeArgument) in instance.TypeArguments.Zip(argumentInstance))
                    Bind(pc, typeArgument, argumentTypeArgument, bindings);
                break;
            case InterfaceType { GenericDefinition: { } definition } instance
                when InstantiationOf(argument, definition) is { } argumentInstance:
                foreach (var (typeArgument, argumentTypeArgument) in instance.TypeArguments.Zip(argumentInstance))
                    Bind(pc, typeArgument, argumentTypeArgument, bindings);
                break;
        }
    }

    /// <summary>
    /// The type arguments <paramref name="type"/> gives the generic <paramref name="definition"/>:
    /// its own when it instantiates it, or those of the base class or implemented interface that
    /// does. <c>null</c> when it is no instantiation of it.
    /// </summary>
    private static List<Type>? InstantiationOf(Type type, Type definition)
    {
        switch (type)
        {
            case ClassType classType:
                for (var current = classType; current != null; current = current.BaseClass)
                {
                    if (current.GenericDefinition == definition) return current.TypeArguments;
                }

                return definition is InterfaceType
                    ? Type.ImplementedInterfaces(classType).FirstOrDefault(iface => iface.GenericDefinition == definition)?.TypeArguments
                    : null;
            case InterfaceType interfaceType:
                return Type.BaseInterfacesOf(interfaceType).Prepend(interfaceType)
                    .FirstOrDefault(iface => iface.GenericDefinition == definition)?.TypeArguments;
            default:
                return null;
        }
    }

    /// <summary>
    /// Records what a type parameter stands for; when several arguments tell it, the wider of
    /// their types, or their union when neither holds the other.
    /// </summary>
    private void Widen(PassContext pc, Dictionary<TypID, Type> bindings, TypeParameterType typeParameter, Type argument)
    {
        if (argument is TypeParameterType) return;
        if (!bindings.TryGetValue(typeParameter.ID, out var bound))
        {
            bindings[typeParameter.ID] = argument;
            return;
        }

        if (IsTypeAssignable(pc, bound.ID, argument.ID)) return;
        bindings[typeParameter.ID] = IsTypeAssignable(pc, argument.ID, bound.ID)
            ? argument
            : GetType(pc, pc.Types.UnionOf([bound, argument]));
    }

    private static bool ContainsTypeParameter(Type type)
    {
        return type switch
        {
            TypeParameterType => true,
            UnionType union => union.Types.Any(ContainsTypeParameter),
            TableArrayType list => ContainsTypeParameter(list.ElementType),
            TableMapType map => ContainsTypeParameter(map.KeyType) || ContainsTypeParameter(map.ValueType),
            TupleType tuple => tuple.Fields.Any(field => ContainsTypeParameter(field.Type)),
            VariadicType variadic => ContainsTypeParameter(variadic.ElementType),
            FunctionType function => function.ParamTypes.Any(ContainsTypeParameter) || ContainsTypeParameter(function.ReturnType),
            ClassType classType => classType.TypeArguments.Any(ContainsTypeParameter),
            InterfaceType interfaceType => interfaceType.TypeArguments.Any(ContainsTypeParameter),
            KeyOfType keyOf => ContainsTypeParameter(keyOf.Target),
            UtilityType utility => ContainsTypeParameter(utility.Target) || utility.Keys != null && ContainsTypeParameter(utility.Keys),
            IndexedAccessType indexed => ContainsTypeParameter(indexed.Target) || ContainsTypeParameter(indexed.Index),
            _ => false,
        };
    }

    /// <summary>
    /// Gives an empty table literal passed as an argument the list or map type its parameter
    /// expects, as a local or an assignment does: <c>{}</c> carries nothing to tell its shape from.
    /// <paramref name="offset"/> is the number of argument types before the written arguments,
    /// such as the receiver of a method call.
    /// </summary>
    private void AdoptArguments(PassContext pc, FunctionType fnType, List<Expr> arguments, List<TypID> argTypes, int offset)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var position = i + offset;
            if (position >= argTypes.Count || position >= fnType.ParamTypes.Count) break;

            var paramType = fnType.ParamTypes[position].ID;
            if (IsEmptyTable(arguments[i]) && TakesEmptyTable(pc, paramType))
                argTypes[position] = paramType;
        }

        AdoptLiteralArguments(pc, fnType, arguments, argTypes, offset);
        AdoptFunctionLiterals(pc, fnType, arguments, argTypes, offset);
    }

    private void CheckCallArguments(PassContext pc, TextSpan span, FunctionType fnType, List<TypID> argTypes)
    {
        fnType = WithDependentParameters(pc, fnType, argTypes);
        _openTypeParamDepth++;
        try
        {
            CheckCallArgumentsAgainst(pc, span, fnType, argTypes);
        }
        finally
        {
            _openTypeParamDepth--;
        }
    }

    private void CheckCallArgumentsAgainst(PassContext pc, TextSpan span, FunctionType fnType, List<TypID> argTypes)
    {
        var paramCount = fnType.ParamTypes.Count;
        var argCount = argTypes.Count;
        var minParams = fnType.MinParamCount;
        
        var requiredCount = minParams;
        while (requiredCount > argCount && requiredCount > 0
               && IsNullable(pc, fnType.ParamTypes[requiredCount - 1].ID))
        {
            requiredCount--;
        }

        if (argCount < requiredCount)
        {
            pc.Diag.Report(span, DiagnosticCode.ErrFuncParamMismatch, minParams, argCount);
            return;
        }

        if (argCount > paramCount && !fnType.IsVararg)
        {
            if (fnType.IsAsync && argCount == paramCount + 1)
            {
            }
            else
            {
                var lastParam = paramCount > 0 ? fnType.ParamTypes[paramCount - 1] : null;
                if (lastParam is not { Kind: TypeKind.PrimitiveAny })
                {
                    pc.Diag.Report(span, DiagnosticCode.ErrFuncParamMismatch, paramCount, argCount);
                    return;
                }
            }
        }

        for (var i = 0; i < paramCount; i++)
        {
            if (i >= argCount) break;
            var paramType = fnType.ParamTypes[i].ID;
            var argType = argTypes[i];
            if (!IsTypeAssignable(pc, paramType, argType))
            {
                if (IsNullable(pc, argType) && IsTypeAssignable(pc, paramType, StripNil(pc, argType)))
                {
                    EnsureNotNil(pc, span, argType);
                }
                else
                {
                    pc.Diag.Report(span, DiagnosticCode.ErrTypeMismatch,
                        TypeName(pc, ExpectedForMessage(pc, paramType)), TypeName(pc, argType));
                }
            }
        }

        if (fnType.IsVararg && fnType.VarargType != null)
        {
            for (var i = paramCount; i < argCount; i++)
            {
                var argType = argTypes[i];
                if (!IsTypeAssignable(pc, fnType.VarargType.ID, argType))
                {
                    pc.Diag.Report(span, DiagnosticCode.ErrTypeMismatch,
                        TypeName(pc, fnType.VarargType.ID), TypeName(pc, argType));
                }
            }
        }
    }

    private List<(TypID typ, TextSpan span)> CollectReturnTypes(PassContext pc, List<Stmt> stmts)
    {
        return stmts.SelectMany(ReturnStatements)
            .Select(statement => (ComputeReturnType(pc, statement.Values), statement.Span))
            .ToList();
    }

    /// <summary>The return statements of <paramref name="stmt"/>, its nested blocks included and the functions inside it left out.</summary>
    private static IEnumerable<ReturnStmt> ReturnStatements(Stmt stmt)
    {
        return stmt switch
        {
            ReturnStmt statement => [statement],
            DoBlockStmt block => block.Body.SelectMany(ReturnStatements),
            TryStmt attempt => attempt.Body.Concat(attempt.Catch?.Body ?? []).SelectMany(ReturnStatements),
            IfStmt branch => branch.Body
                .Concat(branch.ElseIfs.SelectMany(elseIf => elseIf.Body))
                .Concat(branch.ElseBody ?? [])
                .SelectMany(ReturnStatements),
            WhileStmt loop => loop.Body.SelectMany(ReturnStatements),
            RepeatStmt loop => loop.Body.SelectMany(ReturnStatements),
            NumericForStmt loop => loop.Body.SelectMany(ReturnStatements),
            GenericForStmt loop => loop.Body.SelectMany(ReturnStatements),
            _ => []
        };
    }

    private TypID ComputeReturnType(PassContext pc, List<Expr> values)
    {
        if (values.Count == 0)
        {
            return pc.Types.PrimNil.ID;
        }

        if (values.Count == 1)
        {
            return values[0].Type != TypID.Invalid ? values[0].Type : SynthesizeExpr(pc, values[0]);
        }

        var fields = new List<TupleType.Field>();
        foreach (var v in values)
        {
            var t = v.Type != TypID.Invalid ? v.Type : SynthesizeExpr(pc, v);
            fields.Add(new TupleType.Field(GetType(pc, t)));
        }

        return pc.Types.TupleOf(fields);
    }

    private bool IsTerminator(Stmt stmt)
    {
        return stmt is ReturnStmt or BreakStmt or ContinueStmt or GotoStmt;
    }

    /// <summary>
    /// The return-flow checks shared by free functions, methods, interface defaults and extension
    /// methods: every returned value must fit the declared return type, and the body must produce
    /// one on every path. A <c>never</c> return type inverts both halves — no value may be returned
    /// at all, and the body must not be able to complete normally.
    /// </summary>
    private void CheckReturnFlow(PassContext pc, TypID declared, List<(TypID typ, TextSpan span)> collected,
        List<Stmt> body, ReturnStmt? tailReturn, TextSpan reportSpan)
    {
        if (declared == pc.Types.PrimNever.ID)
        {
            foreach (var (_, span) in collected)
            {
                pc.Diag.Report(span, DiagnosticCode.ErrNeverFunctionReturnsValue);
            }

            if (!FunctionBodyAlwaysReturns(pc, body, tailReturn))
            {
                pc.Diag.Report(reportSpan, DiagnosticCode.ErrNeverFunctionCompletes);
            }

            return;
        }

        foreach (var (typ, span) in collected)
        {
            EnsureAssignable(pc, span, declared, typ);
        }

        if (ReturnTypeRequiresValue(pc, declared) && !FunctionBodyAlwaysReturns(pc, body, tailReturn))
        {
            pc.Diag.Report(reportSpan, DiagnosticCode.ErrMissingReturn, TypeName(pc, declared));
        }
    }

    /// <summary>
    /// A declared return type requires the body to actually produce a value on all paths
    /// only when it can hold neither nil nor "anything". Nilable types (`T?`, `nil`, `void`)
    /// tolerate a fall-through (which yields nil in Lua) and `any` opts out of the check.
    /// </summary>
    private bool ReturnTypeRequiresValue(PassContext pc, TypID retId)
    {
        if (retId == pc.Types.PrimAny.ID) return false;
        // A variadic `...T` return may yield zero values, so a fall-through is legal.
        if (pc.Pkg!.Types.GetByID(retId, out var t) && t.Kind == TypeKind.Variadic) return false;
        return !IsNullable(pc, retId);
    }

    /// <summary>
    /// Finds the return type of the method named <paramref name="name"/> as declared by a
    /// base class or an implemented interface, so an override that omits its own return
    /// annotation inherits the overridden signature instead of defaulting to nil.
    /// </summary>
    private static bool TryGetInheritedReturnType(ClassType classType, string name, out Type retType)
    {
        for (var cur = classType.BaseClass; cur != null; cur = cur.BaseClass)
        {
            if (cur.Methods.TryGetValue(name, out var baseFt))
            {
                retType = baseFt.ReturnType;
                return true;
            }
        }

        foreach (var iface in Type.ImplementedInterfaces(classType))
        {
            if (iface.Methods.TryGetValue(name, out var ifaceFt))
            {
                retType = ifaceFt.ReturnType;
                return true;
            }
        }

        retType = null!;
        return false;
    }

    /// <summary>
    /// Conservative all-paths-return analysis for the missing-return check. Returns true
    /// when the body is guaranteed to return or diverge on every path. The trailing return
    /// (stored separately from the body) short-circuits to true. The analysis is biased
    /// toward answering "yes" when unsure so it never produces a false "missing return".
    /// </summary>
    private bool FunctionBodyAlwaysReturns(PassContext pc, List<Stmt> body, ReturnStmt? tailReturn)
    {
        return tailReturn != null || BlockAlwaysExits(pc, body);
    }

    private bool BlockAlwaysExits(PassContext pc, List<Stmt> stmts)
    {
        foreach (var stmt in stmts)
        {
            if (StmtAlwaysExits(pc, stmt)) return true;
        }
        return false;
    }

    private bool StmtAlwaysExits(PassContext pc, Stmt stmt)
    {
        switch (stmt)
        {
            case ReturnStmt:
            case BreakStmt:
            case ContinueStmt:
            case GotoStmt:
                return true;
            case ExprStmt es:
                return IsDivergingCall(pc, es.Expression);
            case DoBlockStmt db:
                return BlockAlwaysExits(pc, db.Body);
            case TryStmt tryStmt:
                return BlockAlwaysExits(pc, tryStmt.Body)
                       && (tryStmt.Catch != null ? BlockAlwaysExits(pc, tryStmt.Catch.Body) : tryStmt.Finally != null);
            case IfStmt ifs:
                if (ifs.ElseBody == null) return false;
                if (!BlockAlwaysExits(pc, ifs.Body)) return false;
                foreach (var e in ifs.ElseIfs)
                    if (!BlockAlwaysExits(pc, e.Body)) return false;
                return BlockAlwaysExits(pc, ifs.ElseBody);
            case WhileStmt ws:
                return IsLiteralTrue(ws.Condition) && !LoopHasEscapingBreak(ws.Body, 0);
            case RepeatStmt rp:
                return IsLiteralFalse(rp.Condition) && !LoopHasEscapingBreak(rp.Body, 0);
            case MatchStmt ms:
                // No exhaustiveness reasoning here: if a match with all-exiting arms is not
                // actually exhaustive, control falls through at runtime — treating it as
                // exiting only risks a missed (never a false) missing-return diagnostic.
                return ms.Arms.Count > 0 && ms.Arms.All(a => BlockAlwaysExits(pc, a.Body));
            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="stmts"/> contains a <c>break</c> that escapes the loop being
    /// analysed (nesting 0). Breaks that target loops nested inside are not counted. Used to
    /// decide whether a <c>while true</c> / <c>repeat until false</c> loop can terminate.
    /// </summary>
    private static bool LoopHasEscapingBreak(List<Stmt> stmts, int nesting)
    {
        foreach (var stmt in stmts)
        {
            if (StmtHasEscapingBreak(stmt, nesting)) return true;
        }
        return false;
    }

    private static bool StmtHasEscapingBreak(Stmt stmt, int nesting)
    {
        switch (stmt)
        {
            case BreakStmt b:
                return b.Depth > nesting;
            case WhileStmt w:
                return LoopHasEscapingBreak(w.Body, nesting + 1);
            case RepeatStmt r:
                return LoopHasEscapingBreak(r.Body, nesting + 1);
            case NumericForStmt nf:
                return LoopHasEscapingBreak(nf.Body, nesting + 1);
            case GenericForStmt gf:
                return LoopHasEscapingBreak(gf.Body, nesting + 1);
            case DoBlockStmt db:
                return LoopHasEscapingBreak(db.Body, nesting);
            case IfStmt ifs:
                if (LoopHasEscapingBreak(ifs.Body, nesting)) return true;
                foreach (var e in ifs.ElseIfs)
                    if (LoopHasEscapingBreak(e.Body, nesting)) return true;
                return ifs.ElseBody != null && LoopHasEscapingBreak(ifs.ElseBody, nesting);
            case MatchStmt ms:
                foreach (var a in ms.Arms)
                    if (LoopHasEscapingBreak(a.Body, nesting)) return true;
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// A call that never returns normally, i.e. one whose declared return type is <c>never</c>
    /// (<c>error(...)</c>, <c>os.exit(...)</c>, or any user function declared that way). The
    /// expression must already have been synthesized, which every caller guarantees by resolving
    /// the enclosing body first.
    /// </summary>
    private bool IsDivergingCall(PassContext pc, Expr expr)
    {
        if (expr is not (FunctionCallExpr or MethodCallExpr)) return false;
        return expr.Type == pc.Types.PrimNever.ID;
    }

    private static Expr Unparen(Expr expr) => expr is ParenExpr p ? Unparen(p.Inner) : expr;

    private static bool IsLiteralTrue(Expr expr) => Unparen(expr) is BoolLiteralExpr { Value: true };

    private static bool IsLiteralFalse(Expr expr) => Unparen(expr) is BoolLiteralExpr { Value: false };

    /// <summary>
    /// Whether a value of type <paramref name="src"/> may be assigned to a location of type
    /// <paramref name="dst"/>. <c>never</c> is the bottom type: it fits every destination, and
    /// nothing — not even <c>any</c> — fits a <c>never</c> destination, so both of its checks come
    /// before the <c>any</c> short-circuit.
    /// </summary>
    private bool IsTypeAssignable(PassContext pc, TypID dst, TypID src)
    {
        if (dst == src) return true;
        if (dst == TypID.Invalid || src == TypID.Invalid) return false;
        var tt = pc.Types;

        if (src == tt.PrimNever.ID) return true;
        if (dst == tt.PrimNever.ID) return false;

        if (dst == tt.PrimAny.ID || src == tt.PrimAny.ID) return true;

        var evaluatedDst = Evaluate(pc, dst);
        var evaluatedSrc = Evaluate(pc, src);
        if (evaluatedDst != dst || evaluatedSrc != src) return IsTypeAssignable(pc, evaluatedDst, evaluatedSrc);
        if (UnevaluatedAccessFits(pc, dst, src) is { } accessFits) return accessFits;

        // The `function` category accepts any concrete function signature.
        if (dst == tt.PrimFunction.ID && pc.Pkg!.Types.GetByID(src, out var srcFn) && srcFn is FunctionType)
            return true;

        // Variadic `...T` target: every returned value must be assignable to T. Zero values
        // (a bare `return` → nil source) are allowed. A tuple source checks each element; a
        // variadic source checks its element type; a single value checks directly.
        if (pc.Pkg!.Types.GetByID(dst, out var dstVarCheck) && dstVarCheck is VariadicType dstVariadic)
        {
            if (src == tt.PrimNil.ID) return true;
            if (pc.Pkg.Types.GetByID(src, out var srcForVar))
            {
                if (srcForVar is VariadicType sv) return IsTypeAssignable(pc, dstVariadic.ElementType.ID, sv.ElementType.ID);
                if (srcForVar is TupleType st) return st.Fields.All(f => IsTypeAssignable(pc, dstVariadic.ElementType.ID, f.Type.ID));
            }
            return IsTypeAssignable(pc, dstVariadic.ElementType.ID, src);
        }
        // A variadic source in a single-value target position collapses to its element type.
        if (pc.Pkg!.Types.GetByID(src, out var srcVarCheck) && srcVarCheck is VariadicType srcVariadic)
        {
            return IsTypeAssignable(pc, dst, srcVariadic.ElementType.ID);
        }

        if (pc.Pkg!.Types.GetByID(dst, out var dstTpCheck) && dstTpCheck is TypeParameterType dstTp)
        {
            if (!IsFixed(dstTp))
            {
                if (dstTp.ExtendsBound is { } eb && !IsTypeAssignable(pc, eb, src)) return false;
                return dstTp.ImplementsBounds.All(bound => IsTypeAssignable(pc, bound, src));
            }

            if (AssignableToFixedTypeParam(pc, src) is { } fitsTypeParam) return fitsTypeParam;
        }
        if (pc.Pkg.Types.GetByID(src, out var srcTpCheck) && srcTpCheck is TypeParameterType srcTp)
        {
            if (!IsFixed(srcTp)) return true;
            if (AssignableFromFixedTypeParam(pc, srcTp, dst) is { } typeParamFits) return typeParamFits;
        }

        if (!pc.Config.Rules.StrictNil)
        {
            if (src == tt.PrimNil.ID) return true;
        }

        if (pc.Pkg!.Types.GetByID(src, out var srcEnumType) && srcEnumType is EnumType srcEnum)
        {
            if (dst == srcEnum.BaseType.ID) return true;
        }

        if (pc.Pkg!.Types.GetByID(dst, out var literalDst) && pc.Pkg.Types.GetByID(src, out var literalSrc)
            && LiteralAssignable(pc, literalDst, literalSrc) is { } literalFits)
            return literalFits;

        if (pc.Pkg!.Types.GetByID(dst, out var dstType) && dstType is UnionType unionDst)
        {
            foreach (var member in unionDst.Types)
            {
                if (IsTypeAssignable(pc, member.ID, src)) return true;
            }

            return pc.Pkg.Types.GetByID(src, out var sourceUnion) && sourceUnion is UnionType { } members
                && members.Types.All(member => IsTypeAssignable(pc, dst, member.ID));
        }

        if (pc.Pkg.Types.GetByID(src, out var srcType) && srcType is UnionType unionSrc)
        {
            if (!pc.Config.Rules.StrictNil)
            {
                var nonNil = unionSrc.Types.Where(m => m.Kind != TypeKind.PrimitiveNil).ToList();
                if (nonNil.Count < unionSrc.Types.Count)
                {
                    var stripped = nonNil.Count == 1 ? nonNil[0].ID : pc.Types.UnionOf(nonNil);
                    return IsTypeAssignable(pc, dst, stripped);
                }
            }

            foreach (var member in unionSrc.Types)
            {
                if (!IsTypeAssignable(pc, dst, member.ID)) return false;
            }

            return true;
        }

        if (pc.Pkg.Types.GetByID(dst, out var dstTypeNode) && pc.Pkg.Types.GetByID(src, out var srcTypeNode))
        {
            // Class extends Class / Class implements Interface / Interface extends Interface.
            if (dstTypeNode is ClassType dstCls && srcTypeNode is ClassType srcCls)
            {
                for (var cur = srcCls; cur != null; cur = cur.BaseClass)
                {
                    if (cur.Definition == dstCls.Definition) return TypeArgumentsFit(pc, ClassArguments(dstCls), ClassArguments(cur));
                }
            }

            if (dstTypeNode is InterfaceType dstIface)
            {
                var candidates = srcTypeNode switch
                {
                    ClassType srcCls2 => Type.ImplementedInterfaces(srcCls2),
                    InterfaceType srcIface => Type.BaseInterfacesOf(srcIface).Prepend(srcIface),
                    _ => [],
                };
                if (candidates.Any(candidate => candidate.Definition == dstIface.Definition
                        && TypeArgumentsFit(pc, InterfaceArguments(dstIface), InterfaceArguments(candidate))))
                    return true;
            }

            // Covariant array element compatibility for ergonomics — Lua tables are
            // mutable so this is not strictly sound, but matches user expectations.
            if (dstTypeNode is TableArrayType dstArr && srcTypeNode is TableArrayType srcArr)
            {
                if (IsTypeAssignable(pc, dstArr.ElementType.ID, srcArr.ElementType.ID)) return true;
            }

            // A table keyed by numbers, such as `{ [1] = a, [2] = b }`, is an array to Lua.
            if (dstTypeNode is TableArrayType dstList && srcTypeNode is TableMapType { KeyType.Kind: TypeKind.PrimitiveNumber } srcNumbered
                && IsTypeAssignable(pc, dstList.ElementType.ID, srcNumbered.ValueType.ID))
            {
                return true;
            }

            if (dstTypeNode is TableMapType dstEntries && srcTypeNode is TableMapType srcEntries
                && IsTypeAssignable(pc, dstEntries.KeyType.ID, srcEntries.KeyType.ID)
                && IsTypeAssignable(pc, dstEntries.ValueType.ID, srcEntries.ValueType.ID))
            {
                return true;
            }

            // A tuple return ending in a variadic tail, e.g. `(string, ...number)`, matches a
            // longer tuple of actual return values: fixed prefix positionally, the rest against
            // the tail element. Tuples of one length match value by value.
            if (dstTypeNode is TupleType dstTuple && srcTypeNode is TupleType srcTuple
                && dstTuple.Fields.Count > 0 && dstTuple.Fields[^1].Type is VariadicType)
            {
                if (TupleWithVariadicTailAssignable(pc, dstTuple, srcTuple)) return true;
            }

            if (dstTypeNode is FunctionType dstFunction && srcTypeNode is FunctionType srcFunction
                && IsFunctionAssignable(pc, dstFunction, srcFunction))
            {
                return true;
            }

            if (dstTypeNode is TableMapType dstMap && srcTypeNode is StructType srcStruct
                && IsTypeAssignable(pc, dstMap.KeyType.ID, tt.PrimString.ID)
                && srcStruct.Fields.Where(field => !field.IsMeta).All(field => IsTypeAssignable(pc, dstMap.ValueType.ID, field.Type.ID)))
            {
                return true;
            }

            if (dstTypeNode is TupleType dstValues && srcTypeNode is TupleType srcValues
                && dstValues.Fields.Count == srcValues.Fields.Count
                && dstValues.Fields.Zip(srcValues.Fields).All(pair => IsTypeAssignable(pc, pair.First.Type.ID, pair.Second.Type.ID)))
            {
                return true;
            }

            if (dstTypeNode is StructType dstShape && srcTypeNode is StructType srcShape
                && dstShape.Fields.Where(field => !field.IsMeta).All(field => HasFittingField(pc, srcShape, field.Name.Name, field.Type)))
            {
                return true;
            }

            if (dstTypeNode is InterfaceType dstContract && srcTypeNode is StructType srcTable
                && TableFitsInterface(pc, srcTable, dstContract))
            {
                return true;
            }
        }

        return StructEqual(pc, dst, src);
    }

    /// <summary>
    /// Whether the table <paramref name="table"/> holds a field <paramref name="name"/> that fits
    /// <paramref name="expected"/>, or leaves it out where <paramref name="expected"/> allows
    /// <c>nil</c>. Fields the table holds beyond the expected ones do not matter.
    /// </summary>
    private bool HasFittingField(PassContext pc, StructType table, string name, Type expected)
    {
        var field = table.Fields.FirstOrDefault(candidate => !candidate.IsMeta && candidate.Name.Name == name);
        return field == null ? IsNullable(pc, expected.ID) : IsTypeAssignable(pc, expected.ID, field.Type.ID);
    }

    private static bool HasFieldOutside(StructType table, InterfaceType contract)
    {
        var members = Type.BaseInterfacesOf(contract).Prepend(contract).ToList();
        return table.Fields.Any(field => !field.IsMeta
                                         && !members.Any(member => member.Fields.ContainsKey(field.Name.Name) || member.Methods.ContainsKey(field.Name.Name)));
    }

    /// <summary>
    /// Whether a table literal's type satisfies <paramref name="contract"/> structurally: it holds
    /// every field the interface and the interfaces it extends declare, and a function for each of
    /// their methods, since a plain table has no class to supply one.
    /// </summary>
    private bool TableFitsInterface(PassContext pc, StructType table, InterfaceType contract)
    {
        if (contract.IsContract && HasFieldOutside(table, contract)) return false;

        foreach (var iface in Type.BaseInterfacesOf(contract).Prepend(contract))
        {
            if (!iface.Fields.All(field => HasFittingField(pc, table, field.Key, field.Value.Type))) return false;

            foreach (var method in iface.Methods.Keys)
            {
                var field = table.Fields.FirstOrDefault(candidate => !candidate.IsMeta && candidate.Name.Name == method);
                if (field == null || field.Type.Kind is not (TypeKind.Function or TypeKind.PrimitiveFunction or TypeKind.PrimitiveAny)) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a function fits where <paramref name="dst"/> is expected: it accepts every argument
    /// a caller passes, so each of its parameters takes the matching parameter type, and what it
    /// returns fits the expected return type. It may declare fewer parameters, as a Lua callback
    /// that ignores trailing arguments does.
    /// </summary>
    private bool IsFunctionAssignable(PassContext pc, FunctionType dst, FunctionType src)
    {
        if (src.ParamTypes.Count > dst.ParamTypes.Count && !dst.IsVararg) return false;

        for (var i = 0; i < src.ParamTypes.Count && i < dst.ParamTypes.Count; i++)
        {
            if (!IsTypeAssignable(pc, src.ParamTypes[i].ID, dst.ParamTypes[i].ID)) return false;
        }

        return IsTypeAssignable(pc, dst.ReturnType.ID, src.ReturnType.ID);
    }

    /// <summary>
    /// Assignability for a destination tuple whose last field is a variadic tail
    /// (e.g. <c>(string, ...number)</c>): the fixed prefix must match positionally and every
    /// remaining source element must be assignable to the tail element type.
    /// </summary>
    private bool TupleWithVariadicTailAssignable(PassContext pc, TupleType dst, TupleType src)
    {
        var fixedCount = dst.Fields.Count - 1;
        var tail = (VariadicType)dst.Fields[^1].Type;

        if (src.Fields.Count < fixedCount) return false;
        for (var i = 0; i < fixedCount; i++)
            if (!IsTypeAssignable(pc, dst.Fields[i].Type.ID, src.Fields[i].Type.ID)) return false;
        for (var i = fixedCount; i < src.Fields.Count; i++)
            if (!IsTypeAssignable(pc, tail.ElementType.ID, src.Fields[i].Type.ID)) return false;
        return true;
    }

    /// <summary>
    /// The type arguments of a class: an instantiation's own, or a generic definition's type
    /// parameters, as which it stands for every instantiation of itself.
    /// </summary>
    private static List<Type> ClassArguments(ClassType classType) =>
        classType.GenericDefinition != null ? classType.TypeArguments : [.. classType.TypeParams];

    private static List<Type> InterfaceArguments(InterfaceType interfaceType) =>
        interfaceType.GenericDefinition != null ? interfaceType.TypeArguments : [.. interfaceType.TypeParams];

    /// <summary>
    /// Whether the type arguments of one instantiation fit those another expects, argument by
    /// argument, the way the elements of a list do.
    /// </summary>
    private bool TypeArgumentsFit(PassContext pc, List<Type> expected, List<Type> actual) =>
        expected.Count != actual.Count || expected.Zip(actual).All(pair => IsTypeAssignable(pc, pair.First.ID, pair.Second.ID));

    private static bool ClassImplementsInterface(ClassType cls, InterfaceType target)
    {
        var current = cls;
        while (current != null)
        {
            foreach (var iface in current.Interfaces)
                if (InterfaceExtendsInterface(iface, target)) return true;
            current = current.BaseClass;
        }
        return false;
    }

    private static bool InterfaceExtendsInterface(InterfaceType iface, InterfaceType target)
    {
        return ReachesThroughBaseInterfaces(iface, target);
    }

    private bool StructEqual(PassContext pc, TypID a, TypID b)
    {
        if (a == b) return true;
        if (!pc.Pkg!.Types.GetByID(a, out var ta)) return false;
        if (!pc.Pkg.Types.GetByID(b, out var tb)) return false;
        if (ta.Kind != tb.Kind) return false;

        switch (ta)
        {
            case TableArrayType aa when tb is TableArrayType ba:
                return aa.ElementType.ID == ba.ElementType.ID;
            case TableMapType am when tb is TableMapType bm:
                return am.KeyType.ID == bm.KeyType.ID && am.ValueType.ID == bm.ValueType.ID;
            case TupleType at when tb is TupleType bt:
                if (at.Fields.Count != bt.Fields.Count) return false;
                for (var i = 0; i < at.Fields.Count; i++)
                {
                    if (at.Fields[i].Type.ID != bt.Fields[i].Type.ID) return false;
                }

                return true;
            case FunctionType af when tb is FunctionType bf:
                if (af.ParamTypes.Count != bf.ParamTypes.Count) return false;
                for (var i = 0; i < af.ParamTypes.Count; i++)
                {
                    if (af.ParamTypes[i].ID != bf.ParamTypes[i].ID) return false;
                }

                return af.ReturnType.ID == bf.ReturnType.ID;
            case StructType sa when tb is StructType sb:
                if (sa.Fields.Count != sb.Fields.Count) return false;
                foreach (var fa in sa.Fields)
                {
                    var fb = sb.Fields.FirstOrDefault(f => f.Name.Name == fa.Name.Name);
                    if (fb == null || fb.Type.ID != fa.Type.ID) return false;
                }

                return true;
            default:
                return false;
        }
    }

    private void EnsureAssignable(PassContext pc, TextSpan span, TypID expected, TypID actual)
    {
        if (IsTypeAssignable(pc, expected, actual)) return;

        if (IsNullable(pc, actual) && IsTypeAssignable(pc, expected, StripNil(pc, actual)))
        {
            EnsureNotNil(pc, span, actual);
            return;
        }

        pc.Diag.Report(span, DiagnosticCode.ErrTypeMismatch, TypeName(pc, expected), TypeName(pc, actual));
    }

    private void EnsureConcatable(PassContext pc, TextSpan span, TypID t)
    {
        var tt = pc.Types;
        t = WithoutLiterals(pc, t);
        if (t == tt.PrimAny.ID || t == tt.PrimString.ID || t == tt.PrimNumber.ID) return;
        if (pc.Pkg!.Types.GetByID(t, out var typ) && typ is EnumType) return;
        pc.Diag.Report(span, DiagnosticCode.ErrTypeMismatch, "string or number", TypeName(pc, t));
    }

    private bool IsConfiguredConcatOp(PassContext pc, BinaryOp op)
    {
        var configured = pc.Config.Code.ConcatOperator;
        if (string.IsNullOrEmpty(configured)) return false;
        var mapped = configured switch
        {
            "+" => BinaryOp.Add,
            "-" => BinaryOp.Sub,
            "*" => BinaryOp.Mul,
            "/" => BinaryOp.Div,
            "//" => BinaryOp.FloorDiv,
            "%" => BinaryOp.Mod,
            "^" => BinaryOp.Pow,
            ".." => BinaryOp.Concat,
            _ => (BinaryOp?)null
        };
        return mapped == op;
    }

    private bool IsNumberLike(PassContext pc, TypID t) =>
        WithoutLiterals(pc, t) == pc.Types.PrimNumber.ID || t == pc.Types.PrimAny.ID;

    private bool IsStringLike(PassContext pc, TypID t) =>
        WithoutLiterals(pc, t) == pc.Types.PrimString.ID || t == pc.Types.PrimAny.ID;

    private bool IsTableLike(PassContext pc, TypID t)
    {
        if (!pc.Pkg!.Types.GetByID(t, out var type)) return false;
        return type.Kind is TypeKind.TableArray or TypeKind.TableMap or TypeKind.Struct;
    }

    /// <summary>
    /// Types a binary operator whose left operand is an instance of a class declaring it: the
    /// overload whose parameter takes the right operand, reported when none does. Runs before the
    /// string concatenation shortcut, since a class may accept a string operand.
    /// </summary>
    private bool TryClassBinaryOperator(PassContext pc, BinaryExpr bin, string metamethod, TypID left, TypID right, out TypID result)
    {
        result = pc.Types.PrimAny.ID;
        if (!pc.Pkg!.Types.GetByID(left, out var leftType) || leftType is not ClassType classType) return false;

        var (overloads, sides) = CollectMethodOverloads(pc, classType, metamethod, staticOnly: false);
        if (overloads.Count == 0) return false;

        var picked = PickOverload(pc, overloads, sides, [right], prefixSelf: true);
        if (picked == null)
        {
            var accepted = overloads.Where(overload => overload.ParamTypes.Count > 1)
                .Select(overload => TypeName(pc, overload.ParamTypes[1].ID)).Distinct();
            pc.Diag.Report(bin.Right.Span, DiagnosticCode.ErrTypeMismatch, string.Join(" or ", accepted), TypeName(pc, right));
            return true;
        }

        result = SpecializedReturn(pc, picked, [left, right]);
        return true;
    }

    private TypID? TryGetMetamethodReturn(PassContext pc, TypID operandType, string metamethodName)
    {
        if (!pc.Pkg!.Types.GetByID(operandType, out var t)) return null;
        if (t is StructType st)
        {
            var metaField = st.Fields.FirstOrDefault(f => f.IsMeta && f.Name.Name == metamethodName);
            if (metaField?.Type is FunctionType ft) return ft.ReturnType.ID;
            return null;
        }
        if (t is ClassType ct)
        {
            var cur = ct;
            while (cur != null)
            {
                if (cur.Methods.TryGetValue(metamethodName, out var fn)) return fn.ReturnType.ID;
                cur = cur.BaseClass;
            }
            return null;
        }
        return null;
    }

    private static string? BinaryOpToMetamethod(BinaryOp op) => op switch
    {
        BinaryOp.Add => "__add",
        BinaryOp.Sub => "__sub",
        BinaryOp.Mul => "__mul",
        BinaryOp.Div => "__div",
        BinaryOp.FloorDiv => "__idiv",
        BinaryOp.Mod => "__mod",
        BinaryOp.Pow => "__pow",
        BinaryOp.Concat => "__concat",
        BinaryOp.Eq => "__eq",
        BinaryOp.Lt => "__lt",
        BinaryOp.Lte => "__le",
        BinaryOp.BitwiseAnd => "__band",
        BinaryOp.BitwiseOr => "__bor",
        BinaryOp.BitwiseXor => "__bxor",
        BinaryOp.LShift => "__shl",
        BinaryOp.RShift => "__shr",
        _ => null
    };

    private static string? UnaryOpToMetamethod(UnaryOp op) => op switch
    {
        UnaryOp.Negate => "__unm",
        UnaryOp.Length => "__len",
        UnaryOp.BitwiseNot => "__bnot",
        _ => null
    };

    private TypID LookupSymbolType(PassContext pc, SymID sym)
    {
        if (sym == SymID.Invalid) return pc.Types.PrimAny.ID;
        if (_narrowed.TryGetValue(new SymPath(sym), out var narrowedType)) return narrowedType;
        if (!pc.Pkg!.Syms.GetByID(sym, out var symbol)) return pc.Types.PrimAny.ID;
        return symbol.Type != TypID.Invalid ? symbol.Type : pc.Types.PrimAny.ID;
    }

    /// <summary>
    /// Builds an AccessPath for an expression if it represents a stable, narrowable location:
    /// a NameExpr (SymPath) or a chain of non-optional dot accesses rooted in a NameExpr (FieldPath).
    /// Returns null for any other shape (calls, indices, optional chains, etc.).
    /// </summary>
    private AccessPath? GetAccessPath(Expr e)
    {
        var cur = e is ParenExpr p ? p.Inner : e;
        switch (cur)
        {
            case NameExpr ne:
                if (ne.Name.Sym == SymID.Invalid) return null;
                return new SymPath(ne.Name.Sym);
            case DotAccessExpr { IsOptional: false } d:
                var baseP = GetAccessPath(d.Object);
                return baseP == null ? null : new FieldPath(baseP, d.FieldName.Name);
            default:
                return null;
        }
    }

    private bool IsNullable(PassContext pc, TypID id)
    {
        if (id == pc.Types.PrimNil.ID) return true;
        if (!pc.Pkg!.Types.GetByID(id, out var t)) return false;
        if (t is UnionType u)
        {
            foreach (var member in u.Types)
            {
                if (member.Kind == TypeKind.PrimitiveNil) return true;
            }
        }

        return false;
    }

    private TypID StripNil(PassContext pc, TypID id)
    {
        if (id == pc.Types.PrimNil.ID) return pc.Types.PrimAny.ID;
        if (!pc.Pkg!.Types.GetByID(id, out var t)) return id;
        if (t is UnionType u)
        {
            var nonNil = u.Types.Where(m => m.Kind != TypeKind.PrimitiveNil).ToList();
            if (nonNil.Count == 0) return pc.Types.PrimAny.ID;
            if (nonNil.Count == 1) return nonNil[0].ID;
            return pc.Types.UnionOf(nonNil);
        }

        return id;
    }

    private TypID MakeNullable(PassContext pc, TypID id)
    {
        if (IsNullable(pc, id)) return id;
        return pc.Types.UnionOf([GetType(pc, id), pc.Types.PrimNil]);
    }

    private bool IsAlwaysNonNil(PassContext pc, TypID id)
    {
        if (id == pc.Types.PrimAny.ID) return true;
        if (id == pc.Types.PrimNil.ID) return false;
        return !IsNullable(pc, id);
    }

    private void EnsureNotNil(PassContext pc, TextSpan span, TypID t)
    {
        if (!pc.Config.Rules.StrictNil) return;
        if (!IsAlwaysNonNil(pc, t))
        {
            pc.Diag.Report(span, DiagnosticCode.ErrPossiblyNil, TypeName(pc, t));
        }
    }

    /// <summary>
    /// Analyzes a condition expression and returns the set of access-path narrowings that apply
    /// in the then-branch and the else-branch respectively. Handles nil checks, `is` checks,
    /// boolean negation (`not`), and conjunctions/disjunctions of those forms.
    /// </summary>
    private (List<(AccessPath path, TypID typ)> thenNarrows, List<(AccessPath path, TypID typ)> elseNarrows)
        AnalyzeCondition(PassContext pc, Expr cond)
    {
        var c = cond is ParenExpr p ? p.Inner : cond;
        var thenN = new List<(AccessPath, TypID)>();
        var elseN = new List<(AccessPath, TypID)>();

        if (c is UnaryExpr un && un.Op == UnaryOp.LogicalNot)
        {
            var (it, ie) = AnalyzeCondition(pc, un.Operand);
            return (ie, it);
        }

        if (c is BinaryExpr binAnd && binAnd.Op == BinaryOp.And)
        {
            var (lt, _) = AnalyzeCondition(pc, binAnd.Left);
            var (rt, _) = AnalyzeCondition(pc, binAnd.Right);
            thenN.AddRange(lt);
            thenN.AddRange(rt);
            return (thenN, elseN);
        }

        if (c is BinaryExpr binOr && binOr.Op == BinaryOp.Or)
        {
            var (_, le) = AnalyzeCondition(pc, binOr.Left);
            var (_, re) = AnalyzeCondition(pc, binOr.Right);
            elseN.AddRange(le);
            elseN.AddRange(re);
            return (thenN, elseN);
        }

        if (c is TypeCheckExpr tchk)
        {
            var path = GetAccessPath(tchk.Inner);
            if (path != null && tchk.TargetType.ResolvedType != TypID.Invalid)
            {
                var current = ResolveAccessPathType(pc, path);
                thenN.Add((path, tchk.TargetType.ResolvedType));
                var subtracted = SubtractType(pc, current, tchk.TargetType.ResolvedType);
                if (subtracted != TypID.Invalid)
                    elseN.Add((path, subtracted));
            }
            return (thenN, elseN);
        }

        if (c is InstanceOfExpr instanceOf)
        {
            NarrowInstanceOf(pc, instanceOf, thenN, elseN);
            return (thenN, elseN);
        }

        // A call to a type-predicate guard (`param is Type`) narrows the argument bound to `param`.
        if (c is FunctionCallExpr predCall
            && pc.Pkg!.Types.GetByID(SynthesizeExpr(pc, predCall.Callee), out var calleeT)
            && calleeT is FunctionType pft && pft.Predicate is { } pred)
        {
            var idx = pft.ParamNames.IndexOf(pred.ParamName);
            if (idx >= 0 && idx < predCall.Arguments.Count)
                NarrowPredicate(pc, GetAccessPath(predCall.Arguments[idx]), pred.TargetType.ID, thenN, elseN);
            return (thenN, elseN);
        }

        if (c is MethodCallExpr predMc
            && pc.Pkg!.Types.GetByID(SynthesizeExpr(pc, predMc.Object), out var recvT))
        {
            var mfn = ResolveMethodOnType(pc, recvT, predMc.MethodName.Name);
            if (mfn?.Predicate is { } mpred)
            {
                var idx = mfn.ParamNames.IndexOf(mpred.ParamName);
                // Param 0 is the implicit `self` (the receiver); later params map to explicit args.
                Expr? subject = idx == 0 ? predMc.Object
                    : idx - 1 >= 0 && idx - 1 < predMc.Arguments.Count ? predMc.Arguments[idx - 1] : null;
                if (subject != null)
                    NarrowPredicate(pc, GetAccessPath(subject), mpred.TargetType.ID, thenN, elseN);
            }
            return (thenN, elseN);
        }

        if (c is BinaryExpr binEq && (binEq.Op == BinaryOp.Eq || binEq.Op == BinaryOp.Neq))
        {
            AccessPath? path = null;
            if (binEq.Right is NilLiteralExpr) path = GetAccessPath(binEq.Left);
            else if (binEq.Left is NilLiteralExpr) path = GetAccessPath(binEq.Right);

            if (path != null)
            {
                var current = ResolveAccessPathType(pc, path);
                if (IsNullable(pc, current))
                {
                    var stripped = StripNil(pc, current);
                    var nilTyp = pc.Types.PrimNil.ID;
                    if (binEq.Op == BinaryOp.Neq)
                    {
                        thenN.Add((path, stripped));
                        elseN.Add((path, nilTyp));
                    }
                    else
                    {
                        thenN.Add((path, nilTyp));
                        elseN.Add((path, stripped));
                    }
                }
            }
            return (thenN, elseN);
        }

        NarrowTruthiness(pc, GetAccessPath(c), thenN, elseN);
        return (thenN, elseN);
    }

    /// <summary>
    /// <c>value instanceof Class</c> narrows the value to the class in the then-branch, and to the
    /// rest of its union in the else-branch.
    /// </summary>
    private void NarrowInstanceOf(PassContext pc, InstanceOfExpr instanceOf, List<(AccessPath, TypID)> thenN, List<(AccessPath, TypID)> elseN)
    {
        var path = GetAccessPath(instanceOf.Inner);
        if (path == null) return;

        var target = instanceOf.TargetType?.ResolvedType ?? TypID.Invalid;
        if (target == TypID.Invalid) target = LookupSymbolType(pc, instanceOf.ClassName.Sym);
        if (target == TypID.Invalid) return;

        thenN.Add((path, target));
        var rest = SubtractType(pc, ResolveAccessPathType(pc, path), target);
        if (rest != TypID.Invalid)
            elseN.Add((path, rest));
    }

    /// <summary>
    /// A value tested on its own, as in <c>if name then</c>, is not nil in the then-branch. In the
    /// else-branch it is nil, unless it could also be <c>false</c>.
    /// </summary>
    private void NarrowTruthiness(PassContext pc, AccessPath? path, List<(AccessPath, TypID)> thenN, List<(AccessPath, TypID)> elseN)
    {
        if (path == null) return;

        var current = ResolveAccessPathType(pc, path);
        if (!IsNullable(pc, current)) return;

        thenN.Add((path, StripNil(pc, current)));
        if (!CanBeFalse(pc, current))
            elseN.Add((path, pc.Types.PrimNil.ID));
    }

    /// <summary>
    /// Returns the type that remains after removing <paramref name="toRemove"/> from <paramref name="src"/>.
    /// Currently supports subtraction from union types only; returns TypID.Invalid when no meaningful
    /// subtraction is possible.
    /// </summary>
    private TypID SubtractType(PassContext pc, TypID src, TypID toRemove)
    {
        if (src == toRemove) return TypID.Invalid;
        if (!pc.Pkg!.Types.GetByID(src, out var srcType)) return TypID.Invalid;
        if (srcType is not UnionType union) return TypID.Invalid;

        var remaining = union.Types.Where(t => t.ID != toRemove).ToList();
        if (remaining.Count == 0) return TypID.Invalid;
        if (remaining.Count == union.Types.Count) return TypID.Invalid;
        if (remaining.Count == 1) return remaining[0].ID;
        return pc.Types.UnionOf(remaining);
    }

    /// <summary>
    /// Resolves the current (possibly narrowed) type for an AccessPath. Walks the field chain
    /// against the underlying StructType if no narrow is registered for the exact path.
    /// </summary>
    private TypID ResolveAccessPathType(PassContext pc, AccessPath path)
    {
        if (_narrowed.TryGetValue(path, out var narrowed)) return narrowed;
        switch (path)
        {
            case SymPath sp:
                if (!pc.Pkg!.Syms.GetByID(sp.Sym, out var sym)) return pc.Types.PrimAny.ID;
                return sym.Type != TypID.Invalid ? sym.Type : pc.Types.PrimAny.ID;
            case FieldPath fp:
            {
                var baseType = ResolveAccessPathType(pc, fp.Base);
                if (!pc.Pkg!.Types.GetByID(baseType, out var t)) return pc.Types.PrimAny.ID;
                if (t is StructType st)
                {
                    var f = st.Fields.FirstOrDefault(x => x.Name.Name == fp.Field);
                    return f?.Type.ID ?? pc.Types.PrimAny.ID;
                }

                return pc.Types.PrimAny.ID;
            }
            default:
                return pc.Types.PrimAny.ID;
        }
    }

    /// <summary>
    /// Verifies that an if/elseif chain without an else branch covers every case of its scrutinee when
    /// matching on a union type via `is` or an enum type via equality with enum members. Emits
    /// <see cref="DiagnosticCode.ErrNonExhaustiveMatch"/> for each missing case. Only runs when
    /// <see cref="RulesSection.ExhaustiveMatch"/> is enabled.
    /// </summary>
    private void CheckExhaustiveMatch(PassContext pc, IfStmt ifStmt)
    {
        var level = pc.Config.Rules.ExhaustiveMatch;
        if (level == ExhaustiveMatchLevel.None) return;
        if (level == ExhaustiveMatchLevel.Relaxed && ifStmt.ElseBody != null) return;

        var conditions = new List<Expr>(1 + ifStmt.ElseIfs.Count) { ifStmt.Condition };
        foreach (var ei in ifStmt.ElseIfs) conditions.Add(ei.Condition);

        AccessPath? scrutPath = null;
        var cases = new List<MatchCase>(conditions.Count);

        foreach (var cond in conditions)
        {
            var extracted = ExtractMatchCase(pc, cond);
            if (extracted == null) return;
            var (path, mc) = extracted.Value;
            if (scrutPath == null) scrutPath = path;
            else if (!scrutPath.Equals(path)) return;
            cases.Add(mc);
        }

        if (scrutPath == null || cases.Count == 0) return;

        if (cases.All(c => c is TypeMatchCase))
        {
            var scrutType = ResolveAccessPathType(pc, scrutPath);
            if (!pc.Pkg!.Types.GetByID(scrutType, out var t) || t is not UnionType union) return;

            var covered = cases.Cast<TypeMatchCase>().Select(c => c.TargetType).ToHashSet();
            var missing = union.Types.Where(m => !covered.Contains(m.ID)).ToList();
            if (missing.Count == 0) return;

            var missingNames = string.Join(", ", missing.Select(m => m.Key.Value));
            pc.Diag.Report(ifStmt.Span, DiagnosticCode.ErrNonExhaustiveMatch,
                union.Key.Value, missingNames);
            return;
        }

        if (cases.All(c => c is EnumMemberMatchCase))
        {
            var first = (EnumMemberMatchCase)cases[0];
            if (cases.Cast<EnumMemberMatchCase>().Any(c => c.EnumTypeId != first.EnumTypeId)) return;
            if (!pc.Pkg!.Types.GetByID(first.EnumTypeId, out var t) || t is not EnumType enumType) return;

            var covered = cases.Cast<EnumMemberMatchCase>().Select(c => c.Member).ToHashSet();
            var missing = enumType.Members.Where(m => !covered.Contains(m.Name)).ToList();
            if (missing.Count == 0) return;

            var missingNames = string.Join(", ", missing.Select(m => enumType.Name + "." + m.Name));
            pc.Diag.Report(ifStmt.Span, DiagnosticCode.ErrNonExhaustiveMatch,
                enumType.Name, missingNames);
        }
    }

    private void CheckExhaustiveMatch(PassContext pc, MatchStmt matchStmt)
    {
        CheckExhaustivePatterns(pc, matchStmt.Scrutinee.Type, matchStmt.Span,
            matchStmt.Arms.Select(a => a.Pattern).ToList());
    }

    /// <summary>
    /// The exhaustiveness check shared by the <c>match</c> statement and the <c>match</c>
    /// expression. Both carry the same patterns, and an expression that matches nothing yields
    /// nil, so a partial one is exactly as wrong there as in the statement form.
    /// </summary>
    private void CheckExhaustivePatterns(PassContext pc, TypID scrutinee, TextSpan span,
        List<MatchPattern> patterns)
    {
        var level = pc.Config.Rules.ExhaustiveMatch;
        if (level == ExhaustiveMatchLevel.None) return;

        var hasWildcard = patterns.Any(p => p.Kind == MatchPatternKind.Wildcard);
        if (level == ExhaustiveMatchLevel.Relaxed && hasWildcard) return;

        var scrutType = scrutinee;
        if (scrutType == TypID.Invalid) return;
        if (!pc.Pkg!.Types.GetByID(scrutType, out var t)) return;

        if (t is EnumType enumType)
        {
            if (level == ExhaustiveMatchLevel.Explicit && hasWildcard)
            {
                var allMembers = string.Join(", ", enumType.Members.Select(m => enumType.Name + "." + m.Name));
                pc.Diag.Report(span, DiagnosticCode.ErrNonExhaustiveMatch,
                    enumType.Name, allMembers + " (wildcard not allowed in explicit mode)");
                return;
            }

            var covered = new HashSet<string>();
            foreach (var pattern in patterns)
            {
                if (pattern.Kind == MatchPatternKind.Value && pattern.ValueExpr is DotAccessExpr dot)
                    covered.Add(dot.FieldName.Name);
                if (pattern.Kind == MatchPatternKind.Value && pattern.ValueExpr is NameExpr { EnumMember: { } member })
                    covered.Add(member.Name);
            }

            var missing = enumType.Members.Where(m => !covered.Contains(m.Name)).ToList();
            if (missing.Count == 0 || hasWildcard) return;

            var missingNames = string.Join(", ", missing.Select(m => enumType.Name + "." + m.Name));
            pc.Diag.Report(span, DiagnosticCode.ErrNonExhaustiveMatch,
                enumType.Name, missingNames);
            return;
        }

        if (t is UnionType union)
        {
            if (level == ExhaustiveMatchLevel.Explicit && hasWildcard) return;

            var covered = new HashSet<TypID>();
            foreach (var pattern in patterns)
            {
                if (pattern.Kind == MatchPatternKind.TypeBinding && pattern.TypeRef != null)
                    covered.Add(pattern.TypeRef.ResolvedType);
            }

            var missing = union.Types.Where(m => !covered.Contains(m.ID)).ToList();
            if (missing.Count == 0 || hasWildcard) return;

            var missingNames = string.Join(", ", missing.Select(m => m.Key.Value));
            pc.Diag.Report(span, DiagnosticCode.ErrNonExhaustiveMatch,
                union.Key.Value, missingNames);
        }
    }

    private void CheckMatchPatternType(PassContext pc, TypID scrutType, TypID patternType, TextSpan span)
    {
        if (scrutType == TypID.Invalid || patternType == TypID.Invalid) return;
        if (scrutType == pc.Types.PrimAny.ID || patternType == pc.Types.PrimAny.ID) return;
        if (IsTypeAssignable(pc, scrutType, patternType)) return;
        if (IsTypeAssignable(pc, patternType, scrutType)) return;

        pc.Diag.Report(span, DiagnosticCode.ErrTypeMismatch,
            TypeName(pc, scrutType), TypeName(pc, patternType));
    }

    private void CheckMatchPatternTypeBinding(PassContext pc, TypID scrutType, TypeRef typeRef, TextSpan span)
    {
        if (scrutType == TypID.Invalid || typeRef.ResolvedType == TypID.Invalid) return;
        if (scrutType == pc.Types.PrimAny.ID) return;

        if (!pc.Pkg!.Types.GetByID(scrutType, out var st)) return;
        if (st is UnionType union)
        {
            if (union.Types.Any(m => IsTypeAssignable(pc, m.ID, typeRef.ResolvedType))) return;
        }
        else
        {
            if (IsTypeAssignable(pc, scrutType, typeRef.ResolvedType)) return;
            if (IsTypeAssignable(pc, typeRef.ResolvedType, scrutType)) return;
        }

        pc.Diag.Report(span, DiagnosticCode.ErrTypeMismatch,
            TypeName(pc, scrutType), TypeName(pc, typeRef.ResolvedType));
    }

    /// <summary>
    /// Extracts a single match case from a branch condition. Returns null if the condition is not a
    /// recognized match form (type test `x is T` or enum equality `x == Enum.Member`).
    /// </summary>
    private (AccessPath path, MatchCase mc)? ExtractMatchCase(PassContext pc, Expr cond)
    {
        var c = cond is ParenExpr p ? p.Inner : cond;

        if (c is TypeCheckExpr tchk)
        {
            var path = GetAccessPath(tchk.Inner);
            if (path == null || tchk.TargetType.ResolvedType == TypID.Invalid) return null;
            return (path, new TypeMatchCase(tchk.TargetType.ResolvedType));
        }

        if (c is BinaryExpr bin && bin.Op == BinaryOp.Eq)
        {
            if (bin.Right is DotAccessExpr rd && IsEnumMemberRef(pc, rd))
            {
                var path = GetAccessPath(bin.Left);
                if (path == null) return null;
                return (path, new EnumMemberMatchCase(rd.Type, rd.FieldName.Name));
            }

            if (bin.Left is DotAccessExpr ld && IsEnumMemberRef(pc, ld))
            {
                var path = GetAccessPath(bin.Right);
                if (path == null) return null;
                return (path, new EnumMemberMatchCase(ld.Type, ld.FieldName.Name));
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true when the dot access refers to a member of an enum type (its expression type is
    /// an <see cref="EnumType"/>).
    /// </summary>
    private bool IsEnumMemberRef(PassContext pc, DotAccessExpr dot)
    {
        if (dot.Type == TypID.Invalid) return false;
        return pc.Pkg!.Types.GetByID(dot.Type, out var t) && t is EnumType;
    }

    /// <summary>
    /// Applies a batch of access-path narrowings, returning a snapshot for restoration via PopAllNarrows.
    /// </summary>
    private List<(AccessPath path, TypID prev, bool hadPrev)> PushAllNarrows(List<(AccessPath path, TypID typ)> narrows)
    {
        var saved = new List<(AccessPath, TypID, bool)>();
        foreach (var (path, typ) in narrows)
        {
            var hadPrev = _narrowed.TryGetValue(path, out var prev);
            _narrowed[path] = typ;
            saved.Add((path, hadPrev ? prev : TypID.Invalid, hadPrev));
        }
        return saved!;
    }

    /// <summary>
    /// Restores narrowings captured by PushAllNarrows, in reverse order.
    /// </summary>
    private void PopAllNarrows(List<(AccessPath path, TypID prev, bool hadPrev)> saved)
    {
        for (var i = saved.Count - 1; i >= 0; i--)
        {
            var (path, prev, hadPrev) = saved[i];
            if (hadPrev) _narrowed[path] = prev;
            else _narrowed.Remove(path);
        }
    }

    private Type GetType(PassContext pc, TypID id)
    {
        if (id == TypID.Invalid) return pc.Types.PrimAny;
        return pc.Pkg!.Types.GetByID(id, out var t) ? t : pc.Types.PrimAny;
    }

    private string TypeName(PassContext pc, TypID id)
    {
        if (id == TypID.Invalid) return "<invalid>";
        return pc.Pkg!.Types.GetByID(id, out var t) ? FormatTypeName(t) : "<unknown>";
    }

    /// <summary>
    /// Renders a type as source-like text for diagnostics (e.g. <c>string</c>, <c>number[]</c>,
    /// <c>(number) -&gt; string</c>, <c>Foo</c>) instead of the internal type key.
    /// </summary>
    private static string FormatTypeName(Type t)
    {
        switch (t)
        {
            case TableArrayType arr: return Type.Grouped(arr.ElementType, FormatTypeName(arr.ElementType)) + "[]";
            case TableMapType m: return $"{{ [{FormatTypeName(m.KeyType)}]: {FormatTypeName(m.ValueType)} }}";
            case UnionType u: return string.Join(" | ", u.Types.Select(member => Type.Grouped(member, FormatTypeName(member))));
            case TupleType tup: return "(" + string.Join(", ", tup.Fields.Select(f => FormatTypeName(f.Type))) + ")";
            case VariadicType v: return "..." + Type.Grouped(v.ElementType, FormatTypeName(v.ElementType));
            case FunctionType ft:
            {
                var ps = string.Join(", ", ft.ParamTypes.Select(FormatTypeName));
                var ret = ft.Predicate != null
                    ? $"{ft.Predicate.ParamName} is {FormatTypeName(ft.Predicate.TargetType)}"
                    : FormatTypeName(ft.ReturnType);
                return $"({ps}) -> {ret}";
            }
            case ClassType c: return Type.WithTypeArguments(c.Name, c.ShownArguments, FormatTypeName);
            case InterfaceType i: return Type.WithTypeArguments(i.Name, i.ShownArguments, FormatTypeName);
            case EnumType e: return e.Name;
            case TypeParameterType tp: return tp.Name;
            case LiteralType or KeyOfType or IndexedAccessType or UtilityType: return Type.FormatComputed(t, FormatTypeName)!;
            case StructType s: return "{ " + string.Join(", ", s.Fields.Select(f => $"{Type.FieldLabel(f.Name.Name)}: {FormatTypeName(f.Type)}")) + " }";
            case ParameterizedType pt: return $"{FormatTypeName(pt.Definition)}<{string.Join(", ", pt.Args.Select(a => a.ToString()))}>";
            default:
                return t.Kind switch
                {
                    TypeKind.PrimitiveNil => "nil",
                    TypeKind.PrimitiveAny => "any",
                    TypeKind.PrimitiveNumber => "number",
                    TypeKind.PrimitiveBool => "boolean",
                    TypeKind.PrimitiveString => "string",
                    TypeKind.PrimitiveFunction => "function",
                    TypeKind.PrimitiveThread => "thread",
                    TypeKind.PrimitiveUserdata => "userdata",
                    TypeKind.PrimitiveNever => "never",
                    _ => "any"
                };
        }
    }
}