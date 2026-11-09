using Nebra.Compiler.Codegen;
using Nebra.IR;

namespace Nebra.Compiler.Passes;

public sealed partial class CodegenPass
{
    /// <summary>
    /// The type of <paramref name="cd"/> when its host creates the class through
    /// <c>@overrideInherit</c>; <c>null</c> for a class Nebra lays out itself.
    /// </summary>
    private static ClassType? HostInheritedClass(PackageContext pkg, ClassDecl cd)
    {
        if (cd.BaseClass == null) return null;
        if (!pkg.Syms.GetByID(cd.Name.Sym, out var symbol)) return null;
        if (!pkg.Types.GetByID(symbol.Type, out var type)) return null;

        return type is ClassType { EffectiveInheritTemplate: not null } classType ? classType : null;
    }

    private static string ExpandInheritTemplate(ClassType classType, string baseName, string className)
    {
        return classType.EffectiveInheritTemplate!
            .Replace("$base", baseName)
            .Replace("$name", $"\"{className}\"");
    }

    /// <summary>
    /// Emits the constructors of a class created by its host as <c>Constructor</c> methods, which
    /// the host calls on the instance it spawns. Overloads get one method each and a
    /// <c>Constructor</c> that dispatches on the arguments it receives.
    /// </summary>
    private void EmitHostInheritedConstructors(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd,
        ClassType classType, string className, string baseName)
    {
        var overloads = classType.ConstructorOverloads;
        if (cd.Constructors.Count < 2 || overloads.Count != cd.Constructors.Count || !OverloadSignature.AreDistinct(overloads, false))
        {
            EmitHostInheritedConstructor(ctx, pkg, gen, cd, cd.Constructor, classType, $"{className}:Constructor", baseName);
            return;
        }

        var branches = new List<OverloadBranch>();
        for (var i = 0; i < cd.Constructors.Count; i++)
        {
            var variant = OverloadSignature.VariantName("Constructor", overloads[i], false);
            EmitHostInheritedConstructor(ctx, pkg, gen, cd, cd.Constructors[i], classType, $"{className}:{variant}", baseName);
            branches.Add(new OverloadBranch(overloads[i], false, $"{className}.{variant}(self, ...)"));
        }

        EmitOverloadDispatcher(gen, $"function {className}:Constructor(...)", "Constructor", branches);
    }

    private void EmitHostInheritedConstructor(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd,
        ClassConstructorNode? ctor, ClassType classType, string functionName, string baseName)
    {
        var parameters = ctor == null
            ? "..."
            : string.Join(", ", ctor.Parameters.Select(parameter => ResolveName(ctx, pkg, parameter.Name)));
        gen.WriteLine($"function {functionName}({parameters})");
        gen.Indent();

        if (ctor != null)
        {
            EmitDefaultParamPreamble(ctx, pkg, gen, ctor.Parameters);
        }

        if (ctor == null || !HasSuperCall(ctor.Body))
        {
            EmitHostSuperConstructor(ctx, pkg, gen, classType, baseName, [], ctor == null);
            EmitInstanceFieldDefaults(ctx, pkg, gen, cd);
        }

        if (ctor != null)
        {
            EmitHostInheritedConstructorBody(ctx, pkg, gen, cd, ctor, classType, baseName);
        }

        gen.Dedent();
        gen.WriteLine("end");
        gen.WriteSemicolon();
    }

    private void EmitHostInheritedConstructorBody(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd,
        ClassConstructorNode ctor, ClassType classType, string baseName)
    {
        foreach (var statement in ctor.Body)
        {
            if (statement is ExprStmt { Expression: SuperCallExpr superCall })
            {
                EmitHostSuperConstructor(ctx, pkg, gen, classType, baseName, superCall.Arguments, false);
                EmitInstanceFieldDefaults(ctx, pkg, gen, cd);
                continue;
            }

            EmitStmt(ctx, pkg, gen, statement);
        }

        if (ctor.ReturnStmt != null)
        {
            EmitReturn(ctx, pkg, gen, ctor.ReturnStmt);
        }
    }

    /// <summary>
    /// Emits the call finishing a spawn: the constructor of a base class compiled from source
    /// through that class, the one of the host through <c>self.Super</c>.
    /// </summary>
    private void EmitHostSuperConstructor(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassType classType,
        string baseName, List<Expr> arguments, bool forwardsVarargs)
    {
        var isSourceBase = classType.BaseClass is { IsCompiledFromSource: true };
        gen.Write(isSourceBase ? $"{baseName}.Constructor(self" : "self.Super:Constructor(");

        var separator = isSourceBase ? ", " : "";
        if (forwardsVarargs)
        {
            gen.Write(separator + "...");
        }
        else if (arguments.Count > 0)
        {
            gen.Write(separator);
            EmitExprList(ctx, pkg, gen, arguments);
        }

        gen.Write(")");
        gen.NewLine();
        gen.WriteSemicolon();
    }

    /// <summary>
    /// Whether a <c>super:method()</c> call of a class created by its host reaches a method of
    /// the host rather than one of a base class compiled from source.
    /// </summary>
    private bool CallsHostMethod(MethodCallExpr call)
    {
        if (_hostInheritedClass == null) return false;

        for (var current = _hostInheritedClass.BaseClass; current != null; current = current.BaseClass)
        {
            if (!current.IsCompiledFromSource) return true;
            if (current.Methods.ContainsKey(call.MethodName.Name)) return false;
        }

        return true;
    }
}
