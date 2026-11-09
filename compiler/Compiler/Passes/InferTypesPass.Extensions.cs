using Nebra.Diagnostics;
using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

public sealed partial class InferTypesPass
{
    private static readonly Dictionary<string, string> OperatorSymbols = new(StringComparer.Ordinal)
    {
        ["__add"] = "+", ["__sub"] = "-", ["__mul"] = "*", ["__div"] = "/", ["__idiv"] = "//",
        ["__mod"] = "%", ["__pow"] = "^", ["__concat"] = "..", ["__eq"] = "==", ["__lt"] = "<",
        ["__le"] = "<=", ["__len"] = "#", ["__unm"] = "-", ["__band"] = "&", ["__bor"] = "|",
        ["__bxor"] = "~", ["__bnot"] = "~", ["__shl"] = "<<", ["__shr"] = ">>",
        ["__op_index"] = "[]", ["__op_newindex"] = "[]=", ["__op_call"] = "()",
    };

    /// <summary>The symbol an operator is written with, given the name of its metamethod.</summary>
    public static string OperatorSymbol(string metamethod) => OperatorSymbols.GetValueOrDefault(metamethod, metamethod);

    /// <summary>
    /// The key under which <see cref="CodegenPass"/> finds the extensions a file uses, so it can
    /// bind the ones its imports bring in.
    /// </summary>
    public static string UsedExtensionsKey(string filename) => "used_extensions:" + Path.GetFullPath(filename);

    /// <summary>
    /// Registers the members of an <c>extend</c> block on the type it extends, each with its
    /// signature, the name of its Lua function and where it may be used from. Runs before bodies
    /// are resolved so a use may precede its <c>extend</c> block.
    /// </summary>
    private void RegisterExtensionSignatures(PassContext pc, ExtendDecl ed, DeclareModuleDecl? module)
    {
        if (!_registeredExtendDecls.Add(ed.ID)) return;
        var target = ResolveExtendTarget(pc, ed);
        if (target == null) return;

        foreach (var member in ed.Methods)
        {
            member.Signature = ExtensionSignature(pc, target, member);
            member.LuaName = ExtensionLuaName(target, member);
            member.DeclaringFile = pc.File?.Filename;
            member.DeclaringModule = module;
            member.IsExported = ed.IsExported || module != null;
            member.IsGlobal = ed.IsDeclare && module == null && pc.File is { IsDeclarationFile: true };

            if (member.Kind != ExtensionMemberKind.Operator && target is not TableMapType && HasOwnMember(target, member.Name.Name))
                pc.Diag.Report(member.Name.Span, DiagnosticCode.WarnShadowedExtension, member.Name.Name, TypeName(pc, target.ID));

            switch (member.Kind)
            {
                case ExtensionMemberKind.Method:
                    if (!IsNewExtension(pc, target, member, target.ExtensionMethods.ContainsKey(member.Name.Name))) break;
                    target.ExtensionMethods[member.Name.Name] = member.Signature;
                    target.ExtensionMethodNodes[member.Name.Name] = member;
                    break;
                case ExtensionMemberKind.Getter:
                    ReportAccessorParameters(pc, AccessorKind.Getter, member.Name, member.Parameters);
                    if (!IsNewExtension(pc, target, member, target.ExtensionGetters.ContainsKey(member.Name.Name))) break;
                    target.ExtensionGetters[member.Name.Name] = member;
                    break;
                case ExtensionMemberKind.Operator:
                    RegisterExtensionOperator(pc, target, member);
                    break;
            }
        }
    }

    private bool IsNewExtension(PassContext pc, Type target, ExtensionMethodNode member, bool exists)
    {
        if (!exists) return true;

        pc.Diag.Report(member.Name.Span, DiagnosticCode.ErrDuplicateExtension, member.Name.Name, TypeName(pc, target.ID));
        return false;
    }

