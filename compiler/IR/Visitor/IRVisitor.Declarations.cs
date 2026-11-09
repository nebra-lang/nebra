using Antlr4.Runtime;
using Nebra.Diagnostics;

namespace Nebra.IR;

internal partial class IRVisitor
{
    /// <summary>
    /// Maps an accessor keyword to its kind. The grammar accepts any identifier in that position,
    /// so anything other than <c>get</c> or <c>set</c> is reported rather than silently treated as
    /// a setter, which is what a mistyped <c>get</c> used to become.
    /// </summary>
    private AccessorKind ResolveAccessorKind(string kindName, Antlr4.Runtime.Tree.ITerminalNode token)
    {
        if (kindName == "get") return AccessorKind.Getter;
        if (kindName == "set") return AccessorKind.Setter;

        diag.Report(SpanFromTerm(token), Diagnostics.DiagnosticCode.ErrInvalidAccessor, kindName);
        return AccessorKind.Getter;
    }

    /// <summary>
    /// The name of a class method, which may be one of Nebra's keywords like a member name of an
    /// interface. <c>new</c> is reported: it is the name of the class's constructor in Lua.
    /// </summary>
    private NameRef MethodName(NebraParser.MemberNameContext? member)
    {
        var name = NameRefFromMember(member);
        if (name.Name == "new")
            diag.Report(name.Span, Diagnostics.DiagnosticCode.ErrUnexpectedToken, "a method cannot be named `new`: that is the name of the class's constructor");
        return name;
    }

    /// <summary>
    /// Tells whether a class member is declared with <c>operator</c>. <c>operator</c> is a
    /// contextual keyword, so the grammar accepts any identifier in its place and anything else
    /// is reported here.
    /// </summary>
    /// <summary>
    /// Reports a type parameter list on <paramref name="what"/>, a member that is never called
    /// with type arguments, so its type parameters could not be filled in.
    /// </summary>
    private void RejectTypeParams(NebraParser.TypeParamListContext? typeParams, string what)
    {
        if (typeParams == null) return;
        diag.Report(SpanFromCtx(typeParams), Diagnostics.DiagnosticCode.ErrTypeParamsNotAllowed, what);
    }

    /// <summary>
    /// Whether a class carries the <c>sealed</c> modifier. Any other word before <c>class</c> is
    /// reported, since <c>sealed</c> is the only modifier there.
    /// </summary>
    private static bool IsContractKeyword(NebraParser.InterfaceKeywordContext keyword)
    {
        return keyword.NAME() != null;
    }

    private bool IsSealedModifier(NebraParser.ClassModifierContext? modifier)
    {
        if (modifier == null) return false;
        if (modifier.NAME().GetText() == "sealed") return true;

        diag.Report(SpanFromTerm(modifier.NAME()), Diagnostics.DiagnosticCode.ErrUnexpectedToken,
            $"unexpected `{modifier.NAME().GetText()}`; the only modifier before `class` is `sealed`");
        return false;
    }

    private bool IsOperatorKeyword(NebraParser.OperatorKeywordContext keyword)
    {
        if (keyword.NAME().GetText() == "operator") return true;

        diag.Report(SpanFromTerm(keyword.NAME()), Diagnostics.DiagnosticCode.ErrUnexpectedToken,
            $"unexpected `{keyword.NAME().GetText()}`; an operator is declared with `operator`");
        return false;
    }

    /// <summary>
    /// Tells whether an interface member is declared with <c>method</c> rather than
    /// <c>function</c>. <c>method</c> is a contextual keyword, so the grammar accepts any
    /// identifier in its place and anything else is reported here.
    /// </summary>
    private bool IsMethodKind(NebraParser.InterfaceMemberKindContext kind)
    {
        if (kind.FUNCTION() != null) return false;
        if (kind.NAME().GetText() == "method") return true;

        diag.Report(SpanFromTerm(kind.NAME()), Diagnostics.DiagnosticCode.ErrInvalidInterfaceMemberKind, kind.NAME().GetText());
        return false;
    }

    public override Node VisitFunctionDecl(NebraParser.FunctionDeclContext context)
    {
        var (namePath, methodName) = VisitFuncNameContent(context.funcName());
        var (parameters, returnType, body, ret) = VisitFuncBodyContent(context.funcBody());
        var isAsync = context.ASYNC() != null;
        var typeParams = VisitTypeParamListContent(context.funcBody().typeParamList());
        var decl = new FunctionDecl(NewNodeID, SpanFromCtx(context), namePath, methodName, parameters, returnType, body, ret, isAsync);
        decl.TypeParams = typeParams;
        decl.Annotations = VisitAnnotationListContent(context.annotationList());
        return decl;
    }

    public override Node VisitLocalFunctionDecl(NebraParser.LocalFunctionDeclContext context)
    {
        var (parameters, returnType, body, ret) = VisitFuncBodyContent(context.funcBody());
        var isAsync = context.ASYNC() != null;
        var typeParams = VisitTypeParamListContent(context.funcBody().typeParamList());
        var decl = new LocalFunctionDecl(NewNodeID, SpanFromCtx(context), NameRefFromTerm(context.NAME()), parameters, returnType, body, ret, isAsync);
        decl.TypeParams = typeParams;
        decl.Annotations = VisitAnnotationListContent(context.annotationList());
        return decl;
    }

    public override Node VisitLocalDecl(NebraParser.LocalDeclContext context)
    {
        var vars = VisitAttribNameListContent(context.attribNameList());
        var values = context.exprList()?.expr().Select(e => (Expr)Visit(e)).ToList() ?? [];
        var isMutable = context.MUT() != null;
        var decl = new LocalDecl(NewNodeID, SpanFromCtx(context), vars, values, isMutable);
        decl.Annotations = VisitAnnotationListContent(context.annotationList());
        return decl;
    }

