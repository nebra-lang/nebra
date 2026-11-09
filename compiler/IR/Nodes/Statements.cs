using Nebra.Diagnostics;

namespace Nebra.IR;

public sealed class AssignStmt(NodeID id, TextSpan span, List<Expr> targets, List<Expr> values) : Stmt(id, span)
{
    public List<Expr> Targets { get; } = targets;
    public List<Expr> Values { get; } = values;
}

public sealed class ExprStmt(NodeID id, TextSpan span, Expr expression) : Stmt(id, span)
{
    public Expr Expression { get; } = expression;
}

public sealed class LabelStmt(NodeID id, TextSpan span, NameRef name) : Stmt(id, span)
{
    public NameRef Name { get; } = name;
}

public sealed class BreakStmt(NodeID id, TextSpan span, int depth = 1) : Stmt(id, span)
{
    /// <summary>Number of enclosing loops to break out of. 1 = current loop only.</summary>
    public int Depth { get; } = depth;
}

public sealed class ContinueStmt(NodeID id, TextSpan span) : Stmt(id, span);

/// <summary>
/// Defers execution of a function call or block until the enclosing function returns.
/// Deferred actions execute in LIFO (last-defer-first) order.
/// </summary>
public sealed class DeferStmt(NodeID id, TextSpan span, Expr? call, List<Stmt>? block) : Stmt(id, span)
{
    /// <summary>Non-null when <c>defer fn()</c> form is used.</summary>
    public Expr? Call { get; } = call;
    /// <summary>Non-null when <c>defer do ... end</c> form is used.</summary>
    public List<Stmt>? Block { get; } = block;

    /// <summary>Whether a function body defers anything, see <see cref="In"/>.</summary>
    public static bool AnyIn(List<Stmt> body) => In(body).Any();

    /// <summary>
    /// The <c>defer</c> statements of a body: those in it or in one of its blocks, not those in a
    /// function nested in it, which keeps its own.
    /// </summary>
    public static IEnumerable<DeferStmt> In(List<Stmt> body)
    {
        foreach (var stmt in body)
        {
            var nested = stmt switch
            {
                DeferStmt defer => [defer],
                DoBlockStmt block => In(block.Body),
                WhileStmt loop => In(loop.Body),
                RepeatStmt loop => In(loop.Body),
                NumericForStmt loop => In(loop.Body),
                GenericForStmt loop => In(loop.Body),
                IfStmt branch => In(branch.Body).Concat(branch.ElseIfs.SelectMany(elseIf => In(elseIf.Body))).Concat(branch.ElseBody != null ? In(branch.ElseBody) : []),
                MatchStmt match => match.Arms.SelectMany(arm => In(arm.Body)),
                _ => Enumerable.Empty<DeferStmt>(),
            };
            foreach (var defer in nested) yield return defer;
        }
    }
}

/// <summary>
/// Early exit from a function when a condition is not met.
/// <c>guard x > 0</c> returns nothing (void functions).
/// <c>guard x > 0 else -1</c> returns the else expression.
/// </summary>
public sealed class GuardStmt(NodeID id, TextSpan span, Expr condition, Expr? elseExpr) : Stmt(id, span)
{
    public Expr Condition { get; } = condition;
    public Expr? ElseExpr { get; } = elseExpr;
}

public sealed class GotoStmt(NodeID id, TextSpan span, NameRef labelName) : Stmt(id, span)
{
    public NameRef LabelName { get; } = labelName;
}

public sealed class DoBlockStmt(NodeID id, TextSpan span, List<Stmt> body) : Stmt(id, span)
{
    public List<Stmt> Body { get; } = body;
}

public sealed class WhileStmt(NodeID id, TextSpan span, Expr condition, List<Stmt> body) : Stmt(id, span)
{
    public Expr Condition { get; } = condition;
    public List<Stmt> Body { get; } = body;
}

public sealed class RepeatStmt(NodeID id, TextSpan span, List<Stmt> body, Expr condition) : Stmt(id, span)
{
    public List<Stmt> Body { get; } = body;
    public Expr Condition { get; } = condition;
}

public sealed class IfStmt(
    NodeID id, TextSpan span,
    Expr condition, List<Stmt> body,
    List<ElseIfClause> elseIfs, List<Stmt>? elseBody
) : Stmt(id, span)
{
    public Expr Condition { get; } = condition;
    public List<Stmt> Body { get; } = body;
    public List<ElseIfClause> ElseIfs { get; } = elseIfs;
    public List<Stmt>? ElseBody { get; } = elseBody;
}

