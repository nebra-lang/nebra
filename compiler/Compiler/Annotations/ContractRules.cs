using System.Globalization;
using System.Text;
using Nebra.IR;
using Nebra.Runtime;

namespace Nebra.Compiler.Annotations;

public enum ContractRuleKind
{
    Range,
    Integer,
    Length,
    NonEmpty,
    Pattern
}

/// <summary>
/// A check written on a field of a contract, such as <c>@range(0, 150)</c>. A literal is checked
/// at compile time, any other value when it is tested with <c>is</c>.
/// </summary>
public sealed record ContractRule(ContractRuleKind Kind, string Name, double? Minimum = null, double? Maximum = null, string? Pattern = null)
{
    /// <summary>What a value has to be to pass, as the end of a sentence about the field.</summary>
    public string Requirement => Kind switch
    {
        ContractRuleKind.Range => "must be " + Bounds(),
        ContractRuleKind.Integer => "must be a whole number",
        ContractRuleKind.Length => "must have a length " + Bounds(),
        ContractRuleKind.NonEmpty => "must not be empty",
        _ => $"must match the pattern '{Pattern}'"
    };

    /// <summary>Whether <paramref name="value"/> is known at compile time and fails this rule.</summary>
    public bool Rejects(Expr value)
    {
        return Kind switch
        {
            ContractRuleKind.Range => NumberOf(value) is { } number && !IsWithin(number),
            ContractRuleKind.Integer => NumberOf(value) is { } number && number % 1 != 0,
            ContractRuleKind.Length => LengthOf(value) is { } length && !IsWithin(length),
            ContractRuleKind.NonEmpty => LengthOf(value) == 0,
            _ => value is StringLiteralExpr text && ContractRules.Matches(text.Value, Pattern!) == false
        };
    }

    /// <summary>The Lua condition that holds when <paramref name="value"/> passes this rule.</summary>
    public string LuaCondition(string value)
    {
        return Kind switch
        {
            ContractRuleKind.Range => BoundsCondition(value),
            ContractRuleKind.Integer => $"{value} % 1 == 0",
            ContractRuleKind.Length => BoundsCondition("#" + value),
            ContractRuleKind.NonEmpty => $"#{value} > 0",
            _ => $"string.find({value}, {LiteralValue.Of(Pattern!).Text}) ~= nil"
        };
    }

    private bool IsWithin(double value)
    {
        return (Minimum == null || value >= Minimum) && (Maximum == null || value <= Maximum);
    }

    private string Bounds()
    {
        if (Minimum == Maximum) return "of " + Format(Minimum!.Value);
        if (Maximum == null) return "of at least " + Format(Minimum!.Value);
        if (Minimum == null) return "of at most " + Format(Maximum.Value);

        return $"between {Format(Minimum.Value)} and {Format(Maximum.Value)}";
    }

    private string BoundsCondition(string value)
    {
        var conditions = new List<string>();
        if (Minimum != null) conditions.Add($"{value} >= {Format(Minimum.Value)}");
        if (Maximum != null) conditions.Add($"{value} <= {Format(Maximum.Value)}");

        return string.Join(" and ", conditions);
    }

    private static string Format(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static double? NumberOf(Expr value)
    {
        var literal = LiteralValue.Of(value);
        return literal?.Kind == LiteralKind.Number ? double.Parse(literal.Text, CultureInfo.InvariantCulture) : null;
    }

    private static double? LengthOf(Expr value)
    {
        return value switch
        {
            StringLiteralExpr text => Encoding.UTF8.GetByteCount(text.Value),
            TableConstructorExpr table when table.Fields.All(field => field.Kind == TableFieldKind.Positional) => table.Fields.Count,
            _ => null
        };
    }
}

/// <summary>Reads the rules written on the fields of a contract.</summary>
public static class ContractRules
{
    public const string Range = "range";
    public const string Integer = "integer";
    public const string Length = "length";
    public const string NonEmpty = "nonEmpty";
    public const string Pattern = "pattern";

