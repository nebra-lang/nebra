using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;

namespace Nebra.Compiler.Passes;

/// <summary>
/// Per-file pass that enforces <c>@side</c> visibility against the per-file
/// side mask resolved from <c>[sides]</c> globs in <c>nebra.toml</c>. Walks every
/// resolved <see cref="NameRef"/> reachable from the file's body and emits
/// <see cref="DiagnosticCode.ErrSymbolWrongSide"/> when the referenced
/// symbol's side mask doesn't cover the file's mask. Files outside any
/// configured glob (default mask = <see cref="Side.All"/>) and unannotated
/// symbols (also <see cref="Side.All"/>) are permissive — this means
/// projects without <c>[sides]</c> or without any <c>@side</c> annotations
/// behave exactly as before.
/// </summary>
public sealed class CheckSidesPass()
    : Pass(PassName, PassScope.PerFile, noErrors: false, ResolveTypeRefsPass.PassName)
{
    public const string PassName = "CheckSides";

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
                    if (arm.Pattern.TypeRef != null) VisitTypeRef(ctx, arm.Pattern.TypeRef, fileMask);
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
                {
                    if (p.TypeAnnotation != null) VisitTypeRef(ctx, p.TypeAnnotation, fileMask);
                    if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                }
                if (fd.ReturnType != null) VisitTypeRef(ctx, fd.ReturnType, fileMask);
                VisitStmtList(ctx, fd.Body, fileMask);
                if (fd.ReturnStmt != null) VisitStmt(ctx, fd.ReturnStmt, fileMask);
                break;
            case LocalFunctionDecl lfd:
                foreach (var p in lfd.Parameters)
                {
                    if (p.TypeAnnotation != null) VisitTypeRef(ctx, p.TypeAnnotation, fileMask);
                    if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                }
                if (lfd.ReturnType != null) VisitTypeRef(ctx, lfd.ReturnType, fileMask);
                VisitStmtList(ctx, lfd.Body, fileMask);
                if (lfd.ReturnStmt != null) VisitStmt(ctx, lfd.ReturnStmt, fileMask);
                break;
            case LocalDecl ld:
                foreach (var v in ld.Variables)
                    if (v.TypeAnnotation != null) VisitTypeRef(ctx, v.TypeAnnotation, fileMask);
                foreach (var e in ld.Values) VisitExpr(ctx, e, fileMask);
                break;
            case ClassDecl cd:
                if (cd.BaseClass != null) CheckNameRef(ctx, cd.BaseClass, fileMask);
                foreach (var iface in cd.Interfaces) CheckNameRef(ctx, iface, fileMask);
                foreach (var f in cd.Fields)
                {
                    if (f.TypeAnnotation != null) VisitTypeRef(ctx, f.TypeAnnotation, fileMask);
                    if (f.DefaultValue != null) VisitExpr(ctx, f.DefaultValue, fileMask);
                }
                if (cd.Constructor != null)
                {
                    foreach (var p in cd.Constructor.Parameters)
                    {
                        if (p.TypeAnnotation != null) VisitTypeRef(ctx, p.TypeAnnotation, fileMask);
                        if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                    }
                    VisitStmtList(ctx, cd.Constructor.Body, fileMask);
                    if (cd.Constructor.ReturnStmt != null) VisitStmt(ctx, cd.Constructor.ReturnStmt, fileMask);
                }
                foreach (var m in cd.Methods)
                {
                    foreach (var p in m.Parameters)
                    {
                        if (p.TypeAnnotation != null) VisitTypeRef(ctx, p.TypeAnnotation, fileMask);
                        if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                    }
                    if (m.ReturnType != null) VisitTypeRef(ctx, m.ReturnType, fileMask);
                    VisitStmtList(ctx, m.Body, fileMask);
                    if (m.ReturnStmt != null) VisitStmt(ctx, m.ReturnStmt, fileMask);
                }
                foreach (var a in cd.Accessors)
                {
                    foreach (var p in a.Parameters)
                        if (p.TypeAnnotation != null) VisitTypeRef(ctx, p.TypeAnnotation, fileMask);
                    if (a.ReturnType != null) VisitTypeRef(ctx, a.ReturnType, fileMask);
                    VisitStmtList(ctx, a.Body, fileMask);
                }
                break;
            case InterfaceDecl id:
                foreach (var bi in id.BaseInterfaces) CheckNameRef(ctx, bi, fileMask);
                foreach (var f in id.Fields) VisitTypeRef(ctx, f.TypeAnnotation, fileMask);
                foreach (var m in id.Methods)
                {
                    foreach (var p in m.Parameters)
                        if (p.TypeAnnotation != null) VisitTypeRef(ctx, p.TypeAnnotation, fileMask);
                    if (m.ReturnType != null) VisitTypeRef(ctx, m.ReturnType, fileMask);
                    if (m.Body == null) continue;
                    VisitStmtList(ctx, m.Body, fileMask);
                    if (m.ReturnStmt != null) VisitStmt(ctx, m.ReturnStmt, fileMask);
                }
                break;
            case ExtendDecl ed:
                VisitTypeRef(ctx, ed.TargetType, fileMask);
                foreach (var m in ed.Methods)
                {
                    foreach (var p in m.Parameters)
                    {
                        if (p.TypeAnnotation != null) VisitTypeRef(ctx, p.TypeAnnotation, fileMask);
                        if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                    }
                    if (m.ReturnType != null) VisitTypeRef(ctx, m.ReturnType, fileMask);
                    VisitStmtList(ctx, m.Body, fileMask);
                    if (m.ReturnStmt != null) VisitStmt(ctx, m.ReturnStmt, fileMask);
                }
                break;
        }
    }

    private static void VisitExpr(PassContext ctx, Expr? expr, Side fileMask)
    {
        if (expr == null) return;
        switch (expr)
        {
            case NameExpr ne:
                CheckNameRef(ctx, ne.Name, fileMask);
                break;
            case DotAccessExpr dot:
                VisitExpr(ctx, dot.Object, fileMask);
                break;
            case IndexAccessExpr idx:
                VisitExpr(ctx, idx.Object, fileMask);
                VisitExpr(ctx, idx.Index, fileMask);
                break;
            case FunctionCallExpr fc:
                VisitExpr(ctx, fc.Callee, fileMask);
                foreach (var a in fc.Arguments) VisitExpr(ctx, a, fileMask);
                break;
            case MethodCallExpr mc:
                VisitExpr(ctx, mc.Object, fileMask);
                foreach (var a in mc.Arguments) VisitExpr(ctx, a, fileMask);
                break;
            case NewExpr nw:
                CheckNameRef(ctx, nw.ClassName, fileMask);
                foreach (var a in nw.Arguments) VisitExpr(ctx, a, fileMask);
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
                {
                    if (p.TypeAnnotation != null) VisitTypeRef(ctx, p.TypeAnnotation, fileMask);
                    if (p.DefaultValue != null) VisitExpr(ctx, p.DefaultValue, fileMask);
                }
                if (fde.ReturnType != null) VisitTypeRef(ctx, fde.ReturnType, fileMask);
                VisitStmtList(ctx, fde.Body, fileMask);
                if (fde.ReturnStmt != null) VisitStmt(ctx, fde.ReturnStmt, fileMask);
                break;
            case NonNilAssertExpr nna: VisitExpr(ctx, nna.Inner, fileMask); break;
            case IncDecExpr inc: VisitExpr(ctx, inc.Target, fileMask); break;
            case TypeCheckExpr tc2:
                VisitTypeRef(ctx, tc2.TargetType, fileMask);
                VisitExpr(ctx, tc2.Inner, fileMask);
                break;
            case TypeCastExpr tcx:
                VisitTypeRef(ctx, tcx.TargetType, fileMask);
                VisitExpr(ctx, tcx.Inner, fileMask);
                break;
            case TypeOfExpr to: VisitExpr(ctx, to.Inner, fileMask); break;
            case InstanceOfExpr io: VisitExpr(ctx, io.Inner, fileMask); break;
            case MatchExpr me:
                VisitExpr(ctx, me.Scrutinee, fileMask);
                foreach (var arm in me.Arms)
                {
                    if (arm.Pattern.ValueExpr != null) VisitExpr(ctx, arm.Pattern.ValueExpr, fileMask);
                    if (arm.Pattern.TypeRef != null) VisitTypeRef(ctx, arm.Pattern.TypeRef, fileMask);
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

    private static void VisitTypeRef(PassContext ctx, TypeRef? tref, Side fileMask)
    {
        if (tref == null) return;
        switch (tref)
        {
            case NamedTypeRef ntr: CheckNameRef(ctx, ntr.Name, fileMask); break;
            case GenericTypeRef gtr:
                CheckNameRef(ctx, gtr.Name, fileMask);
                foreach (var a in gtr.Arguments)
                {
                    if (a is ConcreteTypeArgRef cta) VisitTypeRef(ctx, cta.Type, fileMask);
                    else if (a is WildcardTypeArgRef wta && wta.Bound != null) VisitTypeRef(ctx, wta.Bound, fileMask);
                }
                break;
            case ArrayTypeRef ar: VisitTypeRef(ctx, ar.ElementType, fileMask); break;
            case NullableTypeRef nr: VisitTypeRef(ctx, nr.InnerType, fileMask); break;
            case UnionTypeRef ur:
                foreach (var t in ur.Types) VisitTypeRef(ctx, t, fileMask);
                break;
            case FunctionTypeRef fnr:
                foreach (var p in fnr.ParamTypes) VisitTypeRef(ctx, p, fileMask);
                VisitTypeRef(ctx, fnr.ReturnType, fileMask);
                break;
            case MapTypeRef mr:
                VisitTypeRef(ctx, mr.KeyType, fileMask);
                VisitTypeRef(ctx, mr.ValueType, fileMask);
                break;
            case StructTypeRef sr:
                foreach (var f in sr.Fields) VisitTypeRef(ctx, f.Type, fileMask);
                break;
            case TupleTypeRef tr:
                foreach (var e in tr.ElementTypes) VisitTypeRef(ctx, e, fileMask);
                break;
        }
    }

    /// <summary>
    /// Looks up the symbol bound to the name ref (if resolved) and reports
    /// <see cref="DiagnosticCode.ErrSymbolWrongSide"/> when the symbol's
    /// side mask does not cover the file's mask. Skips type-parameter
    /// symbols and ambient/unresolved references — those don't carry
    /// meaningful sides.
    /// </summary>
    private static void CheckNameRef(PassContext ctx, NameRef name, Side fileMask)
    {
        if (name.Sym == SymID.Invalid) return;
        var pkg = ctx.Pkg!;
        if (!pkg.Syms.GetByID(name.Sym, out var sym)) return;
        if (sym.Kind == SymbolKind.TypeParam) return;
        if (sym.Side == Side.All) return;
        if (sym.Side.IsAccessibleFrom(fileMask)) return;
        ctx.Diag.Report(name.Span, DiagnosticCode.ErrSymbolWrongSide,
            sym.Name, sym.Side.Format(), fileMask.Format());
    }
}
