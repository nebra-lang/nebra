using Nebra.Diagnostics;
using Nebra.IR;

namespace Nebra.LPS;

public static class NodeFinder
{
    public static Node? Find(IRScript script, int line, int col)
    {
        Node? best = null;
        SearchStmtList(script.Body, line, col, ref best);
        if (script.Return != null)
            SearchStmt(script.Return, line, col, ref best);
        return best;
    }

    public static NameRef? FindNameRef(IRScript script, int line, int col)
    {
        NameRef? best = null;
        SearchStmtListForNameRef(script.Body, line, col, ref best);
        if (script.Return != null)
            SearchStmtForNameRef(script.Return, line, col, ref best);
        return best;
    }

    public static List<NameRef> CollectAllNameRefs(IRScript script)
    {
        var refs = new List<NameRef>();
        CollectFromStmtList(script.Body, refs);
        if (script.Return != null)
            CollectFromStmt(script.Return, refs);
        return refs;
    }

    /// <summary>
    /// Returns the span of the name <paramref name="decl"/> introduces as <paramref name="name"/>,
    /// which is the first occurrence of that name inside the declaration. Falls back to the span of
    /// the whole declaration when it does not write the name itself.
    /// </summary>
    public static TextSpan DeclaredNameSpan(Node decl, string name)
    {
        if (decl is not Stmt stmt)
        {
            return decl.Span;
        }

        var refs = new List<NameRef>();
        CollectFromStmt(stmt, refs);

        var declared = refs
            .Where(nameRef => nameRef.Name == name)
            .OrderBy(nameRef => nameRef.Span.StartLn)
            .ThenBy(nameRef => nameRef.Span.StartCol)
            .FirstOrDefault();

        return declared?.Span ?? decl.Span;
    }

    public static Dictionary<NodeID, Node> BuildNodeRegistry(IRScript script)
    {
        var reg = new Dictionary<NodeID, Node>();
        reg[script.ID] = script;
        RegisterStmtList(script.Body, reg);
        if (script.Return != null)
            RegisterStmt(script.Return, reg);
        return reg;
    }

    private static bool Contains(TextSpan span, int line, int col)
    {
        if (line < span.StartLn || line > span.EndLn) return false;
        if (line == span.StartLn && col < span.StartCol) return false;
        if (line == span.EndLn && col > span.EndCol) return false;
        return true;
    }

    private static bool IsTighter(Node candidate, Node? current)
    {
        if (current == null) return true;
        var cs = candidate.Span;
        var bs = current.Span;
        var cLines = cs.EndLn - cs.StartLn;
        var bLines = bs.EndLn - bs.StartLn;
        if (cLines < bLines) return true;
        if (cLines == bLines)
            return (cs.EndCol - cs.StartCol) < (bs.EndCol - bs.StartCol);
        return false;
    }

    public static Expr? FindEnclosingCall(IRScript script, int line, int col)
    {
        Expr? best = null;
        SearchStmtListForCall(script.Body, line, col, ref best);
        if (script.Return != null)
            SearchStmtForCall(script.Return, line, col, ref best);
        return best;
    }

    private static void SearchStmtListForCall(List<Stmt> stmts, int line, int col, ref Expr? best)
    {
        foreach (var s in stmts) SearchStmtForCall(s, line, col, ref best);
    }

    private static void SearchStmtForCall(Stmt stmt, int line, int col, ref Expr? best)
    {
        if (!Contains(stmt.Span, line, col)) return;
        switch (stmt)
        {
            case FunctionDecl fd:
                SearchStmtListForCall(fd.Body, line, col, ref best);
                if (fd.ReturnStmt != null) SearchStmtForCall(fd.ReturnStmt, line, col, ref best);
                break;
            case LocalFunctionDecl lfd:
                SearchStmtListForCall(lfd.Body, line, col, ref best);
                if (lfd.ReturnStmt != null) SearchStmtForCall(lfd.ReturnStmt, line, col, ref best);
                break;
            case LocalDecl ld:
                foreach (var v in ld.Values) SearchExprForCall(v, line, col, ref best);
                break;
            case AssignStmt a:
                foreach (var t in a.Targets) SearchExprForCall(t, line, col, ref best);
                foreach (var v in a.Values) SearchExprForCall(v, line, col, ref best);
                break;
            case ExprStmt es:
                SearchExprForCall(es.Expression, line, col, ref best);
                break;
            case DoBlockStmt db:
                SearchStmtListForCall(db.Body, line, col, ref best);
                break;
            case WhileStmt ws:
                SearchExprForCall(ws.Condition, line, col, ref best);
                SearchStmtListForCall(ws.Body, line, col, ref best);
                break;
            case RepeatStmt rs:
                SearchStmtListForCall(rs.Body, line, col, ref best);
                SearchExprForCall(rs.Condition, line, col, ref best);
                break;
            case IfStmt ifs:
                SearchExprForCall(ifs.Condition, line, col, ref best);
                SearchStmtListForCall(ifs.Body, line, col, ref best);
                foreach (var ei in ifs.ElseIfs)
                {
                    SearchExprForCall(ei.Condition, line, col, ref best);
                    SearchStmtListForCall(ei.Body, line, col, ref best);
                }
                if (ifs.ElseBody != null) SearchStmtListForCall(ifs.ElseBody, line, col, ref best);
                break;
            case NumericForStmt nf:
                SearchExprForCall(nf.Start, line, col, ref best);
                SearchExprForCall(nf.Limit, line, col, ref best);
                if (nf.Step != null) SearchExprForCall(nf.Step, line, col, ref best);
                SearchStmtListForCall(nf.Body, line, col, ref best);
                break;
            case GenericForStmt gf:
                foreach (var iter in gf.Iterators) SearchExprForCall(iter, line, col, ref best);
                SearchStmtListForCall(gf.Body, line, col, ref best);
                break;
            case ReturnStmt ret:
                foreach (var v in ret.Values) SearchExprForCall(v, line, col, ref best);
                break;
            case ExportStmt exp:
                SearchStmtForCall(exp.Declaration, line, col, ref best);
                break;
        }
    }

