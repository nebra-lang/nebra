using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

/// <summary>
/// The resolve type refs pass is responsible for resolving type references in the source code. It binds their
/// type references to their respective type IDs.
/// </summary>
public class ResolveTypeRefsPass() : Pass(PassName, PassScope.PerBuild)
{
    public const string PassName = "ResolveTypeRefs";

    private PassContext _ctx = null!;

    /// <summary>The name of the <c>declare module</c> each declaration in one is a member of.</summary>
    private readonly Dictionary<NodeID, string> _declaringModule = new();
    private PackageContext _pkg = null!;
    private ScopeID _currentScope = ScopeID.Invalid;

    public override bool Run(PassContext context)
    {
        _ctx = context;
        _declaringModule.Clear();
        foreach (var module in context.Pkgs.SelectMany(package => package.Files).SelectMany(file => file.Hir.Body).OfType<DeclareModuleDecl>())
        {
            foreach (var member in module.Members)
            {
                _declaringModule[member.ID] = module.ModuleName.Name;
            }
        }

        foreach (var pkg in context.Pkgs)
        {
            _pkg = pkg;
            foreach (var f in pkg.Files)
            {
                DeclareEnumTypes(pkg, f.Hir.Body);
                DeclareClassAndInterfaceTypes(pkg, f.Hir.Body);
            }
        }

        foreach (var pkg in context.Pkgs)
            foreach (var f in pkg.Files)
                ResolveImportsPass.PropagateImportTypes(context, pkg, f);

        foreach (var pkg in context.Pkgs)
        {
            _pkg = pkg;
            foreach (var f in pkg.Files)
            {
                // A file pulled in from another module is bound into a scope of its own under the
                // package root, so its top-level declarations - and the imported names they refer
                // to - are not visible from the root itself.
                _currentScope = f.BindingScopeOverride ?? pkg.Root;
                ResolveStmtListTypes(pkg.Types, f.Hir.Body);

                if (f.Hir.Return != null)
                {
                    ResolveStmtTypes(pkg.Types, f.Hir.Return);
                }
            }
        }

        return true;
    }

    private ScopeID ScopeOfDecl(NodeID id)
    {
        return _pkg.Scopes.EnclosingScope(id, out var s) ? s : _pkg.Root;
    }

    /// <summary>
    /// The reflection namespace for a package: the module path of its file, so same-named types in
    /// different files get distinct ids. See <see cref="ReflectionRegistry.Namespace"/>.
    /// </summary>
    private string ReflectNamespace(PackageContext pkg, Decl declaration, NameRef declared)
    {
        return _declaringModule.TryGetValue(declaration.ID, out var module)
            ? module
            : ReflectionRegistry.Namespace(_ctx, declared.Span.File, pkg.IsLibrary);
    }

    /// <summary>
    /// Pre-pass: walk top-level statements and create the EnumType for every EnumDecl so that NamedTypeRefs
    /// referring to enums can be resolved later regardless of declaration order.
    /// </summary>
    private void DeclareEnumTypes(PackageContext pkg, List<Stmt> stmts)
    {
        foreach (var stmt in stmts)
        {
            EnumDecl? enumDecl = stmt switch
            {
                EnumDecl ed => ed,
                ExportStmt es when es.Declaration is EnumDecl ed2 => ed2,
                _ => null
            };
            if (enumDecl == null) continue;
            DeclareEnumType(pkg, enumDecl);
        }
    }

