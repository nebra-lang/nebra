using Nebra.Compiler.Annotations;
using Nebra.Diagnostics;
using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

public sealed partial class InferTypesPass
{
    /// <summary>
    /// The function type a function literal is written for, from the parameter, local, field or
    /// assignment target it is given to. Its parameters without an annotation take their types
    /// from it.
    /// </summary>
    private readonly Dictionary<FunctionDefExpr, FunctionType> _expectedSignatures = [];

    /// <summary>
    /// Function literals passed as arguments whose parameter types wait for the signature of the
    /// call: they are typed once the overload and the type arguments of the callee are known.
    /// </summary>
    private readonly HashSet<FunctionDefExpr> _pendingLiterals = [];

    /// <summary>
    /// Table literals passed as arguments or assigned that take their shape from the type they are
    /// written for, typed once it is known: their function fields take parameter types from it,
    /// and their string keys such as <c>["user:login"]</c> name fields of it.
    /// </summary>
    private readonly HashSet<TableConstructorExpr> _pendingTables = [];

    /// <summary>
    /// Table literals being typed for a struct, class or interface, with that type: their string
    /// keys name its fields and each value is typed for the field it fills.
    /// </summary>
    private readonly Dictionary<TableConstructorExpr, Type> _shapedTables = [];

    /// <summary>Whether <paramref name="value"/> is a function literal with a parameter that has no annotation.</summary>
    private static bool TakesSignatureFromContext(Expr value) =>
        value is FunctionDefExpr literal && literal.Parameters.Any(parameter => parameter.TypeAnnotation == null);

    /// <summary>
    /// Whether <paramref name="value"/> is a table literal whose type depends on the type it is
    /// written for: it has a string key or a function field that takes its signature from it.
    /// </summary>
    private static bool TakesShapeFromContext(Expr value) =>
        value is TableConstructorExpr table
        && table.Fields.Any(field => field.Key is StringLiteralExpr || TakesSignatureFromContext(field.Value));

    /// <summary>The name a field of a table literal gives, written as a name or as a string key.</summary>
    private static string? TableFieldName(TableField field) => field.Name?.Name ?? (field.Key as StringLiteralExpr)?.Value;

    /// <summary>
    /// The type of a call argument. A function literal that takes its parameter types from the
    /// call is not typed yet: it stands in with the parameters it declares, anything for the
    /// others, until <see cref="AdoptFunctionLiterals"/> or <see cref="FlushPendingLiterals"/> types it.
    /// </summary>
    private TypID SynthesizeArgument(PassContext pc, Expr argument)
    {
        if (argument is TableConstructorExpr table && TakesShapeFromContext(table))
        {
            _pendingTables.Add(table);
            return pc.Types.PrimAny.ID;
        }

        if (!TakesSignatureFromContext(argument)) return SynthesizeExpr(pc, argument);

        var literal = (FunctionDefExpr)argument;
        _pendingLiterals.Add(literal);
        var parameters = literal.Parameters
            .Where(parameter => !parameter.IsVararg)
            .Select(parameter => ResolveParamType(pc, parameter));
        return pc.Types.FuncOf(parameters, pc.Types.PrimAny, literal.Parameters.Any(parameter => parameter.IsVararg));
    }

    /// <summary>
    /// Types the function literals among <paramref name="arguments"/> that wait for the signature
    /// of <paramref name="fnType"/>. Type parameters in a callback's parameters are filled in from
    /// the other arguments first, so <c>map(items, function(item) ... end)</c> sees the element
    /// type of <c>items</c>.
    /// </summary>
    private void AdoptFunctionLiterals(PassContext pc, FunctionType fnType, List<Expr> arguments, List<TypID> argTypes, int offset)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var position = i + offset;
            if (position >= argTypes.Count) continue;

            var parameter = position < fnType.ParamTypes.Count ? fnType.ParamTypes[position] : fnType.IsVararg ? fnType.VarargType : null;
            if (!IsPending(arguments[i]) && !IsTableForContract(pc, arguments[i], parameter)) continue;