    private static void SearchExprForCall(Expr expr, int line, int col, ref Expr? best)
    {
        if (!Contains(expr.Span, line, col)) return;

        if (expr is FunctionCallExpr or MethodCallExpr)
        {
            if (best == null || IsTighter(expr, best))
                best = expr;
        }

        switch (expr)
        {
            case ParenExpr pe: SearchExprForCall(pe.Inner, line, col, ref best); break;
            case BinaryExpr bin:
                SearchExprForCall(bin.Left, line, col, ref best);
                SearchExprForCall(bin.Right, line, col, ref best);
                break;
            case UnaryExpr un: SearchExprForCall(un.Operand, line, col, ref best); break;
            case DotAccessExpr dot: SearchExprForCall(dot.Object, line, col, ref best); break;
            case IndexAccessExpr idx:
                SearchExprForCall(idx.Object, line, col, ref best);
                SearchExprForCall(idx.Index, line, col, ref best);
                break;
            case FunctionCallExpr call:
                SearchExprForCall(call.Callee, line, col, ref best);
                foreach (var a in call.Arguments) SearchExprForCall(a, line, col, ref best);
                break;
            case MethodCallExpr mc:
                SearchExprForCall(mc.Object, line, col, ref best);
                foreach (var a in mc.Arguments) SearchExprForCall(a, line, col, ref best);
                break;
            case FunctionDefExpr fd:
                SearchStmtListForCall(fd.Body, line, col, ref best);
                if (fd.ReturnStmt != null) SearchStmtForCall(fd.ReturnStmt, line, col, ref best);
                break;
            case TableConstructorExpr tc:
                foreach (var f in tc.Fields)
                {
                    if (f.Key != null) SearchExprForCall(f.Key, line, col, ref best);
                    SearchExprForCall(f.Value, line, col, ref best);
                }
                break;
            case InterpolatedStringExpr interp:
                foreach (var p in interp.Parts)
                    if (p is InterpExprPart ep) SearchExprForCall(ep.Expression, line, col, ref best);
                break;
            case NonNilAssertExpr nna: SearchExprForCall(nna.Inner, line, col, ref best); break;
            case IncDecExpr incDec: SearchExprForCall(incDec.Target, line, col, ref best); break;
            case TypeCheckExpr tchk: SearchExprForCall(tchk.Inner, line, col, ref best); break;
            case TypeCastExpr tcast: SearchExprForCall(tcast.Inner, line, col, ref best); break;
            case TypeOfExpr tof: SearchExprForCall(tof.Inner, line, col, ref best); break;
            case InstanceOfExpr iof: SearchExprForCall(iof.Inner, line, col, ref best); break;
        }
    }

    #region Find Node

    private static void SearchStmtList(List<Stmt> stmts, int line, int col, ref Node? best)
    {
        foreach (var stmt in stmts)
            SearchStmt(stmt, line, col, ref best);
    }

    private static void SearchStmt(Stmt stmt, int line, int col, ref Node? best)
    {
        if (!Contains(stmt.Span, line, col)) return;
        if (IsTighter(stmt, best)) best = stmt;

        switch (stmt)
        {
            case FunctionDecl fd:
                foreach (var p in fd.Parameters) SearchNode(p, line, col, ref best);
                SearchStmtList(fd.Body, line, col, ref best);
                if (fd.ReturnStmt != null) SearchStmt(fd.ReturnStmt, line, col, ref best);
                break;
            case LocalFunctionDecl lfd:
                foreach (var p in lfd.Parameters) SearchNode(p, line, col, ref best);
                SearchStmtList(lfd.Body, line, col, ref best);
                if (lfd.ReturnStmt != null) SearchStmt(lfd.ReturnStmt, line, col, ref best);
                break;
            case LocalDecl ld:
                foreach (var v in ld.Values) SearchExpr(v, line, col, ref best);
                break;
            case EnumDecl ed:
                foreach (var m in ed.Members)
                    if (m.Value != null) SearchExpr(m.Value, line, col, ref best);
                break;
            case AssignStmt a:
                foreach (var t in a.Targets) SearchExpr(t, line, col, ref best);
                foreach (var v in a.Values) SearchExpr(v, line, col, ref best);
                break;
            case ExprStmt es:
                SearchExpr(es.Expression, line, col, ref best);
                break;
            case DoBlockStmt db:
                SearchStmtList(db.Body, line, col, ref best);
                break;
            case WhileStmt ws:
                SearchExpr(ws.Condition, line, col, ref best);
                SearchStmtList(ws.Body, line, col, ref best);
                break;
            case RepeatStmt rs:
                SearchStmtList(rs.Body, line, col, ref best);
                SearchExpr(rs.Condition, line, col, ref best);
                break;
            case IfStmt ifs:
                SearchExpr(ifs.Condition, line, col, ref best);
                SearchStmtList(ifs.Body, line, col, ref best);
                foreach (var ei in ifs.ElseIfs)
                {
                    SearchExpr(ei.Condition, line, col, ref best);
                    SearchStmtList(ei.Body, line, col, ref best);
                }
                if (ifs.ElseBody != null) SearchStmtList(ifs.ElseBody, line, col, ref best);
                break;
            case NumericForStmt nf:
                SearchExpr(nf.Start, line, col, ref best);
                SearchExpr(nf.Limit, line, col, ref best);
                if (nf.Step != null) SearchExpr(nf.Step, line, col, ref best);
                SearchStmtList(nf.Body, line, col, ref best);
                break;
            case GenericForStmt gf:
                foreach (var iter in gf.Iterators) SearchExpr(iter, line, col, ref best);
                SearchStmtList(gf.Body, line, col, ref best);
                break;
            case ReturnStmt ret:
                foreach (var v in ret.Values) SearchExpr(v, line, col, ref best);
                break;
            case ExportStmt exp:
                SearchStmt(exp.Declaration, line, col, ref best);
                break;
            case MatchStmt ms:
                SearchExpr(ms.Scrutinee, line, col, ref best);
                foreach (var arm in ms.Arms)
                {
                    if (arm.Pattern.ValueExpr != null) SearchExpr(arm.Pattern.ValueExpr, line, col, ref best);
                    if (arm.Guard != null) SearchExpr(arm.Guard, line, col, ref best);
                    SearchStmtList(arm.Body, line, col, ref best);
                }
                break;
            case DeferStmt ds:
                if (ds.Call != null) SearchExpr(ds.Call, line, col, ref best);
                if (ds.Block != null) SearchStmtList(ds.Block, line, col, ref best);
                break;
            case GuardStmt gs:
                SearchExpr(gs.Condition, line, col, ref best);
                if (gs.ElseExpr != null) SearchExpr(gs.ElseExpr, line, col, ref best);
                break;
            case ClassDecl cd:
                if (cd.Constructor != null)
                {
                    SearchStmtList(cd.Constructor.Body, line, col, ref best);
                    if (cd.Constructor.ReturnStmt != null) SearchStmt(cd.Constructor.ReturnStmt, line, col, ref best);
                }
                foreach (var method in cd.Methods)
                {
                    SearchStmtList(method.Body, line, col, ref best);
                    if (method.ReturnStmt != null) SearchStmt(method.ReturnStmt, line, col, ref best);
                }
                foreach (var accessor in cd.Accessors)
                {
                    SearchStmtList(accessor.Body, line, col, ref best);
                    if (accessor.ReturnStmt != null) SearchStmt(accessor.ReturnStmt, line, col, ref best);
                }
                foreach (var field in cd.Fields)
                {
                    if (field.DefaultValue != null) SearchExpr(field.DefaultValue, line, col, ref best);
                }
                break;
            case InterfaceDecl:
                break;
        }
    }