    private void DeclareClassAndInterfaceTypes(PackageContext pkg, List<Stmt> stmts)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case ClassDecl cd:
                    DeclareClassType(pkg, cd);
                    break;
                case InterfaceDecl id:
                    DeclareInterfaceType(pkg, id);
                    break;
                case ExportStmt es:
                    if (es.Declaration is ClassDecl cd2) DeclareClassType(pkg, cd2);
                    else if (es.Declaration is InterfaceDecl id2) DeclareInterfaceType(pkg, id2);
                    break;
                case DeclareModuleDecl dmd:
                    foreach (var member in dmd.Members)
                    {
                        if (member is ClassDecl cd3) DeclareClassType(pkg, cd3);
                        else if (member is InterfaceDecl id3) DeclareInterfaceType(pkg, id3);
                    }
                    break;
            }
        }
    }

    private void DeclareClassType(PackageContext pkg, ClassDecl decl)
    {
        if (decl.Name.Sym == SymID.Invalid) return;
        if (!pkg.Syms.GetByID(decl.Name.Sym, out var sym)) return;
        if (sym.Type != TypID.Invalid) return;

        var origin = decl.IsDeclare ? null : decl.Name.Span.File;
        var classType = pkg.Types.ClassOf(decl.Name.Name, isAbstract: decl.IsAbstract, origin: origin);
        classType.IsSealed = decl.IsSealed;
        classType.HasLocalConstructor = decl.Constructors.Count > 0 && decl.Constructors.All(constructor => constructor.IsLocal);
        classType.ReflectionId = $"{ReflectNamespace(pkg, decl, decl.Name)}::{decl.Name.Name}";
        classType.DeclaredAt ??= decl.Name;
        pkg.Syms.SetType(decl.Name.Sym, classType.ID);
        MaterializeTypeParams(pkg, decl.TypeParams, $"class:{decl.Name.Name}@{origin}", classType.TypeParams, ScopeOfDecl(decl.ID));
    }

    private void DeclareInterfaceType(PackageContext pkg, InterfaceDecl decl)
    {
        if (decl.Name.Sym == SymID.Invalid) return;
        if (!pkg.Syms.GetByID(decl.Name.Sym, out var sym)) return;
        if (sym.Type != TypID.Invalid) return;

        var origin = decl.IsDeclare ? null : decl.Name.Span.File;
        var interfaceType = pkg.Types.InterfaceOf(decl.Name.Name, isIntrinsic: decl.IsIntrinsic, origin: origin);
        interfaceType.ReflectionId = $"{ReflectNamespace(pkg, decl, decl.Name)}::{decl.Name.Name}";
        interfaceType.DeclaredAt ??= decl.Name;
        pkg.Syms.SetType(decl.Name.Sym, interfaceType.ID);
        MaterializeTypeParams(pkg, decl.TypeParams, $"iface:{decl.Name.Name}@{origin}", interfaceType.TypeParams, ScopeOfDecl(decl.ID));
    }

    /// <summary>
    /// Resolves the types of a method's signature and body in the method's own scope, where its
    /// type parameters are declared, so <c>function first&lt;T&gt;(items: T[]): T</c> can name them.
    /// </summary>
    private void ResolveMethodTypes(TypeTable tt, string ownerKey, List<TypeParamDef> typeParams, List<Parameter> parameters,
        TypeRef? returnType, List<Stmt>? body, ReturnStmt? returnStmt)
    {
        var prev = _currentScope;
        var anchor = parameters.Count > 0 ? parameters[0].ID : returnType?.ID;
        if (anchor is { } node && _pkg.Scopes.EnclosingScope(node, out var methodScope))
            _currentScope = methodScope;

        MaterializeFunctionTypeParams(_pkg, typeParams, $"{ownerKey}@{anchor}", _currentScope);
        ResolveTypeParamBounds(tt, typeParams);

        foreach (var parameter in parameters)
        {
            if (parameter.TypeAnnotation != null) ResolveTypeRef(tt, parameter.TypeAnnotation);
        }
        if (returnType != null) ResolveTypeRef(tt, returnType);
        if (body != null) ResolveStmtListTypes(tt, body);
        if (returnStmt != null) ResolveStmtTypes(tt, returnStmt);
        _currentScope = prev;
    }

    /// <summary>
    /// Resolves the <c>extends</c> and <c>implements</c> bounds of a function's or method's type
    /// parameters and records them on the parameters' types.
    /// </summary>
    private void ResolveTypeParamBounds(TypeTable tt, List<TypeParamDef> typeParams)
    {
        foreach (var typeParam in typeParams)
        {
            if (typeParam.ExtendsBound != null) ResolveTypeRef(tt, typeParam.ExtendsBound);
            foreach (var bound in typeParam.ImplementsBounds) ResolveTypeRef(tt, bound);
        }
        PopulateTypeParamBounds(tt, typeParams);
    }

    private void MaterializeFunctionTypeParams(PackageContext pkg, List<TypeParamDef> defs, string ownerKey, ScopeID declScope)
    {
        var sink = new List<TypeParameterType>();
        MaterializeTypeParams(pkg, defs, ownerKey, sink, declScope);
    }

    private void MaterializeTypeParams(PackageContext pkg, List<TypeParamDef> defs, string ownerKey, List<TypeParameterType> sink, ScopeID declScope)
    {
        for (var i = 0; i < defs.Count; i++)
        {
            var def = defs[i];
            var tp = pkg.Types.TypeParamOf(def.Name.Name, ownerKey, i);
            def.ResolvedType = tp.ID;
            sink.Add(tp);

            if (declScope != ScopeID.Invalid
                && pkg.Scopes.LookupOnlyCurrent(declScope, def.Name.Name, out var tpSymId)
                && pkg.Syms.GetByID(tpSymId, out var tpSym)
                && tpSym.Type == TypID.Invalid)
            {
                pkg.Syms.SetType(tpSymId, tp.ID);
            }
        }
    }

    /// <summary>
    /// After type-param bounds (<c>extends</c> / <c>implements</c>) have had their
    /// <see cref="TypeRef.ResolvedType"/> filled in, copy those resolved IDs onto
    /// the corresponding <see cref="TypeParameterType"/> so later passes can read
    /// the bounds from the type universe (used for assignability and the
    /// generic-constraint validator).
    /// </summary>
    private static void PopulateTypeParamBounds(TypeTable tt, List<TypeParamDef> defs)
    {
        foreach (var def in defs)
        {
            if (def.ResolvedType == TypID.Invalid) continue;
            if (!tt.GetByID(def.ResolvedType, out var t) || t is not TypeParameterType tpType) continue;

            if (def.ExtendsBound != null && def.ExtendsBound.ResolvedType != TypID.Invalid)
                tpType.ExtendsBound = def.ExtendsBound.ResolvedType;

            foreach (var ib in def.ImplementsBounds)
            {
                if (ib.ResolvedType == TypID.Invalid) continue;
                if (!tpType.ImplementsBounds.Contains(ib.ResolvedType))
                    tpType.ImplementsBounds.Add(ib.ResolvedType);
            }
        }
    }

    private void DeclareEnumType(PackageContext pkg, EnumDecl decl)
    {
        if (decl.Name.Sym == SymID.Invalid) return;
        if (!pkg.Syms.GetByID(decl.Name.Sym, out var sym)) return;
        if (sym.Type != TypID.Invalid) return;

        var hasStringValues = decl.Members.Any(m => m.Value is StringLiteralExpr);
        var baseType = hasStringValues ? pkg.Types.PrimString : pkg.Types.PrimNumber;
        var members = new List<EnumType.Member>();
        long autoIndex = 0;
        foreach (var m in decl.Members)
        {
            object? value = m.Value switch
            {
                NumberLiteralExpr nl => nl.Raw,
                StringLiteralExpr sl => sl.Value,
                _ => null
            };
            if (value == null && !hasStringValues)
            {
                value = autoIndex.ToString();
            }
            if (value is string s && long.TryParse(s, out var parsed))
            {
                autoIndex = parsed + 1;
            }
            else
            {
                autoIndex++;
            }
            members.Add(new EnumType.Member(m.Name.Name, value));
        }

        var enumType = pkg.Types.EnumOf(decl.Name.Name, members, baseType, decl.IsDeclare ? null : decl.Name.Span.File);
        enumType.ReflectionId = $"{ReflectNamespace(pkg, decl, decl.Name)}::{decl.Name.Name}";
        enumType.DeclaredAt ??= decl.Name;
        enumType.IsDeclared = decl.IsDeclare;
        pkg.Syms.SetType(decl.Name.Sym, enumType.ID);
    }
    
    private void ResolveStmtListTypes(TypeTable tt, List<Stmt> stmts)
    {
        foreach (var stmt in stmts)
        {
            ResolveStmtTypes(tt, stmt);
        }
    }
    
    private void ResolveStmtTypes(TypeTable tt, Stmt stmt)
    {
        if (stmt == null) return;
        switch (stmt)
        {
            case Decl decl:
                ResolveDeclTypes(tt, decl);
                break;
            case AssignStmt assignStmt:
                foreach (var target in assignStmt.Targets)
                {
                    ResolveExprTypes(tt, target);
                }

                foreach (var value in assignStmt.Values)
                {
                    ResolveExprTypes(tt, value);
                }
                break;
            case ExprStmt exprStmt:
                ResolveExprTypes(tt, exprStmt.Expression);
                break;
            case LabelStmt:
            case BreakStmt:
            case ContinueStmt:
            case GotoStmt:
                break;
            case DoBlockStmt doBlockStmt:
                ResolveStmtListTypes(tt, doBlockStmt.Body);
                break;
            case TryStmt tryStmt:
                foreach (var block in tryStmt.Blocks) ResolveStmtListTypes(tt, block);
                break;
            case WhileStmt whileStmt:
                ResolveExprTypes(tt, whileStmt.Condition);
                ResolveStmtListTypes(tt, whileStmt.Body);
                break;
            case RepeatStmt repeatStmt:
                ResolveStmtListTypes(tt, repeatStmt.Body);
                ResolveExprTypes(tt, repeatStmt.Condition);
                break;
            case IfStmt ifStmt:
                ResolveExprTypes(tt, ifStmt.Condition);
                ResolveStmtListTypes(tt, ifStmt.Body);
                foreach (var elseIf in ifStmt.ElseIfs)
                {
                    ResolveExprTypes(tt, elseIf.Condition);
                    ResolveStmtListTypes(tt, elseIf.Body);
                }
                
                if (ifStmt.ElseBody != null)
                {
                    ResolveStmtListTypes(tt, ifStmt.ElseBody);
                }
                
                break;
            case NumericForStmt numericForStmt:
                ResolveExprTypes(tt, numericForStmt.Start);
                ResolveExprTypes(tt, numericForStmt.Limit);
                if (numericForStmt.Step != null)
                {
                    ResolveExprTypes(tt, numericForStmt.Step);
                }
                ResolveStmtListTypes(tt, numericForStmt.Body);
                break;
            case GenericForStmt genericForStmt:
                foreach (var iterator in genericForStmt.Iterators)
                {
                    ResolveExprTypes(tt, iterator);
                }
                ResolveStmtListTypes(tt, genericForStmt.Body);
                break;
            case ReturnStmt returnStmt:
                foreach (var value in returnStmt.Values)
                {
                    ResolveExprTypes(tt, value);
                }
                break;
            case ImportStmt:
                break;
            case ExportStmt exportStmt:
                ResolveDeclTypes(tt, exportStmt.Declaration);
                break;
            case MatchStmt matchStmt:
                ResolveExprTypes(tt, matchStmt.Scrutinee);
                foreach (var arm in matchStmt.Arms)
                {
                    if (arm.Pattern.ValueExpr != null) ResolveExprTypes(tt, arm.Pattern.ValueExpr);
                    if (arm.Pattern.TypeRef != null) ResolveTestedType(tt, arm.Pattern.TypeRef);
                    if (arm.Guard != null) ResolveExprTypes(tt, arm.Guard);
                    ResolveStmtListTypes(tt, arm.Body);
                }
                break;
            case DeferStmt ds:
                if (ds.Call != null) ResolveExprTypes(tt, ds.Call);
                if (ds.Block != null) ResolveStmtListTypes(tt, ds.Block);
                break;
            case GuardStmt gs:
                ResolveExprTypes(tt, gs.Condition);
                if (gs.ElseExpr != null) ResolveExprTypes(tt, gs.ElseExpr);
                break;
            default:
                throw new InvalidOperationException($"Unknown statement kind: {stmt.GetType().Name}");
        }
    }

    private void ResolveDeclTypes(TypeTable tt, Decl decl)
    {
        switch (decl)
        {
            case FunctionDecl functionDecl:
            {
                var prev = _currentScope;
                _currentScope = ScopeOfDecl(functionDecl.ID);
                MaterializeFunctionTypeParams(_pkg, functionDecl.TypeParams,
                    $"fn:{string.Join(".", functionDecl.NamePath.Select(n => n.Name))}@{functionDecl.ID}", _currentScope);
                ResolveTypeParamBounds(tt, functionDecl.TypeParams);
                foreach (var param in functionDecl.Parameters)
                {
                    if (param.TypeAnnotation != null)
                    {
                        ResolveTypeRef(tt, param.TypeAnnotation);
                    }
                }

                if (functionDecl.ReturnType != null)
                {
                    ResolveTypeRef(tt, functionDecl.ReturnType);
                }

                ResolveStmtListTypes(tt, functionDecl.Body);

                if (functionDecl.ReturnStmt != null)
                {
                    ResolveStmtTypes(tt, functionDecl.ReturnStmt);
                }
                _currentScope = prev;
                break;
            }
            case LocalFunctionDecl localFunctionDecl:
            {
                var prev = _currentScope;
                _currentScope = ScopeOfDecl(localFunctionDecl.ID);
                MaterializeFunctionTypeParams(_pkg, localFunctionDecl.TypeParams,
                    $"lfn:{localFunctionDecl.Name.Name}@{localFunctionDecl.ID}", _currentScope);
                ResolveTypeParamBounds(tt, localFunctionDecl.TypeParams);
                foreach (var param in localFunctionDecl.Parameters)
                {
                    if (param.TypeAnnotation != null)
                    {
                        ResolveTypeRef(tt, param.TypeAnnotation);
                    }
                }

                if (localFunctionDecl.ReturnType != null)
                {
                    ResolveTypeRef(tt, localFunctionDecl.ReturnType);
                }

                ResolveStmtListTypes(tt, localFunctionDecl.Body);

                if (localFunctionDecl.ReturnStmt != null)
                {
                    ResolveStmtTypes(tt, localFunctionDecl.ReturnStmt);
                }
                _currentScope = prev;
                break;
            }
            case LocalDecl localDecl:
                foreach (var variable in localDecl.Variables)
                {
                    if (variable.TypeAnnotation != null)
                    {
                        ResolveTypeRef(tt, variable.TypeAnnotation);
                    }
                }
                
                foreach (var value in localDecl.Values)
                {
                    ResolveExprTypes(tt, value);
                }
                
                break;
            case DeclareFunctionDecl declareFunctionDecl:
            {
                var prev = _currentScope;
                _currentScope = ScopeOfDecl(declareFunctionDecl.ID);
                MaterializeFunctionTypeParams(_pkg, declareFunctionDecl.TypeParams,
                    $"dfn:{string.Join(".", declareFunctionDecl.NamePath.Select(n => n.Name))}@{declareFunctionDecl.ID}", _currentScope);
                ResolveTypeParamBounds(tt, declareFunctionDecl.TypeParams);
                foreach (var param in declareFunctionDecl.Parameters)
                {
                    if (param.TypeAnnotation != null)
                    {
                        ResolveTypeRef(tt, param.TypeAnnotation);
                    }
                }

                if (declareFunctionDecl.ReturnType != null)
                {
                    ResolveTypeRef(tt, declareFunctionDecl.ReturnType);
                }
                _currentScope = prev;
                break;
            }
            case DeclareVariableDecl declareVariableDecl:
                ResolveTypeRef(tt, declareVariableDecl.TypeAnnotation);
                break;
            case DeclareModuleDecl declareModuleDecl:
                foreach (var member in declareModuleDecl.Members)
                {
                    ResolveDeclTypes(tt, member);
                }

                break;
            case EnumDecl enumDecl:
                if (enumDecl.IsDeclare)
                {
                    DeclareEnumType(_pkg, enumDecl);
                }
                foreach (var member in enumDecl.Members)
                {
                    if (member.TypeAnnotation != null)
                    {
                        ResolveTypeRef(tt, member.TypeAnnotation);
                    }
                }
                break;
            case ClassDecl classDecl:
            {
                var prev = _currentScope;
                _currentScope = ScopeOfDecl(classDecl.ID);
                foreach (var tp in classDecl.TypeParams)
                {
                    if (tp.ExtendsBound != null) ResolveTypeRef(tt, tp.ExtendsBound);
                    foreach (var ib in tp.ImplementsBounds) ResolveTypeRef(tt, ib);
                }
                PopulateTypeParamBounds(tt, classDecl.TypeParams);
                ResolveTypeArgs(tt, classDecl.BaseClassTypeArgs);
                foreach (var interfaceArgs in classDecl.InterfaceTypeArgs) ResolveTypeArgs(tt, interfaceArgs);
                foreach (var field in classDecl.Fields)
                {
                    if (field.TypeAnnotation != null) ResolveTypeRef(tt, field.TypeAnnotation);
                    if (field.DefaultValue != null) ResolveExprTypes(tt, field.DefaultValue);
                }
                foreach (var method in classDecl.Methods)
                {
                    ResolveMethodTypes(tt, $"method:{classDecl.Name.Name}.{method.Name.Name}", method.TypeParams,
                        method.Parameters, method.ReturnType, method.Body, method.ReturnStmt);
                }
                foreach (var classConstructor in classDecl.Constructors)
                {
                    foreach (var p in classConstructor.Parameters)
                    {
                        if (p.TypeAnnotation != null) ResolveTypeRef(tt, p.TypeAnnotation);
                    }
                    ResolveStmtListTypes(tt, classConstructor.Body);
                    if (classConstructor.ReturnStmt != null) ResolveStmtTypes(tt, classConstructor.ReturnStmt);
                }
                foreach (var accessor in classDecl.Accessors)
                {
                    foreach (var p in accessor.Parameters)
                    {
                        if (p.TypeAnnotation != null) ResolveTypeRef(tt, p.TypeAnnotation);
                    }
                    if (accessor.ReturnType != null) ResolveTypeRef(tt, accessor.ReturnType);
                    ResolveStmtListTypes(tt, accessor.Body);
                    if (accessor.ReturnStmt != null) ResolveStmtTypes(tt, accessor.ReturnStmt);
                }
                _currentScope = prev;
                break;
            }
            case InterfaceDecl interfaceDecl:
            {
                var prev = _currentScope;
                _currentScope = ScopeOfDecl(interfaceDecl.ID);
                foreach (var tp in interfaceDecl.TypeParams)
                {
                    if (tp.ExtendsBound != null) ResolveTypeRef(tt, tp.ExtendsBound);
                    foreach (var ib in tp.ImplementsBounds) ResolveTypeRef(tt, ib);
                }
                PopulateTypeParamBounds(tt, interfaceDecl.TypeParams);
                foreach (var baseArgs in interfaceDecl.BaseInterfaceTypeArgs) ResolveTypeArgs(tt, baseArgs);
                foreach (var field in interfaceDecl.Fields)
                {
                    ResolveTypeRef(tt, field.TypeAnnotation);
                }
                foreach (var method in interfaceDecl.Methods)
                {
                    ResolveMethodTypes(tt, $"method:{interfaceDecl.Name.Name}.{method.Name.Name}", method.TypeParams,
                        method.Parameters, method.ReturnType, method.Body, method.ReturnStmt);
                }
                _currentScope = prev;
                break;
            }
            case ExtendDecl extendDecl:
            {
                var prev = _currentScope;
                _currentScope = ScopeOfDecl(extendDecl.ID);
                // TargetType is null when the `extend <target>` head failed to parse; the method
                // bodies below are still resolved so the user gets full diagnostics, not a crash.
                if (extendDecl.TargetType != null) ResolveTypeRef(tt, extendDecl.TargetType);
                foreach (var method in extendDecl.Methods)
                {
                    ResolveMethodTypes(tt, $"extension:{method.Name.Name}", method.TypeParams,
                        method.Parameters, method.ReturnType, method.Body, method.ReturnStmt);
                }
                _currentScope = prev;
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown declaration kind: {decl.GetType().Name}");
        }
    }

    private void ResolveExprTypes(TypeTable tt, Expr expr)
    {
        if (expr == null) return;
        switch (expr)
        {
            case NilLiteralExpr:
            case BoolLiteralExpr:
            case NumberLiteralExpr:
            case StringLiteralExpr:
            case VarargExpr:
                break;
            case FunctionDefExpr functionDefExpr:
                foreach (var param in functionDefExpr.Parameters)
                {
                    if (param.TypeAnnotation != null)
                    {
                        ResolveTypeRef(tt, param.TypeAnnotation);
                    }
                }
                
                if (functionDefExpr.ReturnType != null)
                {
                    ResolveTypeRef(tt, functionDefExpr.ReturnType);
                }
                
                ResolveStmtListTypes(tt, functionDefExpr.Body);
                break;
            case BinaryExpr binaryExpr:
                ResolveExprTypes(tt, binaryExpr.Left);
                ResolveExprTypes(tt, binaryExpr.Right);
                break;
            case UnaryExpr unaryExpr:
                ResolveExprTypes(tt, unaryExpr.Operand);
                break;
            case NameExpr:
                break;
            case ParenExpr parenExpr:
                ResolveExprTypes(tt, parenExpr.Inner);
                break;
            case DotAccessExpr dotAccessExpr:
                ResolveExprTypes(tt, dotAccessExpr.Object);
                break;
            case IndexAccessExpr indexAccessExpr:
                ResolveExprTypes(tt, indexAccessExpr.Object);
                ResolveExprTypes(tt, indexAccessExpr.Index);
                break;
            case FunctionCallExpr functionCallExpr:
                ResolveExprTypes(tt, functionCallExpr.Callee);
                
                foreach (var arg in functionCallExpr.Arguments)
                {
                    ResolveExprTypes(tt, arg);
                }
                
                break;
            case MethodCallExpr methodCallExpr:
                ResolveExprTypes(tt, methodCallExpr.Object);
                
                foreach (var arg in methodCallExpr.Arguments)
                {
                    ResolveExprTypes(tt, arg);
                }
                
                break;
            case InterpolatedStringExpr interpolatedStringExpr:
                foreach (var part in interpolatedStringExpr.Parts)
                {
                    if (part is InterpExprPart exprPart)
                    {
                        ResolveExprTypes(tt, exprPart.Expression);
                    }
                }
                break;
            case NonNilAssertExpr nonNilAssert:
                ResolveExprTypes(tt, nonNilAssert.Inner);
                break;
            case IncDecExpr incDec:
                ResolveExprTypes(tt, incDec.Target);
                break;
            case TypeCheckExpr typeCheck:
                ResolveExprTypes(tt, typeCheck.Inner);
                ResolveTestedType(tt, typeCheck.TargetType);
                break;
            case TypeCastExpr typeCast:
                ResolveExprTypes(tt, typeCast.Inner);
                ResolveTypeRef(tt, typeCast.TargetType);
                break;
            case TypeOfExpr typeOf:
                ResolveExprTypes(tt, typeOf.Inner);
                break;
            case InstanceOfExpr instOf:
                ResolveExprTypes(tt, instOf.Inner);
                if (instOf.TargetType != null) ResolveTestedType(tt, instOf.TargetType);
                break;
            case TableConstructorExpr tableConstructorExpr:
                foreach (var field in tableConstructorExpr.Fields)
                {
                    if (field.Key != null)
                    {
                        ResolveExprTypes(tt, field.Key);
                    }

                    ResolveExprTypes(tt, field.Value);
                }

                break;
            case AwaitExpr awaitExpr:
                ResolveExprTypes(tt, awaitExpr.Expression);
                break;
            case NewExpr newExpr:
                foreach (var arg in newExpr.Arguments) ResolveExprTypes(tt, arg);
                break;
            case SuperCallExpr superCallExpr:
                foreach (var arg in superCallExpr.Arguments) ResolveExprTypes(tt, arg);
                break;
            case MatchExpr matchExpr:
                ResolveExprTypes(tt, matchExpr.Scrutinee);
                foreach (var arm in matchExpr.Arms)
                {
                    if (arm.Pattern.ValueExpr != null) ResolveExprTypes(tt, arm.Pattern.ValueExpr);
                    if (arm.Pattern.TypeRef != null) ResolveTestedType(tt, arm.Pattern.TypeRef);
                    if (arm.Guard != null) ResolveExprTypes(tt, arm.Guard);
                    ResolveExprTypes(tt, arm.Value);
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown expression kind: {expr.GetType().Name}");
        }
    }

    /// <summary>
    /// Looks up the symbol a type name stands for. A static class member is a value, not a type,
    /// so the lookup passes over it to whatever the name means outside the class. So does a
    /// variable or function, such as the parameter <c>Vector</c> in <c>(Vector: Vector)</c>,
    /// unless no type of that name is in scope, as for a name an import binds.
    /// </summary>
    private bool LookupTypeName(ScopeID scope, string name, out SymID sym)
    {
        SymID? firstValue = null;
        while (_pkg.Scopes.Lookup(scope, name, out sym))
        {
            if (!_pkg.Syms.GetByID(sym, out var symbol)) return true;
            var isValue = symbol.Kind is SymbolKind.Variable or SymbolKind.Function;
            if (!symbol.Flags.HasFlag(SymbolFlags.Static) && !isValue) return true;
            if (isValue && !symbol.Flags.HasFlag(SymbolFlags.Static)) firstValue ??= sym;
            if (!_pkg.Scopes.ParentScope(symbol.Owner, out scope)) break;
        }

        sym = firstValue ?? SymID.Invalid;
        return firstValue != null;
    }

    /// <summary>
    /// Reports a generic class or interface named without its type arguments, such as <c>Box</c>
    /// for <c>Box&lt;T&gt;</c>: what its type parameters stand for would be left open.
    /// </summary>
    private void ReportMissingTypeArguments(Nebra.Diagnostics.TextSpan span, string name, Type? type)
    {
        var arity = type switch
        {
            ClassType classType => classType.TypeParams.Count,
            InterfaceType interfaceType => interfaceType.TypeParams.Count,
            _ => 0,
        };
        if (arity > 0 && !_resolvingTestedType)
            _ctx.Diag.Report(span, Nebra.Diagnostics.DiagnosticCode.ErrTypeParamArityMismatch, name, arity, 0);
    }

    /// <summary>
    /// Set while the type an <c>is</c>, an <c>instanceof</c> or a match pattern tests against is
    /// resolved. There a generic class named without type arguments asks for the class alone.
    /// </summary>
    private bool _resolvingTestedType;

    private void ResolveTestedType(TypeTable tt, TypeRef type)
    {
        _resolvingTestedType = true;
        try
        {
            ResolveTypeRef(tt, type);
        }
        finally
        {
            _resolvingTestedType = false;
        }
    }

    /// <summary>The utility types a generic type reference names when no declaration in scope takes the name, with their number of type arguments.</summary>
    private static readonly Dictionary<string, (UtilityKind Kind, int Arity)> UtilityTypes = new(StringComparer.Ordinal)
    {
        ["Partial"] = (UtilityKind.Partial, 1),
        ["Required"] = (UtilityKind.Required, 1),
        ["Pick"] = (UtilityKind.Pick, 2),
        ["Omit"] = (UtilityKind.Omit, 2),
    };

    /// <summary>Whether <paramref name="symbol"/> names a type declared with <c>declare type</c>, such as the standard library's <c>Partial</c>.</summary>
    private bool IsIntrinsicType(TypeTable tt, SymID symbol)
    {
        return _pkg.Syms.GetByID(symbol, out var declared)
            && tt.GetByID(declared.Type, out var type)
            && type is InterfaceType { IsIntrinsic: true };
    }

    /// <summary>Resolves a utility type such as <c>Partial&lt;User&gt;</c> or <c>Pick&lt;User, "name"&gt;</c>.</summary>
    private Type ResolveUtilityType(TypeTable tt, GenericTypeRef reference, UtilityKind kind, int arity)
    {
        if (reference.Arguments.Count != arity)
            _ctx.Diag.Report(reference.Span, Nebra.Diagnostics.DiagnosticCode.ErrTypeParamArityMismatch, reference.Name.Name, arity, reference.Arguments.Count);

        ResolveTypeArgs(tt, reference.Arguments);
        var arguments = reference.Arguments
            .Select(argument => argument is ConcreteTypeArgRef concrete && tt.GetByID(concrete.Type.ResolvedType, out var resolved) ? resolved : tt.PrimAny)
            .ToList();
        var target = arguments.Count > 0 ? arguments[0] : tt.PrimAny;
        var keys = arity > 1 ? arguments.Count > 1 ? arguments[1] : tt.PrimAny : null;

        var result = tt.DeclareType(new UtilityType(kind, target, keys));
        reference.ResolvedType = result.ID;
        return result;
    }

    private void ResolveTypeArgs(TypeTable tt, List<TypeArgRef> arguments)
    {
        foreach (var argRef in arguments)
        {
            switch (argRef)
            {
                case ConcreteTypeArgRef cta:
                    ResolveTypeRef(tt, cta.Type);
                    break;
                case WildcardTypeArgRef wta:
                    if (wta.Bound != null) ResolveTypeRef(tt, wta.Bound);
                    break;
            }
        }
    }

    /// <summary>
    /// The instantiation <paramref name="arguments"/> make of the generic class or interface
    /// <paramref name="definition"/>, such as <c>Box&lt;number&gt;</c>. Wildcard arguments and a
    /// wrong number of them leave the definition itself, which accepts every instantiation.
    /// </summary>
    public static Type Instantiation(TypeTable tt, Type definition, List<TypeArgRef> arguments)
    {
        var concrete = arguments.OfType<ConcreteTypeArgRef>()
            .Select(argument => tt.GetByID(argument.Type.ResolvedType, out var type) ? type : null)
            .ToList();
        if (concrete.Count != arguments.Count || concrete.Any(type => type == null)) return definition;

        return definition switch
        {
            ClassType classType => tt.Instantiate(classType, concrete!),
            InterfaceType interfaceType => tt.Instantiate(interfaceType, concrete!),
            _ => definition,
        };
    }

    private Type ResolveTypeRef(TypeTable tt, TypeRef tr)
    {
        // Error recovery leaves holes where a type was expected (`local t: = {`), so a reference
        // can be missing entirely. The syntax error is already reported; `any` keeps the rest of
        // the pass running over what did parse.
        if (tr == null) return tt.PrimAny;

        if (tr.ResolvedType != TypID.Invalid)
        {
            tt.GetByID(tr.ResolvedType, out var resolvedType);
            return resolvedType;
        }

        var lookupScope = _currentScope != ScopeID.Invalid ? _currentScope : _pkg.Root;

        if (tr is TypePredicateRef tpr)
        {
            // `param is Type` — resolve the narrowed-to target; the guard's runtime type is boolean.
            ResolveTypeRef(tt, tpr.TargetType);
            tr.ResolvedType = tt.PrimBool.ID;
            return tt.PrimBool;
        }

        if (tr is NamedTypeRef nrt)
        {
            if (LookupTypeName(lookupScope, nrt.Name.Name, out var symId)
                && _pkg.Syms.GetByID(symId, out var sym)
                && sym.Type != TypID.Invalid)
            {
                tt.GetByID(sym.Type, out var resolved);
                nrt.Name.Sym = symId;
                tr.ResolvedType = sym.Type;
                ReportMissingTypeArguments(tr.Span, nrt.Name.Name, resolved);
                return resolved;
            }

            _ctx.Diag.Report(tr.Span, Nebra.Diagnostics.DiagnosticCode.ErrUndeclaredSymbol, nrt.Name.Name);
            tr.ResolvedType = tt.PrimAny.ID;
            return tt.PrimAny;
        }

        if (tr is GenericTypeRef gtr)
        {
            var found = LookupTypeName(lookupScope, gtr.Name.Name, out var gSym);
            if (UtilityTypes.TryGetValue(gtr.Name.Name, out var utility) && (!found || IsIntrinsicType(tt, gSym)))
            {
                if (found) gtr.Name.Sym = gSym;
                return ResolveUtilityType(tt, gtr, utility.Kind, utility.Arity);
            }

            if (!found
                || !_pkg.Syms.GetByID(gSym, out var gsym)
                || gsym.Type == TypID.Invalid
                || !tt.GetByID(gsym.Type, out var gDefType))
            {
                _ctx.Diag.Report(tr.Span, Nebra.Diagnostics.DiagnosticCode.ErrUndeclaredSymbol, gtr.Name.Name);
                tr.ResolvedType = tt.PrimAny.ID;
                return tt.PrimAny;
            }

            gtr.Name.Sym = gSym;

            int expectedArity;
            switch (gDefType)
            {
                case ClassType ct: expectedArity = ct.TypeParams.Count; break;
                case InterfaceType it: expectedArity = it.TypeParams.Count; break;
                default:
                    _ctx.Diag.Report(tr.Span, Nebra.Diagnostics.DiagnosticCode.ErrNonGenericTypeArgs, gtr.Name.Name);
                    tr.ResolvedType = gDefType.ID;
                    return gDefType;
            }

            if (expectedArity == 0)
            {
                _ctx.Diag.Report(tr.Span, Nebra.Diagnostics.DiagnosticCode.ErrNonGenericTypeArgs, gtr.Name.Name);
                tr.ResolvedType = gDefType.ID;
                return gDefType;
            }

            if (gtr.Arguments.Count != expectedArity)
            {
                _ctx.Diag.Report(tr.Span, Nebra.Diagnostics.DiagnosticCode.ErrTypeParamArityMismatch,
                    gtr.Name.Name, expectedArity, gtr.Arguments.Count);
            }

            ResolveTypeArgs(tt, gtr.Arguments);

            var instance = Instantiation(tt, gDefType, gtr.Arguments);
            tr.ResolvedType = instance.ID;
            return instance;
        }

        if (tr is NullableTypeRef nt)
        {
            var inner = ResolveTypeRef(tt, nt.InnerType);
            var unionType = tt.DeclareType(new UnionType([inner, tt.PrimNil]));
            tr.ResolvedType = unionType.ID;
            return unionType;
        }

        Type result;
        switch (tr.Kind)
        {
            case TypeKind.PrimitiveAny:
                result = tt.PrimAny;
                break;
            case TypeKind.PrimitiveNil:
                result = tt.PrimNil;
                break;
            case TypeKind.PrimitiveString:
                result = tt.PrimString;
                break;
            case TypeKind.PrimitiveNumber:
                result = tt.PrimNumber;
                break;
            case TypeKind.PrimitiveBool:
                result = tt.PrimBool;
                break;
            case TypeKind.PrimitiveFunction:
                result = tt.PrimFunction;
                break;
            case TypeKind.PrimitiveThread:
                result = tt.PrimThread;
                break;
            case TypeKind.PrimitiveUserdata:
                result = tt.PrimUserdata;
                break;
            case TypeKind.PrimitiveNever:
                result = tt.PrimNever;
                break;
            case TypeKind.TableArray:
            {
                var et = ResolveTypeRef(tt, ((ArrayTypeRef) tr).ElementType);
                result = tt.DeclareType(new TableArrayType(et));
                break;
            }
            case TypeKind.TableMap:
            {
                var t = (MapTypeRef) tr;
                var kt = ResolveTypeRef(tt, t.KeyType);
                var vt = ResolveTypeRef(tt, t.ValueType);
                result = tt.DeclareType(new TableMapType(kt, vt));
                break;
            }
            case TypeKind.Tuple:
            {
                var t = (TupleTypeRef) tr;
                result = tt.DeclareType(new TupleType(t.ElementTypes.Select(et => new TupleType.Field(ResolveTypeRef(tt, et))).ToList()));
                break;
            }
            case TypeKind.Union:
            {
                var t = (UnionTypeRef) tr;
                result = tt.DeclareType(new UnionType(t.Types.Select(ut => ResolveTypeRef(tt, ut)).ToList()));
                break;
            }
            case TypeKind.Variadic:
            {
                var t = (VariadicTypeRef) tr;
                result = tt.DeclareType(new VariadicType(ResolveTypeRef(tt, t.ElementType)));
                break;
            }
            case TypeKind.Struct:
            {
                var t = (StructTypeRef) tr;
                result = tt.DeclareType(new StructType(t.Fields.Select(f => new StructType.Field(f.Name, ResolveTypeRef(tt, f.Type), f.IsMeta)).ToList()));
                break;
            }
            case TypeKind.Function:
            {
                var t = (FunctionTypeRef) tr;
                var paramTypes = t.ParamTypes.Select(pt => ResolveTypeRef(tt, pt)).ToList();
                var returnType = ResolveTypeRef(tt, t.ReturnType);
                result = tt.DeclareType(new FunctionType(paramTypes, returnType));
                break;
            }
            case TypeKind.Literal:
                result = tt.LiteralOf(((LiteralTypeRef) tr).Value);
                break;
            case TypeKind.KeyOf:
                result = tt.DeclareType(new KeyOfType(ResolveTypeRef(tt, ((KeyOfTypeRef) tr).Target)));
                break;
            case TypeKind.IndexedAccess:
            {
                var t = (IndexedAccessTypeRef) tr;
                result = tt.DeclareType(new IndexedAccessType(ResolveTypeRef(tt, t.Target), ResolveTypeRef(tt, t.Index)));
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown type kind: {tr.Kind}");
        }

        tr.ResolvedType = result.ID;
        return result;
    }
}