public sealed class NumericForStmt(
    NodeID id, TextSpan span,
    NameRef varName, Expr start, Expr limit, Expr? step,
    List<Stmt> body
) : Stmt(id, span)
{
    public NameRef VarName { get; } = varName;
    public Expr Start { get; } = start;
    public Expr Limit { get; } = limit;
    public Expr? Step { get; } = step;
    public List<Stmt> Body { get; } = body;
}

public sealed class GenericForStmt(
    NodeID id, TextSpan span,
    List<NameRef> varNames, List<Expr> iterators,
    List<Stmt> body
) : Stmt(id, span)
{
    public List<NameRef> VarNames { get; } = varNames;
    public List<Expr> Iterators { get; } = iterators;
    public List<Stmt> Body { get; } = body;

    /// <summary>
    /// Set when the loop runs over a value whose type has an <c>iterator()</c> method, as in
    /// <c>for i, item in collection do</c>: the loop iterates what <c>collection:iterator()</c> returns.
    /// </summary>
    public bool CallsIteratorMethod { get; set; }

    /// <summary>
    /// Set when the loop runs over a single value typed <c>any</c> that is not a call: whether it
    /// has an <c>iterator()</c> method is only known at runtime, so the loop asks there.
    /// </summary>
    public bool ChecksIteratorAtRuntime { get; set; }

    /// <summary>
    /// Set when the loop runs its single variable over a range, as in <c>for i in 1..10 do</c>,
    /// which lowers to Lua's numeric <c>for</c>.
    /// </summary>
    public bool RunsOverRange { get; set; }
}

public sealed class ReturnStmt(NodeID id, TextSpan span, List<Expr> values) : Stmt(id, span)
{
    public List<Expr> Values { get; } = values;
}

public sealed class MatchStmt(NodeID id, TextSpan span, Expr scrutinee, List<MatchArm> arms) : Stmt(id, span)
{
    public Expr Scrutinee { get; } = scrutinee;
    public List<MatchArm> Arms { get; } = arms;
}

public sealed class ImportStmt(NodeID id, TextSpan span, ImportKind kind, NameRef module) : Stmt(id, span)
{
    public ImportKind Kind { get; } = kind;
    public NameRef Module { get; } = module;
    public List<ImportSpecifier> Specifiers { get; init; } = [];
    public NameRef? Alias { get; init; }

    /// <summary>
    /// True when this import was injected by <c>[code] auto_imports</c> with
    /// <c>auto_imports_emit = false</c>. Name resolution still sees the
    /// binding but <see cref="Passes.CodegenPass"/> skips the <c>require</c>
    /// call so the host runtime (which already pre-loaded the module) doesn't
    /// re-execute it.
    /// </summary>
    public bool IsTypeOnly { get; init; }

    /// <summary>
    /// Written <c>import type</c>: the names are only used where types are written, so nothing is
    /// loaded at runtime, and the import may be part of a cycle of imports.
    /// </summary>
    public bool IsTypeImport { get; set; }

    /// <summary>
    /// Written <c>export { ... } from</c>: the names are imported and exported again under the
    /// name each specifier gives them, so a module can gather the exports of others.
    /// </summary>
    public bool IsReExport { get; init; }

    /// <summary>The glob this import was expanded from, when it was written as <c>import "models/*.neb"</c>.</summary>
    public string? ExpandedFrom { get; init; }
}

public sealed class ExportStmt(NodeID id, TextSpan span, Decl declaration) : Stmt(id, span)
{
    public Decl Declaration { get; } = declaration;
}

/// <summary>
/// <c>try ... catch (err) ... finally ... end</c>. The body runs protected: an error it raises
/// runs the catch clause, if any, and the finally block runs either way. Without a catch clause
/// an error is raised again after the finally block, and without both it is ignored.
/// </summary>
public sealed class TryStmt(NodeID id, TextSpan span, List<Stmt> body, CatchClause? catchClause, List<Stmt>? finallyBody) : Stmt(id, span)
{
    public List<Stmt> Body { get; } = body;
    public CatchClause? Catch { get; } = catchClause;
    public List<Stmt>? Finally { get; } = finallyBody;

    /// <summary>The body, the catch body and the finally block, those that are written.</summary>
    public IEnumerable<List<Stmt>> Blocks
    {
        get
        {
            yield return Body;
            if (Catch != null) yield return Catch.Body;
            if (Finally != null) yield return Finally;
        }
    }
}

/// <summary>The <c>catch</c> of a <see cref="TryStmt"/>, with the name the error is bound to, if any.</summary>
public sealed class CatchClause(NodeID id, TextSpan span, NameRef? name, List<Stmt> body) : Node(id, span)
{
    public NameRef? Name { get; } = name;
    public List<Stmt> Body { get; } = body;
}