    private static void SearchExpr(Expr expr, int line, int col, ref Node? best)
    {
        if (!Contains(expr.Span, line, col)) return;
        if (IsTighter(expr, best)) best = expr;

        switch (expr)
        {
            case ParenExpr pe:
                SearchExpr(pe.Inner, line, col, ref best);
                break;
            case BinaryExpr bin:
                SearchExpr(bin.Left, line, col, ref best);
                SearchExpr(bin.Right, line, col, ref best);
                break;
            case UnaryExpr un:
                SearchExpr(un.Operand, line, col, ref best);
                break;
            case DotAccessExpr dot:
                SearchExpr(dot.Object, line, col, ref best);
                break;
            case IndexAccessExpr idx:
                SearchExpr(idx.Object, line, col, ref best);
                SearchExpr(idx.Index, line, col, ref best);
                break;
            case FunctionCallExpr call:
                SearchExpr(call.Callee, line, col, ref best);
                foreach (var a in call.Arguments) SearchExpr(a, line, col, ref best);
                break;
            case MethodCallExpr mc:
                SearchExpr(mc.Object, line, col, ref best);
                foreach (var a in mc.Arguments) SearchExpr(a, line, col, ref best);
                break;
            case FunctionDefExpr fd:
                foreach (var p in fd.Parameters) SearchNode(p, line, col, ref best);
                SearchStmtList(fd.Body, line, col, ref best);
                if (fd.ReturnStmt != null) SearchStmt(fd.ReturnStmt, line, col, ref best);
                break;
            case TableConstructorExpr tc:
                foreach (var f in tc.Fields)
                {
                    if (f.Key != null) SearchExpr(f.Key, line, col, ref best);
                    SearchExpr(f.Value, line, col, ref best);
                }
                break;
            case InterpolatedStringExpr interp:
                foreach (var p in interp.Parts)
                {
                    if (p is InterpExprPart ep) SearchExpr(ep.Expression, line, col, ref best);
                }
                break;
            case NonNilAssertExpr nna:
                SearchExpr(nna.Inner, line, col, ref best);
                break;
            case IncDecExpr incDec:
                SearchExpr(incDec.Target, line, col, ref best);
                break;
            case TypeCheckExpr tchk:
                SearchExpr(tchk.Inner, line, col, ref best);
                break;
            case TypeCastExpr tcast:
                SearchExpr(tcast.Inner, line, col, ref best);
                break;
            case TypeOfExpr tof:
                SearchExpr(tof.Inner, line, col, ref best);
                break;
            case InstanceOfExpr iof:
                SearchExpr(iof.Inner, line, col, ref best);
                break;
            case MatchExpr me:
                SearchExpr(me.Scrutinee, line, col, ref best);
                foreach (var arm in me.Arms)
                {
                    if (arm.Pattern.ValueExpr != null) SearchExpr(arm.Pattern.ValueExpr, line, col, ref best);
                    if (arm.Guard != null) SearchExpr(arm.Guard, line, col, ref best);
                    SearchExpr(arm.Value, line, col, ref best);
                }
                break;
            case AwaitExpr aw:
                SearchExpr(aw.Expression, line, col, ref best);
                break;
            case NewExpr ne:
                foreach (var arg in ne.Arguments) SearchExpr(arg, line, col, ref best);
                break;
            case SuperCallExpr sc:
                foreach (var arg in sc.Arguments) SearchExpr(arg, line, col, ref best);
                break;
        }
    }

    private static void SearchNode(Node node, int line, int col, ref Node? best)
    {
        if (!Contains(node.Span, line, col)) return;
        if (IsTighter(node, best)) best = node;
    }

    #endregion

    #region Find NameRef

    private static bool NameRefContains(NameRef nr, int line, int col) => Contains(nr.Span, line, col);

    private static void CheckNameRef(NameRef? nr, int line, int col, ref NameRef? best)
    {
        if (nr != null && NameRefContains(nr, line, col)) best = nr;
    }

    private static void SearchStmtListForNameRef(List<Stmt> stmts, int line, int col, ref NameRef? best)
    {
        foreach (var stmt in stmts)
            SearchStmtForNameRef(stmt, line, col, ref best);
    }