    public override Node VisitDeclareStat(NebraParser.DeclareStatContext context)
    {
        var annotations = VisitAnnotationListContent(context.annotationList());
        var body = (Decl)Visit(context.declareBody());
        if (annotations.Count > 0) AttachAnnotations(body, annotations);
        return body;
    }

    /// <summary>
    /// Routes annotations parsed on a <c>declare ...</c> statement onto the
    /// concrete decl node. <c>declareBody</c> alternatives produce different
    /// concrete types (function / variable / module / class / interface /
    /// enum) and we want the annotations to live on whichever one came back.
    /// </summary>
    private static void AttachAnnotations(Decl decl, List<Annotation> annotations)
    {
        switch (decl)
        {
            case DeclareFunctionDecl df: df.Annotations = annotations; break;
            case DeclareVariableDecl dv: dv.Annotations = annotations; break;
            case DeclareModuleDecl dm: dm.Annotations = annotations; break;
            case ClassDecl cd: cd.Annotations = annotations; break;
            case InterfaceDecl id: id.Annotations = annotations; break;
            case EnumDecl ed: ed.Annotations = annotations; break;
        }
    }

    public override Node VisitDeclareFunction(NebraParser.DeclareFunctionContext context)
    {
        var (namePath, methodName) = VisitFuncNameContent(context.funcName());
        var (parameters, returnType) = VisitFuncSignatureContent(context.funcSignature());
        var isAsync = context.ASYNC() != null;
        var typeParams = VisitTypeParamListContent(context.funcSignature().typeParamList());
        var decl = new DeclareFunctionDecl(NewNodeID, SpanFromCtx(context), namePath, methodName, parameters, returnType, isAsync);
        decl.TypeParams = typeParams;
        return decl;
    }

    public override Node VisitDeclareVariable(NebraParser.DeclareVariableContext context)
    {
        var typeRef = (TypeRef)Visit(context.typeAnnotation().typeExpr());
        return new DeclareVariableDecl(NewNodeID, SpanFromCtx(context), NameRefFromTerm(context.NAME()), typeRef);
    }

    public override Node VisitDeclareModule(NebraParser.DeclareModuleContext context)
    {
        var moduleName = NameRefFromString(context.str());
        var members = new List<Decl>();

        foreach (var member in context.declareModuleBlock().declareModuleMember())
            members.Add((Decl)Visit(member));

        return new DeclareModuleDecl(NewNodeID, SpanFromCtx(context), moduleName, members);
    }

    public override Node VisitModuleDeclareFunction(NebraParser.ModuleDeclareFunctionContext context)
    {
        var (namePath, methodName) = VisitFuncNameContent(context.funcName());
        var (parameters, returnType) = VisitFuncSignatureContent(context.funcSignature());
        var isAsync = context.ASYNC() != null;
        var typeParams = VisitTypeParamListContent(context.funcSignature().typeParamList());
        var decl = new DeclareFunctionDecl(NewNodeID, SpanFromCtx(context), namePath, methodName, parameters, returnType, isAsync);
        decl.TypeParams = typeParams;
        decl.Annotations = VisitAnnotationListContent(context.annotationList());
        return decl;
    }

    public override Node VisitModuleDeclareVariable(NebraParser.ModuleDeclareVariableContext context)
    {
        var typeRef = (TypeRef)Visit(context.typeAnnotation().typeExpr());
        var decl = new DeclareVariableDecl(NewNodeID, SpanFromCtx(context), NameRefFromTerm(context.NAME()), typeRef);
        decl.Annotations = VisitAnnotationListContent(context.annotationList());
        return decl;
    }

    public override Node VisitEnumDecl(NebraParser.EnumDeclContext context)
    {
        var name = NameRefFromTerm(context.NAME());
        var members = new List<EnumMember>();
        foreach (var memberCtx in context.enumMember())
        {
            var memberName = NameRefFromTerm(memberCtx.NAME());
            Expr? value = memberCtx.expr() != null ? (Expr)Visit(memberCtx.expr()) : null;
            var member = new EnumMember(memberName, value, null, SpanFromCtx(memberCtx));
            member.Annotations = VisitAnnotationListContent(memberCtx.annotationList());
            members.Add(member);
        }
        var enumDecl = new EnumDecl(NewNodeID, SpanFromCtx(context), name, members, isDeclare: false);
        enumDecl.Annotations = VisitAnnotationListContent(context.annotationList());
        return enumDecl;
    }

    public override Node VisitDeclareEnum(NebraParser.DeclareEnumContext context)
    {
        var name = NameRefFromTerm(context.NAME());
        var members = new List<EnumMember>();
        foreach (var memberCtx in context.declareEnumMember())
        {
            var memberName = NameRefFromTerm(memberCtx.NAME());
            TypeRef? typeAnn = memberCtx.typeAnnotation() != null
                ? (TypeRef)Visit(memberCtx.typeAnnotation().typeExpr())
                : null;
            members.Add(new EnumMember(memberName, null, typeAnn, SpanFromCtx(memberCtx)));
        }
        return new EnumDecl(NewNodeID, SpanFromCtx(context), name, members, isDeclare: true);
    }

    public override Node VisitModuleDeclareEnum(NebraParser.ModuleDeclareEnumContext context)
    {
        var name = NameRefFromTerm(context.NAME());
        var members = new List<EnumMember>();
        foreach (var memberCtx in context.declareEnumMember())
        {
            var memberName = NameRefFromTerm(memberCtx.NAME());
            TypeRef? typeAnn = memberCtx.typeAnnotation() != null
                ? (TypeRef)Visit(memberCtx.typeAnnotation().typeExpr())
                : null;
            members.Add(new EnumMember(memberName, null, typeAnn, SpanFromCtx(memberCtx)));
        }
        var ed = new EnumDecl(NewNodeID, SpanFromCtx(context), name, members, isDeclare: true);
        ed.Annotations = VisitAnnotationListContent(context.annotationList());
        return ed;
    }