    private void RegisterExtensionOperator(PassContext pc, Type target, ExtensionMethodNode member)
    {
        var metamethod = member.Name.Name;
        if (!member.IsGlobal && member.DeclaringModule == null)
            ReportOperatorReturns(pc, metamethod, member.ReturnType, member.Signature!.ReturnType);
        var operand = member.Signature!.ParamTypes.Count > 1 ? member.Signature.ParamTypes[1] : null;
        var symbol = OperatorSymbols.GetValueOrDefault(metamethod, metamethod);
        if (ReplacesBuiltInOperator(target, metamethod, operand))
        {
            pc.Diag.Report(member.Name.Span, DiagnosticCode.ErrExtensionReplacesOperator,
                TypeName(pc, target.ID), symbol, operand == null ? "no operand" : TypeName(pc, operand.ID));
            return;
        }

        if (!target.ExtensionOperators.TryGetValue(metamethod, out var overloads))
        {
            overloads = [];
            target.ExtensionOperators[metamethod] = overloads;
        }

        var suffix = OverloadSignature.Suffix(member.Signature, true);
        if (overloads.Any(overload => OverloadSignature.Suffix(overload.Signature!, true) == suffix))
        {
            pc.Diag.Report(member.Name.Span, DiagnosticCode.ErrDuplicateExtension, "operator " + symbol, TypeName(pc, target.ID));
            return;
        }

        overloads.Add(member);
    }

    /// <summary>The signature of an extension member, with the extended type as its first parameter <c>self</c>.</summary>
    private FunctionType ExtensionSignature(PassContext pc, Type target, ExtensionMethodNode member)
    {
        var parameters = new List<Tuple<string, Type>> { new("self", target) };
        var isVararg = false;
        Type? varargType = null;
        var defaultIndices = new List<int>();
        for (var i = 0; i < member.Parameters.Count; i++)
        {
            var parameter = member.Parameters[i];
            var type = ResolveParamType(pc, parameter);
            if (parameter.IsVararg)
            {
                isVararg = true;
                varargType = type.Kind == TypeKind.PrimitiveAny ? null : type;
            }
            else
            {
                parameters.Add(new Tuple<string, Type>(parameter.Name.Name, type));
            }

            if (parameter.DefaultValue != null) defaultIndices.Add(i + 1);
        }

        var returnType = member.ReturnType != null && member.ReturnType.ResolvedType != TypID.Invalid
            ? GetType(pc, member.ReturnType.ResolvedType)
            : UnannotatedReturnType(pc, member.Body, member.ReturnStmt);
        return (FunctionType)GetType(pc, pc.Types.FuncOf(parameters, returnType, isVararg, varargType,
            defaultIndices.Count > 0 ? defaultIndices : null, member.IsAsync));
    }