    private static void SearchStmtForNameRef(Stmt stmt, int line, int col, ref NameRef? best)
    {
        if (!Contains(stmt.Span, line, col)) return;

        switch (stmt)
        {
            case FunctionDecl fd:
                foreach (var n in fd.NamePath) CheckNameRef(n, line, col, ref best);
                CheckNameRef(fd.MethodName, line, col, ref best);
                foreach (var tp in fd.TypeParams) SearchTypeParamDefForNameRef(tp, line, col, ref best);
                foreach (var p in fd.Parameters)
                {
                    CheckNameRef(p.Name, line, col, ref best);
                    SearchTypeRefForNameRef(p.TypeAnnotation, line, col, ref best);
                }
                SearchTypeRefForNameRef(fd.ReturnType, line, col, ref best);
                SearchStmtListForNameRef(fd.Body, line, col, ref best);
                if (fd.ReturnStmt != null) SearchStmtForNameRef(fd.ReturnStmt, line, col, ref best);
                break;
            case LocalFunctionDecl lfd:
                CheckNameRef(lfd.Name, line, col, ref best);
                foreach (var tp in lfd.TypeParams) SearchTypeParamDefForNameRef(tp, line, col, ref best);
                foreach (var p in lfd.Parameters)
                {
                    CheckNameRef(p.Name, line, col, ref best);
                    SearchTypeRefForNameRef(p.TypeAnnotation, line, col, ref best);
                }
                SearchTypeRefForNameRef(lfd.ReturnType, line, col, ref best);
                SearchStmtListForNameRef(lfd.Body, line, col, ref best);
                if (lfd.ReturnStmt != null) SearchStmtForNameRef(lfd.ReturnStmt, line, col, ref best);
                break;
            case LocalDecl ld:
                foreach (var v in ld.Variables)
                {
                    CheckNameRef(v.Name, line, col, ref best);
                    SearchTypeRefForNameRef(v.TypeAnnotation, line, col, ref best);
                }
                foreach (var v in ld.Values) SearchExprForNameRef(v, line, col, ref best);
                break;
            case EnumDecl ed:
                CheckNameRef(ed.Name, line, col, ref best);
                foreach (var m in ed.Members)
                {
                    CheckNameRef(m.Name, line, col, ref best);
                    if (m.Value != null) SearchExprForNameRef(m.Value, line, col, ref best);
                }
                break;
            case AssignStmt a:
                foreach (var t in a.Targets) SearchExprForNameRef(t, line, col, ref best);
                foreach (var v in a.Values) SearchExprForNameRef(v, line, col, ref best);
                break;
            case ExprStmt es:
                SearchExprForNameRef(es.Expression, line, col, ref best);
                break;
            case DoBlockStmt db:
                SearchStmtListForNameRef(db.Body, line, col, ref best);
                break;
            case WhileStmt ws:
                SearchExprForNameRef(ws.Condition, line, col, ref best);
                SearchStmtListForNameRef(ws.Body, line, col, ref best);
                break;
            case RepeatStmt rs:
                SearchStmtListForNameRef(rs.Body, line, col, ref best);
                SearchExprForNameRef(rs.Condition, line, col, ref best);
                break;
            case IfStmt ifs:
                SearchExprForNameRef(ifs.Condition, line, col, ref best);
                SearchStmtListForNameRef(ifs.Body, line, col, ref best);
                foreach (var ei in ifs.ElseIfs)
                {
                    SearchExprForNameRef(ei.Condition, line, col, ref best);
                    SearchStmtListForNameRef(ei.Body, line, col, ref best);
                }
                if (ifs.ElseBody != null) SearchStmtListForNameRef(ifs.ElseBody, line, col, ref best);
                break;
            case NumericForStmt nf:
                CheckNameRef(nf.VarName, line, col, ref best);
                SearchExprForNameRef(nf.Start, line, col, ref best);
                SearchExprForNameRef(nf.Limit, line, col, ref best);
                if (nf.Step != null) SearchExprForNameRef(nf.Step, line, col, ref best);
                SearchStmtListForNameRef(nf.Body, line, col, ref best);
                break;
            case GenericForStmt gf:
                foreach (var vn in gf.VarNames) CheckNameRef(vn, line, col, ref best);
                foreach (var iter in gf.Iterators) SearchExprForNameRef(iter, line, col, ref best);
                SearchStmtListForNameRef(gf.Body, line, col, ref best);
                break;
            case ReturnStmt ret:
                foreach (var v in ret.Values) SearchExprForNameRef(v, line, col, ref best);
                break;
            case ImportStmt imp:
                CheckNameRef(imp.Module, line, col, ref best);
                CheckNameRef(imp.Alias, line, col, ref best);
                foreach (var s in imp.Specifiers)
                {
                    CheckNameRef(s.Name, line, col, ref best);
                    CheckNameRef(s.Alias, line, col, ref best);
                }
                break;
            case MatchStmt ms:
                SearchExprForNameRef(ms.Scrutinee, line, col, ref best);
                foreach (var arm in ms.Arms)
                {
                    if (arm.Pattern.ValueExpr != null) SearchExprForNameRef(arm.Pattern.ValueExpr, line, col, ref best);
                    if (arm.Pattern.TypeRef != null) SearchTypeRefForNameRef(arm.Pattern.TypeRef, line, col, ref best);
                    if (arm.Pattern.Binding != null) CheckNameRef(arm.Pattern.Binding, line, col, ref best);
                    if (arm.Guard != null) SearchExprForNameRef(arm.Guard, line, col, ref best);
                    SearchStmtListForNameRef(arm.Body, line, col, ref best);
                }
                break;
            case DeferStmt ds:
                if (ds.Call != null) SearchExprForNameRef(ds.Call, line, col, ref best);
                if (ds.Block != null) SearchStmtListForNameRef(ds.Block, line, col, ref best);
                break;
            case GuardStmt gs:
                SearchExprForNameRef(gs.Condition, line, col, ref best);
                if (gs.ElseExpr != null) SearchExprForNameRef(gs.ElseExpr, line, col, ref best);
                break;
            case ExportStmt exp:
                SearchStmtForNameRef(exp.Declaration, line, col, ref best);
                break;
            case GotoStmt gs:
                CheckNameRef(gs.LabelName, line, col, ref best);
                break;
            case LabelStmt ls:
                CheckNameRef(ls.Name, line, col, ref best);
                break;
            case ClassDecl cd:
                CheckNameRef(cd.Name, line, col, ref best);
                foreach (var tp in cd.TypeParams) SearchTypeParamDefForNameRef(tp, line, col, ref best);
                if (cd.BaseClass != null) CheckNameRef(cd.BaseClass, line, col, ref best);
                foreach (var arg in cd.BaseClassTypeArgs) SearchTypeArgRefForNameRef(arg, line, col, ref best);
                foreach (var iface in cd.Interfaces) CheckNameRef(iface, line, col, ref best);
                foreach (var argList in cd.InterfaceTypeArgs)
                    foreach (var arg in argList) SearchTypeArgRefForNameRef(arg, line, col, ref best);
                if (cd.Constructor != null)
                {
                    foreach (var p in cd.Constructor.Parameters)
                    {
                        CheckNameRef(p.Name, line, col, ref best);
                        SearchTypeRefForNameRef(p.TypeAnnotation, line, col, ref best);
                    }
                    SearchStmtListForNameRef(cd.Constructor.Body, line, col, ref best);
                    if (cd.Constructor.ReturnStmt != null) SearchStmtForNameRef(cd.Constructor.ReturnStmt, line, col, ref best);
                }
                foreach (var method in cd.Methods)
                {
                    CheckNameRef(method.Name, line, col, ref best);
                    foreach (var tp in method.TypeParams) SearchTypeParamDefForNameRef(tp, line, col, ref best);
                    foreach (var p in method.Parameters)
                    {
                        CheckNameRef(p.Name, line, col, ref best);
                        SearchTypeRefForNameRef(p.TypeAnnotation, line, col, ref best);
                    }
                    SearchTypeRefForNameRef(method.ReturnType, line, col, ref best);
                    SearchStmtListForNameRef(method.Body, line, col, ref best);
                    if (method.ReturnStmt != null) SearchStmtForNameRef(method.ReturnStmt, line, col, ref best);
                }
                foreach (var accessor in cd.Accessors)
                {
                    CheckNameRef(accessor.Name, line, col, ref best);
                    foreach (var p in accessor.Parameters)
                    {
                        CheckNameRef(p.Name, line, col, ref best);
                        SearchTypeRefForNameRef(p.TypeAnnotation, line, col, ref best);
                    }
                    SearchTypeRefForNameRef(accessor.ReturnType, line, col, ref best);
                    SearchStmtListForNameRef(accessor.Body, line, col, ref best);
                    if (accessor.ReturnStmt != null) SearchStmtForNameRef(accessor.ReturnStmt, line, col, ref best);
                }
                foreach (var field in cd.Fields)
                {
                    CheckNameRef(field.Name, line, col, ref best);
                    SearchTypeRefForNameRef(field.TypeAnnotation, line, col, ref best);
                    if (field.DefaultValue != null) SearchExprForNameRef(field.DefaultValue, line, col, ref best);
                }
                break;
            case InterfaceDecl id:
                CheckNameRef(id.Name, line, col, ref best);
                foreach (var tp in id.TypeParams) SearchTypeParamDefForNameRef(tp, line, col, ref best);
                foreach (var iface in id.BaseInterfaces) CheckNameRef(iface, line, col, ref best);
                foreach (var argList in id.BaseInterfaceTypeArgs)
                    foreach (var arg in argList) SearchTypeArgRefForNameRef(arg, line, col, ref best);
                foreach (var field in id.Fields)
                {
                    CheckNameRef(field.Name, line, col, ref best);
                    SearchTypeRefForNameRef(field.TypeAnnotation, line, col, ref best);
                }
                foreach (var method in id.Methods)
                {
                    CheckNameRef(method.Name, line, col, ref best);
                    foreach (var tp in method.TypeParams) SearchTypeParamDefForNameRef(tp, line, col, ref best);
                    foreach (var p in method.Parameters)
                    {
                        CheckNameRef(p.Name, line, col, ref best);
                        SearchTypeRefForNameRef(p.TypeAnnotation, line, col, ref best);
                    }
                    SearchTypeRefForNameRef(method.ReturnType, line, col, ref best);
                }
                break;
            case DeclareFunctionDecl dfd:
                foreach (var n in dfd.NamePath) CheckNameRef(n, line, col, ref best);
                CheckNameRef(dfd.MethodName, line, col, ref best);
                foreach (var tp in dfd.TypeParams) SearchTypeParamDefForNameRef(tp, line, col, ref best);
                foreach (var p in dfd.Parameters)
                {
                    CheckNameRef(p.Name, line, col, ref best);
                    SearchTypeRefForNameRef(p.TypeAnnotation, line, col, ref best);
                }
                SearchTypeRefForNameRef(dfd.ReturnType, line, col, ref best);
                break;
            case DeclareVariableDecl dvd:
                CheckNameRef(dvd.Name, line, col, ref best);
                SearchTypeRefForNameRef(dvd.TypeAnnotation, line, col, ref best);
                break;
            case DeclareModuleDecl dmd:
                CheckNameRef(dmd.ModuleName, line, col, ref best);
                foreach (var member in dmd.Members)
                    SearchStmtForNameRef(member, line, col, ref best);
                break;
        }
    }