    public override Node VisitDeclareClass(NebraParser.DeclareClassContext context)
    {
        var name = NameRefFromTerm(context.NAME());
        var classRefs = context.classRef();

        NameRef? baseClass = null;
        var baseClassTypeArgs = new List<TypeArgRef>();
        var interfaces = new List<NameRef>();
        var interfaceTypeArgs = new List<List<TypeArgRef>>();

        var refIdx = 0;
        if (context.EXTENDS() != null && classRefs.Length > refIdx)
        {
            var (bcName, bcArgs) = VisitClassRefContent(classRefs[refIdx]);
            baseClass = bcName;
            baseClassTypeArgs = bcArgs;
            refIdx++;
        }

        if (context.IMPLEMENTS() != null)
        {
            for (var i = refIdx; i < classRefs.Length; i++)
            {
                var (iName, iArgs) = VisitClassRefContent(classRefs[i]);
                interfaces.Add(iName);
                interfaceTypeArgs.Add(iArgs);
            }
        }

        var typeParams = VisitTypeParamListContent(context.typeParamList());

        var fields = new List<ClassFieldNode>();
        var methods = new List<ClassMethodNode>();
        var constructors = new List<ClassConstructorNode>();
        var accessors = new List<ClassAccessorNode>();

        foreach (var member in context.declareClassMember())
        {
            switch (member)
            {
                case NebraParser.DeclareClassFieldMemberContext field:
                {
                    var isLocal = field.LOCAL() != null;
                    var isStatic = field.STATIC() != null;
                    var isProtected = field.PROTECTED() != null;
                    var fieldName = NameRefFromTerm(field.NAME());
                    TypeRef? typeAnn = field.typeAnnotation() != null
                        ? (TypeRef)Visit(field.typeAnnotation().typeExpr())
                        : null;
                    var fnode = new ClassFieldNode(fieldName, typeAnn, null, isLocal, isStatic, isProtected, SpanFromCtx(field));
                    fnode.Annotations = VisitAnnotationListContent(field.annotationList());
                    fields.Add(fnode);
                    break;
                }
                case NebraParser.DeclareClassMethodMemberContext method:
                {
                    var isLocal = method.LOCAL() != null;
                    var isStatic = method.STATIC() != null;
                    var isAsync = method.ASYNC() != null;
                    var isProtected = method.PROTECTED() != null;
                    var isOverride = method.OVERRIDE() != null;
                    var isAbstract = method.ABSTRACT() != null;
                    var methodName = MethodName(method.memberName());
                    var (parameters, returnType) = VisitFuncSignatureContent(method.funcSignature());
                    var methodTypeParams = VisitTypeParamListContent(method.funcSignature().typeParamList());
                    var cmNode = new ClassMethodNode(methodName, parameters, returnType, [], null, isLocal, isStatic, isAsync, isProtected, isOverride, isAbstract, SpanFromCtx(method));
                    cmNode.TypeParams = methodTypeParams;
                    cmNode.Annotations = VisitAnnotationListContent(method.annotationList());
                    methods.Add(cmNode);
                    break;
                }
                case NebraParser.DeclareClassConstructorMemberContext ctor:
                {
                    var (parameters, _) = VisitFuncSignatureContent(ctor.funcSignature());
                    RejectTypeParams(ctor.funcSignature().typeParamList(), "a constructor");
                    var constructor = new ClassConstructorNode(parameters, [], null, SpanFromCtx(ctor)) { IsLocal = ctor.LOCAL() != null };
                    constructor.Annotations = VisitAnnotationListContent(ctor.annotationList());
                    constructors.Add(constructor);
                    break;
                }
                case NebraParser.DeclareClassOperatorMemberContext opMember:
                {
                    var (parameters, returnType) = VisitFuncSignatureContent(opMember.funcSignature());
                    if (!IsOperatorKeyword(opMember.operatorKeyword())) break;
                    RejectTypeParams(opMember.funcSignature().typeParamList(), "an operator");
                    var symText = opMember.operatorSymbol().GetText();
                    var metaName = OperatorSymbolToMetamethod(symText, parameters.Count, out var diagMsg);
                    if (metaName == null)
                    {
                        diag.Report(SpanFromCtx(opMember.operatorSymbol()), Diagnostics.DiagnosticCode.ErrInvalidOperator, diagMsg ?? symText);
                        break;
                    }
                    var opNameRef = NameRefFromText(metaName, SpanFromCtx(opMember.operatorSymbol()));
                    var opMethodNode = new ClassMethodNode(
                        opNameRef, parameters, returnType, [], null,
                        isLocal: false, isStatic: false, isAsync: false,
                        isProtected: false, isOverride: false, isAbstract: false,
                        SpanFromCtx(opMember), isOperator: true, operatorSymbol: symText);
                    opMethodNode.Annotations = VisitAnnotationListContent(opMember.annotationList());
                    methods.Add(opMethodNode);
                    break;
                }
                case NebraParser.DeclareClassAccessorMemberContext accessor:
                {
                    var kindName = accessor.NAME(0).GetText();
                    var propName = NameRefFromTerm(accessor.NAME(1));
                    var kind = ResolveAccessorKind(kindName, accessor.NAME(0));
                    var (parameters, returnType) = VisitFuncSignatureContent(accessor.funcSignature());
                    RejectTypeParams(accessor.funcSignature().typeParamList(), "a getter or setter");
                    var anode = new ClassAccessorNode(kind, propName, parameters, returnType, [], null, false, SpanFromCtx(accessor));
                    anode.Annotations = VisitAnnotationListContent(accessor.annotationList());
                    accessors.Add(anode);
                    break;
                }
            }
        }

        var isClassAbstract = context.ABSTRACT() != null;
        var decl = new ClassDecl(NewNodeID, SpanFromCtx(context), name, baseClass, interfaces, fields, methods, constructors, accessors, isDeclare: true, isAbstract: isClassAbstract);
        decl.TypeParams = typeParams;
        decl.IsSealed = IsSealedModifier(context.classModifier());
        decl.BaseClassTypeArgs = baseClassTypeArgs;
        decl.InterfaceTypeArgs = interfaceTypeArgs;
        return decl;
    }

