using System.Collections.Concurrent;
using Nebra.Compiler.Annotations;
using Nebra.IR;

namespace Nebra.LPS;

/// <summary>
/// One annotation that can be written in a file: its parameters, the declarations it may be put
/// on (every kind when null), the file defining it and what that file says about it.
/// </summary>
public sealed record AnnotationEntry(
    string Name,
    IReadOnlyCollection<AnnotationTargetKind>? Targets,
    IReadOnlyList<AnnotationParamSpec> Parameters,
    string? SourcePath,
    string? Documentation,
    bool TakesNames = false);

/// <summary>
/// The annotations a file can use: the compiler's built-in ones and every annotation the build
/// registered, from the project and from its dependencies. Also tells which kind of declaration
/// an annotation written on a given line would apply to.
/// </summary>
public static class AnnotationCatalog
{
    private static readonly ConcurrentDictionary<string, (DateTime Modified, string? Text)> Documentation = new(StringComparer.Ordinal);

    private static readonly AnnotationEntry[] Builtins =
    [
        new(BuiltinAnnotations.Side, null, [new AnnotationParamSpec("sides", "client | server | shared", null, true)], null,
            "Restricts where a declaration may be used, such as `@side(server)` or `@side(client, shared)`. Using it from a file of another side is an error.",
            TakesNames: true),
        new(BuiltinAnnotations.Reflectable, null, [], null,
            "Includes the declaration in runtime reflection when `[reflection] mode` is `\"annotated\"`."),
        new(BuiltinAnnotations.OverrideCtor, [AnnotationTargetKind.Class], [new AnnotationParamSpec("template", "string", null, true)], null,
            "Replaces how `new` constructs the class in the generated Lua. `$class` stands for the class and `$args` for the arguments."),
        ContractRuleEntry(ContractRules.Range, "Limits a number in a contract: `@range(0, 150)`, `@range(min = 0)`.", "min", "max"),
        ContractRuleEntry(ContractRules.Integer, "Requires a whole number in a contract."),
        ContractRuleEntry(ContractRules.Length, "Limits the length of a string or list in a contract: `@length(8)`, `@length(1, 32)`, `@length(max = 32)`.", "min", "max"),
        ContractRuleEntry(ContractRules.NonEmpty, "Requires a string or list in a contract to have content."),
        ContractRuleEntry(ContractRules.Pattern, "Requires a string in a contract to match a Lua pattern: `@pattern(\"^%a+$\")`.", "pattern"),
    ];

    private static AnnotationEntry ContractRuleEntry(string name, string documentation, params string[] parameters)
    {
        var specs = parameters.Select(parameter => new AnnotationParamSpec(parameter, parameter == "pattern" ? "string" : "number", null, parameter == "pattern")).ToList();
        return new AnnotationEntry(name, [AnnotationTargetKind.InterfaceField], specs, null, documentation);
    }

    public static IEnumerable<AnnotationEntry> All(AnalysisResult result)
    {
        return Builtins.Concat(result.Snapshot.Annotations.All.OrderBy(definition => definition.Name, StringComparer.Ordinal).Select(Entry));
    }

    public static AnnotationEntry? Find(AnalysisResult result, string name)
    {
        return All(result).FirstOrDefault(entry => entry.Name == name);
    }

    /// <summary>The annotation as it is written: its name and parameters, defaults included.</summary>
    public static string Signature(AnnotationEntry entry)
    {
        if (entry.Parameters.Count == 0) return "@" + entry.Name;
        if (entry.TakesNames) return $"@{entry.Name}({entry.Parameters[0].TypeName}, ...)";
        return $"@{entry.Name}({string.Join(", ", entry.Parameters.Select(Parameter))})";
    }

    public static string Parameter(AnnotationParamSpec parameter)
    {
        if (parameter.Required) return $"{parameter.Name}: {parameter.TypeName}";
        return parameter.DefaultValue == null
            ? $"{parameter.Name}: {parameter.TypeName}?"
            : $"{parameter.Name}: {parameter.TypeName} = {Literal(parameter.DefaultValue)}";
    }

    /// <summary>The declarations an annotation may be put on, in words.</summary>
    public static string TargetsText(AnnotationEntry entry)
    {
        if (entry.Targets == null) return "any declaration";
        return string.Join(", ", entry.Targets.Select(Describe));
    }

