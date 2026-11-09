using System.Text;
using Nebra.Compiler.Annotations;
using Nebra.Configuration;
using Nebra.Doc;
using Nebra.IR;

using Type = Nebra.IR.Type;

namespace Nebra.LPS;

/// <summary>
/// Writes what a hover shows: the declaration the way it is written in Nebra, where it runs, the
/// types it mentions as links, and its documentation. Classes, interfaces and enums list their
/// public members; an enum member shows the value it stands for.
/// </summary>
public sealed class HoverRenderer(AnalysisResult result, NebraWorkspace workspace)
{
    private const int MaxListedMembers = 40;

    /// <summary>The parts of a hover; everything but the code is optional.</summary>
    private sealed record HoverText(string Code, Side Side = Side.All, DocComment? Doc = null, Type? Mentions = null);

    /// <summary>The hover for a name bound to <paramref name="symbol"/>, typed <paramref name="effectiveType"/> where it is used.</summary>
    public string ForSymbol(Symbol symbol, TypID effectiveType)
    {
        var origin = result.Snapshot.TryGetSymbol(result.Snapshot.Origin(symbol.ID), out var declared) ? declared : symbol;
        var node = DeclarationOf(origin);
        var doc = (node as Decl)?.Doc;

        if (result.Types.GetByID(origin.Type, out var ownType) && origin.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Enum)
        {
            return Render(new HoverText(Body(ownType), origin.Side, doc, ownType));
        }

        if (node is DeclareModuleDecl module)
        {
            return Render(new HoverText(ModuleSummary(module), origin.Side, doc));
        }

        result.Types.GetByID(effectiveType, out var shownType);
        if (origin.Kind == SymbolKind.Function && ownType is FunctionType function)
        {
            var code = Signature(FunctionKeyword(node), symbol.Name, function, ParametersOf(node), skipSelf: false);
            return Render(new HoverText(code, origin.Side, doc, function.ReturnType));
        }

        var variable = $"{VariableKeyword(node)} {symbol.Name}: {Format(shownType)}";
        if (effectiveType != origin.Type && ownType != null)
        {
            variable += $"\n-- narrowed from {Format(ownType)}";
        }

        return Render(new HoverText(variable, origin.Side, doc, shownType));
    }

    /// <summary>
    /// The hover for the member <paramref name="name"/> of <paramref name="receiverType"/>, where
    /// it is used or where it is declared. Null when the type has no such member.
    /// </summary>
    public string? ForMember(Type receiverType, string name)
    {
        var receiver = WithoutNil(receiverType);

        if (receiver is EnumType enumType)
        {
            return ForEnumMember(enumType, name);
        }

        var owner = MemberResolver.FindOwner(receiver, name);
        if (owner == null)
        {
            return ForExtension(receiver, name);
        }

        var side = SideMarkers.MemberSide(result, owner, name);
        var doc = MemberDoc(owner, name);
        var ownerName = NameOf(owner);

        return owner switch
        {
            ClassType classType => ForClassMember(classType, ownerName, name, side, doc),
            InterfaceType interfaceType => ForInterfaceMember(interfaceType, ownerName, name, side, doc),
            _ => null
        };
    }

    public string ForMemberDeclaration(Type owner, string name)
    {
        return ForMember(owner, name) ?? $"```nebra\n{name}\n```";
    }