    public override Node VisitDeclareInterface(NebraParser.DeclareInterfaceContext context)
    {
        var name = NameRefFromTerm(context.NAME());
        var classRefs = context.classRef();

        var baseInterfaces = new List<NameRef>();
        var baseInterfaceTypeArgs = new List<List<TypeArgRef>>();
        if (context.EXTENDS() != null)
        {
            foreach (var cr in classRefs)
            {
                var (iName, iArgs) = VisitClassRefContent(cr);
                baseInterfaces.Add(iName);
                baseInterfaceTypeArgs.Add(iArgs);
            }
        }

        var typeParams = VisitTypeParamListContent(context.typeParamList());

        var (fields, methods) = VisitDeclareInterfaceMembers(context.declareInterfaceMember());

        var ifaceDecl = new InterfaceDecl(NewNodeID, SpanFromCtx(context), name, baseInterfaces, fields, methods, isDeclare: true) { IsContract = IsContractKeyword(context.interfaceKeyword()) };
        ifaceDecl.TypeParams = typeParams;
        ifaceDecl.BaseInterfaceTypeArgs = baseInterfaceTypeArgs;
        return ifaceDecl;
    }

    public override Node VisitDeclareIntrinsicType(NebraParser.DeclareIntrinsicTypeContext context)
    {
        var keyword = context.NAME(0);
        if (keyword.GetText() != "type")
            diag.Report(SpanFromTerm(keyword), DiagnosticCode.ErrUnexpectedToken, $"unexpected `{keyword.GetText()}`; a built-in type is declared with `declare type`");

        var declaration = new InterfaceDecl(NewNodeID, SpanFromCtx(context), NameRefFromTerm(context.NAME(1)), [], [], [], isDeclare: true);
        declaration.TypeParams = VisitTypeParamListContent(context.typeParamList());
        declaration.IsIntrinsic = true;
        return declaration;
    }

