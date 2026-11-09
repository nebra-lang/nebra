using Nebra.Diagnostics;

namespace Nebra.IR;

public abstract class TypeRef(NodeID id, TextSpan span, TypeKind kind) : Node(id, span)
{
    public TypeKind Kind { get; } = kind;
    
    public TypID ResolvedType { get; set; } = TypID.Invalid;
}

public sealed class PrimitiveTypeRef(NodeID id, TextSpan span, TypeKind kind) : TypeRef(id, span, kind);

public sealed class NamedTypeRef(NodeID id, TextSpan span, NameRef name) : TypeRef(id, span, TypeKind.Named)
{
    public NameRef Name { get; } = name;
}

public sealed class ArrayTypeRef(NodeID id, TextSpan span, TypeRef elementType) : TypeRef(id, span, TypeKind.TableArray)
{
    public TypeRef ElementType { get; } = elementType;
}

public sealed class NullableTypeRef(NodeID id, TextSpan span, TypeRef innerType) : TypeRef(id, span, innerType.Kind)
{
    public TypeRef InnerType { get; } = innerType;
}

public sealed class UnionTypeRef(NodeID id, TextSpan span, List<TypeRef> types) : TypeRef(id, span, TypeKind.Union)
{
    public List<TypeRef> Types { get; } = types;
}

public sealed class FunctionTypeRef(NodeID id, TextSpan span, List<TypeRef> paramTypes, TypeRef returnType) : TypeRef(id, span, TypeKind.Function)
{
    public List<TypeRef> ParamTypes { get; } = paramTypes;
    public TypeRef ReturnType { get; } = returnType;

    /// <summary>The name written for each parameter, parallel to <see cref="ParamTypes"/>; <c>null</c> where none is.</summary>
    public List<string?> ParamNames { get; init; } = [];
}

/// <summary>
/// A type-predicate return annotation <c>param is Type</c>. The function's runtime return type is
/// <c>boolean</c>; <see cref="TargetType"/> is the type <see cref="ParamName"/> is narrowed to.
/// Resolved by <see cref="Passes.ResolveTypeRefsPass"/> to <c>boolean</c>, with the target resolved separately.
/// </summary>
public sealed class TypePredicateRef(NodeID id, TextSpan span, NameRef paramName, TypeRef targetType) : TypeRef(id, span, TypeKind.Predicate)
{
    public NameRef ParamName { get; } = paramName;
    public TypeRef TargetType { get; } = targetType;
}

public sealed class MapTypeRef(NodeID id, TextSpan span, TypeRef keyType, TypeRef valueType) : TypeRef(id, span, TypeKind.TableMap)
{
    public TypeRef KeyType { get; } = keyType;
    public TypeRef ValueType { get; } = valueType;
}

public sealed class StructTypeRef(NodeID id, TextSpan span, List<StructTypeField> fields) : TypeRef(id, span, TypeKind.Struct)
{
    public List<StructTypeField> Fields { get; } = fields;
}

public sealed class TupleTypeRef(NodeID id, TextSpan span, List<TypeRef> elementTypes) : TypeRef(id, span, TypeKind.Tuple)
{
    public List<TypeRef> ElementTypes { get; } = elementTypes;
}

/// <summary>
/// A variadic type reference <c>...T</c>, used in a function return position (or the trailing
/// element of a tuple return) to declare an arbitrary number of returned values of type T.
/// Resolved by <see cref="Passes.ResolveTypeRefsPass"/> to a <see cref="VariadicType"/>.
/// </summary>
public sealed class VariadicTypeRef(NodeID id, TextSpan span, TypeRef elementType) : TypeRef(id, span, TypeKind.Variadic)
{
    public TypeRef ElementType { get; } = elementType;
}

/// <summary>
/// A reference to a generic (parameterized) type, such as <c>List&lt;number&gt;</c> or <c>Map&lt;string, Foo&gt;</c>.
/// The head <see cref="Name"/> points at a generic class or interface definition; <see cref="Arguments"/> are the
/// type arguments supplied at the use site.
/// </summary>
public sealed class GenericTypeRef(NodeID id, TextSpan span, NameRef name, List<TypeArgRef> arguments) : TypeRef(id, span, TypeKind.Named)
{
    public NameRef Name { get; } = name;
    public List<TypeArgRef> Arguments { get; } = arguments;
}

/// <summary>
/// A reference to a declared type parameter (e.g. the <c>T</c> inside a <c>class Box&lt;T&gt;</c>).
/// Resolved by <see cref="ResolveTypeRefsPass"/> to the owning <see cref="TypeParameterType"/>.
/// </summary>
public sealed class TypeParamRef(NodeID id, TextSpan span, NameRef name) : TypeRef(id, span, TypeKind.TypeParameter)
{
    public NameRef Name { get; } = name;
}

public enum WildcardKind
{
    /// <summary>Unbounded wildcard: <c>?</c></summary>
    Unbounded,
    /// <summary>Upper-bounded wildcard: <c>? extends T</c></summary>
    Extends,
    /// <summary>Lower-bounded wildcard: <c>? super T</c></summary>
    Super,
}

/// <summary>
/// A single type argument inside a <see cref="GenericTypeRef"/>. Either a concrete type or a wildcard.
/// </summary>
public abstract class TypeArgRef(NodeID id, TextSpan span) : Node(id, span);

public sealed class ConcreteTypeArgRef(NodeID id, TextSpan span, TypeRef type) : TypeArgRef(id, span)
{
    public TypeRef Type { get; } = type;
}

public sealed class WildcardTypeArgRef(NodeID id, TextSpan span, WildcardKind kind, TypeRef? bound) : TypeArgRef(id, span)
{
    public WildcardKind Kind { get; } = kind;
    public TypeRef? Bound { get; } = bound;
}

/// <summary>
/// Declaration of a single type parameter on a generic class, interface, or function.
/// Carries optional upper bounds via <c>extends</c> (a single class/interface) and <c>implements</c> (interfaces).
/// </summary>
public sealed class TypeParamDef(NodeID id, TextSpan span, NameRef name, TypeRef? extendsBound, List<TypeRef> implementsBounds) : Node(id, span)
{
    public NameRef Name { get; } = name;
    public TypeRef? ExtendsBound { get; } = extendsBound;
    public List<TypeRef> ImplementsBounds { get; } = implementsBounds;

    /// <summary>The <see cref="TypID"/> assigned to this parameter after <see cref="ResolveTypeRefsPass"/>.</summary>
    public TypID ResolvedType { get; set; } = TypID.Invalid;
}

/// <summary>A literal type such as <c>"user:login"</c>, <c>1</c> or <c>true</c>.</summary>
public sealed class LiteralTypeRef(NodeID id, TextSpan span, LiteralValue value) : TypeRef(id, span, TypeKind.Literal)
{
    public LiteralValue Value { get; } = value;
}

/// <summary><c>keyof T</c>: the names of the fields of <see cref="Target"/> as literal types.</summary>
public sealed class KeyOfTypeRef(NodeID id, TextSpan span, TypeRef target) : TypeRef(id, span, TypeKind.KeyOf)
{
    public TypeRef Target { get; } = target;
}

/// <summary><c>T[K]</c>: the type of the field <see cref="Index"/> of <see cref="Target"/>.</summary>
public sealed class IndexedAccessTypeRef(NodeID id, TextSpan span, TypeRef target, TypeRef index) : TypeRef(id, span, TypeKind.IndexedAccess)
{
    public TypeRef Target { get; } = target;
    public TypeRef Index { get; } = index;
}
