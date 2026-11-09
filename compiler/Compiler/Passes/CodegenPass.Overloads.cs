using Nebra.Compiler.Codegen;
using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

public sealed partial class CodegenPass
{
    private readonly Dictionary<SymID, string> _overloadVariants = [];
    private readonly Dictionary<SymID, string> _overloadDispatchers = [];

    /// <summary>
    /// One overload a dispatcher can pick: its signature and the Lua call, written with <c>...</c>
    /// for the arguments, that reaches it.
    /// </summary>
    private sealed record OverloadBranch(FunctionType Function, bool HasSelf, string Call);

    /// <summary>
    /// The overload groups among <paramref name="stmts"/>: functions of one name declared in the
    /// same statement list, in declaration order.
    /// </summary>
    private List<List<Stmt>> FunctionOverloadGroups(PassContext ctx, PackageContext pkg, List<Stmt> stmts) =>
        stmts
            .Select(stmt => (Stmt: stmt, Name: FunctionDeclName(stmt)?.Name))
            .Where(entry => entry.Name != null)
            .GroupBy(entry => entry.Name!)
            .Where(group => group.Count() > 1)
            .Select(group => group.Select(entry => entry.Stmt).ToList())
            .ToList();

    /// <summary>
    /// Names the Lua function of every overload in <paramref name="group"/> after the first one's
    /// emitted name, which the dispatcher takes, and remembers both for the names that refer to an
    /// overload. Returns <c>null</c> when two overloads share their parameter types, which leaves
    /// the group to the dispatcher alone.
    /// </summary>
    private List<string>? RegisterOverloadVariants(PassContext ctx, PackageContext pkg, List<Stmt> group)
    {
        var overloads = group.Select(stmt => FunctionOverload(ctx, pkg, stmt)).ToList();
        if (overloads.Any(overload => overload.Function == null)) return null;
        if (!OverloadSignature.AreDistinct(overloads.Select(overload => overload.Function!), false)) return null;

        var dispatcher = ResolveName(ctx, pkg, overloads[0].Name);
        var variants = new List<string>();
        foreach (var overload in overloads)
        {
            var variant = OverloadSignature.VariantName(dispatcher, overload.Function!, false);
            _overloadVariants[overload.Name.Sym] = variant;
            _overloadDispatchers[overload.Name.Sym] = dispatcher;
            variants.Add(variant);
        }

        return variants;
    }

    private (NameRef Name, FunctionType? Function) FunctionOverload(PassContext ctx, PackageContext pkg, Stmt stmt)
    {
        var name = (stmt is ExportStmt export ? export.Declaration : stmt) switch
        {
            FunctionDecl declaration => declaration.NamePath[0],
            LocalFunctionDecl local => local.Name,
            _ => throw new InvalidOperationException($"Unexpected overload declaration: {stmt.GetType().Name}"),
        };
        var function = pkg.Syms.GetByID(name.Sym, out var symbol) && ctx.Types.GetByID(symbol.Type, out var type)
            ? type as FunctionType
            : null;
        return (name, function);
    }

    private void EmitOverloadedFunction(PassContext ctx, PackageContext pkg, LuaGenerator gen, string name, List<Stmt> overloads)
    {
        var variants = RegisterOverloadVariants(ctx, pkg, overloads);
        if (variants == null)
        {
            EmitInlineOverloads(ctx, pkg, gen, name, overloads);
            return;
        }

        var unhoisted = variants.Where(variant => !IsHoisted(variant)).ToList();
        if (unhoisted.Count > 0)
        {
            gen.WriteLine("local " + string.Join(", ", unhoisted));
            gen.WriteSemicolon();
        }

        var branches = new List<OverloadBranch>();
        for (var i = 0; i < overloads.Count; i++)
        {
            var declaration = overloads[i] is ExportStmt export ? export.Declaration : overloads[i];
            var (parameters, body, returnStmt, isAsync) = declaration switch
            {
                FunctionDecl function => (function.Parameters, function.Body, function.ReturnStmt, function.IsAsync),
                LocalFunctionDecl local => (local.Parameters, local.Body, local.ReturnStmt, local.IsAsync),
                _ => throw new InvalidOperationException($"Unexpected overload declaration: {declaration.GetType().Name}"),
            };

            gen.Write("function " + variants[i] + "(");
            EmitParamList(ctx, pkg, gen, parameters);
            EmitAsyncDoneParam(gen, isAsync, parameters.Count > 0);
            gen.Write(")");
            gen.NewLine();
            gen.Indent();
            EmitFuncBodyContent(ctx, pkg, gen, parameters, body, returnStmt, isAsync);
            gen.Dedent();
            gen.WriteLine("end");
            gen.WriteSemicolon();

            branches.Add(new OverloadBranch(FunctionOverload(ctx, pkg, overloads[i]).Function!, false, variants[i] + "(...)"));
        }

        var isLocal = overloads.Any(stmt => (stmt is ExportStmt export ? export.Declaration : stmt) is LocalFunctionDecl);
        var header = (isLocal && !IsHoisted(name) ? "local function " : "function ") + name + "(...)";
        EmitOverloadDispatcher(gen, header, name, branches);
    }