    public override Node VisitModuleDeclareClass(NebraParser.ModuleDeclareClassContext context)
    {
        var name = NameRefFromTerm(context.NAME());
        var classRefs = context.classRef();

        NameRef? baseClass = null;
        var baseClassTypeArgs = new List<TypeArgRef>();
        var interfaces = new List<NameRef>();
        var interfaceTypeArgs = new List<List<TypeArgRef>>();

        var refIdx = 0;
        if (context.EXTENDS() != null && classRefs.Length > refIdx)
        {
            var (bcName, bcArgs) = VisitClassRefContent(classRefs[refIdx]);
            baseClass = bcName;
            baseClassTypeArgs = bcArgs;
            refIdx++;
        }

        if (context.IMPLEMENTS() != null)
        {
            for (var i = refIdx; i < classRefs.Length; i++)
            {
                var (iName, iArgs) = VisitClassRefContent(classRefs[i]);
                interfaces.Add(iName);
                interfaceTypeArgs.Add(iArgs);
            }
        }

        var typeParams = VisitTypeParamListContent(context.typeParamList());

        var fields = new List<ClassFieldNode>();
        var methods = new List<ClassMethodNode>();
        var constructors = new List<ClassConstructorNode>();
        var accessors = new List<ClassAccessorNode>();

        foreach (var member in context.declareClassMember())
        {
            switch (member)
            {
                case NebraParser.DeclareClassFieldMemberContext field:
                {
                    var isLocal = field.LOCAL() != null;
                    var isStatic = field.STATIC() != null;
                    var isProtected = field.PROTECTED() != null;
                    var fieldName = NameRefFromTerm(field.NAME());
                    TypeRef? typeAnn = field.typeAnnotation() != null
                        ? (TypeRef)Visit(field.typeAnnotation().typeExpr())
                        : null;
                    var fnode = new ClassFieldNode(fieldName, typeAnn, null, isLocal, isStatic, isProtected, SpanFromCtx(field));
                    fnode.Annotations = VisitAnnotationListContent(field.annotationList());
                    fields.Add(fnode);
                    break;
                }
                case NebraParser.DeclareClassMethodMemberContext method:
                {
                    var isLocal = method.LOCAL() != null;
                    var isStatic = method.STATIC() != null;
                    var isAsync = method.ASYNC() != null;
                    var isProtected = method.PROTECTED() != null;
                    var isOverride = method.OVERRIDE() != null;
                    var isAbstract = method.ABSTRACT() != null;
                    var methodName = MethodName(method.memberName());
                    var (parameters, returnType) = VisitFuncSignatureContent(method.funcSignature());
                    var methodTypeParams = VisitTypeParamListContent(method.funcSignature().typeParamList());
                    var cmNode = new ClassMethodNode(methodName, parameters, returnType, [], null, isLocal, isStatic, isAsync, isProtected, isOverride, isAbstract, SpanFromCtx(method));
                    cmNode.TypeParams = methodTypeParams;
                    cmNode.Annotations = VisitAnnotationListContent(method.annotationList());
                    methods.Add(cmNode);
                    break;
                }
                case NebraParser.DeclareClassConstructorMemberContext ctor:
                {
                    var (parameters, _) = VisitFuncSignatureContent(ctor.funcSignature());
                    RejectTypeParams(ctor.funcSignature().typeParamList(), "a constructor");
                    var constructor = new ClassConstructorNode(parameters, [], null, SpanFromCtx(ctor)) { IsLocal = ctor.LOCAL() != null };
                    constructor.Annotations = VisitAnnotationListContent(ctor.annotationList());
                    constructors.Add(constructor);
                    break;
                }
                case NebraParser.DeclareClassOperatorMemberContext opMember:
                {
                    var (parameters, returnType) = VisitFuncSignatureContent(opMember.funcSignature());
                    if (!IsOperatorKeyword(opMember.operatorKeyword())) break;
                    RejectTypeParams(opMember.funcSignature().typeParamList(), "an operator");
                    var symText = opMember.operatorSymbol().GetText();
                    var metaName = OperatorSymbolToMetamethod(symText, parameters.Count, out var diagMsg);
                    if (metaName == null)
                    {
                        diag.Report(SpanFromCtx(opMember.operatorSymbol()), Diagnostics.DiagnosticCode.ErrInvalidOperator, diagMsg ?? symText);
                        break;
                    }
                    var opNameRef = NameRefFromText(metaName, SpanFromCtx(opMember.operatorSymbol()));
                    var opMethodNode = new ClassMethodNode(
                        opNameRef, parameters, returnType, [], null,
                        isLocal: false, isStatic: false, isAsync: false,
                        isProtected: false, isOverride: false, isAbstract: false,
                        SpanFromCtx(opMember), isOperator: true, operatorSymbol: symText);
                    opMethodNode.Annotations = VisitAnnotationListContent(opMember.annotationList());
                    methods.Add(opMethodNode);
                    break;
                }
                case NebraParser.DeclareClassAccessorMemberContext accessor:
                {
                    var kindName = accessor.NAME(0).GetText();
                    var propName = NameRefFromTerm(accessor.NAME(1));
                    var kind = ResolveAccessorKind(kindName, accessor.NAME(0));
                    var (parameters, returnType) = VisitFuncSignatureContent(accessor.funcSignature());
                    RejectTypeParams(accessor.funcSignature().typeParamList(), "a getter or setter");
                    var anode = new ClassAccessorNode(kind, propName, parameters, returnType, [], null, false, SpanFromCtx(accessor));
                    anode.Annotations = VisitAnnotationListContent(accessor.annotationList());
                    accessors.Add(anode);
                    break;
                }
            }
        }

        var isClassAbstract = context.ABSTRACT() != null;
        var declMod = new ClassDecl(NewNodeID, SpanFromCtx(context), name, baseClass, interfaces, fields, methods, constructors, accessors, isDeclare: true, isAbstract: isClassAbstract);
        declMod.TypeParams = typeParams;
        declMod.IsSealed = IsSealedModifier(context.classModifier());
        declMod.BaseClassTypeArgs = baseClassTypeArgs;
        declMod.InterfaceTypeArgs = interfaceTypeArgs;
        declMod.Annotations = VisitAnnotationListContent(context.annotationList());
        return declMod;
    }

    public override Node VisitModuleDeclareInterface(NebraParser.ModuleDeclareInterfaceContext context)
    {
        var name = NameRefFromTerm(context.NAME());
        var classRefs = context.classRef();

        var baseInterfaces = new List<NameRef>();
        var baseInterfaceTypeArgs = new List<List<TypeArgRef>>();
        if (context.EXTENDS() != null)
        {
            foreach (var cr in classRefs)
            {
                var (iName, iArgs) = VisitClassRefContent(cr);
                baseInterfaces.Add(iName);
                baseInterfaceTypeArgs.Add(iArgs);
            }
        }

        var typeParams = VisitTypeParamListContent(context.typeParamList());

        var (fields, methods) = VisitDeclareInterfaceMembers(context.declareInterfaceMember());

        var ifaceModDecl = new InterfaceDecl(NewNodeID, SpanFromCtx(context), name, baseInterfaces, fields, methods, isDeclare: true) { IsContract = IsContractKeyword(context.interfaceKeyword()) };
        ifaceModDecl.TypeParams = typeParams;
        ifaceModDecl.BaseInterfaceTypeArgs = baseInterfaceTypeArgs;
        ifaceModDecl.Annotations = VisitAnnotationListContent(context.annotationList());
        return ifaceModDecl;
    }

    /// <summary>
    /// Lowers the members of an interface in a declaration file, which are fields and method
    /// signatures only.
    /// </summary>
    private (List<InterfaceFieldNode> Fields, List<InterfaceMethodNode> Methods) VisitDeclareInterfaceMembers(
        NebraParser.DeclareInterfaceMemberContext[] members)
    {
        var fields = new List<InterfaceFieldNode>();
        var methods = new List<InterfaceMethodNode>();

        foreach (var member in members)
        {
            switch (member)
            {
                case NebraParser.DeclareInterfaceFieldMemberContext field:
                {
                    var fieldName = NameRefFromField(field.fieldName());
                    var typeAnn = (TypeRef)Visit(field.typeAnnotation().typeExpr());
                    var fnode = new InterfaceFieldNode(fieldName, typeAnn, SpanFromCtx(field));
                    fnode.Annotations = VisitAnnotationListContent(field.annotationList());
                    fields.Add(fnode);
                    break;
                }
                case NebraParser.DeclareInterfaceMethodMemberContext method:
                {
                    var methodName = NameRefFromMember(method.memberName());
                    var (parameters, returnType) = VisitFuncSignatureContent(method.funcSignature());
                    var imNode = new InterfaceMethodNode(methodName, parameters, returnType, method.ASYNC() != null, SpanFromCtx(method))
                    {
                        IsMethod = IsMethodKind(method.interfaceMemberKind())
                    };
                    imNode.TypeParams = VisitTypeParamListContent(method.funcSignature().typeParamList());
                    imNode.Annotations = VisitAnnotationListContent(method.annotationList());
                    methods.Add(imNode);
                    break;
                }
            }
        }

        return (fields, methods);
    }