    /// <summary>
    /// The hover for an annotation: how it is written, what it may be put on, where it is
    /// defined, what its file says about it and, when it was applied here, what it expanded to.
    /// </summary>
    public static string ForAnnotation(AnnotationEntry entry, AnnotationExpansion? expansion)
    {
        var markdown = new StringBuilder();
        markdown.Append("```nebra\n").Append(AnnotationCatalog.Signature(entry)).Append("\n```");
        markdown.Append("\n\nApplies to ").Append(AnnotationCatalog.TargetsText(entry));

        if (entry.SourcePath == null)
        {
            markdown.Append(" · built into the compiler");
        }
        else
        {
            var link = OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri.FromFileSystemPath(entry.SourcePath);
            markdown.Append(" · defined in [").Append(Path.GetFileName(entry.SourcePath)).Append("](").Append(link).Append(')');
        }

        if (!string.IsNullOrEmpty(entry.Documentation))
        {
            markdown.Append("\n\n---\n\n").Append(entry.Documentation);
        }

        if (expansion != null)
        {
            markdown.Append("\n\n---\n\n**Expands to:**\n\n```nebra\n").Append(IrPrinter.Print(expansion.Result)).Append("\n```");
        }

        return markdown.ToString();
    }

    public string ForExpression(TypID type)
    {
        result.Types.GetByID(type, out var shown);
        return Render(new HoverText(Format(shown), Mentions: shown));
    }

    private string? ForClassMember(ClassType classType, string ownerName, string name, Side side, DocComment? doc)
    {
        if (classType.InstanceFields.TryGetValue(name, out var field))
        {
            var prefix = classType.PrivateMembers.Contains(name) ? "(private field)"
                : classType.ProtectedMembers.Contains(name) ? "(protected field)"
                : "(field)";
            return Render(new HoverText($"{prefix} {ownerName}.{name}: {Format(field.Type)}", side, doc, field.Type));
        }

        if (classType.Getters.TryGetValue(name, out var getter))
        {
            return Render(new HoverText($"get {ownerName}.{name}: {Format(getter.ReturnType)}", side, doc, getter.ReturnType));
        }

        if (classType.Methods.TryGetValue(name, out var method))
        {
            var keyword = classType.PrivateMembers.Contains(name) ? "local function"
                : classType.ProtectedMembers.Contains(name) ? "protected function"
                : "function";
            return Render(new HoverText(Signature(keyword, $"{ownerName}:{name}", method, null, skipSelf: true), side, doc, method.ReturnType));
        }

        if (classType.StaticMethods.TryGetValue(name, out var staticMethod))
        {
            return Render(new HoverText(Signature("static function", $"{ownerName}.{name}", staticMethod, null, skipSelf: false), side, doc, staticMethod.ReturnType));
        }

        if (classType.Setters.TryGetValue(name, out var setter))
        {
            return Render(new HoverText(Signature("set", $"{ownerName}.{name}", setter, null, skipSelf: true), side, doc));
        }

        return null;
    }

    private string? ForInterfaceMember(InterfaceType interfaceType, string ownerName, string name, Side side, DocComment? doc)
    {
        if (interfaceType.Fields.TryGetValue(name, out var field))
        {
            return Render(new HoverText($"(field) {ownerName}.{name}: {Format(field.Type)}", side, doc, field.Type));
        }

        if (!interfaceType.Methods.TryGetValue(name, out var method)) return null;

        var isMethod = interfaceType.MethodNodes.TryGetValue(name, out var node) && node.IsMethod;
        var code = isMethod
            ? Signature("method", $"{ownerName}:{name}", method, node?.Parameters, skipSelf: false)
            : Signature("function", $"{ownerName}.{name}", method, node?.Parameters, skipSelf: false);
        return Render(new HoverText(code, side, doc, method.ReturnType));
    }

    private string? ForEnumMember(EnumType enumType, string name)
    {
        var member = enumType.Members.FirstOrDefault(candidate => candidate.Name == name);
        if (member == null) return null;

        var declaration = DeclarationOf(enumType) as EnumDecl;
        var doc = declaration?.Members.FirstOrDefault(candidate => candidate.Name.Name == name)?.Doc;
        var code = $"(enum member) {enumType.Name}.{name} = {EnumValue(enumType, member)}";
        return Render(new HoverText(code, SideOf(enumType), doc, enumType));
    }