    /// <summary>
    /// Recurses into a <see cref="TypeRef"/> so that names appearing inside type
    /// annotations (e.g. <c>Vec2</c> in <c>function(): Vec2</c>) are reachable for
    /// hover and go-to-definition. Without this, the LSP only sees identifiers
    /// in value-position and treats type names as inert.
    /// </summary>
    private static void SearchTypeRefForNameRef(TypeRef? tr, int line, int col, ref NameRef? best)
    {
        if (tr == null) return;
        if (!Contains(tr.Span, line, col)) return;
        switch (tr)
        {
            case NamedTypeRef nt:
                CheckNameRef(nt.Name, line, col, ref best);
                break;
            case GenericTypeRef gt:
                CheckNameRef(gt.Name, line, col, ref best);
                foreach (var a in gt.Arguments) SearchTypeArgRefForNameRef(a, line, col, ref best);
                break;
            case ArrayTypeRef at:
                SearchTypeRefForNameRef(at.ElementType, line, col, ref best);
                break;
            case NullableTypeRef nu:
                SearchTypeRefForNameRef(nu.InnerType, line, col, ref best);
                break;
            case UnionTypeRef ut:
                foreach (var t in ut.Types) SearchTypeRefForNameRef(t, line, col, ref best);
                break;
            case FunctionTypeRef ft:
                foreach (var p in ft.ParamTypes) SearchTypeRefForNameRef(p, line, col, ref best);
                SearchTypeRefForNameRef(ft.ReturnType, line, col, ref best);
                break;
            case MapTypeRef mt:
                SearchTypeRefForNameRef(mt.KeyType, line, col, ref best);
                SearchTypeRefForNameRef(mt.ValueType, line, col, ref best);
                break;
            case StructTypeRef st:
                foreach (var f in st.Fields)
                {
                    CheckNameRef(f.Name, line, col, ref best);
                    SearchTypeRefForNameRef(f.Type, line, col, ref best);
                }
                break;
            case TupleTypeRef tt:
                foreach (var t in tt.ElementTypes) SearchTypeRefForNameRef(t, line, col, ref best);
                break;
            case TypeParamRef tp:
                CheckNameRef(tp.Name, line, col, ref best);
                break;
        }
    }

    private static void SearchTypeArgRefForNameRef(TypeArgRef arg, int line, int col, ref NameRef? best)
    {
        switch (arg)
        {
            case ConcreteTypeArgRef cta:
                SearchTypeRefForNameRef(cta.Type, line, col, ref best);
                break;
            case WildcardTypeArgRef wta when wta.Bound != null:
                SearchTypeRefForNameRef(wta.Bound, line, col, ref best);
                break;
        }
    }

    private static void SearchTypeParamDefForNameRef(TypeParamDef tp, int line, int col, ref NameRef? best)
    {
        CheckNameRef(tp.Name, line, col, ref best);
        SearchTypeRefForNameRef(tp.ExtendsBound, line, col, ref best);
        foreach (var b in tp.ImplementsBounds) SearchTypeRefForNameRef(b, line, col, ref best);
    }

    private static void SearchExprForNameRef(Expr expr, int line, int col, ref NameRef? best)
    {
        if (!Contains(expr.Span, line, col)) return;

        switch (expr)
        {
            case NameExpr ne:
                CheckNameRef(ne.Name, line, col, ref best);
                break;
            case ParenExpr pe:
                SearchExprForNameRef(pe.Inner, line, col, ref best);
                break;
            case BinaryExpr bin:
                SearchExprForNameRef(bin.Left, line, col, ref best);
                SearchExprForNameRef(bin.Right, line, col, ref best);
                break;
            case UnaryExpr un:
                SearchExprForNameRef(un.Operand, line, col, ref best);
                break;
            case DotAccessExpr dot:
                SearchExprForNameRef(dot.Object, line, col, ref best);
                CheckNameRef(dot.FieldName, line, col, ref best);
                break;
            case IndexAccessExpr idx:
                SearchExprForNameRef(idx.Object, line, col, ref best);
                SearchExprForNameRef(idx.Index, line, col, ref best);
                break;
            case FunctionCallExpr call:
                SearchExprForNameRef(call.Callee, line, col, ref best);
                foreach (var a in call.Arguments) SearchExprForNameRef(a, line, col, ref best);
                break;
            case MethodCallExpr mc:
                SearchExprForNameRef(mc.Object, line, col, ref best);
                CheckNameRef(mc.MethodName, line, col, ref best);
                foreach (var a in mc.Arguments) SearchExprForNameRef(a, line, col, ref best);
                break;
            case FunctionDefExpr fd:
                foreach (var p in fd.Parameters) CheckNameRef(p.Name, line, col, ref best);
                SearchStmtListForNameRef(fd.Body, line, col, ref best);
                if (fd.ReturnStmt != null) SearchStmtForNameRef(fd.ReturnStmt, line, col, ref best);
                break;
            case TableConstructorExpr tc:
                foreach (var f in tc.Fields)
                {
                    CheckNameRef(f.Name, line, col, ref best);
                    if (f.Key != null) SearchExprForNameRef(f.Key, line, col, ref best);
                    SearchExprForNameRef(f.Value, line, col, ref best);
                }
                break;
            case InterpolatedStringExpr interp:
                foreach (var p in interp.Parts)
                {
                    if (p is InterpExprPart ep) SearchExprForNameRef(ep.Expression, line, col, ref best);
                }
                break;
            case NonNilAssertExpr nna:
                SearchExprForNameRef(nna.Inner, line, col, ref best);
                break;
            case IncDecExpr incDec:
                SearchExprForNameRef(incDec.Target, line, col, ref best);
                break;
            case TypeCheckExpr tchk:
                SearchExprForNameRef(tchk.Inner, line, col, ref best);
                break;
            case TypeCastExpr tcast:
                SearchExprForNameRef(tcast.Inner, line, col, ref best);
                break;
            case TypeOfExpr tof:
                SearchExprForNameRef(tof.Inner, line, col, ref best);
                break;
            case InstanceOfExpr iof:
                CheckNameRef(iof.ClassName, line, col, ref best);
                SearchExprForNameRef(iof.Inner, line, col, ref best);
                break;
            case NewExpr ne:
                CheckNameRef(ne.ClassName, line, col, ref best);
                foreach (var a in ne.Arguments) SearchExprForNameRef(a, line, col, ref best);
                break;
            case SuperCallExpr sc:
                foreach (var a in sc.Arguments) SearchExprForNameRef(a, line, col, ref best);
                break;
        }
    }

    #endregion

    #region Collect NameRefs

    private static void CollectFromStmtList(List<Stmt> stmts, List<NameRef> refs)
    {
        foreach (var stmt in stmts) CollectFromStmt(stmt, refs);
    }