    public override Node VisitClassDecl(NebraParser.ClassDeclContext context)
    {
        var name = NameRefFromTerm(context.NAME());
        var classRefs = context.classRef();

        NameRef? baseClass = null;
        var baseClassTypeArgs = new List<TypeArgRef>();
        var interfaces = new List<NameRef>();
        var interfaceTypeArgs = new List<List<TypeArgRef>>();

        var refIdx = 0;
        if (context.EXTENDS() != null && classRefs.Length > refIdx)
        {
            var (bcName, bcArgs) = VisitClassRefContent(classRefs[refIdx]);
            baseClass = bcName;
            baseClassTypeArgs = bcArgs;
            refIdx++;
        }

        if (context.IMPLEMENTS() != null)
        {
            for (var i = refIdx; i < classRefs.Length; i++)
            {
                var (iName, iArgs) = VisitClassRefContent(classRefs[i]);
                interfaces.Add(iName);
                interfaceTypeArgs.Add(iArgs);
            }
        }

        var typeParams = VisitTypeParamListContent(context.typeParamList());

        var fields = new List<ClassFieldNode>();
        var methods = new List<ClassMethodNode>();
        var constructors = new List<ClassConstructorNode>();
        var accessors = new List<ClassAccessorNode>();

        foreach (var member in context.classMember())
        {
            switch (member)
            {
                case NebraParser.ClassFieldMemberContext field:
                {
                    var isLocal = field.LOCAL() != null;
                    var isStatic = field.STATIC() != null;
                    var isProtected = field.PROTECTED() != null;
                    var fieldName = NameRefFromTerm(field.NAME());
                    TypeRef? typeAnn = field.typeAnnotation() != null
                        ? (TypeRef)Visit(field.typeAnnotation().typeExpr())
                        : null;
                    Expr? defaultValue = field.expr() != null ? (Expr)Visit(field.expr()) : null;
                    var fieldNode = new ClassFieldNode(fieldName, typeAnn, defaultValue, isLocal, isStatic, isProtected, SpanFromCtx(field));
                    fieldNode.Annotations = VisitAnnotationListContent(field.annotationList());
                    fields.Add(fieldNode);
                    break;
                }
                case NebraParser.ClassMethodMemberContext method:
                {
                    var isLocal = method.LOCAL() != null;
                    var isStatic = method.STATIC() != null;
                    var isAsync = method.ASYNC() != null;
                    var isProtected = method.PROTECTED() != null;
                    var isOverride = method.OVERRIDE() != null;
                    var methodName = MethodName(method.memberName());
                    var (parameters, returnType, body, ret) = VisitFuncBodyContent(method.funcBody());
                    var regMethodTypeParams = VisitTypeParamListContent(method.funcBody().typeParamList());
                    var regMethodNode = new ClassMethodNode(methodName, parameters, returnType, body, ret, isLocal, isStatic, isAsync, isProtected, isOverride, false, SpanFromCtx(method));
                    regMethodNode.TypeParams = regMethodTypeParams;
                    regMethodNode.Annotations = VisitAnnotationListContent(method.annotationList());
                    methods.Add(regMethodNode);
                    break;
                }
                case NebraParser.ClassAbstractMethodMemberContext absMethod:
                {
                    var isProtected = absMethod.PROTECTED() != null;
                    var isAsync = absMethod.ASYNC() != null;
                    var methodName = MethodName(absMethod.memberName());
                    var (parameters, returnType) = VisitFuncSignatureContent(absMethod.funcSignature());
                    var absMethodTypeParams = VisitTypeParamListContent(absMethod.funcSignature().typeParamList());
                    var absMethodNode = new ClassMethodNode(methodName, parameters, returnType, [], null, false, false, isAsync, isProtected, false, true, SpanFromCtx(absMethod));
                    absMethodNode.TypeParams = absMethodTypeParams;
                    absMethodNode.Annotations = VisitAnnotationListContent(absMethod.annotationList());
                    methods.Add(absMethodNode);
                    break;
                }
                case NebraParser.ClassConstructorMemberContext ctor:
                {
                    var (parameters, _, body, ret) = VisitFuncBodyContent(ctor.funcBody());
                    RejectTypeParams(ctor.funcBody().typeParamList(), "a constructor");
                    var constructor = new ClassConstructorNode(parameters, body, ret, SpanFromCtx(ctor)) { IsLocal = ctor.LOCAL() != null };
                    constructor.Annotations = VisitAnnotationListContent(ctor.annotationList());
                    constructors.Add(constructor);
                    break;
                }
                case NebraParser.ClassAccessorMemberContext accessor:
                {
                    var isOverride = accessor.OVERRIDE() != null;
                    var kindName = accessor.NAME(0).GetText();
                    var propName = NameRefFromTerm(accessor.NAME(1));
                    var kind = ResolveAccessorKind(kindName, accessor.NAME(0));
                    var (parameters, returnType, body, ret) = VisitFuncBodyContent(accessor.funcBody());
                    RejectTypeParams(accessor.funcBody().typeParamList(), "a getter or setter");
                    var accNode = new ClassAccessorNode(kind, propName, parameters, returnType, body, ret, isOverride, SpanFromCtx(accessor));
                    accNode.Annotations = VisitAnnotationListContent(accessor.annotationList());
                    accessors.Add(accNode);
                    break;
                }
                case NebraParser.ClassOperatorMemberContext opMember:
                {
                    var (parameters, returnType, body, ret) = VisitFuncBodyContent(opMember.funcBody());
                    if (!IsOperatorKeyword(opMember.operatorKeyword())) break;
                    RejectTypeParams(opMember.funcBody().typeParamList(), "an operator");
                    var symText = opMember.operatorSymbol().GetText();
                    var metaName = OperatorSymbolToMetamethod(symText, parameters.Count, out var diagMsg);
                    if (metaName == null)
                    {
                        diag.Report(SpanFromCtx(opMember.operatorSymbol()), Diagnostics.DiagnosticCode.ErrInvalidOperator, diagMsg ?? symText);
                        break;
                    }
                    var opNameRef = NameRefFromText(metaName, SpanFromCtx(opMember.operatorSymbol()));
                    var opMethodNode = new ClassMethodNode(
                        opNameRef, parameters, returnType, body, ret,
                        isLocal: false, isStatic: false, isAsync: false,
                        isProtected: false, isOverride: false, isAbstract: false,
                        SpanFromCtx(opMember), isOperator: true, operatorSymbol: symText);
                    opMethodNode.Annotations = VisitAnnotationListContent(opMember.annotationList());
                    methods.Add(opMethodNode);
                    break;
                }
            }
        }

        var isClassAbstract = context.ABSTRACT() != null;
        var regularDecl = new ClassDecl(NewNodeID, SpanFromCtx(context), name, baseClass, interfaces, fields, methods, constructors, accessors, isAbstract: isClassAbstract);
        regularDecl.TypeParams = typeParams;
        regularDecl.IsSealed = IsSealedModifier(context.classModifier());
        regularDecl.BaseClassTypeArgs = baseClassTypeArgs;
        regularDecl.InterfaceTypeArgs = interfaceTypeArgs;
        regularDecl.Annotations = VisitAnnotationListContent(context.annotationList());
        return regularDecl;
    }