    private const string PatternCheck = "function matches(text, pattern) return string.find(text, pattern) ~= nil end";

    /// <summary>The rule <paramref name="annotation"/> stands for, or <c>null</c> with the <paramref name="problem"/> in how it is written.</summary>
    public static ContractRule? Parse(Annotation annotation, out string? problem)
    {
        problem = null;
        var name = annotation.Name.Name;
        switch (name)
        {
            case Range:
                return ParseBounds(annotation, ContractRuleKind.Range, out problem);
            case Length:
                return ParseBounds(annotation, ContractRuleKind.Length, out problem);
            case Integer:
                return ParseFlag(annotation, ContractRuleKind.Integer, out problem);
            case NonEmpty:
                return ParseFlag(annotation, ContractRuleKind.NonEmpty, out problem);
            case Pattern:
                return ParsePattern(annotation, out problem);
            default:
                problem = $"is not a rule of a contract; the rules are @{Range}, @{Integer}, @{Length}, @{NonEmpty} and @{Pattern}";
                return null;
        }
    }

    /// <summary>Whether <paramref name="text"/> matches a Lua pattern; <c>null</c> when the pattern is malformed.</summary>
    public static bool? Matches(string text, string pattern)
    {
        using var runtime = NebraRuntime.CreateSandboxed();
        if (!runtime.RunChunk(PatternCheck, "pattern")) return null;

        var result = runtime.CallGlobalFunction("matches", [text, pattern], out var error);
        return error == null ? result is true : null;
    }

    private static ContractRule? ParseFlag(Annotation annotation, ContractRuleKind kind, out string? problem)
    {
        problem = annotation.Args.Count == 0 ? null : "takes no arguments";
        return problem == null ? new ContractRule(kind, annotation.Name.Name) : null;
    }

    private static ContractRule? ParsePattern(Annotation annotation, out string? problem)
    {
        problem = "takes one string, a Lua pattern";
        if (annotation.Args is not [{ Value: StringLiteralExpr pattern }]) return null;

        problem = "has a malformed Lua pattern";
        if (Matches("", pattern.Value) == null) return null;

        problem = null;
        return new ContractRule(ContractRuleKind.Pattern, annotation.Name.Name, Pattern: pattern.Value);
    }

    private static ContractRule? ParseBounds(Annotation annotation, ContractRuleKind kind, out string? problem)
    {
        var positional = annotation.Args.Where(argument => argument.Name == null).ToList();
        var minimum = Bound(annotation, "min", positional, 0);
        var maximum = Bound(annotation, "max", positional, 1);
        var exact = kind == ContractRuleKind.Length && annotation.Args.Count == 1 && positional.Count == 1;
        if (exact) maximum = minimum;

        problem = BoundsProblem(annotation, minimum, maximum);
        return problem == null ? new ContractRule(kind, annotation.Name.Name, minimum, maximum) : null;
    }

    private static string? BoundsProblem(Annotation annotation, double? minimum, double? maximum)
    {
        var known = annotation.Args.All(argument => argument.Name is null or "min" or "max" && LiteralNumber(argument.Value) != null);
        if (!known || annotation.Args.Count is 0 or > 2 || (minimum == null && maximum == null)) return "takes a minimum and a maximum number";

        return minimum > maximum ? "has a minimum above its maximum" : null;
    }

    private static double? Bound(Annotation annotation, string name, List<AnnotationArg> positional, int index)
    {
        var argument = annotation.Args.FirstOrDefault(candidate => candidate.Name == name)
                       ?? (index < positional.Count ? positional[index] : null);
        return argument == null ? null : LiteralNumber(argument.Value);
    }

    private static double? LiteralNumber(Expr value)
    {
        var literal = LiteralValue.Of(value);
        return literal?.Kind == LiteralKind.Number ? double.Parse(literal.Text, CultureInfo.InvariantCulture) : null;
    }
}
