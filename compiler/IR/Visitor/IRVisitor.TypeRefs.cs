using Nebra.Diagnostics;

namespace Nebra.IR;

internal partial class IRVisitor
{
    private static readonly Dictionary<string, TypeKind> PrimitiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "string", TypeKind.PrimitiveString },
        { "number", TypeKind.PrimitiveNumber },
        { "boolean", TypeKind.PrimitiveBool },
        { "any", TypeKind.PrimitiveAny },
        { "thread", TypeKind.PrimitiveThread },
        { "userdata", TypeKind.PrimitiveUserdata },
        { "never", TypeKind.PrimitiveNever }
    };

    public override Node VisitBareFunctionType(NebraParser.BareFunctionTypeContext context)
        => new PrimitiveTypeRef(NewNodeID, SpanFromCtx(context), TypeKind.PrimitiveFunction);
    
    public override Node VisitUnionType(NebraParser.UnionTypeContext context)
    {
        var types = context.typeSingle().Select(t => (TypeRef)Visit(t)).ToList();
        if (types.Count == 1) return types[0];
        return new UnionTypeRef(NewNodeID, SpanFromCtx(context), types);
    }

    public override Node VisitPostfixType(NebraParser.PostfixTypeContext context)
    {
        var result = (TypeRef)Visit(context.typeAtom());
        foreach (var suffix in context.typeSuffix())
        {
            result = suffix switch
            {
                NebraParser.ArraySuffixContext => new ArrayTypeRef(NewNodeID, SpanFromCtx(suffix), result),
                NebraParser.NullableSuffixContext => new NullableTypeRef(NewNodeID, SpanFromCtx(suffix), result),
                NebraParser.IndexedAccessSuffixContext indexed => new IndexedAccessTypeRef(NewNodeID, SpanFromCtx(suffix), result, (TypeRef)Visit(indexed.typeExpr())),
                _ => throw new InvalidOperationException($"Unknown type suffix: {suffix.GetType().Name}")
            };
        }

        return result;
    }

    public override Node VisitKeyOfType(NebraParser.KeyOfTypeContext context)
        => new KeyOfTypeRef(NewNodeID, SpanFromCtx(context), (TypeRef)Visit(context.typeSingle()));

    public override Node VisitStringLiteralType(NebraParser.StringLiteralTypeContext context)
    {
        if (Visit(context.str()) is StringLiteralExpr literal)
            return new LiteralTypeRef(NewNodeID, SpanFromCtx(context), LiteralValue.Of(literal.Value));

        diag.Report(SpanFromCtx(context), DiagnosticCode.ErrInterpolatedLiteralType);
        return new PrimitiveTypeRef(NewNodeID, SpanFromCtx(context), TypeKind.PrimitiveString);
    }

    public override Node VisitNumberLiteralType(NebraParser.NumberLiteralTypeContext context)
    {
        var literal = (NumberLiteralExpr)Visit(context.number());
        return new LiteralTypeRef(NewNodeID, SpanFromCtx(context), LiteralValue.Of(literal, context.MINUS() != null));
    }

    public override Node VisitTrueLiteralType(NebraParser.TrueLiteralTypeContext context)
        => new LiteralTypeRef(NewNodeID, SpanFromCtx(context), LiteralValue.Of(true));

    public override Node VisitFalseLiteralType(NebraParser.FalseLiteralTypeContext context)
        => new LiteralTypeRef(NewNodeID, SpanFromCtx(context), LiteralValue.Of(false));

    public override Node VisitNilType(NebraParser.NilTypeContext context)
        => new PrimitiveTypeRef(NewNodeID, SpanFromCtx(context), TypeKind.PrimitiveNil);

    public override Node VisitNamedType(NebraParser.NamedTypeContext context)
    {
        var nameText = context.NAME().GetText();
        var typeArgList = context.typeArgList();

        if (typeArgList != null)
        {
            if (PrimitiveTypes.TryGetValue(nameText, out var value))
            {
                diag.Report(SpanFromCtx(context), DiagnosticCode.ErrGenericOnPrimitive, nameText);
                return new PrimitiveTypeRef(NewNodeID, SpanFromCtx(context), value);
            }

            var args = VisitTypeArgListContent(typeArgList);
            return new GenericTypeRef(NewNodeID, SpanFromCtx(context), NameRefFromTerm(context.NAME()), args);
        }

        if (PrimitiveTypes.TryGetValue(nameText, out var kind))
        {
            return new PrimitiveTypeRef(NewNodeID, SpanFromCtx(context), kind);
        }

        // `void` documents "no return value". It is an alias for `nil`: a function
        // that returns nothing yields `nil`, and a `nil`/`void` return is exempt from
        // the missing-return check. Modelling true zero-value `none` semantics (distinct
        // from `nil`) is tracked with the return-arity work (variadic returns).
        if (string.Equals(nameText, "void", StringComparison.OrdinalIgnoreCase))
        {
            return new PrimitiveTypeRef(NewNodeID, SpanFromCtx(context), TypeKind.PrimitiveNil);
        }

        if (nameText == "table")
        {
            var span = SpanFromCtx(context);
            return new MapTypeRef(NewNodeID, span, new PrimitiveTypeRef(NewNodeID, span, TypeKind.PrimitiveAny),
                new PrimitiveTypeRef(NewNodeID, span, TypeKind.PrimitiveAny));
        }

        return new NamedTypeRef(NewNodeID, SpanFromCtx(context), NameRefFromTerm(context.NAME()));
    }

    public override Node VisitVariadicType(NebraParser.VariadicTypeContext context)
        => new VariadicTypeRef(NewNodeID, SpanFromCtx(context), (TypeRef)Visit(context.typeSingle()));

    public override Node VisitFuncType(NebraParser.FuncTypeContext context)
        => Visit(context.functionType());

    public override Node VisitTableType_(NebraParser.TableType_Context context)
        => Visit(context.tableType());

    public override Node VisitGroupedOrTupleType(NebraParser.GroupedOrTupleTypeContext context)
    {
        var types = context.typeExpr().Select(t => (TypeRef)Visit(t)).ToList();
        if (types.Count == 1) return types[0];
        return new TupleTypeRef(NewNodeID, SpanFromCtx(context), types);
    }

    public override Node VisitFunctionType(NebraParser.FunctionTypeContext context)
    {
        var parameters = context.functionTypeParams()?.functionTypeParam() ?? [];
        var paramTypes = parameters.Select(parameter => (TypeRef)Visit(parameter.typeExpr())).ToList();
        var returnType = (TypeRef)Visit(context.typeExpr());
        return new FunctionTypeRef(NewNodeID, SpanFromCtx(context), paramTypes, returnType)
        {
            ParamNames = parameters.Select(parameter => parameter.memberName()?.GetText()).ToList()
        };
    }

    public override Node VisitEmptyTableType(NebraParser.EmptyTableTypeContext context)
        => new StructTypeRef(NewNodeID, SpanFromCtx(context), []);

    public override Node VisitMapType(NebraParser.MapTypeContext context)
    {
        var keyType = (TypeRef)Visit(context.typeExpr(0));
        var valueType = (TypeRef)Visit(context.typeExpr(1));
        return new MapTypeRef(NewNodeID, SpanFromCtx(context), keyType, valueType);
    }

    public override Node VisitStructType(NebraParser.StructTypeContext context)
    {
        var fields = context.structField().Select(f => new StructTypeField(
            NameRefFromField(f.fieldName()),
            (TypeRef)Visit(f.typeExpr()),
            f.META() != null,
            SpanFromCtx(f)
        )).ToList();
        return new StructTypeRef(NewNodeID, SpanFromCtx(context), fields);
    }
}
