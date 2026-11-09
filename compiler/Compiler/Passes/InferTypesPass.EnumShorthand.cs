using Nebra.Diagnostics;
using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

public sealed partial class InferTypesPass
{
    /// <summary>
    /// The names used as values that have no declaration in scope, in the order they were met.
    /// Each one an enum is expected for reads as a member of it; the rest are reported once
    /// every body is resolved.
    /// </summary>
    private readonly Dictionary<NameExpr, PassContext> _unresolvedNames = [];


    /// <summary>
    /// The type of a name without a declaration in scope: the enum it was read as a member of, or
    /// <c>any</c> until one is expected for it.
    /// </summary>
    private TypID SynthesizeUnresolvedName(PassContext pc, NameExpr name)
    {
        if (name.Enum != null) return name.Enum.ID;
        _unresolvedNames[name] = pc;
        return pc.Types.PrimAny.ID;
    }

    /// <summary>
    /// The enum type of <paramref name="value"/> where a value of <paramref name="expected"/> is
    /// wanted and <paramref name="value"/> is a name without a declaration that names a member of
    /// the one enum expected, as <c>RED</c> in <c>local color: Color = RED</c>; <c>null</c> otherwise.
    /// </summary>
    private TypID? EnumMemberFor(PassContext pc, Expr value, TypID expected)
    {
        if (value is not NameExpr { IsUnresolved: true } name || expected == TypID.Invalid) return null;
        if (name.Enum != null) return name.Enum.ID;

        var candidates = EnumsIn(pc, expected)
            .Select(enumType => (Enum: enumType, Member: enumType.Members.FirstOrDefault(member => member.Name == name.Name.Name)))
            .Where(candidate => candidate.Member != null)
            .DistinctBy(candidate => candidate.Enum.ID)
            .ToList();
        if (candidates.Count != 1) return null;

        name.Enum = candidates[0].Enum;
        name.EnumMember = candidates[0].Member;
        name.Type = name.Enum.ID;
        return name.Enum.ID;
    }

    private IEnumerable<EnumType> EnumsIn(PassContext pc, TypID expected)
    {
        if (!pc.Pkg!.Types.GetByID(Evaluate(pc, expected), out var type)) return [];

        return type switch
        {
            EnumType enumType => [enumType],
            UnionType union => union.Types.SelectMany(member => EnumsIn(pc, member.ID)),
            TypeParameterType { ExtendsBound: { } bound } => EnumsIn(pc, bound),
            _ => [],
        };
    }

    /// <summary>
    /// Reads an operand of <c>==</c> or <c>~=</c> that is a name without a declaration as a member
    /// of the enum the other operand is, as <c>color == RED</c>.
    /// </summary>
    private void AdoptComparedEnumMember(PassContext pc, BinaryExpr bin, TypID left, TypID right)
    {
        if (bin.Op is not (BinaryOp.Eq or BinaryOp.Neq)) return;

        EnumMemberFor(pc, bin.Right, left);
        EnumMemberFor(pc, bin.Left, right);
    }

    /// <summary>Reports every name without a declaration that no enum was expected for.</summary>
    private void ReportUnresolvedNames()
    {
        foreach (var (name, pc) in _unresolvedNames)
        {
            if (name.Enum == null) pc.Diag.Report(name.Name.Span, DiagnosticCode.ErrUndeclaredSymbol, name.Name.Name);
        }

        _unresolvedNames.Clear();
    }
}