    private static void AddRef(NameRef? nr, List<NameRef> refs)
    {
        if (nr != null && nr.Sym != SymID.Invalid) refs.Add(nr);
    }

    private static void CollectFromStmt(Stmt stmt, List<NameRef> refs)
    {
        switch (stmt)
        {
            case FunctionDecl fd:
                foreach (var n in fd.NamePath) AddRef(n, refs);
                AddRef(fd.MethodName, refs);
                foreach (var tp in fd.TypeParams) CollectFromTypeParamDef(tp, refs);
                foreach (var p in fd.Parameters)
                {
                    AddRef(p.Name, refs);
                    CollectFromTypeRef(p.TypeAnnotation, refs);
                }
                CollectFromTypeRef(fd.ReturnType, refs);
                CollectFromStmtList(fd.Body, refs);
                if (fd.ReturnStmt != null) CollectFromStmt(fd.ReturnStmt, refs);
                break;
            case LocalFunctionDecl lfd:
                AddRef(lfd.Name, refs);
                foreach (var tp in lfd.TypeParams) CollectFromTypeParamDef(tp, refs);
                foreach (var p in lfd.Parameters)
                {
                    AddRef(p.Name, refs);
                    CollectFromTypeRef(p.TypeAnnotation, refs);
                }
                CollectFromTypeRef(lfd.ReturnType, refs);
                CollectFromStmtList(lfd.Body, refs);
                if (lfd.ReturnStmt != null) CollectFromStmt(lfd.ReturnStmt, refs);
                break;
            case LocalDecl ld:
                foreach (var v in ld.Variables)
                {
                    AddRef(v.Name, refs);
                    CollectFromTypeRef(v.TypeAnnotation, refs);
                }
                foreach (var v in ld.Values) CollectFromExpr(v, refs);
                break;
            case EnumDecl ed:
                AddRef(ed.Name, refs);
                foreach (var m in ed.Members)
                {
                    AddRef(m.Name, refs);
                    if (m.Value != null) CollectFromExpr(m.Value, refs);
                }
                break;
            case AssignStmt a:
                foreach (var t in a.Targets) CollectFromExpr(t, refs);
                foreach (var v in a.Values) CollectFromExpr(v, refs);
                break;
            case ExprStmt es:
                CollectFromExpr(es.Expression, refs);
                break;
            case DoBlockStmt db:
                CollectFromStmtList(db.Body, refs);
                break;
            case WhileStmt ws:
                CollectFromExpr(ws.Condition, refs);
                CollectFromStmtList(ws.Body, refs);
                break;
            case RepeatStmt rs:
                CollectFromStmtList(rs.Body, refs);
                CollectFromExpr(rs.Condition, refs);
                break;
            case IfStmt ifs:
                CollectFromExpr(ifs.Condition, refs);
                CollectFromStmtList(ifs.Body, refs);
                foreach (var ei in ifs.ElseIfs)
                {
                    CollectFromExpr(ei.Condition, refs);
                    CollectFromStmtList(ei.Body, refs);
                }
                if (ifs.ElseBody != null) CollectFromStmtList(ifs.ElseBody, refs);
                break;
            case NumericForStmt nf:
                AddRef(nf.VarName, refs);
                CollectFromExpr(nf.Start, refs);
                CollectFromExpr(nf.Limit, refs);
                if (nf.Step != null) CollectFromExpr(nf.Step, refs);
                CollectFromStmtList(nf.Body, refs);
                break;
            case GenericForStmt gf:
                foreach (var vn in gf.VarNames) AddRef(vn, refs);
                foreach (var iter in gf.Iterators) CollectFromExpr(iter, refs);
                CollectFromStmtList(gf.Body, refs);
                break;
            case ReturnStmt ret:
                foreach (var v in ret.Values) CollectFromExpr(v, refs);
                break;
            case ImportStmt imp:
                AddRef(imp.Module, refs);
                AddRef(imp.Alias, refs);
                foreach (var s in imp.Specifiers) { AddRef(s.Name, refs); AddRef(s.Alias, refs); }
                break;
            case ExportStmt exp:
                CollectFromStmt(exp.Declaration, refs);
                break;
            case MatchStmt ms:
                CollectFromExpr(ms.Scrutinee, refs);
                foreach (var arm in ms.Arms)
                {
                    if (arm.Pattern.ValueExpr != null) CollectFromExpr(arm.Pattern.ValueExpr, refs);
                    if (arm.Pattern.TypeRef != null) CollectFromTypeRef(arm.Pattern.TypeRef, refs);
                    if (arm.Pattern.Binding != null) AddRef(arm.Pattern.Binding, refs);
                    if (arm.Guard != null) CollectFromExpr(arm.Guard, refs);
                    CollectFromStmtList(arm.Body, refs);
                }
                break;
            case DeferStmt ds:
                if (ds.Call != null) CollectFromExpr(ds.Call, refs);
                if (ds.Block != null) CollectFromStmtList(ds.Block, refs);
                break;
            case GuardStmt gs:
                CollectFromExpr(gs.Condition, refs);
                if (gs.ElseExpr != null) CollectFromExpr(gs.ElseExpr, refs);
                break;
            case GotoStmt gs:
                AddRef(gs.LabelName, refs);
                break;
            case LabelStmt ls:
                AddRef(ls.Name, refs);
                break;
            case ClassDecl cd:
                AddRef(cd.Name, refs);
                foreach (var tp in cd.TypeParams) CollectFromTypeParamDef(tp, refs);
                if (cd.BaseClass != null) AddRef(cd.BaseClass, refs);
                foreach (var arg in cd.BaseClassTypeArgs) CollectFromTypeArgRef(arg, refs);
                foreach (var iface in cd.Interfaces) AddRef(iface, refs);
                foreach (var argList in cd.InterfaceTypeArgs)
                    foreach (var arg in argList) CollectFromTypeArgRef(arg, refs);
                if (cd.Constructor != null)
                {
                    foreach (var p in cd.Constructor.Parameters)
                    {
                        AddRef(p.Name, refs);
                        CollectFromTypeRef(p.TypeAnnotation, refs);
                    }
                    CollectFromStmtList(cd.Constructor.Body, refs);
                    if (cd.Constructor.ReturnStmt != null) CollectFromStmt(cd.Constructor.ReturnStmt, refs);
                }
                foreach (var method in cd.Methods)
                {
                    AddRef(method.Name, refs);
                    foreach (var tp in method.TypeParams) CollectFromTypeParamDef(tp, refs);
                    foreach (var p in method.Parameters)
                    {
                        AddRef(p.Name, refs);
                        CollectFromTypeRef(p.TypeAnnotation, refs);
                    }
                    CollectFromTypeRef(method.ReturnType, refs);
                    CollectFromStmtList(method.Body, refs);
                    if (method.ReturnStmt != null) CollectFromStmt(method.ReturnStmt, refs);
                }
                foreach (var accessor in cd.Accessors)
                {
                    AddRef(accessor.Name, refs);
                    foreach (var p in accessor.Parameters)
                    {
                        AddRef(p.Name, refs);
                        CollectFromTypeRef(p.TypeAnnotation, refs);
                    }
                    CollectFromTypeRef(accessor.ReturnType, refs);
                    CollectFromStmtList(accessor.Body, refs);
                    if (accessor.ReturnStmt != null) CollectFromStmt(accessor.ReturnStmt, refs);
                }
                foreach (var field in cd.Fields)
                {
                    AddRef(field.Name, refs);
                    CollectFromTypeRef(field.TypeAnnotation, refs);
                    if (field.DefaultValue != null) CollectFromExpr(field.DefaultValue, refs);
                }
                break;
            case InterfaceDecl id:
                AddRef(id.Name, refs);
                foreach (var tp in id.TypeParams) CollectFromTypeParamDef(tp, refs);
                foreach (var iface in id.BaseInterfaces) AddRef(iface, refs);
                foreach (var argList in id.BaseInterfaceTypeArgs)
                    foreach (var arg in argList) CollectFromTypeArgRef(arg, refs);
                foreach (var field in id.Fields)
                {
                    AddRef(field.Name, refs);
                    CollectFromTypeRef(field.TypeAnnotation, refs);
                }
                foreach (var method in id.Methods)
                {
                    AddRef(method.Name, refs);
                    foreach (var tp in method.TypeParams) CollectFromTypeParamDef(tp, refs);
                    foreach (var p in method.Parameters)
                    {
                        AddRef(p.Name, refs);
                        CollectFromTypeRef(p.TypeAnnotation, refs);
                    }
                    CollectFromTypeRef(method.ReturnType, refs);
                }
                break;
            case DeclareFunctionDecl dfd:
                foreach (var n in dfd.NamePath) AddRef(n, refs);
                AddRef(dfd.MethodName, refs);
                foreach (var tp in dfd.TypeParams) CollectFromTypeParamDef(tp, refs);
                foreach (var p in dfd.Parameters)
                {
                    AddRef(p.Name, refs);
                    CollectFromTypeRef(p.TypeAnnotation, refs);
                }
                CollectFromTypeRef(dfd.ReturnType, refs);
                break;
            case DeclareVariableDecl dvd:
                AddRef(dvd.Name, refs);
                CollectFromTypeRef(dvd.TypeAnnotation, refs);
                break;
            case DeclareModuleDecl dmd:
                AddRef(dmd.ModuleName, refs);
                foreach (var member in dmd.Members) CollectFromStmt(member, refs);
                break;
        }
    }