    public static string Describe(AnnotationTargetKind target)
    {
        return target switch
        {
            AnnotationTargetKind.Function => "functions",
            AnnotationTargetKind.LocalFunction => "local functions",
            AnnotationTargetKind.Variable => "variables",
            AnnotationTargetKind.Parameter => "parameters",
            AnnotationTargetKind.Enum => "enums",
            AnnotationTargetKind.EnumMember => "enum members",
            AnnotationTargetKind.Class => "classes",
            AnnotationTargetKind.ClassMethod => "class methods",
            AnnotationTargetKind.ClassField => "class fields",
            AnnotationTargetKind.ClassConstructor => "constructors",
            AnnotationTargetKind.ClassAccessor => "getters and setters",
            AnnotationTargetKind.Interface => "interfaces",
            AnnotationTargetKind.InterfaceMethod => "interface methods",
            AnnotationTargetKind.InterfaceField => "interface fields",
            AnnotationTargetKind.Extend => "extend blocks",
            AnnotationTargetKind.ExtensionMethod => "extension methods",
            _ => target.ToString()
        };
    }

    public static bool AppliesTo(AnnotationEntry entry, AnnotationTargetKind? target)
    {
        return target == null || entry.Targets == null || entry.Targets.Contains(target.Value);
    }

    /// <summary>
    /// The kind of declaration an annotation written on line <paramref name="line"/> (1-based)
    /// applies to: the first line below it that is not an annotation itself, read in the light
    /// of the class, interface or enum around it. Null when that line declares nothing.
    /// </summary>
    public static AnnotationTargetKind? TargetAt(AnalysisResult result, int line)
    {
        var lines = result.SourceText.Split('\n');
        var index = line;
        while (index < lines.Length && IsAnnotationOrBlank(lines[index])) index++;
        if (index >= lines.Length) return null;

        var text = lines[index].Trim();
        var container = ContainerAt(result.Hir.Body, index + 1);
        return container switch
        {
            ClassDecl => ClassMemberKind(text),
            InterfaceDecl => text.StartsWith("method ") || text.StartsWith("function ") || text.StartsWith("async ")
                ? AnnotationTargetKind.InterfaceMethod
                : AnnotationTargetKind.InterfaceField,
            EnumDecl => AnnotationTargetKind.EnumMember,
            ExtendDecl => AnnotationTargetKind.ExtensionMethod,
            _ => TopLevelKind(text)
        };
    }

    private static AnnotationTargetKind? TopLevelKind(string text)
    {
        var declaration = StripPrefixes(text, "export ");
        if (declaration.StartsWith("local function ") || declaration.StartsWith("local async function ")) return AnnotationTargetKind.LocalFunction;
        if (declaration.StartsWith("function ") || declaration.StartsWith("async function ")) return AnnotationTargetKind.Function;
        if (declaration.StartsWith("local ")) return AnnotationTargetKind.Variable;
        if (declaration.StartsWith("class ") || declaration.StartsWith("abstract class ")) return AnnotationTargetKind.Class;
        if (declaration.StartsWith("interface ") || declaration.StartsWith("contract ")) return AnnotationTargetKind.Interface;
        if (declaration.StartsWith("enum ")) return AnnotationTargetKind.Enum;
        if (declaration.StartsWith("extend ")) return AnnotationTargetKind.Extend;
        return null;
    }

    private static AnnotationTargetKind ClassMemberKind(string text)
    {
        var member = StripPrefixes(text, "static ", "protected ", "override ", "abstract ", "local ", "async ");
        if (member.StartsWith("constructor")) return AnnotationTargetKind.ClassConstructor;
        if (member.StartsWith("get ") || member.StartsWith("set ")) return AnnotationTargetKind.ClassAccessor;
        if (member.StartsWith("function ") || member.StartsWith("operator")) return AnnotationTargetKind.ClassMethod;
        return AnnotationTargetKind.ClassField;
    }

    private static string StripPrefixes(string text, params string[] prefixes)
    {
        var stripped = true;
        while (stripped)
        {
            stripped = false;
            foreach (var prefix in prefixes.Where(prefix => text.StartsWith(prefix, StringComparison.Ordinal)))
            {
                text = text[prefix.Length..].TrimStart();
                stripped = true;
            }
        }

        return text;
    }

    private static bool IsAnnotationOrBlank(string line)
    {
        var text = line.Trim();
        return text.Length == 0 || text.StartsWith('@') || text.StartsWith("--");
    }

