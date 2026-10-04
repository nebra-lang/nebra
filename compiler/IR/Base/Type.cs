using System.ComponentModel;
using Nebra.Configuration;

namespace Nebra.IR;

/// <summary>
/// The kind of a type. This is used to distinguish different types in the type system, such as primitive types, table
/// types, function types, etc.
/// </summary>
public enum TypeKind
{
    [Description("nil")]
    PrimitiveNil,
    [Description("any")]
    PrimitiveAny,
    [Description("number")]
    PrimitiveNumber,
    [Description("boolean")]
    PrimitiveBool,
    [Description("string")]
    PrimitiveString,
    [Description("function")]
    PrimitiveFunction,
    [Description("thread")]
    PrimitiveThread,
    [Description("userdata")]
    PrimitiveUserdata,
    [Description("never")]
    PrimitiveNever,
    TableArray,
    TableMap,
    Tuple,
    Union,
    Struct,
    Function,
    Enum,
    Class,
    Interface,
    TypeParameter,
    Parameterized,
    Variadic,
    Predicate,
}

/// <summary>
/// A type predicate carried by a guard function's signature: <c>param is TargetType</c>.
/// The function returns a boolean at runtime; where the call appears as an <c>if</c> condition,
/// the argument bound to <see cref="ParamName"/> is narrowed to <see cref="TargetType"/>.
/// </summary>
public sealed record TypePredicate(string ParamName, Type TargetType);

/// <summary>
/// Represents a type in the type system. A type is a set of values that share common properties and operations. 
/// </summary>
public class Type(TypeKind kind)
{
    /// <summary>
    /// The type ID. This is a unique identifier for the type.
    /// </summary>
    public TypID ID { get; set; } = TypID.Invalid;
    
    /// <summary>
    /// The type kind. This is used to distinguish different types in the type system, such as primitive types, table types, function types, etc.
    /// </summary>
    public TypeKind Kind { get; } = kind;

    /// <summary>
    /// Extension methods registered on this type via an <c>extend Type</c> block (name →
    /// signature, self-prefixed). Consulted when a <c>receiver:method(...)</c> call finds no
    /// real member; the call then lowers to a plain function invocation at codegen.
    /// </summary>
    public Dictionary<string, FunctionType> ExtensionMethods { get; } = new();

    /// <summary>The declaring AST node for each extension method (for go-to-definition / hover).</summary>
    public Dictionary<string, ExtensionMethodNode> ExtensionMethodNodes { get; } = new();

    /// <summary>
    /// The stable reflection id (<c>package::Name</c>) for a named type (class/interface/enum),
    /// assigned once at type creation using its defining package's namespace. Both the metadata
    /// emission and cross-references read this so ids stay consistent across files and packages.
    /// </summary>
    public string? ReflectionId { get; set; }

    /// <summary>
    /// Resolves an extension method named <paramref name="name"/> visible on
    /// <paramref name="objType"/> — checking the type itself, its base classes, and its
    /// implemented/extended interfaces. Returns the self-prefixed signature and the type the
    /// extension was declared on. Shared by type inference and the language server.
    /// </summary>
    public static (FunctionType? Fn, Type? Target) ResolveExtension(Type objType, string name, Type? functionCategory = null)
    {
        if (objType.ExtensionMethods.TryGetValue(name, out var direct)) return (direct, objType);

        // Every concrete function signature inherits the extensions declared on `extend function`.
        if (objType is FunctionType && functionCategory != null
            && functionCategory.ExtensionMethods.TryGetValue(name, out var fnExt))
            return (fnExt, functionCategory);

        switch (objType)
        {
            case ClassType ct:
                for (var cur = ct.BaseClass; cur != null; cur = cur.BaseClass)
                {
                    if (cur.ExtensionMethods.TryGetValue(name, out var bft))
                    {
                        return (bft, cur);
                    }
                }

                foreach (var iface in ImplementedInterfaces(ct))
                {
                    if (iface.ExtensionMethods.TryGetValue(name, out var ift))
                    {
                        return (ift, iface);
                    }
                }

                break;
            case InterfaceType it:
                foreach (var b in BaseInterfacesOf(it))
                    if (b.ExtensionMethods.TryGetValue(name, out var bift)) return (bift, b);
                break;
        }

        return (null, null);
    }

    /// <summary>
    /// Enumerates every extension method visible on <paramref name="objType"/> — its own, plus
    /// those on base classes and implemented/extended interfaces. Used by editor completion.
    /// May yield the same name more than once (nearest-first); callers dedupe.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, FunctionType>> EnumerateExtensions(Type objType, Type? functionCategory = null)
    {
        foreach (var kv in objType.ExtensionMethods) yield return kv;

        if (objType is FunctionType && functionCategory != null)
            foreach (var kv in functionCategory.ExtensionMethods) yield return kv;

        switch (objType)
        {
            case ClassType ct:
                for (var cur = ct.BaseClass; cur != null; cur = cur.BaseClass)
                {
                    foreach (var kv in cur.ExtensionMethods)
                    {
                        yield return kv;
                    }
                }

                foreach (var iface in ImplementedInterfaces(ct))
                {
                    foreach (var kv in iface.ExtensionMethods)
                    {
                        yield return kv;
                    }
                }

                break;
            case InterfaceType it:
                foreach (var b in BaseInterfacesOf(it))
                    foreach (var kv in b.ExtensionMethods) yield return kv;
                break;
        }
    }