    public override Node VisitExtendDecl(NebraParser.ExtendDeclContext context)
    {
        // typeExpr is null when the extend head failed to parse (e.g. `extend function`);
        // keep a null target and let later passes report the error without crashing.
        var target = context.typeExpr() != null ? (TypeRef)Visit(context.typeExpr()) : null!;
        var members = new List<ExtensionMethodNode>();
        foreach (var member in context.extendMethod())
        {
            var node = member switch
            {
                NebraParser.ExtendFunctionMemberContext function => ExtensionFunction(function.annotationList(),
                    function.ASYNC() != null, function.NAME(), VisitFuncBodyContent(function.funcBody()), function.funcBody().typeParamList(), function),
                NebraParser.ExtendGetterMemberContext getter => ExtensionGetter(getter.annotationList(),
                    getter.NAME(0), getter.NAME(1), VisitFuncBodyContent(getter.funcBody()), getter.funcBody().typeParamList(), getter),
                NebraParser.ExtendOperatorMemberContext op => ExtensionOperator(op.annotationList(),
                    op.operatorKeyword(), op.operatorSymbol(), VisitFuncBodyContent(op.funcBody()), op.funcBody().typeParamList(), op),
                _ => null,
            };
            if (node != null) members.Add(node);
        }

        return new ExtendDecl(NewNodeID, SpanFromCtx(context), target, members)
        {
            Annotations = VisitAnnotationListContent(context.annotationList())
        };
    }

    public override Node VisitModuleDeclareExtend(NebraParser.ModuleDeclareExtendContext context)
    {
        var target = context.typeExpr() != null ? (TypeRef)Visit(context.typeExpr()) : null!;
        var members = new List<ExtensionMethodNode>();
        foreach (var member in context.declareExtendMethod())
        {
            var node = member switch
            {
                NebraParser.DeclareExtendFunctionMemberContext function => ExtensionFunction(function.annotationList(),
                    function.ASYNC() != null, function.NAME(), Signature(function.funcSignature()), function.funcSignature().typeParamList(), function),
                NebraParser.DeclareExtendGetterMemberContext getter => ExtensionGetter(getter.annotationList(),
                    getter.NAME(0), getter.NAME(1), Signature(getter.funcSignature()), getter.funcSignature().typeParamList(), getter),
                NebraParser.DeclareExtendOperatorMemberContext op => ExtensionOperator(op.annotationList(),
                    op.operatorKeyword(), op.operatorSymbol(), Signature(op.funcSignature()), op.funcSignature().typeParamList(), op),
                _ => null,
            };
            if (node != null) members.Add(node);
        }

        return new ExtendDecl(NewNodeID, SpanFromCtx(context), target, members, true)
        {
            Annotations = VisitAnnotationListContent(context.annotationList())
        };
    }

    private (List<Parameter> Parameters, TypeRef? ReturnType, List<Stmt> Body, ReturnStmt? Return) Signature(NebraParser.FuncSignatureContext signature)
    {
        var (parameters, returnType) = VisitFuncSignatureContent(signature);
        return (parameters, returnType, [], null);
    }

    private ExtensionMethodNode ExtensionFunction(NebraParser.AnnotationListContext annotations, bool isAsync,
        Antlr4.Runtime.Tree.ITerminalNode name, (List<Parameter> Parameters, TypeRef? ReturnType, List<Stmt> Body, ReturnStmt? Return) function,
        NebraParser.TypeParamListContext? typeParams, ParserRuleContext context)
    {
        return new ExtensionMethodNode(NameRefFromTerm(name), function.Parameters, function.ReturnType, function.Body,
            function.Return, isAsync, SpanFromCtx(context))
        {
            TypeParams = VisitTypeParamListContent(typeParams),
            Annotations = VisitAnnotationListContent(annotations),
        };
    }

