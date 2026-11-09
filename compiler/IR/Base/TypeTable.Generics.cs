namespace Nebra.IR;

public sealed partial class TypeTable
{
    /// <summary>
    /// How deeply filling in one instantiation may create and fill in further ones. A generic
    /// member such as <c>wrap(): Box&lt;Box&lt;T&gt;&gt;</c> would otherwise nest instantiations without end.
    /// </summary>
    private const int MaxPopulationDepth = 8;

    private int _populationDepth;

    /// <summary>
    /// The instantiation of the generic class <paramref name="definition"/> with
    /// <paramref name="arguments"/>, such as <c>Box&lt;number&gt;</c>. Its members are the
    /// definition's with the type parameters replaced; they are filled in as soon as the definition's
    /// own members are declared. Instantiating a class with its own type parameters gives the
    /// class itself.
    /// </summary>
    public ClassType Instantiate(ClassType definition, List<Type> arguments)
    {
        if (definition.GenericDefinition != null || arguments.Count != definition.TypeParams.Count || arguments.Count == 0) return definition;
        if (arguments.Select((argument, index) => argument.ID == definition.TypeParams[index].ID).All(same => same)) return definition;

        var instance = (ClassType)DeclareType(new ClassType(definition.Name, null, [], definition.IsAbstract)
        {
            GenericDefinition = definition,
            TypeArguments = arguments,
            Origin = definition.Origin,
        });
        if (definition.Instantiations.Contains(instance)) return instance;

        definition.Instantiations.Add(instance);
        if (definition.MembersDeclared) Populate(instance);
        return instance;
    }

    /// <summary>The interface version of <see cref="Instantiate(ClassType, List{Type})"/>.</summary>
    public InterfaceType Instantiate(InterfaceType definition, List<Type> arguments)
    {
        if (definition.GenericDefinition != null || arguments.Count != definition.TypeParams.Count || arguments.Count == 0) return definition;
        if (arguments.Select((argument, index) => argument.ID == definition.TypeParams[index].ID).All(same => same)) return definition;

        var instance = (InterfaceType)DeclareType(new InterfaceType(definition.Name, [])
        {
            GenericDefinition = definition,
            TypeArguments = arguments,
            Origin = definition.Origin,
        });
        if (definition.Instantiations.Contains(instance)) return instance;

        definition.Instantiations.Add(instance);
        if (definition.MembersDeclared) Populate(instance);
        return instance;
    }

    /// <summary>
    /// Marks the members of <paramref name="definition"/> as declared and fills in, or refreshes,
    /// every instantiation of it created so far.
    /// </summary>
    public void CompleteMembers(ClassType definition)
    {
        definition.MembersDeclared = true;
        foreach (var instance in definition.Instantiations.ToList()) Populate(instance);
    }

    /// <summary>The interface version of <see cref="CompleteMembers(ClassType)"/>.</summary>
    public void CompleteMembers(InterfaceType definition)
    {
        definition.MembersDeclared = true;
        foreach (var instance in definition.Instantiations.ToList()) Populate(instance);
    }

    private void Populate(ClassType instance)
    {
        if (_populationDepth >= MaxPopulationDepth) return;
        _populationDepth++;
        try
        {
            var definition = instance.GenericDefinition!;
            var map = Substitution(definition.TypeParams, instance.TypeArguments, definition.ID, instance);

            instance.IsCompiledFromSource = definition.IsCompiledFromSource;
            instance.IsSealed = definition.IsSealed;
            instance.HasLocalConstructor = definition.HasLocalConstructor;
            instance.CtorTemplate = definition.CtorTemplate;
            instance.InheritTemplate = definition.InheritTemplate;
            instance.IndexHook = definition.IndexHook;
            instance.NewIndexHook = definition.NewIndexHook;
            instance.IsTemplate = definition.IsTemplate;
            instance.ReflectionId = definition.ReflectionId;
            instance.DeclaredAt = definition.DeclaredAt;
            instance.ConstructorSide = definition.ConstructorSide;
            instance.BaseClass = definition.BaseClass == null ? null : Substitute(definition.BaseClass, map) as ClassType;
            instance.ConstructorType = definition.ConstructorType == null ? null : (FunctionType)Substitute(definition.ConstructorType, map);
            Replace(instance.ConstructorOverloads, definition.ConstructorOverloads.Select(constructor => (FunctionType)Substitute(constructor, map)));
            Replace(instance.LocalConstructors, definition.LocalConstructors.Select(constructor => (FunctionType)Substitute(constructor, map)));

            Replace(instance.Interfaces, definition.Interfaces.Select(iface => Substitute(iface, map)).OfType<InterfaceType>());
            Replace(instance.InstanceFields, definition.InstanceFields, field => new StructType.Field(field.Name, Substitute(field.Type, map), field.IsMeta));
            Replace(instance.Methods, definition.Methods, method => (FunctionType)Substitute(method, map));
            Replace(instance.StaticMethods, definition.StaticMethods, method => (FunctionType)Substitute(method, map));
            Replace(instance.Getters, definition.Getters, getter => (FunctionType)Substitute(getter, map));
            Replace(instance.Setters, definition.Setters, setter => (FunctionType)Substitute(setter, map));
            Replace(instance.MethodOverloads, definition.MethodOverloads, overloads => overloads.Select(method => (FunctionType)Substitute(method, map)).ToList());
            Replace(instance.StaticMethodOverloads, definition.StaticMethodOverloads, overloads => overloads.Select(method => (FunctionType)Substitute(method, map)).ToList());
            Replace(instance.MethodOverloadSides, definition.MethodOverloadSides, sides => sides.ToList());
            Replace(instance.StaticMethodOverloadSides, definition.StaticMethodOverloadSides, sides => sides.ToList());
            Replace(instance.MemberDeclarations, definition.MemberDeclarations, declaration => declaration);
            Replace(instance.DefaultsToEmit, definition.DefaultsToEmit, node => node);
            Replace(instance.FieldSides, definition.FieldSides, side => side);
            Replace(instance.MethodSides, definition.MethodSides, side => side);
            Replace(instance.StaticMethodSides, definition.StaticMethodSides, side => side);
            Replace(instance.GetterSides, definition.GetterSides, side => side);
            Replace(instance.SetterSides, definition.SetterSides, side => side);
            Replace(instance.AbstractMethods, definition.AbstractMethods);
            Replace(instance.ProtectedMembers, definition.ProtectedMembers);
            Replace(instance.PrivateMembers, definition.PrivateMembers);
            instance.MembersDeclared = definition.MembersDeclared;
        }
        finally
        {
            _populationDepth--;
        }
    }