    /// <summary>The class, interface, enum or extend block whose body holds line <paramref name="line"/>.</summary>
    private static Decl? ContainerAt(IEnumerable<Stmt> statements, int line)
    {
        foreach (var statement in statements)
        {
            var declaration = statement is ExportStmt export ? export.Declaration : statement as Decl;
            if (declaration is not (ClassDecl or InterfaceDecl or EnumDecl or ExtendDecl)) continue;
            if (declaration.Span.StartLn < line && line < declaration.Span.EndLn) return declaration;
        }

        return null;
    }

    /// <summary>
    /// The annotation name under the cursor: a word directly after an <c>@</c>, with the column
    /// it starts at (0-based, the <c>@</c> excluded). Read from the source, because applied
    /// annotations no longer appear in the analysed program.
    /// </summary>
    public static (string Name, int Start)? NameAt(string source, int line, int character)
    {
        var lines = source.Split('\n');
        if (line < 0 || line >= lines.Length) return null;

        var text = lines[line].TrimEnd('\r');
        var start = Math.Min(character, text.Length);
        while (start > 0 && IsNameCharacter(text[start - 1])) start--;
        var end = Math.Min(character, text.Length);
        while (end < text.Length && IsNameCharacter(text[end])) end++;

        if (start == 0 || text[start - 1] != '@' || end == start) return null;
        return (text[start..end], start);
    }

    /// <summary>
    /// The annotation whose argument list the cursor is in, with the arguments written before
    /// the cursor; null when the cursor is not inside <c>@Name(...)</c>.
    /// </summary>
    public static (string Name, List<string> Arguments)? CallAt(string source, int line, int character)
    {
        var lines = source.Split('\n');
        if (line < 0 || line >= lines.Length) return null;

        var text = lines[line].TrimEnd('\r');
        var before = text[..Math.Min(character, text.Length)];
        var open = OpenParenthesis(before);
        if (open <= 0) return null;

        var nameEnd = open;
        var nameStart = nameEnd;
        while (nameStart > 0 && IsNameCharacter(before[nameStart - 1])) nameStart--;
        if (nameStart == 0 || before[nameStart - 1] != '@' || nameStart == nameEnd) return null;

        return (before[nameStart..nameEnd], SplitArguments(before[(open + 1)..]));
    }

    /// <summary>The position of the parenthesis left open in <paramref name="text"/>, outside strings; -1 when none is.</summary>
    private static int OpenParenthesis(string text)
    {
        var open = new Stack<int>();
        char? quote = null;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != null)
            {
                if (character == quote) quote = null;
                continue;
            }

            switch (character)
            {
                case '"' or '\'':
                    quote = character;
                    break;
                case '(':
                    open.Push(index);
                    break;
                case ')' when open.Count > 0:
                    open.Pop();
                    break;
            }
        }

        return open.Count > 0 ? open.Peek() : -1;
    }

    private static List<string> SplitArguments(string text)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        char? quote = null;

        foreach (var character in text)
        {
            if (quote != null)
            {
                if (character == quote) quote = null;
                current.Append(character);
                continue;
            }

            if (character is '"' or '\'') quote = character;
            if (character is '(' or '{') depth++;
            if (character is ')' or '}') depth--;

            if (character == ',' && depth == 0)
            {
                arguments.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        arguments.Add(current.ToString().Trim());
        return arguments;
    }

    private static bool IsNameCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character == '_';
    }

    private static AnnotationEntry Entry(AnnotationDefinition definition)
    {
        return new AnnotationEntry(definition.Name, definition.Targets, definition.Parameters, definition.SourcePath, ReadDocumentation(definition.SourcePath));
    }

    /// <summary>
    /// The comment at the top of an annotation file, which describes the annotation. Read as
    /// plain Markdown: such comments usually open with an example like <c>@Event("Tick")</c>,
    /// which a LuaCATS reader would take for a tag.
    /// </summary>
    private static string? ReadDocumentation(string path)
    {
        DateTime modified;
        try
        {
            modified = File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return null;
        }

        if (Documentation.TryGetValue(path, out var cached) && cached.Modified == modified) return cached.Text;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return null;
        }

        var comment = lines
            .SkipWhile(line => line.Trim().Length == 0)
            .TakeWhile(line => line.TrimStart().StartsWith("---"))
            .Select(line => line.TrimStart()[3..].Trim())
            .ToList();

        var text = comment.Count == 0 ? null : string.Join("\n", comment);
        Documentation[path] = (modified, text);
        return text;
    }

    private static string Literal(object value)
    {
        return value switch
        {
            string text => $"\"{text}\"",
            bool flag => flag ? "true" : "false",
            _ => value.ToString() ?? "nil"
        };
    }
}