    private ExtensionMethodNode? ExtensionGetter(NebraParser.AnnotationListContext annotations,
        Antlr4.Runtime.Tree.ITerminalNode kind, Antlr4.Runtime.Tree.ITerminalNode name,
        (List<Parameter> Parameters, TypeRef? ReturnType, List<Stmt> Body, ReturnStmt? Return) getter,
        NebraParser.TypeParamListContext? typeParams, ParserRuleContext context)
    {
        RejectTypeParams(typeParams, "a getter");
        if (ResolveAccessorKind(kind.GetText(), kind) == AccessorKind.Setter)
        {
            diag.Report(SpanFromTerm(kind), Diagnostics.DiagnosticCode.ErrExtensionSetter, name.GetText());
            return null;
        }

        return new ExtensionMethodNode(NameRefFromTerm(name), getter.Parameters, getter.ReturnType, getter.Body,
            getter.Return, false, SpanFromCtx(context))
        {
            Annotations = VisitAnnotationListContent(annotations),
            Kind = ExtensionMemberKind.Getter,
        };
    }

    private ExtensionMethodNode? ExtensionOperator(NebraParser.AnnotationListContext annotations,
        NebraParser.OperatorKeywordContext keyword, NebraParser.OperatorSymbolContext symbol,
        (List<Parameter> Parameters, TypeRef? ReturnType, List<Stmt> Body, ReturnStmt? Return) op,
        NebraParser.TypeParamListContext? typeParams, ParserRuleContext context)
    {
        if (!IsOperatorKeyword(keyword)) return null;
        RejectTypeParams(typeParams, "an operator");

        var metamethod = OperatorSymbolToMetamethod(symbol.GetText(), op.Parameters.Count, out var message);
        if (metamethod == null)
        {
            diag.Report(SpanFromCtx(symbol), Diagnostics.DiagnosticCode.ErrInvalidOperator, message ?? symbol.GetText());
            return null;
        }

        return new ExtensionMethodNode(NameRefFromText(metamethod, SpanFromCtx(symbol)), op.Parameters, op.ReturnType,
            op.Body, op.Return, false, SpanFromCtx(context))
        {
            Annotations = VisitAnnotationListContent(annotations),
            Kind = ExtensionMemberKind.Operator,
        };
    }

    public override Node VisitInterfaceDecl(NebraParser.InterfaceDeclContext context)
    {
        var name = NameRefFromTerm(context.NAME());
        var classRefs = context.classRef();

        var baseInterfaces = new List<NameRef>();
        var baseInterfaceTypeArgs = new List<List<TypeArgRef>>();
        if (context.EXTENDS() != null)
        {
            foreach (var cr in classRefs)
            {
                var (iName, iArgs) = VisitClassRefContent(cr);
                baseInterfaces.Add(iName);
                baseInterfaceTypeArgs.Add(iArgs);
            }
        }

        var typeParams = VisitTypeParamListContent(context.typeParamList());

        var fields = new List<InterfaceFieldNode>();
        var methods = new List<InterfaceMethodNode>();

        foreach (var member in context.interfaceMember())
        {
            switch (member)
            {
                case NebraParser.InterfaceFieldMemberContext field:
                {
                    var fieldName = NameRefFromField(field.fieldName());
                    var typeAnn = (TypeRef)Visit(field.typeAnnotation().typeExpr());
                    var ifaceField = new InterfaceFieldNode(fieldName, typeAnn, SpanFromCtx(field));
                    ifaceField.Annotations = VisitAnnotationListContent(field.annotationList());
                    fields.Add(ifaceField);
                    break;
                }
                case NebraParser.InterfaceMethodMemberContext method:
                {
                    var isAsync = method.ASYNC() != null;
                    var methodName = NameRefFromMember(method.memberName());
                    var (parameters, returnType) = VisitFuncSignatureContent(method.funcSignature());
                    var imTypeParams = VisitTypeParamListContent(method.funcSignature().typeParamList());
                    var imNode = new InterfaceMethodNode(methodName, parameters, returnType, isAsync, SpanFromCtx(method))
                    {
                        IsMethod = IsMethodKind(method.interfaceMemberKind())
                    };
                    imNode.TypeParams = imTypeParams;
                    imNode.Annotations = VisitAnnotationListContent(method.annotationList());
                    methods.Add(imNode);
                    break;
                }
                case NebraParser.InterfaceDefaultMethodMemberContext method:
                {
                    var isAsync = method.ASYNC() != null;
                    var methodName = NameRefFromMember(method.memberName());
                    var (parameters, returnType, body, ret) = VisitFuncBodyContent(method.funcBody());
                    var imTypeParams = VisitTypeParamListContent(method.funcBody().typeParamList());
                    var imNode = new InterfaceMethodNode(methodName, parameters, returnType, isAsync,
                        SpanFromCtx(method), body, ret)
                    {
                        IsMethod = IsMethodKind(method.interfaceMemberKind())
                    };
                    imNode.TypeParams = imTypeParams;
                    imNode.Annotations = VisitAnnotationListContent(method.annotationList());
                    methods.Add(imNode);
                    break;
                }
            }
        }

        var ifaceRegular = new InterfaceDecl(NewNodeID, SpanFromCtx(context), name, baseInterfaces, fields, methods) { IsContract = IsContractKeyword(context.interfaceKeyword()) };
        ifaceRegular.TypeParams = typeParams;
        ifaceRegular.BaseInterfaceTypeArgs = baseInterfaceTypeArgs;
        ifaceRegular.Annotations = VisitAnnotationListContent(context.annotationList());
        return ifaceRegular;
    }
}