    private static void CollectFromTypeRef(TypeRef? tr, List<NameRef> refs)
    {
        if (tr == null) return;
        switch (tr)
        {
            case NamedTypeRef nt: AddRef(nt.Name, refs); break;
            case GenericTypeRef gt:
                AddRef(gt.Name, refs);
                foreach (var a in gt.Arguments) CollectFromTypeArgRef(a, refs);
                break;
            case ArrayTypeRef at: CollectFromTypeRef(at.ElementType, refs); break;
            case NullableTypeRef nu: CollectFromTypeRef(nu.InnerType, refs); break;
            case UnionTypeRef ut:
                foreach (var t in ut.Types) CollectFromTypeRef(t, refs);
                break;
            case FunctionTypeRef ft:
                foreach (var p in ft.ParamTypes) CollectFromTypeRef(p, refs);
                CollectFromTypeRef(ft.ReturnType, refs);
                break;
            case MapTypeRef mt:
                CollectFromTypeRef(mt.KeyType, refs);
                CollectFromTypeRef(mt.ValueType, refs);
                break;
            case StructTypeRef st:
                foreach (var f in st.Fields)
                {
                    AddRef(f.Name, refs);
                    CollectFromTypeRef(f.Type, refs);
                }
                break;
            case TupleTypeRef tt:
                foreach (var t in tt.ElementTypes) CollectFromTypeRef(t, refs);
                break;
            case TypeParamRef tp: AddRef(tp.Name, refs); break;
        }
    }

    private static void CollectFromTypeArgRef(TypeArgRef arg, List<NameRef> refs)
    {
        switch (arg)
        {
            case ConcreteTypeArgRef cta: CollectFromTypeRef(cta.Type, refs); break;
            case WildcardTypeArgRef wta when wta.Bound != null: CollectFromTypeRef(wta.Bound, refs); break;
        }
    }

    private static void CollectFromTypeParamDef(TypeParamDef tp, List<NameRef> refs)
    {
        AddRef(tp.Name, refs);
        CollectFromTypeRef(tp.ExtendsBound, refs);
        foreach (var b in tp.ImplementsBounds) CollectFromTypeRef(b, refs);
    }

    private static void CollectFromExpr(Expr expr, List<NameRef> refs)
    {
        switch (expr)
        {
            case NameExpr ne:
                AddRef(ne.Name, refs);
                break;
            case ParenExpr pe:
                CollectFromExpr(pe.Inner, refs);
                break;
            case BinaryExpr bin:
                CollectFromExpr(bin.Left, refs);
                CollectFromExpr(bin.Right, refs);
                break;
            case UnaryExpr un:
                CollectFromExpr(un.Operand, refs);
                break;
            case DotAccessExpr dot:
                CollectFromExpr(dot.Object, refs);
                AddRef(dot.FieldName, refs);
                break;
            case IndexAccessExpr idx:
                CollectFromExpr(idx.Object, refs);
                CollectFromExpr(idx.Index, refs);
                break;
            case FunctionCallExpr call:
                CollectFromExpr(call.Callee, refs);
                foreach (var a in call.Arguments) CollectFromExpr(a, refs);
                break;
            case MethodCallExpr mc:
                CollectFromExpr(mc.Object, refs);
                AddRef(mc.MethodName, refs);
                foreach (var a in mc.Arguments) CollectFromExpr(a, refs);
                break;
            case FunctionDefExpr fd:
                foreach (var p in fd.Parameters) AddRef(p.Name, refs);
                CollectFromStmtList(fd.Body, refs);
                if (fd.ReturnStmt != null) CollectFromStmt(fd.ReturnStmt, refs);
                break;
            case TableConstructorExpr tc:
                foreach (var f in tc.Fields)
                {
                    AddRef(f.Name, refs);
                    if (f.Key != null) CollectFromExpr(f.Key, refs);
                    CollectFromExpr(f.Value, refs);
                }
                break;
            case InterpolatedStringExpr interp:
                foreach (var p in interp.Parts)
                {
                    if (p is InterpExprPart ep) CollectFromExpr(ep.Expression, refs);
                }
                break;
            case NonNilAssertExpr nna:
                CollectFromExpr(nna.Inner, refs);
                break;
            case IncDecExpr incDec:
                CollectFromExpr(incDec.Target, refs);
                break;
            case TypeCheckExpr tchk:
                CollectFromExpr(tchk.Inner, refs);
                break;
            case TypeCastExpr tcast:
                CollectFromExpr(tcast.Inner, refs);
                break;
            case TypeOfExpr tof:
                CollectFromExpr(tof.Inner, refs);
                break;
            case InstanceOfExpr iof:
                AddRef(iof.ClassName, refs);
                CollectFromExpr(iof.Inner, refs);
                break;
            case NewExpr ne:
                AddRef(ne.ClassName, refs);
                foreach (var a in ne.Arguments) CollectFromExpr(a, refs);
                break;
            case SuperCallExpr sc:
                foreach (var a in sc.Arguments) CollectFromExpr(a, refs);
                break;
        }
    }