    /// <summary>
    /// Emits the instance or static method <paramref name="name"/> of a class whose overloads are
    /// split: each overload the class declares under its variant name, then a dispatcher under the
    /// plain name covering every overload visible on the class, inherited ones included.
    /// </summary>
    private void EmitSplitMethods(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd,
        ClassType classType, string className, string? baseName, string name, bool isStatic)
    {
        var declared = cd.Methods.Where(method => method.IsStatic == isStatic && method.Name.Name == name).ToList();
        var functions = isStatic ? classType.StaticMethodOverloads[name] : classType.MethodOverloads[name];
        var separator = isStatic ? "." : ":";

        for (var i = 0; i < declared.Count; i++)
        {
            var method = declared[i];
            gen.Write("function " + className + separator + OverloadSignature.VariantName(name, functions[i], !isStatic) + "(");
            EmitParamList(ctx, pkg, gen, method.Parameters);
            EmitAsyncDoneParam(gen, method.IsAsync, method.Parameters.Count > 0);
            gen.Write(")");
            gen.NewLine();
            gen.Indent();
            if (method.IsAbstract)
            {
                gen.WriteLine($"error(\"Abstract method '{name}' must be implemented\")");
                gen.WriteSemicolon();
            }
            else
            {
                EmitFuncBodyContent(ctx, pkg, gen, method.Parameters, method.Body, method.ReturnStmt, method.IsAsync);
            }
            gen.Dedent();
            gen.WriteLine("end");
            gen.WriteSemicolon();
        }

        var branches = isStatic
            ? functions.Select(function => new OverloadBranch(function, false,
                $"{className}.{OverloadSignature.VariantName(name, function, false)}(...)")).ToList()
            : OverloadSignature.InstanceOverloads(classType, name)
                .Select(overload => new OverloadBranch(overload.Function, true, InheritedOverloadCall(overload.Owner, baseName, name, overload.Function)))
                .ToList();
        EmitOverloadDispatcher(gen, $"function {className}{separator}{name}(...)", name, branches);
    }

    /// <summary>
    /// The call a dispatcher makes for an instance overload declared by <paramref name="owner"/>.
    /// A split owner holds it under its variant name, which a subclass overriding it shares. An
    /// owner keeping it under the plain name is reached through the base class, unless a subclass
    /// has overridden it under the variant name since.
    /// </summary>
    private static string InheritedOverloadCall(ClassType owner, string? baseName, string name, FunctionType function)
    {
        var variant = OverloadSignature.VariantName(name, function, true);
        return OverloadSignature.IsSplit(owner, name, false) || baseName == null
            ? $"self:{variant}(...)"
            : $"(self.{variant} or {baseName}.{name})(self, ...)";
    }

    /// <summary>
    /// Emits a function under <paramref name="header"/> that calls the first of
    /// <paramref name="branches"/> whose parameters fit the arguments it receives, testing the
    /// more specific overloads first.
    /// </summary>
    private void EmitOverloadDispatcher(LuaGenerator gen, string header, string name, List<OverloadBranch> branches)
    {
        var ordered = branches.OrderByDescending(Specificity).ToList();
        var arity = ordered.Max(branch => ParametersOf(branch).Count);

        gen.WriteLine(header);
        gen.Indent();
        gen.WriteLine("local __n = select(\"#\", ...)");
        gen.WriteSemicolon();
        if (arity > 0)
        {
            gen.WriteLine("local " + string.Join(", ", Enumerable.Range(1, arity).Select(index => $"__a{index}")) + " = ...");
            gen.WriteSemicolon();
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            gen.WriteLine((i == 0 ? "if " : "elseif ") + BranchCondition(gen, ordered, i) + " then");
            gen.Indent();
            gen.WriteLine("return " + ordered[i].Call);
            gen.WriteSemicolon();
            gen.Dedent();
        }

        gen.WriteLine("end");
        gen.WriteLine($"error(\"no overload of '{name}' takes these arguments\")");
        gen.WriteSemicolon();
        gen.Dedent();
        gen.WriteLine("end");
        gen.WriteSemicolon();
    }

