using Nebra.Configuration;
using Nebra.IR;

namespace Nebra.Compiler.Annotations;

/// <summary>
/// Built-in compiler annotations that bypass the user-script
/// <see cref="AnnotationRegistry"/> pipeline. These are recognised by name
/// and consumed directly by specific compiler passes — they do not require
/// an annotation definition file and are not invoked at runtime.
/// </summary>
public static class BuiltinAnnotations
{
    public const string Side = "side";
    public const string OverrideCtor = "overrideCtor";
    public const string OverrideInherit = "overrideInherit";

    /// <summary>
    /// Marks a declaration for inclusion under <c>[reflection] mode = "annotated"</c>. Read
    /// directly by the reflection emitter, so it is a builtin like the others: it needs no
    /// definition file, and it has to survive the annotation pass to still be there when the
    /// emitter looks.
    /// </summary>
    public const string Reflectable = "reflectable";

    private static readonly HashSet<string> Names = new(StringComparer.Ordinal) { Side, OverrideCtor, OverrideInherit, Reflectable };

    public static bool IsBuiltin(string name) => Names.Contains(name);

    /// <summary>
    /// Filters the given annotation list down to the subset of builtin
    /// annotations. Used by <see cref="Passes.ApplyAnnotationsPass"/> to
    /// preserve builtin annotations on the decl after user-script
    /// annotations have been processed and cleared.
    /// </summary>
    public static List<Annotation> KeepBuiltins(List<Annotation> annotations)
    {
        if (annotations.Count == 0) return annotations;
        var kept = new List<Annotation>();
        foreach (var ann in annotations)
            if (IsBuiltin(ann.Name.Name)) kept.Add(ann);
        return kept;
    }

    /// <summary>
    /// Extracts the <see cref="Side"/> mask from <c>@side(client, server, ...)</c>
    /// annotations on a decl. Returns <see cref="Configuration.Side.All"/>
    /// when no <c>@side</c> annotation is present (the unannotated default —
    /// "available everywhere"). Diagnostics for unknown side names are
    /// emitted by the caller via the <paramref name="onUnknownName"/> hook so
    /// this helper stays free of <see cref="Diagnostics.DiagnosticsBag"/>
    /// dependencies.
    /// </summary>
    public static Side ExtractSide(List<Annotation> annotations, Action<Annotation, string>? onUnknownName = null)
    {
        if (annotations == null || annotations.Count == 0) return Configuration.Side.All;
        var found = Configuration.Side.None;
        var anyFound = false;
        foreach (var ann in annotations)
        {
            if (ann.Name.Name != Side) continue;
            anyFound = true;

            if (ann.Args.Count == 0)
            {
                onUnknownName?.Invoke(ann, "");
                continue;
            }

            foreach (var arg in ann.Args)
            {
                var name = ExtractNameLiteral(arg.Value);
                if (name == null)
                {
                    onUnknownName?.Invoke(ann, "<non-literal>");
                    continue;
                }

                var parsed = SideExtensions.ParseSideName(name);
                if (parsed == Configuration.Side.None)
                {
                    onUnknownName?.Invoke(ann, name);
                    continue;
                }

                found |= parsed;
            }
        }

        if (!anyFound) return Configuration.Side.All;
        return found == Configuration.Side.None ? Configuration.Side.All : found;
    }

    private static string? ExtractNameLiteral(Expr expr) => expr switch
    {
        NameExpr ne => ne.Name.Name,
        StringLiteralExpr s => s.Value,
        DotAccessExpr { Object: NameExpr } dot => dot.FieldName.Name,
        _ => null,
    };

    /// <summary>
    /// Extracts the format-string template from <c>@overrideCtor("...")</c> on
    /// a class declaration. Returns null when the annotation is absent or its
    /// argument is not a string literal. The template controls how
    /// <c>new ClassName(args)</c> lowers to Lua at codegen — see
    /// <see cref="Passes.CodegenPass"/> for the substitution rules:
    /// <list type="bullet">
    ///   <item><description><c>$class</c> → the resolved class name in Lua.</description></item>
    ///   <item><description><c>$args</c> → the comma-separated rendered arguments.</description></item>
    /// </list>
    /// </summary>
    public static string? ExtractOverrideCtor(List<Annotation>? annotations)
    {
        return ExtractTemplate(annotations, OverrideCtor);
    }

    /// <summary>
    /// Extracts the template from <c>@overrideInherit("...")</c> on a class declaration, or null
    /// when the annotation is absent or its argument is not a string literal. The template is the
    /// Lua expression creating the table of a class extending this one:
    /// <list type="bullet">
    ///   <item><description><c>$base</c> → the resolved name of the class being extended.</description></item>
    ///   <item><description><c>$name</c> → the name of the new class as a quoted string.</description></item>
    /// </list>
    /// </summary>
    public static string? ExtractOverrideInherit(List<Annotation>? annotations)
    {
        return ExtractTemplate(annotations, OverrideInherit);
    }

    private static string? ExtractTemplate(List<Annotation>? annotations, string annotationName)
    {
        if (annotations == null || annotations.Count == 0) return null;
        foreach (var ann in annotations)
        {
            if (ann.Name.Name != annotationName) continue;
            if (ann.Args.Count == 0) continue;
            if (ann.Args[0].Value is StringLiteralExpr sl) return sl.Value;
        }
        return null;
    }
}