    #endregion

    #region Node Registry

    private static void RegisterStmtList(List<Stmt> stmts, Dictionary<NodeID, Node> reg)
    {
        foreach (var s in stmts) RegisterStmt(s, reg);
    }

    private static void RegisterStmt(Stmt stmt, Dictionary<NodeID, Node> reg)
    {
        reg[stmt.ID] = stmt;
        switch (stmt)
        {
            case FunctionDecl fd:
                foreach (var p in fd.Parameters) reg[p.ID] = p;
                RegisterStmtList(fd.Body, reg);
                if (fd.ReturnStmt != null) RegisterStmt(fd.ReturnStmt, reg);
                break;
            case LocalFunctionDecl lfd:
                foreach (var p in lfd.Parameters) reg[p.ID] = p;
                RegisterStmtList(lfd.Body, reg);
                if (lfd.ReturnStmt != null) RegisterStmt(lfd.ReturnStmt, reg);
                break;
            case LocalDecl ld:
                foreach (var v in ld.Values) RegisterExpr(v, reg);
                break;
            case EnumDecl ed:
                foreach (var m in ed.Members)
                    if (m.Value != null) RegisterExpr(m.Value, reg);
                break;
            case AssignStmt a:
                foreach (var t in a.Targets) RegisterExpr(t, reg);
                foreach (var v in a.Values) RegisterExpr(v, reg);
                break;
            case ExprStmt es:
                RegisterExpr(es.Expression, reg);
                break;
            case DoBlockStmt db:
                RegisterStmtList(db.Body, reg);
                break;
            case WhileStmt ws:
                RegisterExpr(ws.Condition, reg);
                RegisterStmtList(ws.Body, reg);
                break;
            case RepeatStmt rs:
                RegisterStmtList(rs.Body, reg);
                RegisterExpr(rs.Condition, reg);
                break;
            case IfStmt ifs:
                RegisterExpr(ifs.Condition, reg);
                RegisterStmtList(ifs.Body, reg);
                foreach (var ei in ifs.ElseIfs) RegisterStmtList(ei.Body, reg);
                if (ifs.ElseBody != null) RegisterStmtList(ifs.ElseBody, reg);
                break;
            case NumericForStmt nf:
                RegisterExpr(nf.Start, reg);
                RegisterExpr(nf.Limit, reg);
                if (nf.Step != null) RegisterExpr(nf.Step, reg);
                RegisterStmtList(nf.Body, reg);
                break;
            case GenericForStmt gf:
                foreach (var iter in gf.Iterators) RegisterExpr(iter, reg);
                RegisterStmtList(gf.Body, reg);
                break;
            case ReturnStmt ret:
                foreach (var v in ret.Values) RegisterExpr(v, reg);
                break;
            case ExportStmt exp:
                RegisterStmt(exp.Declaration, reg);
                break;
            case MatchStmt ms:
                RegisterExpr(ms.Scrutinee, reg);
                foreach (var arm in ms.Arms)
                {
                    if (arm.Pattern.ValueExpr != null) RegisterExpr(arm.Pattern.ValueExpr, reg);
                    if (arm.Guard != null) RegisterExpr(arm.Guard, reg);
                    RegisterStmtList(arm.Body, reg);
                }
                break;
            case DeferStmt ds:
                if (ds.Call != null) RegisterExpr(ds.Call, reg);
                if (ds.Block != null) RegisterStmtList(ds.Block, reg);
                break;
            case GuardStmt gs:
                RegisterExpr(gs.Condition, reg);
                if (gs.ElseExpr != null) RegisterExpr(gs.ElseExpr, reg);
                break;
            case ClassDecl cd:
                if (cd.Constructor != null)
                {
                    foreach (var p in cd.Constructor.Parameters) reg[p.ID] = p;
                    RegisterStmtList(cd.Constructor.Body, reg);
                    if (cd.Constructor.ReturnStmt != null) RegisterStmt(cd.Constructor.ReturnStmt, reg);
                }
                foreach (var method in cd.Methods)
                {
                    foreach (var p in method.Parameters) reg[p.ID] = p;
                    RegisterStmtList(method.Body, reg);
                    if (method.ReturnStmt != null) RegisterStmt(method.ReturnStmt, reg);
                }
                foreach (var accessor in cd.Accessors)
                {
                    foreach (var p in accessor.Parameters) reg[p.ID] = p;
                    RegisterStmtList(accessor.Body, reg);
                    if (accessor.ReturnStmt != null) RegisterStmt(accessor.ReturnStmt, reg);
                }
                foreach (var field in cd.Fields)
                {
                    if (field.DefaultValue != null) RegisterExpr(field.DefaultValue, reg);
                }
                break;
            case InterfaceDecl:
                break;
        }
    }

    private static void RegisterExpr(Expr expr, Dictionary<NodeID, Node> reg)
    {
        reg[expr.ID] = expr;
        switch (expr)
        {
            case ParenExpr pe: RegisterExpr(pe.Inner, reg); break;
            case BinaryExpr bin: RegisterExpr(bin.Left, reg); RegisterExpr(bin.Right, reg); break;
            case UnaryExpr un: RegisterExpr(un.Operand, reg); break;
            case DotAccessExpr dot: RegisterExpr(dot.Object, reg); break;
            case IndexAccessExpr idx: RegisterExpr(idx.Object, reg); RegisterExpr(idx.Index, reg); break;
            case FunctionCallExpr call:
                RegisterExpr(call.Callee, reg);
                foreach (var a in call.Arguments) RegisterExpr(a, reg);
                break;
            case MethodCallExpr mc:
                RegisterExpr(mc.Object, reg);
                foreach (var a in mc.Arguments) RegisterExpr(a, reg);
                break;
            case FunctionDefExpr fd:
                foreach (var p in fd.Parameters) reg[p.ID] = p;
                RegisterStmtList(fd.Body, reg);
                if (fd.ReturnStmt != null) RegisterStmt(fd.ReturnStmt, reg);
                break;
            case TableConstructorExpr tc:
                foreach (var f in tc.Fields)
                {
                    if (f.Key != null) RegisterExpr(f.Key, reg);
                    RegisterExpr(f.Value, reg);
                }
                break;
            case InterpolatedStringExpr interp:
                foreach (var p in interp.Parts)
                {
                    if (p is InterpExprPart ep) RegisterExpr(ep.Expression, reg);
                }
                break;
            case NonNilAssertExpr nna:
                RegisterExpr(nna.Inner, reg);
                break;
            case IncDecExpr incDec:
                RegisterExpr(incDec.Target, reg);
                break;
            case TypeCheckExpr tchk:
                RegisterExpr(tchk.Inner, reg);
                break;
            case TypeCastExpr tcast:
                RegisterExpr(tcast.Inner, reg);
                break;
            case TypeOfExpr tof:
                RegisterExpr(tof.Inner, reg);
                break;
            case InstanceOfExpr iof:
                RegisterExpr(iof.Inner, reg);
                break;
            case NewExpr ne:
                foreach (var a in ne.Arguments) RegisterExpr(a, reg);
                break;
            case SuperCallExpr sc:
                foreach (var a in sc.Arguments) RegisterExpr(a, reg);
                break;
        }
    }

    #endregion
}