    private string? ForExtension(Type receiver, string name)
    {
        var (extension, target) = Type.ResolveExtension(receiver, name, result.Types.PrimFunction);
        if (extension == null || target == null) return null;

        var code = Signature("function", $"{NameOf(target)}:{name}", extension, null, skipSelf: true);
        return Render(new HoverText($"{code}\n-- extension", Doc: MemberDoc(target, name), Mentions: extension.ReturnType));
    }

    private string Render(HoverText text)
    {
        var markdown = new StringBuilder();
        markdown.Append("```nebra\n").Append(text.Code).Append("\n```");

        var side = workspace.Settings.SideInHover ? SideMarkers.NameOf(text.Side) : null;
        if (side != null)
        {
            markdown.Append("\n\n").Append(SideIcon(side)).Append(" runs on the **").Append(side).Append("**");
        }

        var mentions = text.Mentions == null ? null : workspace.FormatTypeReferencesLine(result, text.Mentions);
        if (!string.IsNullOrEmpty(mentions))
        {
            markdown.Append("\n\n").Append(mentions);
        }

        var doc = text.Doc == null ? null : DocMarkdown.Render(text.Doc);
        if (!string.IsNullOrEmpty(doc))
        {
            markdown.Append("\n\n---\n\n").Append(doc);
        }

        return markdown.ToString();
    }

    private static string SideIcon(string side)
    {
        return side switch
        {
            SideMarkers.Client => "🟧",
            SideMarkers.Server => "🟦",
            _ => "🟩"
        };
    }

    /// <summary>A class, interface or enum as declared, listing its public members.</summary>
    private string Body(Type type)
    {
        return type switch
        {
            ClassType classType => ClassBody(classType),
            InterfaceType interfaceType => InterfaceBody(interfaceType),
            EnumType enumType => EnumBody(enumType),
            _ => Format(type)
        };
    }

    private string ClassBody(ClassType classType)
    {
        var header = new StringBuilder(classType.IsAbstract ? "abstract class " : "class ").Append(classType.Name);
        if (classType.BaseClass != null) header.Append(" extends ").Append(classType.BaseClass.Name);
        if (classType.Interfaces.Count > 0) header.Append(" implements ").Append(string.Join(", ", classType.Interfaces.Select(iface => iface.Name)));

        var members = new List<string>();
        members.AddRange(classType.InstanceFields.Where(entry => IsPublic(classType, entry.Key)).Select(entry => $"{entry.Key}: {Format(entry.Value.Type)}"));
        if (classType.ConstructorOverloads.Count > 0) members.AddRange(classType.ConstructorOverloads.Select(Constructor));
        else if (classType.ConstructorType != null) members.Add(Constructor(classType.ConstructorType));
        members.AddRange(classType.Getters.Where(entry => IsPublic(classType, entry.Key)).Select(entry => $"get {entry.Key}(): {Format(entry.Value.ReturnType)}"));
        members.AddRange(classType.Methods.Where(entry => IsPublic(classType, entry.Key)).Select(entry => Signature("function", entry.Key, entry.Value, null, skipSelf: true)));
        members.AddRange(classType.StaticMethods.Where(entry => IsPublic(classType, entry.Key)).Select(entry => Signature("static function", entry.Key, entry.Value, null, skipSelf: false)));

        return Block(header.ToString(), members);
    }

    private string Constructor(FunctionType constructor)
    {
        return $"constructor({string.Join(", ", TypedParameters(constructor, skipSelf: false))})";
    }

    private static bool IsPublic(ClassType classType, string member)
    {
        return !classType.ProtectedMembers.Contains(member) && !classType.PrivateMembers.Contains(member);
    }