    private static List<Type> ParametersOf(OverloadBranch branch) =>
        branch.Function.ParamTypes.Skip(branch.HasSelf ? 1 : 0).ToList();

    private string BranchCondition(LuaGenerator gen, List<OverloadBranch> branches, int index)
    {
        var branch = branches[index];
        var parameters = ParametersOf(branch);
        var required = branch.Function.MinParamCount - (branch.HasSelf ? 1 : 0);
        var maximum = parameters.Count + (branch.Function.IsAsync ? 1 : 0);

        var conditions = new List<string>();
        if (branch.Function.IsVararg)
            conditions.Add($"__n >= {required}");
        else if (required == maximum)
            conditions.Add($"__n == {required}");
        else
            conditions.Add($"__n >= {required} and __n <= {maximum}");

        for (var position = 0; position < parameters.Count; position++)
        {
            var precise = branches.Where((_, other) => other != index)
                .Any(other => position < ParametersOf(other).Count && IsTableValue(ParametersOf(other)[position]));
            var check = RuntimeCheck(gen, parameters[position], $"__a{position + 1}", precise);
            if (check == null) continue;

            conditions.Add(position >= required ? $"(__a{position + 1} == nil or {check})" : check);
        }

        return string.Join(" and ", conditions);
    }

    /// <summary>
    /// A Lua condition testing that <paramref name="value"/> holds a <paramref name="type"/>, or
    /// <c>null</c> when nothing can be tested at runtime. Classes and interfaces are only told
    /// apart from other tables when <paramref name="precise"/>, since another overload takes a
    /// table at the same position.
    /// </summary>
    private string? RuntimeCheck(LuaGenerator gen, Type type, string value, bool precise)
    {
        switch (type)
        {
            case UnionType union:
            {
                var checks = union.Types.Select(member => member.Kind == TypeKind.PrimitiveNil
                    ? $"{value} == nil"
                    : RuntimeCheck(gen, member, value, precise)).ToList();
                return checks.Any(check => check == null) ? null : "(" + string.Join(" or ", checks) + ")";
            }
            case ClassType classType when precise && classType.NearestIsTemplate == null:
                return $"{gen.GetClassCheckHelper()}({value}, \"{classType.Name}\")";
            case InterfaceType interfaceType when precise:
                return $"(type({value}) == \"table\" and {value}.__interfaces ~= nil and {value}.__interfaces[\"{interfaceType.Name}\"] == true)";
            case EnumType enumType:
                return RuntimeCheck(gen, enumType.BaseType, value, precise);
        }

        var luaType = type.Kind switch
        {
            TypeKind.PrimitiveNumber => "number",
            TypeKind.PrimitiveString => "string",
            TypeKind.PrimitiveBool => "boolean",
            TypeKind.PrimitiveThread => "thread",
            TypeKind.PrimitiveUserdata => "userdata",
            TypeKind.Function or TypeKind.PrimitiveFunction => "function",
            _ when IsTableValue(type) => "table",
            _ => null,
        };
        if (type.Kind == TypeKind.PrimitiveNil) return $"{value} == nil";
        return luaType == null ? null : $"type({value}) == \"{luaType}\"";
    }

    private static bool IsTableValue(Type type) => type.Kind is TypeKind.Class or TypeKind.Interface
        or TypeKind.TableArray or TypeKind.TableMap or TypeKind.Struct or TypeKind.Tuple;

    /// <summary>
    /// How narrowly an overload's parameters match, so the dispatcher tests a subclass before the
    /// class it extends, a typed parameter before an untyped one, and an overload taking any
    /// number of arguments after all others.
    /// </summary>
    private static int Specificity(OverloadBranch branch) =>
        ParametersOf(branch).Sum(ParameterSpecificity) - (branch.Function.IsVararg ? 1000 : 0);

    private static int ParameterSpecificity(Type type) => type switch
    {
        ClassType classType => 10 + Depth(classType),
        InterfaceType => 6,
        UnionType union => union.Types.Where(member => member.Kind != TypeKind.PrimitiveNil).Select(ParameterSpecificity).DefaultIfEmpty(0).Min() - 1,
        _ when type.Kind is TypeKind.PrimitiveAny or TypeKind.TypeParameter => 0,
        _ => 3,
    };

    private static int Depth(ClassType classType)
    {
        var depth = 0;
        for (var current = classType.BaseClass; current != null; current = current.BaseClass) depth++;
        return depth;
    }
}