    /// <summary>
    /// Enumerates every interface visible on <paramref name="classType"/>: the ones it
    /// implements itself, the ones its base classes implement, and every interface those extend,
    /// transitively. Each interface is yielded once, nearest declaration first. Shared by member
    /// lookup, extension resolution and the language server.
    /// </summary>
    public static IEnumerable<InterfaceType> ImplementedInterfaces(ClassType classType)
    {
        var seen = new HashSet<InterfaceType>();
        for (var cur = classType; cur != null; cur = cur.BaseClass)
        {
            foreach (var iface in cur.Interfaces)
            {
                if (seen.Add(iface))
                {
                    yield return iface;
                }

                foreach (var baseInterface in BaseInterfacesOf(iface))
                {
                    if (seen.Add(baseInterface))
                    {
                        yield return baseInterface;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Enumerates the interfaces <paramref name="iface"/> extends, transitively. Each one is
    /// yielded once; <paramref name="iface"/> itself is not part of the result.
    /// </summary>
    public static IEnumerable<InterfaceType> BaseInterfacesOf(InterfaceType iface)
    {
        var seen = new HashSet<InterfaceType>();
        var stack = new Stack<InterfaceType>(iface.BaseInterfaces);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur)) continue;
            yield return cur;
            foreach (var bb in cur.BaseInterfaces) stack.Push(bb);
        }
    }

    /// <summary>
    /// The type key. This is a string representation of the type, and is used to identify the type in the type table.
    /// </summary>
    public TypeKey Key => field ??= GenerateNewKey();

    /// <summary>
    /// Generates a new type key based on the type and its information.
    /// </summary>
    protected virtual TypeKey GenerateNewKey()
    {
        return Kind.ToString();
    }
    
    public static implicit operator TypeKey(Type type) => type.Key;
    public static implicit operator TypID(Type type) => type.ID;
}

/// <summary>
/// Represents a table array type, which is a type of table that maps integer keys to values of a certain type. This is
/// used to represent arrays in the IR, where the keys are the indices of the array and the values are the elements of
/// the array. The element type of the table array type can be any type in the type system, including primitive types,
/// other table types, function types, etc.
/// </summary>
public sealed class TableArrayType(Type elementType) : Type(TypeKind.TableArray)
{
    /// <summary>
    /// The element type of the table array type. This is the type of the values that are mapped by the integer keys in
    /// the table array type.
    /// </summary>
    public Type ElementType { get; } = elementType;

    protected override TypeKey GenerateNewKey()
    {
        return $"{ElementType.Key}[]";
    }
}

/// <summary>
/// Represents a variadic type <c>...T</c> — zero or more values of <see cref="ElementType"/>.
/// Only meaningful as a function return type (or the trailing element of a tuple return type):
/// it models Lua's multiple-return-value tail so a signature can say "returns any number of T".
/// A variadic return may legitimately yield zero values, so it is exempt from the missing-return
/// check, and it collapses to <see cref="ElementType"/> when bound to a single value.
/// </summary>
public sealed class VariadicType(Type elementType) : Type(TypeKind.Variadic)
{
    /// <summary>The type of each value in the variadic tail.</summary>
    public Type ElementType { get; } = elementType;

    protected override TypeKey GenerateNewKey()
    {
        return $"...{ElementType.Key}";
    }
}

/// <summary>
/// Represents a table map type, which is a type of table that maps keys of a certain type to values of another type.
/// This is used to represent maps in the IR, where the keys can be of any type in the type system, and the values can
/// also be of any type in the type system. The key type and the value type of the table map type can be any types in
/// the type system, including primitive types, other table types, function types, etc.
/// </summary>
public sealed class TableMapType(Type keyType, Type valueType) : Type(TypeKind.TableMap)
{
    /// <summary>
    /// The key type of the table map type. This is the type of the keys that are mapped to the values in the table map type.
    /// </summary>
    public Type KeyType { get; } = keyType;
    
    /// <summary>
    /// The value type of the table map type. This is the type of the values that are mapped by the keys in the table map type.
    /// </summary>
    public Type ValueType { get; } = valueType;

    protected override TypeKey GenerateNewKey()
    {
        return $"map<{KeyType.Key}, {ValueType.Key}>";
    }
}

/// <summary>
/// Represents a tuple type, which is a type that represents a fixed-size collection of values of different types. Each
/// value in the tuple is called a field, and each field has a name (which can be null for unnamed fields) and a type.
/// The fields of the tuple type can be of any types in the type system, including primitive types, other table types,
/// function types, etc. The tuple type is used to represent tuples in the IR, where the fields of the tuple correspond
/// to the elements of the tuple.
/// </summary>
public sealed class TupleType(IEnumerable<TupleType.Field> fields) : Type(TypeKind.Tuple)
{
    /// <summary>
    /// The fields of the tuple type. Each field has a name (which can be null for unnamed fields) and a type.
    /// </summary>
    public List<Field> Fields { get; } = fields.ToList();

    protected override TypeKey GenerateNewKey()
    {
        var fieldKeys = Fields.Select(f => f.ToString());
        return $"tuple<{string.Join(",", fieldKeys)}>";
    }
    
    /// <summary>
    /// Represents a field in a tuple type.
    /// </summary>
    public sealed class Field(NameRef? name, Type type)
    {
        /// <summary>
        /// The name of the field. Can be null for unnamed fields, such as in a tuple type. For named fields, this is the actual name of the field, such as "x", "y", "z", etc.
        /// </summary>
        public NameRef? Name { get; } = name;
        /// <summary>
        /// The type of the field.
        /// </summary>
        public Type Type { get; } = type;
        
        /// <summary>
        /// Creates a new field with the specified type and no name. This is used for unnamed fields, such as in a tuple type.
        /// </summary>
        public Field(Type type) : this(null, type)
        {
        }
        
        public override string ToString()
        {
            return Name is null ? Type.Key.Value : $"{Name.Name}: {Type.Key.Value}";
        }
    }
}

public sealed class UnionType(IEnumerable<Type> types) : Type(TypeKind.Union)
{
    public List<Type> Types { get; } = ConvertTypes(types);

    protected override TypeKey GenerateNewKey()
    {
        var typeKeys = Types.Select(t => t.Key);
        return string.Join(" | ", typeKeys);
    }

    /// <summary>
    /// Flattens nested unions, drops duplicates, and drops <c>never</c> members: <c>never</c> is the
    /// empty set of values, so <c>T | never</c> is just <c>T</c>. A union of nothing but <c>never</c>
    /// stays <c>never</c> so the key never degenerates to the empty string.
    /// </summary>
    private static List<Type> ConvertTypes(IEnumerable<Type> types)
    {
        var result = new List<Type>();
        var never = default(Type);
        foreach (var t in types)
        {
            if (t is UnionType ut)
            {                
                foreach (var member in ut.Types)
                {
                    if (member.Kind == TypeKind.PrimitiveNever)
                    {
                        never ??= member;
                        continue;
                    }

                    if (result.All(existing => existing.Key != member.Key))
                    {
                        result.Add(member);
                    }
                }
            }
            else
            {
                if (t.Kind == TypeKind.PrimitiveNever)
                {
                    never ??= t;
                    continue;
                }

                if (result.All(existing => existing.Key != t.Key))
                {
                    result.Add(t);
                }
            }
        }
        
        if (result.Count == 0 && never != null) result.Add(never);
        return result;
    }
}

public sealed class StructType(IEnumerable<StructType.Field> fields) : Type(TypeKind.Struct)
{
    public List<Field> Fields { get; } = fields.ToList();

    protected override TypeKey GenerateNewKey()
    {
        var fieldKeys = Fields.Select(f => f.ToString());
        return $"struct<{string.Join(",", fieldKeys)}>";
    }
    
    public sealed class Field(NameRef name, Type type, bool isMeta = false)
    {
        public NameRef Name { get; } = name;
        public Type Type { get; } = type;
        public bool IsMeta { get; } = isMeta;

        public override string ToString()
        {
            return $"{(IsMeta ? "meta " : "")}{Name.Name}:{Type.Key.Value}";
        }
    }
}

public sealed class FunctionType : Type
{
    public List<Type> ParamTypes { get; }
    public List<string> ParamNames { get; }
    public Type ReturnType { get; }
    public bool IsVararg { get; }
    public Type? VarargType { get; }
    public List<int> DefaultParams { get; }
    public bool IsAsync { get; }
    public int CallbackParamIndex { get; set; } = -1;

    /// <summary>
    /// Non-null when this is a guard function whose signature is a type predicate
    /// (<c>param is Type</c>). The runtime <see cref="ReturnType"/> stays <c>boolean</c>; this
    /// drives call-site narrowing. Set before the type is interned so it is part of the key.
    /// </summary>
    public TypePredicate? Predicate { get; set; }

    public int MinParamCount => ParamTypes.Count - DefaultParams.Count;

    public FunctionType(IEnumerable<Tuple<string, Type>> paramTypes, Type returnType, bool isVararg = false, Type? varargType = null, List<int>? defaultParams = null, bool isAsync = false) : base(TypeKind.Function)
    {
        var @params = paramTypes.ToList();
        ParamTypes = @params.Select(p => p.Item2).ToList();
        ParamNames = @params.Select(p => p.Item1).ToList();
        ReturnType = returnType;
        IsVararg = isVararg;
        VarargType = varargType;
        DefaultParams = AddImplicitTrailingNullableDefaults(ParamTypes, defaultParams);
        IsAsync = isAsync;
    }

    public FunctionType(IEnumerable<Type> paramTypes, Type returnType, bool isVararg = false, Type? varargType = null, List<int>? defaultParams = null, bool isAsync = false) : base(TypeKind.Function)
    {
        ParamTypes = paramTypes.ToList();
        ParamNames = [];
        for (var i = 0; i < ParamTypes.Count; i++)
        {
            ParamNames.Add($"arg{i}");
        }
        ReturnType = returnType;
        IsVararg = isVararg;
        VarargType = varargType;
        DefaultParams = AddImplicitTrailingNullableDefaults(ParamTypes, defaultParams);
        IsAsync = isAsync;
    }

    public FunctionType(IEnumerable<Type> paramTypes, List<string> paramNames, Type returnType, bool isVarargs, Type? varargType, List<int>? defaultParams, bool isAsync = false) : base(TypeKind.Function)
    {
        ParamTypes = paramTypes.ToList();
        ParamNames = paramNames;
        ReturnType = returnType;
        IsVararg = isVarargs;
        VarargType = varargType;
        DefaultParams = AddImplicitTrailingNullableDefaults(ParamTypes, defaultParams);
        IsAsync = isAsync;
    }

    /// <summary>
    /// Auto-extends the explicit <paramref name="defaults"/> list with the
    /// indices of every <em>trailing</em> nullable parameter so callers can
    /// elide them at the call site. With this, a signature like
    /// <c>(a: number, b: number?, c: number?)</c> accepts 1, 2, or 3 args
    /// without requiring the author to spell out <c>= nil</c>. Stops at the
    /// first non-nullable param walking from the end, matching how
    /// TypeScript's <c>?</c> works.
    /// </summary>
    private static List<int> AddImplicitTrailingNullableDefaults(List<Type> paramTypes, List<int>? defaults)
    {
        var result = defaults != null ? new List<int>(defaults) : [];
        for (var i = paramTypes.Count - 1; i >= 0; i--)
        {
            if (!IsNullableParamType(paramTypes[i])) break;
            if (!result.Contains(i)) result.Add(i);
        }
        return result;
    }

    private static bool IsNullableParamType(Type t)
    {
        if (t.Kind == TypeKind.PrimitiveNil) return true;
        if (t is UnionType u)
        {
            foreach (var m in u.Types)
                if (m.Kind == TypeKind.PrimitiveNil) return true;
        }
        return false;
    }

    protected override TypeKey GenerateNewKey()
    {
        var prefix = IsAsync ? "async " : "";
        var parameters = new List<string>();
        for (var i = 0; i < ParamTypes.Count; i++)
        {
            var pType = ParamTypes[i].Key.Value;
            var pName = ParamNames[i];
            if (DefaultParams.Contains(i))
                pType += " = ...";
            parameters.Add($"{pName}: {pType}");
        }

        // Encode the variadic tail explicitly so signatures that only differ
        // by their vararg get distinct keys (e.g. `() -> T` vs `(...:T) -> T`).
        if (IsVararg)
        {
            var vaType = VarargType?.Key.Value ?? "any";
            parameters.Add($"...: {vaType}");
        }

        var ret = Predicate != null
            ? $"{Predicate.ParamName} is {Predicate.TargetType.Key}"
            : ReturnType.Key.Value;
        return $"{prefix}({string.Join(", ", parameters)}) -> {ret}";
    }
}

public sealed class EnumType(string name, IEnumerable<EnumType.Member> members, Type baseType) : Type(TypeKind.Enum)
{
    public string Name { get; } = name;
    public List<Member> Members { get; } = members.ToList();
    public Type BaseType { get; } = baseType;

    protected override TypeKey GenerateNewKey()
    {
        return $"enum<{Name}>";
    }

    public sealed class Member(string name, object? value)
    {
        public string Name { get; } = name;
        public object? Value { get; } = value;
    }
}

public sealed class ClassType(
    string name,
    ClassType? baseClass,
    List<InterfaceType> interfaces,
    bool isAbstract = false
) : Type(TypeKind.Class)
{
    public string Name { get; } = name;
    public ClassType? BaseClass { get; set; } = baseClass;
    public List<InterfaceType> Interfaces { get; } = interfaces;
    public bool IsAbstract { get; } = isAbstract;
    public Dictionary<string, StructType.Field> InstanceFields { get; } = new();
    public Dictionary<string, FunctionType> Methods { get; } = new();
    /// <summary>
    /// The name each member was declared under, keyed by member name. The name carries its span,
    /// file included, so tooling can locate a member's declaration from the type alone.
    /// </summary>
    public Dictionary<string, NameRef> MemberDeclarations { get; } = new();
    public Dictionary<string, FunctionType> StaticMethods { get; } = new();
    public Dictionary<string, FunctionType> Getters { get; } = new();
    public Dictionary<string, FunctionType> Setters { get; } = new();
    public FunctionType? ConstructorType { get; set; }
    public HashSet<string> AbstractMethods { get; } = new();
    public HashSet<string> ProtectedMembers { get; } = new();
    public List<TypeParameterType> TypeParams { get; } = new();

    /// <summary>
    /// Interface default methods this class inherits and that codegen must materialise on
    /// the class table (name → the interface method node whose body is copied). Populated
    /// when the class implements an interface with a default it neither overrides nor
    /// inherits a concrete implementation of from a base class.
    /// </summary>
    public Dictionary<string, InterfaceMethodNode> DefaultsToEmit { get; } = new();

    /// <summary>
    /// All overloads for each method name (instance + static). Insertion-ordered;
    /// the entry in <see cref="Methods"/>/<see cref="StaticMethods"/> mirrors the
    /// last-inserted overload (the "primary"). Resolution that needs to consider
    /// every candidate (per-side dispatch, arity-based overload picking) walks
    /// this list. The parallel side lists in <see cref="MethodOverloadSides"/> /
    /// <see cref="StaticMethodOverloadSides"/> carry one <see cref="Side"/> per
    /// overload at the same index.
    /// </summary>
    public Dictionary<string, List<FunctionType>> MethodOverloads { get; } = new();
    public Dictionary<string, List<Side>> MethodOverloadSides { get; } = new();
    public Dictionary<string, List<FunctionType>> StaticMethodOverloads { get; } = new();
    public Dictionary<string, List<Side>> StaticMethodOverloadSides { get; } = new();

    /// <summary>
    /// Per-member side masks. Names that match the corresponding member table
    /// (e.g. <see cref="Methods"/>) carry a <see cref="Side"/>; absence in this
    /// dictionary means the member inherits the wildcard <see cref="Side.All"/>.
    /// </summary>
    public Dictionary<string, Side> FieldSides { get; } = new();
    public Dictionary<string, Side> MethodSides { get; } = new();
    public Dictionary<string, Side> StaticMethodSides { get; } = new();
    public Dictionary<string, Side> GetterSides { get; } = new();
    public Dictionary<string, Side> SetterSides { get; } = new();
    public Side ConstructorSide { get; set; } = Side.All;

    /// <summary>
    /// Optional format-string template that overrides how <c>new ClassName(args)</c>
    /// lowers to Lua at codegen. <c>null</c> means the default <c>ClassName.new(args)</c>
    /// shape. Source: a <c>@overrideCtor("...")</c> builtin annotation on the class.
    /// Placeholders: <c>$class</c> → the class identifier, <c>$args</c> → the
    /// comma-separated rendered arguments. Used by external runtimes that expose
    /// classes via a different call convention (e.g. nanos-world's <c>Database(args)</c>
    /// vs Nebra's <c>Database.new(args)</c>).
    /// </summary>
    public string? CtorTemplate { get; set; }

    protected override TypeKey GenerateNewKey()
    {
        return $"class<{Name}>";
    }
}

public sealed class InterfaceType(
    string name,
    List<InterfaceType> baseInterfaces
) : Type(TypeKind.Interface)
{
    public string Name { get; } = name;
    public List<InterfaceType> BaseInterfaces { get; } = baseInterfaces;
    public Dictionary<string, StructType.Field> Fields { get; } = new();
    public Dictionary<string, FunctionType> Methods { get; } = new();
    /// <summary>
    /// The name each member was declared under, keyed by member name. The name carries its span,
    /// file included, so tooling can locate a member's declaration from the type alone.
    /// </summary>
    public Dictionary<string, NameRef> MemberDeclarations { get; } = new();
    public List<TypeParameterType> TypeParams { get; } = new();

    /// <summary>
    /// Names of methods that carry a default implementation (a body). Implementing classes
    /// inherit these unless they override them, so they are not required to be implemented.
    /// </summary>
    public HashSet<string> DefaultMethods { get; } = new();

    /// <summary>
    /// The AST node for each default method, keyed by name — the source of the body that
    /// codegen copies into implementing classes that do not override it.
    /// </summary>
    public Dictionary<string, InterfaceMethodNode> DefaultMethodNodes { get; } = new();

    /// <summary>
    /// The AST node for every method, keyed by name (the last overload wins, mirroring
    /// <see cref="Methods"/>). Implementing classes read it to adopt the parameter defaults this
    /// interface promises, which <see cref="DefaultMethodNodes"/> cannot cover because it only
    /// holds the methods that carry a body.
    /// </summary>
    public Dictionary<string, InterfaceMethodNode> MethodNodes { get; } = new();

    /// <summary>
    /// All overloads per method name; the entry in <see cref="Methods"/> mirrors
    /// the last-inserted overload. See <see cref="ClassType.MethodOverloads"/>
    /// for the rationale.
    /// </summary>
    public Dictionary<string, List<FunctionType>> MethodOverloads { get; } = new();
    public Dictionary<string, List<Side>> MethodOverloadSides { get; } = new();

    /// <summary>Per-member side masks; same convention as <see cref="ClassType"/>.</summary>
    public Dictionary<string, Side> FieldSides { get; } = new();
    public Dictionary<string, Side> MethodSides { get; } = new();

    protected override TypeKey GenerateNewKey()
    {
        return $"interface<{Name}>";
    }
}

/// <summary>
/// Represents a type parameter in a generic declaration (e.g. <c>T</c> in <c>class List&lt;T&gt;</c>).
/// The owner key disambiguates <c>T</c> declared in different scopes so two unrelated generic
/// definitions do not collide in the type table.
/// </summary>
public sealed class TypeParameterType(
    string name,
    string ownerKey,
    int index,
    TypID? extendsBound = null,
    List<TypID>? implementsBounds = null
) : Type(TypeKind.TypeParameter)
{
    public string Name { get; } = name;
    public string OwnerKey { get; } = ownerKey;
    public int Index { get; } = index;
    public TypID? ExtendsBound { get; set; } = extendsBound;
    public List<TypID> ImplementsBounds { get; } = implementsBounds ?? new List<TypID>();

    protected override TypeKey GenerateNewKey()
    {
        return $"T#{OwnerKey}#{Index}#{Name}";
    }
}

/// <summary>
/// A concrete / wildcard argument supplied to a <see cref="ParameterizedType"/>.
/// </summary>
public sealed class TypeArg
{
    public enum ArgKind { Concrete, WildcardUnbounded, WildcardExtends, WildcardSuper }

    public ArgKind Kind { get; }
    public TypID? TypeID { get; }

    private TypeArg(ArgKind kind, TypID? typeID)
    {
        Kind = kind;
        TypeID = typeID;
    }

    public static TypeArg Concrete(TypID id) => new(ArgKind.Concrete, id);
    public static TypeArg Unbounded() => new(ArgKind.WildcardUnbounded, null);
    public static TypeArg Extends(TypID id) => new(ArgKind.WildcardExtends, id);
    public static TypeArg Super(TypID id) => new(ArgKind.WildcardSuper, id);

    public override string ToString()
    {
        return Kind switch
        {
            ArgKind.Concrete => TypeID?.ToString() ?? "?",
            ArgKind.WildcardUnbounded => "?",
            ArgKind.WildcardExtends => $"? extends {TypeID}",
            ArgKind.WildcardSuper => $"? super {TypeID}",
            _ => "?"
        };
    }
}

/// <summary>
/// Represents a generic type instantiation such as <c>List&lt;number&gt;</c>. The <see cref="Definition"/>
/// is the raw generic <see cref="ClassType"/> or <see cref="InterfaceType"/>; <see cref="Args"/> holds the
/// concrete or wildcard arguments. At codegen time this type is erased to <see cref="Definition"/>.
/// </summary>
public sealed class ParameterizedType(Type definition, List<TypeArg> args) : Type(TypeKind.Parameterized)
{
    public Type Definition { get; } = definition;
    public List<TypeArg> Args { get; } = args;

    protected override TypeKey GenerateNewKey()
    {
        return $"{Definition.Key}<{string.Join(",", Args.Select(a => a.ToString()))}>";
    }
}

/// <summary>
/// Represents a type table that maps type keys to their corresponding types. This is used to keep track of the types in
/// the IR, and to ensure that the types are unique and do not conflict with each other.
/// </summary>
public sealed class TypeTable
{
    /// <summary>
    /// The ID of the primitive nil.
    /// </summary>
    public Type PrimNil { get; }
    
    /// <summary>
    /// The ID of the primitive any.
    /// </summary>
    public Type PrimAny { get; }
    
    /// <summary>
    /// The ID of the primitive number.
    /// </summary>
    public Type PrimNumber { get; }
    
    /// <summary>
    /// The ID of the primitive boolean.
    /// </summary>
    public Type PrimBool { get; }
    
    /// <summary>
    /// The ID of the primitive string.
    /// </summary>
    public Type PrimString { get; }

    /// <summary>Canonical "any function" type — the target of <c>extend function</c>.</summary>
    public Type PrimFunction { get; }

    /// <summary>Canonical Lua <c>thread</c> (coroutine) type.</summary>
    public Type PrimThread { get; }

    /// <summary>Canonical Lua <c>userdata</c> type.</summary>
    public Type PrimUserdata { get; }

    /// <summary>
    /// The bottom type <c>never</c>: the type of an expression that never produces a value because
    /// control flow does not come back (<c>error(...)</c>, <c>os.exit(...)</c>). It is assignable to
    /// every type and nothing but itself is assignable to it, and it disappears from unions.
    /// </summary>
    public Type PrimNever { get; }

    private readonly IDAlloc<TypID> _typeAlloc;
    private readonly Dictionary<TypeKey, TypID> _types = new();
    private readonly Dictionary<TypID, Type> _byID = new();

    /// <summary>
    /// Creates a new type table with the specified type ID allocator. The type ID allocator is used to generate unique type IDs for the types in the type table.
    /// </summary>
    public TypeTable(IDAlloc<TypID> typeAlloc)
    {
        _typeAlloc = typeAlloc;
        
        PrimNil = DeclareType(new Type(TypeKind.PrimitiveNil));
        PrimAny = DeclareType(new Type(TypeKind.PrimitiveAny));
        PrimNumber = DeclareType(new Type(TypeKind.PrimitiveNumber));
        PrimBool = DeclareType(new Type(TypeKind.PrimitiveBool));
        PrimString = DeclareType(new Type(TypeKind.PrimitiveString));
        PrimFunction = DeclareType(new Type(TypeKind.PrimitiveFunction));
        PrimThread = DeclareType(new Type(TypeKind.PrimitiveThread));
        PrimUserdata = DeclareType(new Type(TypeKind.PrimitiveUserdata));
        PrimNever = DeclareType(new Type(TypeKind.PrimitiveNever));
    }

    /// <summary>
    /// Declares the specified type in the type table. If the type is already declared in the type table, this method
    /// returns the existing type ID of the type. Otherwise, this method generates a new type ID for the type, adds the
    /// type to the type table, and returns the new type ID.
    /// </summary>
    /// <param name="typ">The type to be declared in the type table.</param>
    /// <returns>The type ID of the declared type. This is a unique identifier for the type, and is used to reference the type from other nodes or from external code.</returns>
    public Type DeclareType(Type typ)
    {
        if (_types.TryGetValue(typ.Key, out var existingID))
        {
            return _byID[existingID];
        }

        var newID = _typeAlloc.Next();
        _types[typ.Key] = newID;
        _byID[newID] = typ;
        typ.ID = newID;
        return typ;
    }
    
    /// <summary>
    /// Tries to get the type with the specified ID from the type table. If the type with the specified ID exists in the
    /// type table, this method returns true and sets the output parameter to the type; otherwise, this method returns
    /// false and sets the output parameter to null.
    /// </summary>
    /// <param name="id">The type ID of the type to be retrieved from the type table.</param>
    /// <param name="typ">The output parameter that will contain the type with the specified ID if it exists in the type table; otherwise, null.</param>
    /// <returns>true if the type with the specified ID exists in the type table; otherwise, false.</returns>
    public bool GetByID(TypID id, out Type typ)
    {
        if (_byID.TryGetValue(id, out var outType))
        {
            typ = outType;
            return true;
        }
        
        typ = null!;
        return false;
    }

    /// <summary>
    /// Tries to get the type ID of the type with the specified key from the type table. If the type with the specified
    /// key exists in the type table, this method returns true and sets the output parameter to the type ID; otherwise,
    /// this method returns false and sets the output parameter to an invalid type ID.
    /// </summary>
    /// <param name="key">The type key of the type whose type ID is to be retrieved from the type table.</param>
    /// <param name="typ">The output parameter that will contain the type ID of the type with the specified key if it exists in the type table; otherwise, an invalid type ID.</param>
    /// <returns>true if the type with the specified key exists in the type table; otherwise, false.</returns>
    public bool GetByType(TypeKey key, out TypID typ)
    {
        if (_types.TryGetValue(key, out var outID))
        {
            typ = outID;
            return true;
        }
        
        typ = TypID.Invalid;
        return false;
    }
    
    /// <summary>
    /// Checks if the type with the specified ID is of the specified type kind. If the type with the specified ID does not exist in the type table, this method returns false.
    /// </summary>
    /// <param name="typ">The type ID of the type to be checked.</param>
    /// <param name="base">The type kind to be checked against. This is used to distinguish different types in the type system, such as primitive types, table types, function types, etc.</param>
    /// <returns>true if the type with the specified ID exists in the type table and is of the specified type kind; otherwise, false.</returns>
    public bool IsTypeOfKind(TypID typ, TypeKind @base)
    {
        if (!GetByID(typ, out var actualType))
        {
            return false;
        }
        
        return actualType.Kind == @base;
    }
    
    /// <summary>
    /// Creates a new table array type with the specified element type, declares the new type in the type table, and
    /// returns the type ID of the new type.
    /// </summary>
    /// <param name="elementType">The element type of the table array type to be created.</param>
    /// <returns>The type ID of the created table array type.</returns>
    public TypID ArrayOf(Type elementType)
    {
        var arrayType = new TableArrayType(elementType);
        return DeclareType(arrayType);
    }

    /// <summary>
    /// Creates (or returns cached) a <see cref="VariadicType"/> <c>...T</c> over the given element type.
    /// </summary>
    public VariadicType VariadicOf(Type elementType)
    {
        return (VariadicType)DeclareType(new VariadicType(elementType));
    }
    
    /// <summary>
    /// Creates a new table map type with the specified key type and value type, declares the new type in the type table, and
    /// returns the type ID of the new type.
    /// </summary>
    /// <param name="keyType">The key type of the table map type to be created.</param>
    /// <param name="valueType">The value type of the table map type to be created.</param>
    /// <returns>The type ID of the created table map type.</returns>
    public TypID MapOf(Type keyType, Type valueType)
    {
        var mapType = new TableMapType(keyType, valueType);
        return DeclareType(mapType);
    }
    
    /// <summary>
    /// Creates a new tuple type with the specified fields, declares the new type in the type table, and returns the type ID of the new type.
    /// </summary>
    /// <param name="fields">The fields of the tuple type to be created. Each field has a name (which can be null for unnamed fields) and a type.</param>
    /// <returns>The type ID of the created tuple type.</returns>
    public TypID TupleOf(IEnumerable<TupleType.Field> fields)
    {
        var tupleType = new TupleType(fields);
        return DeclareType(tupleType);
    }
    
    /// <summary>
    /// Creates a new tuple type with the specified fields, declares the new type in the type table, and returns the type ID of the new type.
    /// This is an overload of the <see cref="TupleOf(IEnumerable{TupleType.Field})"/> method that allows passing the fields as a params array for convenience.
    /// </summary>
    /// <param name="fields">The fields of the tuple type to be created. Each field has a name (which can be null for unnamed fields) and a type.</param>
    /// <returns>The type ID of the created tuple type.</returns>
    public TypID TupleOf(params TupleType.Field[] fields)
    {
        return TupleOf((IEnumerable<TupleType.Field>)fields);
    }

    /// <summary>
    /// Creates a new function type with the specified parameter types and return type, declares the new type in the
    /// type table, and returns the type ID of the new type.
    /// </summary>
    public TypID FuncOf(IEnumerable<Type> paramTypes, Type returnType, bool isVararg = false, Type? varargType = null, List<int>? defaultParams = null, bool isAsync = false)
    {
        var funcType = new FunctionType(paramTypes, returnType, isVararg, varargType, defaultParams, isAsync);
        return DeclareType(funcType);
    }

    public TypID FuncOf(IEnumerable<Tuple<string, Type>> paramTypes, Type returnType, bool isVararg = false, Type? varargType = null, List<int>? defaultParams = null, bool isAsync = false, TypePredicate? predicate = null)
    {
        var funcType = new FunctionType(paramTypes, returnType, isVararg, varargType, defaultParams, isAsync)
        {
            Predicate = predicate
        };
        return DeclareType(funcType);
    }

    /// <summary>
    /// Creates a new union type containing the specified member types, declares the new type in the type table,
    /// and returns the type ID of the new type.
    /// </summary>
    public TypID UnionOf(IEnumerable<Type> types)
    {
        var unionType = new UnionType(types);
        return DeclareType(unionType);
    }

    /// <summary>
    /// Creates a new struct type containing the specified fields, declares the new type in the type table,
    /// and returns the type ID of the new type.
    /// </summary>
    public TypID StructOf(IEnumerable<StructType.Field> fields)
    {
        var structType = new StructType(fields);
        return DeclareType(structType);
    }

    /// <summary>
    /// Creates a new enum type with the specified name, members and base type, declares it in the type table,
    /// and returns the registered type instance.
    /// </summary>
    public EnumType EnumOf(string name, IEnumerable<EnumType.Member> members, Type baseType)
    {
        var enumType = new EnumType(name, members, baseType);
        return (EnumType)DeclareType(enumType);
    }

    public ClassType ClassOf(string name, ClassType? baseClass = null, List<InterfaceType>? interfaces = null, bool isAbstract = false)
    {
        var classType = new ClassType(name, baseClass, interfaces ?? [], isAbstract);
        return (ClassType)DeclareType(classType);
    }

    public InterfaceType InterfaceOf(string name, List<InterfaceType>? baseInterfaces = null)
    {
        var interfaceType = new InterfaceType(name, baseInterfaces ?? []);
        return (InterfaceType)DeclareType(interfaceType);
    }

    /// <summary>
    /// Creates (or returns cached) a <see cref="TypeParameterType"/> for a generic declaration.
    /// </summary>
    public TypeParameterType TypeParamOf(string name, string ownerKey, int index, TypID? extendsBound = null, List<TypID>? implementsBounds = null)
    {
        var tp = new TypeParameterType(name, ownerKey, index, extendsBound, implementsBounds);
        return (TypeParameterType)DeclareType(tp);
    }

    /// <summary>
    /// Creates (or returns cached) a <see cref="ParameterizedType"/> representing a generic
    /// instantiation like <c>List&lt;number&gt;</c>.
    /// </summary>
    public ParameterizedType ParameterizedOf(Type definition, List<TypeArg> args)
    {
        var pt = new ParameterizedType(definition, args);
        return (ParameterizedType)DeclareType(pt);
    }

    /// <summary>
    /// Substitutes type parameter types referenced in <paramref name="t"/> using the supplied
    /// mapping. Types that contain no type parameters are returned unchanged. This is a best-effort
    /// structural substitution used when resolving member accesses on a parameterized receiver.
    /// </summary>
    public Type Substitute(Type t, Dictionary<TypID, Type> subst)
    {
        if (subst.Count == 0) return t;
        switch (t)
        {
            case TypeParameterType tp:
                return subst.TryGetValue(tp.ID, out var mapped) ? mapped : t;
            case TableArrayType arr:
            {
                var inner = Substitute(arr.ElementType, subst);
                return ReferenceEquals(inner, arr.ElementType) ? t : DeclareType(new TableArrayType(inner));
            }
            case TableMapType map:
            {
                var k = Substitute(map.KeyType, subst);
                var v = Substitute(map.ValueType, subst);
                return (ReferenceEquals(k, map.KeyType) && ReferenceEquals(v, map.ValueType))
                    ? t : DeclareType(new TableMapType(k, v));
            }
            case UnionType u:
            {
                var changed = false;
                var newTypes = new List<Type>();
                foreach (var mt in u.Types)
                {
                    var nt = Substitute(mt, subst);
                    if (!ReferenceEquals(nt, mt)) changed = true;
                    newTypes.Add(nt);
                }
                return changed ? DeclareType(new UnionType(newTypes)) : t;
            }
            case FunctionType fn:
            {
                var changed = false;
                var newParams = new List<Type>();
                foreach (var pt2 in fn.ParamTypes)
                {
                    var np = Substitute(pt2, subst);
                    if (!ReferenceEquals(np, pt2)) changed = true;
                    newParams.Add(np);
                }
                var newRet = Substitute(fn.ReturnType, subst);
                if (!ReferenceEquals(newRet, fn.ReturnType)) changed = true;
                return changed
                    ? DeclareType(new FunctionType(newParams, fn.ParamNames, newRet, fn.IsVararg, fn.VarargType, fn.DefaultParams, fn.IsAsync))
                    : t;
            }
            default:
                return t;
        }
    }
}

/// <summary>
/// The type key is a string representation of the type. 
/// </summary>
public sealed class TypeKey(string value) : IEquatable<TypeKey>
{
    /// <summary>
    /// The invalid type key. This is used to represent an invalid type, such as a type that cannot be resolved or a type that is not defined in the type table.
    /// </summary>
    public static readonly TypeKey Invalid = new("<invalid>");
    
    /// <summary>
    /// The string representation of the type.
    /// </summary>
    public string Value { get; } = value;

    #region General object overrides and operators

    public override string ToString()
    {
        return Value;
    }
    
    public static implicit operator string(TypeKey typeKey) => typeKey.Value;
    
    public static implicit operator TypeKey(string value) => new(value);

    public bool Equals(TypeKey? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Value == other.Value;
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj) || obj is TypeKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Value.GetHashCode();
    }

    public static bool operator ==(TypeKey? left, TypeKey? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(TypeKey? left, TypeKey? right)
    {
        return !Equals(left, right);
    }

    #endregion
}