    private string InterfaceBody(InterfaceType interfaceType)
    {
        if (interfaceType.IsIntrinsic)
            return $"type {interfaceType.Name}<{string.Join(", ", interfaceType.TypeParams.Select(parameter => parameter.Name))}>";

        var header = (interfaceType.IsContract ? "contract " : "interface ") + interfaceType.Name;
        if (interfaceType.BaseInterfaces.Count > 0) header += " extends " + string.Join(", ", interfaceType.BaseInterfaces.Select(iface => iface.Name));

        var members = new List<string>();
        members.AddRange(interfaceType.Fields.Select(entry => $"{entry.Key}: {Format(entry.Value.Type)}"));
        foreach (var (name, method) in interfaceType.Methods)
        {
            var isMethod = interfaceType.MethodNodes.TryGetValue(name, out var node) && node.IsMethod;
            members.Add(Signature(isMethod ? "method" : "function", name, method, node?.Parameters, skipSelf: false));
        }

        return Block(header, members);
    }

    private string EnumBody(EnumType enumType)
    {
        return Block("enum " + enumType.Name, enumType.Members.Select(member => $"{member.Name} = {EnumValue(enumType, member)}").ToList());
    }

    private static string Block(string header, List<string> members)
    {
        var block = new StringBuilder(header).Append('\n');
        foreach (var member in members.Take(MaxListedMembers))
        {
            block.Append("    ").Append(member).Append('\n');
        }

        if (members.Count > MaxListedMembers)
        {
            block.Append("    -- ").Append(members.Count - MaxListedMembers).Append(" more\n");
        }

        return block.Append("end").ToString();
    }

    private static string EnumValue(EnumType enumType, EnumType.Member member)
    {
        if (member.Value == null) return "nil";
        return enumType.BaseType.Kind == TypeKind.PrimitiveString ? $"\"{member.Value}\"" : member.Value.ToString()!;
    }

    /// <summary>
    /// A function as it is declared: <paramref name="keyword"/>, the name, every parameter with
    /// its type and default, and the return type. Parameter names and defaults come from the
    /// declaration when there is one; a method's implicit <c>self</c> is left out.
    /// </summary>
    private string Signature(string keyword, string name, FunctionType function, List<Parameter>? declared, bool skipSelf)
    {
        var parameters = declared != null ? DeclaredParameters(function, declared) : TypedParameters(function, skipSelf);
        var async = function.IsAsync ? "async " : "";
        var head = name.Length == 0 ? keyword : $"{keyword} {name}";
        return $"{async}{head}({string.Join(", ", parameters)}): {Format(function.ReturnType)}";
    }

    private IEnumerable<string> DeclaredParameters(FunctionType function, List<Parameter> declared)
    {
        var typeIndex = 0;
        foreach (var parameter in declared)
        {
            if (parameter.IsVararg)
            {
                var name = parameter.Name.Name == "..." ? "" : parameter.Name.Name;
                yield return $"...{name}: {Format(function.VarargType)}";
                continue;
            }

            var type = typeIndex < function.ParamTypes.Count ? Format(function.ParamTypes[typeIndex]) : "any";
            typeIndex++;
            yield return parameter.DefaultValue == null
                ? $"{parameter.Name.Name}: {type}"
                : $"{parameter.Name.Name}: {type} = {DefaultText(parameter.DefaultValue)}";
        }
    }

    private IEnumerable<string> TypedParameters(FunctionType function, bool skipSelf)
    {
        var first = skipSelf && function.ParamNames.Count > 0 && function.ParamNames[0] == "self" ? 1 : 0;
        for (var index = first; index < function.ParamTypes.Count; index++)
        {
            var name = index < function.ParamNames.Count ? function.ParamNames[index] : $"arg{index}";
            var optional = function.DefaultParams.Contains(index) ? " = ..." : "";
            yield return $"{name}: {Format(function.ParamTypes[index])}{optional}";
        }

        if (function.IsVararg)
        {
            yield return $"...: {Format(function.VarargType)}";
        }
    }

    private static string DefaultText(Expr value)
    {
        return value switch
        {
            NumberLiteralExpr number => number.Raw,
            StringLiteralExpr text => $"\"{text.Value}\"",
            BoolLiteralExpr flag => flag.Value ? "true" : "false",
            NilLiteralExpr => "nil",
            _ => "..."
        };
    }