    /// <summary>
    /// The name of the Lua function holding an extension member: <c>__ext_number_hours</c> for a
    /// method, <c>__ext_number_get_hour</c> for a getter and <c>__ext_string_shl_number</c> for an
    /// operator, which names its operand so its overloads differ.
    /// </summary>
    private static string ExtensionLuaName(Type target, ExtensionMethodNode member)
    {
        var type = new string(target.Key.Value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
        var name = member.Name.Name;
        return member.Kind switch
        {
            ExtensionMemberKind.Getter => $"__ext_{type}_get_{name}",
            ExtensionMemberKind.Operator when member.Signature!.ParamTypes.Count > 1 =>
                $"__ext_{type}_{name.TrimStart('_')}_{OverloadSignature.Suffix(member.Signature, true)}",
            ExtensionMemberKind.Operator => $"__ext_{type}_{name.TrimStart('_')}",
            _ => $"__ext_{type}_{name}",
        };
    }

    /// <summary>
    /// Whether Lua already applies <paramref name="metamethod"/> to a value of
    /// <paramref name="target"/> and an <paramref name="operand"/>, which an extension must not
    /// take over: arithmetic and bitwise operators between numbers, concatenation of strings and
    /// numbers, comparisons within numbers or strings, the length of strings and tables, indexing
    /// strings and tables, calling functions, equality everywhere, and any operator a class
    /// declares itself.
    /// </summary>
    private static bool ReplacesBuiltInOperator(Type target, string metamethod, Type? operand)
    {
        if (metamethod == "__eq") return true;

        if (target is ClassType classType)
        {
            for (var current = classType; current != null; current = current.BaseClass)
            {
                if (current.Methods.ContainsKey(metamethod)) return true;
            }

            return false;
        }

        var number = target.Kind == TypeKind.PrimitiveNumber;
        var text = target.Kind == TypeKind.PrimitiveString;
        var table = target.Kind is TypeKind.TableArray or TypeKind.TableMap or TypeKind.Struct or TypeKind.Tuple;
        var operandNumber = operand == null || operand.Kind is TypeKind.PrimitiveNumber or TypeKind.PrimitiveAny;
        var operandText = operand == null || operand.Kind is TypeKind.PrimitiveString or TypeKind.PrimitiveAny;
        return metamethod switch
        {
            "__unm" or "__bnot" => number,
            "__len" => text || table,
            "__op_index" => text || table,
            "__op_newindex" => table,
            "__op_call" => target.Kind is TypeKind.Function or TypeKind.PrimitiveFunction,
            "__concat" => (number || text) && (operandNumber || operandText),
            "__lt" or "__le" => (number && operandNumber) || (text && operandText),
            _ => number && operandNumber,
        };
    }

    /// <summary>
    /// Yields every <c>extend</c> block reachable from <paramref name="body"/> with the
    /// <c>declare module</c> holding it, if any, as a generated declaration file writes them.
    /// </summary>
    private static IEnumerable<(ExtendDecl Decl, DeclareModuleDecl? Module)> CollectExtendDecls(List<Stmt> body)
    {
        foreach (var stmt in body)
        {
            if (stmt is ExtendDecl ed)
            {
                yield return (ed, null);
            }
            else if (stmt is DeclareModuleDecl dmd)
            {
                foreach (var member in dmd.Members.OfType<ExtendDecl>())
                {
                    yield return (member, dmd);
                }
            }
        }
    }

    /// <summary>
    /// Records that the current file uses <paramref name="member"/>, reporting the use when the
    /// member is local to another module or declared by a module this file does not import.
    /// </summary>
    private void UseExtension(PassContext pc, ExtensionMethodNode member, TextSpan span, Type target)
    {
        if (pc.File?.Filename is not { } filename) return;

        var display = member.Kind == ExtensionMemberKind.Operator
            ? "operator " + OperatorSymbols.GetValueOrDefault(member.Name.Name, member.Name.Name)
            : member.Name.Name;
        if (!member.IsGlobal && member.DeclaringFile != null && !IsSameFile(member.DeclaringFile, filename))
        {
            if (!member.IsExported)
                pc.Diag.Report(span, DiagnosticCode.ErrExtensionNotExported, display, TypeName(pc, target.ID), ModuleLabel(pc, member));
            else if (!ImportsExtension(pc, member))
                pc.Diag.Report(span, DiagnosticCode.ErrExtensionNotImported, display, TypeName(pc, target.ID), ModuleLabel(pc, member));
        }

        var key = UsedExtensionsKey(filename);
        if (!pc.Cache.TryGetValue(key, out var cached) || cached is not HashSet<ExtensionMethodNode> used)
        {
            used = [];
            pc.Cache[key] = used;
        }

        used.Add(member);
    }

    private static bool IsSameFile(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.Ordinal);

    private static string ModuleLabel(PassContext pc, ExtensionMethodNode member)
    {
        if (member.DeclaringModule != null) return member.DeclaringModule.ModuleName.Name;

        var source = Path.GetFullPath(Path.Combine(pc.Config.ProjectRoot, pc.Config.Source));
        var relative = Path.GetRelativePath(source, Path.GetFullPath(member.DeclaringFile!));
        return Path.ChangeExtension(relative, null).Replace('\\', '/');
    }

    /// <summary>Whether the current file imports the module declaring <paramref name="member"/>.</summary>
    private static bool ImportsExtension(PassContext pc, ExtensionMethodNode member)
    {
        return pc.File!.Hir.Body.OfType<ImportStmt>()
            .Where(import => !import.IsTypeOnly && !import.IsTypeImport)
            .Any(import => ExtensionComesFrom(member, import,
                pc.Cache.GetValueOrDefault($"import_resolved:{pc.File.Filename}:{import.Module.Name}") as ResolvedModule));
    }

    /// <summary>
    /// Whether <paramref name="import"/>, resolved to <paramref name="resolved"/>, loads the module
    /// declaring <paramref name="member"/>: the source file holding it, or the <c>declare module</c>
    /// describing it.
    /// </summary>
    public static bool ExtensionComesFrom(ExtensionMethodNode member, ImportStmt import, ResolvedModule? resolved)
    {
        if (member.DeclaringModule != null)
            return import.Module.Name == member.DeclaringModule.ModuleName.Name || resolved?.DeclareModule == member.DeclaringModule;

        return resolved?.File?.Filename is { } imported && member.DeclaringFile != null && IsSameFile(imported, member.DeclaringFile);
    }

    /// <summary>
    /// Types <paramref name="dot"/> as a read of an extension getter when the object's type does
    /// not have a member of that name itself.
    /// </summary>
    private bool TryExtensionGetter(PassContext pc, DotAccessExpr dot, Type baseType, out TypID result)
    {
        result = pc.Types.PrimAny.ID;
        if (baseType.Kind == TypeKind.PrimitiveAny || HasOwnMember(baseType, dot.FieldName.Name)) return false;

        var getter = Type.ResolveExtensionGetter(baseType, dot.FieldName.Name, pc.Types.PrimFunction);
        if (getter?.Signature == null) return false;

        UseExtension(pc, getter, dot.FieldName.Span, baseType);
        dot.Extension = getter;
        result = getter.Signature.ReturnType.ID;
        return true;
    }

    private static bool HasOwnMember(Type type, string name)
    {
        switch (type)
        {
            case StructType structType:
                return structType.Fields.Any(field => field.Name.Name == name);
            case TableMapType:
                return true;
            case EnumType enumType:
                return enumType.Members.Any(member => member.Name == name);
            case ClassType classType:
                for (var current = classType; current != null; current = current.BaseClass)
                {
                    if (current.InstanceFields.ContainsKey(name) || current.Methods.ContainsKey(name)
                        || current.Getters.ContainsKey(name) || current.StaticMethods.ContainsKey(name)) return true;
                }

                return Type.ImplementedInterfaces(classType).Any(iface => iface.Fields.ContainsKey(name) || iface.Methods.ContainsKey(name));
            case InterfaceType interfaceType:
                return Type.BaseInterfacesOf(interfaceType).Prepend(interfaceType)
                    .Any(iface => iface.Fields.ContainsKey(name) || iface.Methods.ContainsKey(name));
            default:
                return false;
        }
    }

    /// <summary>
    /// Types <paramref name="bin"/> as a call of an extension operator of the left operand's type
    /// whose operand takes the right one.
    /// </summary>
    private bool TryExtensionOperator(PassContext pc, BinaryExpr bin, TypID left, TypID right, out TypID result)
    {
        result = pc.Types.PrimAny.ID;
        var metamethod = BinaryOpToMetamethod(bin.Op);
        if (metamethod == null || !pc.Types.GetByID(left, out var leftType) || leftType.Kind == TypeKind.PrimitiveAny) return false;

        var extension = Type.ResolveExtensionOperators(leftType, metamethod, pc.Types.PrimFunction)
            .FirstOrDefault(candidate => candidate.Signature!.ParamTypes.Count == 2
                && IsTypeAssignable(pc, candidate.Signature.ParamTypes[1].ID, right));
        if (extension == null) return false;

        UseExtension(pc, extension, bin.Span, leftType);
        bin.Extension = extension;
        result = extension.Signature!.ReturnType.ID;
        return true;
    }

    /// <summary>Types <paramref name="un"/> as a call of a unary extension operator of the operand's type.</summary>
    private bool TryExtensionOperator(PassContext pc, UnaryExpr un, TypID operand, out TypID result)
    {
        result = pc.Types.PrimAny.ID;
        var metamethod = UnaryOpToMetamethod(un.Op);
        if (metamethod == null || !pc.Types.GetByID(operand, out var operandType) || operandType.Kind == TypeKind.PrimitiveAny) return false;

        var extension = Type.ResolveExtensionOperators(operandType, metamethod, pc.Types.PrimFunction)
            .FirstOrDefault(candidate => candidate.Signature!.ParamTypes.Count == 1);
        if (extension == null) return false;

        UseExtension(pc, extension, un.Span, operandType);
        un.Extension = extension;
        result = extension.Signature!.ReturnType.ID;
        return true;
    }
}