    private void Populate(InterfaceType instance)
    {
        if (_populationDepth >= MaxPopulationDepth) return;
        _populationDepth++;
        try
        {
            var definition = instance.GenericDefinition!;
            var map = Substitution(definition.TypeParams, instance.TypeArguments, definition.ID, instance);

            instance.ReflectionId = definition.ReflectionId;
            instance.DeclaredAt = definition.DeclaredAt;
            Replace(instance.BaseInterfaces, definition.BaseInterfaces.Select(iface => Substitute(iface, map)).OfType<InterfaceType>());
            Replace(instance.Fields, definition.Fields, field => new StructType.Field(field.Name, Substitute(field.Type, map), field.IsMeta));
            Replace(instance.Methods, definition.Methods, method => (FunctionType)Substitute(method, map));
            Replace(instance.MethodOverloads, definition.MethodOverloads, overloads => overloads.Select(method => (FunctionType)Substitute(method, map)).ToList());
            Replace(instance.MethodOverloadSides, definition.MethodOverloadSides, sides => sides.ToList());
            Replace(instance.MemberDeclarations, definition.MemberDeclarations, declaration => declaration);
            Replace(instance.DefaultMethodNodes, definition.DefaultMethodNodes, node => node);
            Replace(instance.MethodNodes, definition.MethodNodes, node => node);
            Replace(instance.FieldSides, definition.FieldSides, side => side);
            Replace(instance.MethodSides, definition.MethodSides, side => side);
            Replace(instance.DefaultMethods, definition.DefaultMethods);
            instance.MembersDeclared = definition.MembersDeclared;
        }
        finally
        {
            _populationDepth--;
        }
    }

    /// <summary>
    /// The map replacing the type parameters of a generic definition with the arguments of one of
    /// its instantiations, and the definition itself, as the type of <c>self</c>, with the instantiation.
    /// </summary>
    private static Dictionary<TypID, Type> Substitution(List<TypeParameterType> typeParams, List<Type> arguments, TypID definition, Type instance)
    {
        var map = new Dictionary<TypID, Type> { [definition] = instance };
        for (var index = 0; index < typeParams.Count && index < arguments.Count; index++)
            map[typeParams[index].ID] = arguments[index];
        return map;
    }

    private static void Replace<T>(List<T> target, IEnumerable<T> source)
    {
        var items = source.ToList();
        target.Clear();
        target.AddRange(items);
    }

    private static void Replace<T>(HashSet<T> target, IEnumerable<T> source)
    {
        var items = source.ToList();
        target.Clear();
        target.UnionWith(items);
    }

    private static void Replace<TValue, TSource>(Dictionary<string, TValue> target, Dictionary<string, TSource> source, Func<TSource, TValue> convert)
    {
        var items = source.ToList();
        target.Clear();
        foreach (var (key, value) in items) target[key] = convert(value);
    }

    /// <summary>
    /// Substitutes inside the named types: a class or interface the map replaces, such as a generic
    /// definition standing for <c>self</c>, and the type arguments of an instantiation.
    /// </summary>
    private Type? SubstituteNamed(Type t, Dictionary<TypID, Type> subst)
    {
        switch (t)
        {
            case ClassType or InterfaceType when subst.TryGetValue(t.ID, out var mapped):
                return mapped;
            case ClassType { GenericDefinition: { } definition } instance:
            {
                var arguments = instance.TypeArguments.Select(argument => Substitute(argument, subst)).ToList();
                return arguments.Zip(instance.TypeArguments).All(pair => ReferenceEquals(pair.First, pair.Second)) ? t : Instantiate(definition, arguments);
            }
            case InterfaceType { GenericDefinition: { } definition } instance:
            {
                var arguments = instance.TypeArguments.Select(argument => Substitute(argument, subst)).ToList();
                return arguments.Zip(instance.TypeArguments).All(pair => ReferenceEquals(pair.First, pair.Second)) ? t : Instantiate(definition, arguments);
            }
            case StructType structType:
            {
                var fields = structType.Fields.Select(field => new StructType.Field(field.Name, Substitute(field.Type, subst), field.IsMeta)).ToList();
                return fields.Zip(structType.Fields).All(pair => ReferenceEquals(pair.First.Type, pair.Second.Type)) ? t : DeclareType(new StructType(fields));
            }
            default:
                return null;
        }
    }
}