    /// <summary>A declared module as it is written, with the names of what it declares.</summary>
    private static string ModuleSummary(DeclareModuleDecl module)
    {
        var members = module.Members.Select(member => member switch
        {
            DeclareFunctionDecl function => $"function {string.Join(".", function.NamePath.Select(name => name.Name))}",
            DeclareVariableDecl variable => variable.Name.Name,
            ClassDecl classDecl => $"class {classDecl.Name.Name}",
            InterfaceDecl interfaceDecl => $"{(interfaceDecl.IsContract ? "contract" : "interface")} {interfaceDecl.Name.Name}",
            EnumDecl enumDecl => $"enum {enumDecl.Name.Name}",
            _ => null,
        }).OfType<string>().Select(line => "    " + line);
        return string.Join("\n", [$"declare module \"{module.ModuleName.Name}\"", .. members, "end"]);
    }

    private static string FunctionKeyword(Node? declaration)
    {
        return declaration switch
        {
            LocalFunctionDecl => "local function",
            DeclareFunctionDecl => "declare function",
            _ => "function"
        };
    }

    private static List<Parameter>? ParametersOf(Node? declaration)
    {
        return declaration switch
        {
            FunctionDecl function => function.Parameters,
            LocalFunctionDecl function => function.Parameters,
            DeclareFunctionDecl function => function.Parameters,
            _ => null
        };
    }

    private static string VariableKeyword(Node? declaration)
    {
        return declaration switch
        {
            LocalDecl { IsMutable: true } => "local mut",
            DeclareVariableDecl => "declare",
            Parameter or ClassDecl => "(parameter)",
            _ => "local"
        };
    }

    private Node? DeclarationOf(Symbol symbol)
    {
        if (symbol.DeclaringNode == NodeID.Invalid) return null;
        if (result.NodeRegistry.TryGetValue(symbol.DeclaringNode, out var local)) return local;
        return result.Snapshot.TryGetDeclaration(symbol.DeclaringNode, out var node) ? node : null;
    }

    private Node? DeclarationOf(Type type)
    {
        var name = type.DeclaredAt;
        if (name == null || !result.Snapshot.TryGetSymbol(name.Sym, out var symbol)) return null;
        return DeclarationOf(symbol);
    }

    private Side SideOf(Type type)
    {
        var name = type.DeclaredAt;
        return name != null && result.Snapshot.TryGetSymbol(name.Sym, out var symbol) ? symbol.Side : Side.All;
    }

    /// <summary>The documentation written on the declaration of <paramref name="member"/> in <paramref name="owner"/>.</summary>
    private DocComment? MemberDoc(Type owner, string member)
    {
        return DeclarationOf(owner) switch
        {
            ClassDecl classDecl => classDecl.Methods.FirstOrDefault(method => method.Name.Name == member)?.Doc
                ?? classDecl.Fields.FirstOrDefault(field => field.Name.Name == member)?.Doc
                ?? classDecl.Accessors.FirstOrDefault(accessor => accessor.Name.Name == member)?.Doc,
            InterfaceDecl interfaceDecl => interfaceDecl.Methods.FirstOrDefault(method => method.Name.Name == member)?.Doc
                ?? interfaceDecl.Fields.FirstOrDefault(field => field.Name.Name == member)?.Doc,
            _ => null
        };
    }

    private string NameOf(Type type)
    {
        return type switch
        {
            ClassType classType => classType.Name,
            InterfaceType interfaceType => interfaceType.Name,
            EnumType enumType => enumType.Name,
            _ => Format(type)
        };
    }

    private string Format(Type? type)
    {
        return type == null ? "any" : NebraProject.FormatType(result.Types, type);
    }

    private static Type WithoutNil(Type type)
    {
        if (type is not UnionType union) return type;

        var present = union.Types.Where(member => member.Kind != TypeKind.PrimitiveNil).ToList();
        return present.Count == 1 ? WithoutNil(present[0]) : type;
    }
}
