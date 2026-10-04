using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

/// <summary>
/// Per-file pass that enforces <c>@side</c> on individual class / interface
/// members. Runs after <see cref="InferTypesPass"/> so receiver types are
/// resolved on every <see cref="DotAccessExpr"/> / <see cref="MethodCallExpr"/>
/// / <see cref="NewExpr"/>. Looks the resolved member up on the receiver
/// class/interface and reports <see cref="DiagnosticCode.ErrSymbolWrongSide"/>
/// when the member's side doesn't cover the file's mask.
/// </summary>
/// <remarks>
/// Companion to <see cref="CheckSidesPass"/>: that pass handles top-level
/// <see cref="NameRef"/>s (typed before inference); this one handles member
/// access (typed after inference). Both no-op when the project has no
/// <c>[sides]</c> config or when the file's mask is <see cref="Side.All"/>.
/// </remarks>
public sealed class CheckMemberSidesPass()
    : Pass(PassName, PassScope.PerFile, noErrors: false, InferTypesPass.PassName)
{
    public const string PassName = "CheckMemberSides";

    public override bool Run(PassContext context)
    {
        if (context.Pkg == null || context.File == null) return true;
        if (context.Config.Sides.Count == 0) return true;

        var fileMask = SidesResolver.ResolveFileSide(
            context.Config.Sides,
            context.File.Filename ?? "",
            context.Config.ProjectRoot);
        if (fileMask == Side.All) return true;

        VisitStmtList(context, context.File.Hir.Body, fileMask);
        if (context.File.Hir.Return != null)
            VisitStmt(context, context.File.Hir.Return, fileMask);
        return true;
    }

    private static void VisitStmtList(PassContext ctx, List<Stmt> stmts, Side fileMask)
    {
        foreach (var stmt in stmts) VisitStmt(ctx, stmt, fileMask);
    }

    private static void VisitStmt(PassContext ctx, Stmt stmt, Side fileMask)
    {
        switch (stmt)
        {
            case ExportStmt ex: VisitDecl(ctx, ex.Declaration, fileMask); break;
            case Decl d: VisitDecl(ctx, d, fileMask); break;
            case ExprStmt es: VisitExpr(ctx, es.Expression, fileMask); break;
            case AssignStmt asg:
                foreach (var t in asg.Targets) VisitExpr(ctx, t, fileMask);
                foreach (var v in asg.Values) VisitExpr(ctx, v, fileMask);
                break;
            case DoBlockStmt db: VisitStmtList(ctx, db.Body, fileMask); break;
            case WhileStmt w:
                VisitExpr(ctx, w.Condition, fileMask);
                VisitStmtList(ctx, w.Body, fileMask);
                break;
            case RepeatStmt r:
                VisitStmtList(ctx, r.Body, fileMask);
                VisitExpr(ctx, r.Condition, fileMask);
                break;
            case IfStmt ifs:
                VisitExpr(ctx, ifs.Condition, fileMask);
                VisitStmtList(ctx, ifs.Body, fileMask);
                foreach (var clause in ifs.ElseIfs)
                {
                    VisitExpr(ctx, clause.Condition, fileMask);
                    VisitStmtList(ctx, clause.Body, fileMask);
                }
                if (ifs.ElseBody != null) VisitStmtList(ctx, ifs.ElseBody, fileMask);
                break;
            case NumericForStmt nf:
                VisitExpr(ctx, nf.Start, fileMask);
                VisitExpr(ctx, nf.Limit, fileMask);
                if (nf.Step != null) VisitExpr(ctx, nf.Step, fileMask);
                VisitStmtList(ctx, nf.Body, fileMask);
                break;
            case GenericForStmt gf:
                foreach (var it in gf.Iterators) VisitExpr(ctx, it, fileMask);
                VisitStmtList(ctx, gf.Body, fileMask);
                break;
            case ReturnStmt rs:
                foreach (var v in rs.Values) VisitExpr(ctx, v, fileMask);
                break;
            case MatchStmt ms:
                VisitExpr(ctx, ms.Scrutinee, fileMask);
                foreach (var arm in ms.Arms)
                {
                    if (arm.Pattern.ValueExpr != null) VisitExpr(ctx, arm.Pattern.ValueExpr, fileMask);
                    if (arm.Guard != null) VisitExpr(ctx, arm.Guard, fileMask);
                    VisitStmtList(ctx, arm.Body, fileMask);
                }
                break;
            case DeferStmt ds:
                if (ds.Call != null) VisitExpr(ctx, ds.Call, fileMask);
                if (ds.Block != null) VisitStmtList(ctx, ds.Block, fileMask);
                break;
            case GuardStmt gs:
                VisitExpr(ctx, gs.Condition, fileMask);
                if (gs.ElseExpr != null) VisitExpr(ctx, gs.ElseExpr, fileMask);
                break;
        }
    }

    private static void VisitDecl(PassContext ctx, Decl decl, Side fileMask)
    {
        switch (decl)
        {
            case FunctionDecl fd:
                foreach (var p in fd.Parameters)
                    if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                VisitStmtList(ctx, fd.Body, fileMask);
                if (fd.ReturnStmt != null) VisitStmt(ctx, fd.ReturnStmt, fileMask);
                break;
            case LocalFunctionDecl lfd:
                foreach (var p in lfd.Parameters)
                    if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                VisitStmtList(ctx, lfd.Body, fileMask);
                if (lfd.ReturnStmt != null) VisitStmt(ctx, lfd.ReturnStmt, fileMask);
                break;
            case LocalDecl ld:
                foreach (var e in ld.Values) VisitExpr(ctx, e, fileMask);
                break;
            case ClassDecl cd:
                foreach (var f in cd.Fields)
                    if (f.DefaultValue != null) VisitExpr(ctx, f.DefaultValue, fileMask);
                if (cd.Constructor != null)
                {
                    foreach (var p in cd.Constructor.Parameters)
                        if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                    VisitStmtList(ctx, cd.Constructor.Body, fileMask);
                    if (cd.Constructor.ReturnStmt != null) VisitStmt(ctx, cd.Constructor.ReturnStmt, fileMask);
                }
                foreach (var m in cd.Methods)
                {
                    foreach (var p in m.Parameters)
                        if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                    VisitStmtList(ctx, m.Body, fileMask);
                    if (m.ReturnStmt != null) VisitStmt(ctx, m.ReturnStmt, fileMask);
                }
                foreach (var a in cd.Accessors)
                {
                    foreach (var p in a.Parameters)
                        if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                    VisitStmtList(ctx, a.Body, fileMask);
                }
                break;
        }
    }

    private static void VisitExpr(PassContext ctx, Expr? expr, Side fileMask)
    {
        if (expr == null) return;
        switch (expr)
        {
            case DotAccessExpr dot:
                VisitExpr(ctx, dot.Object, fileMask);
                CheckMember(ctx, dot.Object, dot.FieldName, fileMask, isMethodCall: false);
                break;
            case MethodCallExpr mc:
                VisitExpr(ctx, mc.Object, fileMask);
                CheckMember(ctx, mc.Object, mc.MethodName, fileMask, isMethodCall: true);
                foreach (var a in mc.Arguments) VisitExpr(ctx, a, fileMask);
                break;
            case NewExpr nw:
                CheckConstructor(ctx, nw, fileMask);
                foreach (var a in nw.Arguments) VisitExpr(ctx, a, fileMask);
                break;
            case FunctionCallExpr fc:
                VisitExpr(ctx, fc.Callee, fileMask);
                foreach (var a in fc.Arguments) VisitExpr(ctx, a, fileMask);
                break;
            case IndexAccessExpr idx:
                VisitExpr(ctx, idx.Object, fileMask);
                VisitExpr(ctx, idx.Index, fileMask);
                break;
            case BinaryExpr b:
                VisitExpr(ctx, b.Left, fileMask);
                VisitExpr(ctx, b.Right, fileMask);
                break;
            case UnaryExpr u: VisitExpr(ctx, u.Operand, fileMask); break;
            case ParenExpr p: VisitExpr(ctx, p.Inner, fileMask); break;
            case TableConstructorExpr tc:
                foreach (var f in tc.Fields)
                {
                    if (f.Key != null) VisitExpr(ctx, f.Key, fileMask);
                    VisitExpr(ctx, f.Value, fileMask);
                }
                break;
            case InterpolatedStringExpr iss:
                foreach (var part in iss.Parts)
                    if (part is InterpExprPart ep) VisitExpr(ctx, ep.Expression, fileMask);
                break;
            case FunctionDefExpr fde:
                foreach (var p in fde.Parameters)
                    if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                VisitStmtList(ctx, fde.Body, fileMask);
                if (fde.ReturnStmt != null) VisitStmt(ctx, fde.ReturnStmt, fileMask);
                break;
            case NonNilAssertExpr nna: VisitExpr(ctx, nna.Inner, fileMask); break;
            case IncDecExpr inc: VisitExpr(ctx, inc.Target, fileMask); break;
            case TypeCheckExpr tc2: VisitExpr(ctx, tc2.Inner, fileMask); break;
            case TypeCastExpr tcx: VisitExpr(ctx, tcx.Inner, fileMask); break;
            case TypeOfExpr to: VisitExpr(ctx, to.Inner, fileMask); break;
            case InstanceOfExpr io: VisitExpr(ctx, io.Inner, fileMask); break;
            case MatchExpr me:
                VisitExpr(ctx, me.Scrutinee, fileMask);
                foreach (var arm in me.Arms)
                {
                    if (arm.Pattern.ValueExpr != null) VisitExpr(ctx, arm.Pattern.ValueExpr, fileMask);
                    if (arm.Guard != null) VisitExpr(ctx, arm.Guard, fileMask);
                    VisitExpr(ctx, arm.Value, fileMask);
                }
                break;
            case AwaitExpr aw: VisitExpr(ctx, aw.Expression, fileMask); break;
            case SuperCallExpr sc:
                foreach (var a in sc.Arguments) VisitExpr(ctx, a, fileMask);
                break;
        }
    }

    /// <summary>
    /// Resolves the receiver's class/interface type (unwrapping nullable and
    /// parameterized types) and looks up the member's side mask. When the
    /// member belongs to an inherited type we walk up the base chain so a
    /// <c>@side(server)</c> on <c>Entity.Kick</c> covers <c>Player.Kick</c>
    /// too without needing to re-annotate.
    /// </summary>
    private static void CheckMember(PassContext ctx, Expr receiver, NameRef memberName, Side fileMask, bool isMethodCall)
    {
        var pkg = ctx.Pkg!;
        if (!pkg.Types.GetByID(receiver.Type, out var recvType)) return;

        // Unwrap nullable / parameterized to the underlying definition.
        recvType = Unwrap(recvType);

        var isClassRef = receiver is NameExpr ne
            && pkg.Syms.GetByID(ne.Name.Sym, out var s)
            && s.Kind == SymbolKind.Class;

        if (recvType is ClassType ct)
        {
            // Locate the class in the inheritance chain where the member is
            // actually defined, then check THAT class's side map. A child
            // class that shadows a member without an explicit @side means the
            // override is unrestricted — we must not lift the parent's
            // restriction up through the shadow.
            for (var cur = ct; cur != null; cur = cur.BaseClass)
            {
                var name = memberName.Name;
                if (isClassRef && cur.StaticMethods.ContainsKey(name))
                {
                    if (AnyOverloadAccessible(cur.StaticMethodOverloadSides, name, fileMask)) return;
                    cur.StaticMethodSides.TryGetValue(name, out var ss);
                    Report(ctx, memberName, fileMask, ss, cur.Name, name);
                    return;
                }
                if (cur.InstanceFields.ContainsKey(name))
                {
                    cur.FieldSides.TryGetValue(name, out var fs);
                    Report(ctx, memberName, fileMask, fs, cur.Name, name);
                    return;
                }
                if (cur.Methods.ContainsKey(name))
                {
                    if (AnyOverloadAccessible(cur.MethodOverloadSides, name, fileMask)) return;
                    cur.MethodSides.TryGetValue(name, out var ms);
                    Report(ctx, memberName, fileMask, ms, cur.Name, name);
                    return;
                }
                if (cur.Getters.ContainsKey(name))
                {
                    cur.GetterSides.TryGetValue(name, out var gs);
                    Report(ctx, memberName, fileMask, gs, cur.Name, name);
                    return;
                }
                if (cur.StaticMethods.ContainsKey(name))
                {
                    if (AnyOverloadAccessible(cur.StaticMethodOverloadSides, name, fileMask)) return;
                    cur.StaticMethodSides.TryGetValue(name, out var ss2);
                    Report(ctx, memberName, fileMask, ss2, cur.Name, name);
                    return;
                }
            }
        }
        else if (recvType is InterfaceType it)
        {
            if (CheckInterfaceMember(ctx, memberName, fileMask, it)) return;
            // Walk base interfaces breadth-first.
            var visited = new HashSet<InterfaceType>();
            var queue = new Queue<InterfaceType>(it.BaseInterfaces);
            while (queue.TryDequeue(out var bi))
            {
                if (!visited.Add(bi)) continue;
                if (CheckInterfaceMember(ctx, memberName, fileMask, bi)) return;
                foreach (var p in bi.BaseInterfaces) queue.Enqueue(p);
            }
        }
    }

    private static bool CheckInterfaceMember(PassContext ctx, NameRef memberName, Side fileMask, InterfaceType it)
    {
        var name = memberName.Name;
        if (it.Methods.ContainsKey(name))
        {
            if (AnyOverloadAccessible(it.MethodOverloadSides, name, fileMask)) return true;
            it.MethodSides.TryGetValue(name, out var ms);
            Report(ctx, memberName, fileMask, ms, it.Name, name);
            return true;
        }
        if (it.Fields.ContainsKey(name))
        {
            it.FieldSides.TryGetValue(name, out var fs);
            Report(ctx, memberName, fileMask, fs, it.Name, name);
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when any overload of <paramref name="name"/> in
    /// <paramref name="overloadSides"/> is reachable from
    /// <paramref name="fileMask"/>. An overloaded method with at least one
    /// side-appropriate variant is OK at the call site — <see cref="InferTypesPass"/>'s
    /// overload dispatcher will pick that variant and surface a real diagnostic
    /// (param-count / type mismatch) if no overload actually matches.
    /// </summary>
    private static bool AnyOverloadAccessible(Dictionary<string, List<Side>> overloadSides,
        string name, Side fileMask)
    {
        if (!overloadSides.TryGetValue(name, out var sides) || sides.Count <= 1) return false;
        foreach (var s in sides)
        {
            if (s == Side.None || s == Side.All) return true;
            if (s.IsAccessibleFrom(fileMask)) return true;
        }
        return false;
    }

    private static void CheckConstructor(PassContext ctx, NewExpr nw, Side fileMask)
    {
        if (nw.ClassName.Sym == SymID.Invalid) return;
        var pkg = ctx.Pkg!;
        if (!pkg.Syms.GetByID(nw.ClassName.Sym, out var sym)) return;
        if (!pkg.Types.GetByID(sym.Type, out var raw)) return;
        if (Unwrap(raw) is not ClassType ct) return;
        var side = ct.ConstructorSide;
        if (side == Side.All) return;
        if (side.IsAccessibleFrom(fileMask)) return;
        ctx.Diag.Report(nw.ClassName.Span, DiagnosticCode.ErrSymbolWrongSide,
            $"{ct.Name}.constructor", side.Format(), fileMask.Format());
    }

    private static Type Unwrap(Type t)
    {
        // `T?` is encoded as `T | nil` (a UnionType) — pull out the non-nil
        // member so member lookup hits the class/interface. ParameterizedType
        // is type-erased here; the side maps live on the raw definition.
        if (t is UnionType u)
        {
            foreach (var member in u.Types)
                if (member.Kind != TypeKind.PrimitiveNil)
                    return Unwrap(member);
        }
        if (t is ParameterizedType pt) return pt.Definition;
        return t;
    }

    private static void Report(PassContext ctx, NameRef memberName, Side fileMask, Side memberSide, string typeName, string member)
    {
        // `Side.None` (= 0) is the dictionary default for a key that was not
        // explicitly stamped — treat as unrestricted.
        if (memberSide == Side.None || memberSide == Side.All) return;
        if (memberSide.IsAccessibleFrom(fileMask)) return;
        ctx.Diag.Report(memberName.Span, DiagnosticCode.ErrSymbolWrongSide,
            $"{typeName}.{member}", memberSide.Format(), fileMask.Format());
    }
}