            if (parameter != null && ContainsTypeParameter(parameter))
                parameter = Specialize(pc, parameter, fnType, arguments, argTypes, offset);

            if (arguments[i] is TableConstructorExpr table)
            {
                argTypes[position] = SynthesizeTableFor(pc, table, parameter != null ? Evaluate(pc, parameter.ID) : TypID.Invalid);
                continue;
            }

            var literal = (FunctionDefExpr)arguments[i];
            var expected = parameter != null ? ExpectedSignature(pc, Evaluate(pc, parameter.ID)) : null;
            if (expected != null && ContainsTypeParameter(expected)) expected = WithoutTypeParameters(pc, expected);

            argTypes[position] = SynthesizeWithSignature(pc, literal, expected);
        }
    }

    /// <summary>Types the function and table literals among <paramref name="arguments"/> that no signature was found for.</summary>
    private void FlushPendingLiterals(PassContext pc, IEnumerable<Expr> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument is FunctionDefExpr literal && _pendingLiterals.Contains(literal))
                SynthesizeWithSignature(pc, literal, null);
            else if (argument is TableConstructorExpr table && _pendingTables.Contains(table))
                SynthesizeTableFor(pc, table, TypID.Invalid);
        }
    }

    /// <summary>Whether <paramref name="value"/> is a function or table literal still waiting for the type it is written for.</summary>
    private bool IsPending(Expr value) =>
        value is FunctionDefExpr literal ? _pendingLiterals.Contains(literal) : value is TableConstructorExpr table && _pendingTables.Contains(table);

    private bool IsTableForContract(PassContext pc, Expr argument, Type? parameter)
    {
        return argument is TableConstructorExpr
               && parameter != null
               && pc.Types.GetByID(StripNil(pc, parameter.ID), out var shape)
               && shape is InterfaceType { IsContract: true };
    }

    /// <summary>Reports the literal fields of a table written for a contract that break a rule of the contract.</summary>
    private static void CheckContractLiteral(PassContext pc, TableConstructorExpr table, Type shape)
    {
        if (shape is not InterfaceType { IsContract: true } contract) return;

        foreach (var field in table.Fields)
        {
            var name = field.Name?.Name ?? (field.Key as StringLiteralExpr)?.Value;
            if (name == null) continue;

            foreach (var rule in contract.RulesOf(name).Where(rule => rule.Rejects(field.Value)))
            {
                pc.Diag.Report(field.Value.Span, DiagnosticCode.ErrContractViolation, name, contract.Name, rule.Requirement);
            }
        }
    }

    /// <summary>Records the rules written on a field of a contract, reporting those that are malformed or do not fit the field's type.</summary>
    private static void ReadContractRules(PassContext pc, InterfaceType contract, InterfaceFieldNode field, Type fieldType)
    {
        var rules = new List<ContractRule>();
        foreach (var annotation in field.Annotations.Where(annotation => !BuiltinAnnotations.IsBuiltin(annotation.Name.Name)))
        {
            var rule = ContractRules.Parse(annotation, out var problem);
            if (rule == null)
            {
                pc.Diag.Report(annotation.Span, DiagnosticCode.ErrContractRuleInvalid, annotation.Name.Name, problem!);
                continue;
            }

            if (!RuleFits(rule, fieldType))
            {
                pc.Diag.Report(annotation.Span, DiagnosticCode.ErrContractRuleType, annotation.Name.Name, fieldType.ToString() ?? "");
                continue;
            }

            rules.Add(rule);
        }

        contract.FieldRules[field.Name.Name] = rules;
    }

    private static bool RuleFits(ContractRule rule, Type fieldType)
    {
        if (fieldType is UnionType union)
        {
            return union.Types.Where(member => member.Kind != TypeKind.PrimitiveNil).All(member => RuleFits(rule, member));
        }

        if (fieldType.Kind == TypeKind.PrimitiveAny) return true;

        var isText = fieldType.Kind == TypeKind.PrimitiveString || fieldType is LiteralType { Value.Kind: LiteralKind.String };
        return rule.Kind switch
        {
            ContractRuleKind.Range or ContractRuleKind.Integer => fieldType.Kind == TypeKind.PrimitiveNumber || fieldType is LiteralType { Value.Kind: LiteralKind.Number },
            ContractRuleKind.Pattern => isText,
            _ => isText || fieldType.Kind == TypeKind.TableArray
        };
    }

    /// <summary>
    /// Types <paramref name="table"/> for a value of <paramref name="expected"/>: for a struct,
    /// class or interface its string keys name fields and its function fields take their
    /// parameter types from the fields of the same name.
    /// </summary>
    private TypID SynthesizeTableFor(PassContext pc, TableConstructorExpr table, TypID expected)
    {
        _pendingTables.Remove(table);
        if (expected == TypID.Invalid || !pc.Pkg!.Types.GetByID(StripNil(pc, expected), out var shape) || Type.FieldNamesOf(shape) == null)
            return SynthesizeExpr(pc, table);

        _shapedTables[table] = shape;
        try
        {
            var synthesized = SynthesizeExpr(pc, table);
            CheckContractLiteral(pc, table, shape);
            return synthesized;
        }
        finally
        {
            _shapedTables.Remove(table);
        }
    }

    /// <summary>
    /// <paramref name="parameter"/> with the type parameters of <paramref name="fnType"/> filled in
    /// from the arguments already typed.
    /// </summary>
    private Type Specialize(PassContext pc, Type parameter, FunctionType fnType, List<Expr> arguments, List<TypID> argTypes, int offset)
    {
        var bindings = new Dictionary<TypID, Type>();
        for (var position = 0; position < argTypes.Count; position++)
        {
            var index = position - offset;
            if (index >= 0 && index < arguments.Count && arguments[index] is FunctionDefExpr literal && _pendingLiterals.Contains(literal)) continue;

            var bound = position < fnType.ParamTypes.Count ? fnType.ParamTypes[position] : fnType.IsVararg ? fnType.VarargType : null;
            if (bound != null && pc.Pkg!.Types.GetByID(argTypes[position], out var argument))
                Bind(pc, bound, argument, bindings);
        }

        return bindings.Count == 0 ? parameter : pc.Pkg!.Types.Substitute(parameter, bindings);
    }

    /// <summary><paramref name="function"/> with every parameter no argument told the type of taking anything.</summary>
    private FunctionType WithoutTypeParameters(PassContext pc, FunctionType function)
    {
        var parameters = function.ParamTypes.Select(type => ContainsTypeParameter(type) ? pc.Types.PrimAny : type);
        var varargType = function.VarargType != null && ContainsTypeParameter(function.VarargType) ? pc.Types.PrimAny : function.VarargType;
        return (FunctionType)GetType(pc, pc.Types.FuncOf(parameters, function.ReturnType, function.IsVararg, varargType));
    }

    /// <summary>Synthesizes <paramref name="literal"/> with the parameter types of <paramref name="expected"/>.</summary>
    private TypID SynthesizeWithSignature(PassContext pc, FunctionDefExpr literal, FunctionType? expected)
    {
        _pendingLiterals.Remove(literal);
        if (expected == null) return SynthesizeExpr(pc, literal);

        _expectedSignatures[literal] = expected;
        try
        {
            return SynthesizeExpr(pc, literal);
        }
        finally
        {
            _expectedSignatures.Remove(literal);
        }
    }

    /// <summary>
    /// The function type a value of <paramref name="expected"/> is: the type itself, or the one
    /// function type of a union such as <c>((number) -&gt; nil)?</c>.
    /// </summary>
    private FunctionType? ExpectedSignature(PassContext pc, TypID expected)
    {
        if (!pc.Pkg!.Types.GetByID(expected, out var type)) return null;
        if (type is FunctionType function) return function;
        if (type is not UnionType union) return null;

        var functions = union.Types.OfType<FunctionType>().ToList();
        return functions.Count == 1 ? functions[0] : null;
    }

    /// <summary>
    /// The signature <c>function a.b.c(...)</c> is written for: the type of the field <c>c</c> of
    /// what <c>a.b</c> is, as <c>love.update</c> is a field of the <c>love</c> table typed as the
    /// callback LÖVE calls. <c>null</c> for a plain function or a method, or a field of no function type.
    /// </summary>
    private FunctionType? ExpectedSignatureOfField(PassContext pc, FunctionDecl declaration)
    {
        var path = declaration.MethodName != null ? declaration.NamePath.Append(declaration.MethodName).ToList() : declaration.NamePath;
        if (path.Count < 2 || TypeOfPath(pc, path) is not { } field) return null;

        var signature = ExpectedSignature(pc, Evaluate(pc, field.ID));
        if (signature == null || declaration.MethodName == null || signature.ParamNames.Count == 0 || signature.ParamNames[0] != "self") return signature;
        return (FunctionType)GetType(pc, pc.Types.FuncOf(signature.ParamTypes.Skip(1), signature.ReturnType, signature.IsVararg, signature.VarargType));
    }

    /// <summary>
    /// The type of <c>a.b.c</c>, each name a field of what the names before it are; <c>null</c>
    /// where one is not known.
    /// </summary>
    private Type? TypeOfPath(PassContext pc, List<NameRef> path)
    {
        if (!pc.Pkg!.Types.GetByID(LookupSymbolType(pc, path[0].Sym), out var current)) return null;

        foreach (var name in path.Skip(1))
        {
            if (!pc.Pkg.Types.GetByID(Evaluate(pc, StripNil(pc, current.ID)), out var owner)) return null;
            var member = Type.FieldTypeOf(owner, name.Name) ?? MethodOf(owner, name.Name);
            if (member == null) return null;
            current = member;
        }

        return current;
    }

    private static Type? MethodOf(Type owner, string name)
    {
        return owner switch
        {
            ClassType classType => Type.ClassChainOf(classType).Select(current => current.Methods.GetValueOrDefault(name)).FirstOrDefault(method => method != null),
            InterfaceType interfaceType => Type.BaseInterfacesOf(interfaceType).Prepend(interfaceType)
                .Select(candidate => candidate.Methods.GetValueOrDefault(name))
                .FirstOrDefault(method => method != null),
            _ => null,
        };
    }

    /// <summary>
    /// Types the implicit <c>self</c> of <c>function a.b:method(...)</c> as what <c>a.b</c> is,
    /// the table the method is called on.
    /// </summary>
    private void TypeMethodSelf(PassContext pc, FunctionDecl declaration)
    {
        if (declaration.MethodName == null) return;
        if (!pc.Pkg!.Scopes.EnclosingScope(declaration.ID, out var scope) || !pc.Pkg.Scopes.LookupOnlyCurrent(scope, "self", out var self)) return;

        var owner = TypeOfPath(pc, declaration.NamePath);
        pc.Pkg.Syms.SetType(self, owner != null ? StripNil(pc, owner.ID) : pc.Types.PrimAny.ID);
    }

    /// <summary>The type of an unannotated parameter from the signature <paramref name="expected"/> a function is written for.</summary>
    private static Type? ParamTypeFrom(FunctionType? expected, Parameter parameter, int index)
    {
        if (expected == null || parameter.TypeAnnotation != null) return null;
        if (parameter.IsVararg) return expected.IsVararg ? expected.VarargType : null;
        if (index < expected.ParamTypes.Count) return expected.ParamTypes[index];
        return expected.IsVararg ? expected.VarargType : null;
    }

    /// <summary>
    /// The type of the parameter at <paramref name="index"/> of <paramref name="literal"/> from the
    /// signature it is written for, or <c>null</c> when it has an annotation or there is none.
    /// </summary>
    private Type? ContextualParamType(FunctionDefExpr literal, Parameter parameter, int index)
    {
        if (parameter.TypeAnnotation != null || !_expectedSignatures.TryGetValue(literal, out var expected)) return null;
        if (parameter.IsVararg) return expected.IsVararg ? expected.VarargType : null;
        if (index < expected.ParamTypes.Count) return expected.ParamTypes[index];
        return expected.IsVararg ? expected.VarargType : null;
    }
}
