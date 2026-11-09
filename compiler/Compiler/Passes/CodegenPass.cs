using Nebra.Compiler.Codegen;
using Nebra.Diagnostics;
using Nebra.IR;

namespace Nebra.Compiler.Passes;

public sealed partial class CodegenPass() : Pass(PassName, PassScope.PerBuild, true)
{
    public const string PassName = "CodegenPass";

    private int _loopLabelCounter;
    private int _stmtListDepth;
    private string _reflectNamespace = "main";
    private readonly Stack<int> _loopLabelStack = new();
    private int _deferDepth;
    private readonly HashSet<int> _neededBreakLabels = [];
    private readonly Dictionary<ScopeID, string> _staticMemberTables = [];
    private readonly HashSet<string> _hoistedNames = [];
    private readonly HashSet<string> _boundExtensions = [];
    private string? _currentFile;
    private string? _superClassName;
    private ClassType? _hostInheritedClass;

    public override bool Run(PassContext context)
    {
        foreach (var pkg in context.Pkgs)
        {
            foreach (var file in pkg.Files)
            {
                if (file.IsDeclarationFile) continue;

                var gen = new LuaGenerator(context.Config);
                EmitFile(context, pkg, gen, file);
                file.GeneratedLua = gen.Finish();
            }
        }

        return true;
    }

    private void EmitFile(PassContext ctx, PackageContext pkg, LuaGenerator gen, PreparsedFile file)
    {
        _currentFile = file.Filename;
        var exportedNames = new List<ExportName>();
        CollectExportNames(ctx, pkg, file.Hir.Body, exportedNames);
        exportedNames = exportedNames.DistinctBy(name => name.Source).ToList();

        _reflectNamespace = ReflectNamespace(ctx, pkg, file);
        EmitReflectionPrelude(ctx, gen);

        EmitHoistedNames(ctx, pkg, gen, file.Hir.Body);
        EmitStmtList(ctx, pkg, gen, file.Hir.Body);

        if (exportedNames.Count > 0)
        {
            if (file.Hir.Return != null)
            {
                EmitReturn(ctx, pkg, gen, file.Hir.Return);
                // TODO: in the future we could merge exports into an existing return table
            }
            else
            {
                var setsGlobal = IsRuntimeGlobalRoot(ctx, file);
                gen.Write(setsGlobal ? "local __nebra_exports = {" : "return {");
                if (!gen.Minify) gen.NewLine();
                gen.Indent();
                for (var i = 0; i < exportedNames.Count; i++)
                {
                    // The key is the name importers write; only the value follows any renaming.
                    gen.Write(exportedNames[i].Source);
                    gen.Write(" = ");
                    gen.Write(exportedNames[i].Emitted);
                    if (i < exportedNames.Count - 1) gen.Write(",");
                    if (!gen.Minify) gen.NewLine();
                }
                gen.Dedent();
                gen.WriteLine("}");
                if (setsGlobal) EmitRuntimeGlobal(ctx, gen);
            }
        }
        else if (file.Hir.Return != null)
        {
            EmitReturn(ctx, pkg, gen, file.Hir.Return);
        }
    }

    /// <summary>
    /// Whether <paramref name="file"/> is the root module of a package that declares a
    /// <c>runtime_global</c>: the <c>init</c> module of its source directory, which consumers
    /// import by the package's name.
    /// </summary>
    private static bool IsRuntimeGlobalRoot(PassContext ctx, PreparsedFile file)
    {
        if (string.IsNullOrWhiteSpace(ctx.Config.RuntimeGlobal) || file.Filename == null) return false;

        var root = Path.GetFullPath(Path.Combine(ctx.Config.ProjectRoot, ctx.Config.Source, "init.neb"));
        return Path.GetFullPath(file.Filename) == root;
    }

    /// <summary>
    /// Sets the package's <c>runtime_global</c> to the root module's exports through the
    /// configured <c>runtime_export</c> statement, then returns them as the module's value.
    /// </summary>
    private static void EmitRuntimeGlobal(PassContext ctx, LuaGenerator gen)
    {
        var template = string.IsNullOrWhiteSpace(ctx.Config.RuntimeExport) ? "_G[%name] = %value" : ctx.Config.RuntimeExport;
        gen.WriteLine(template
            .Replace("%name", "\"" + EscapeLuaString(ctx.Config.RuntimeGlobal!) + "\"")
            .Replace("%value", "__nebra_exports"));
        gen.WriteLine("return __nebra_exports");
    }

    /// <summary>
    /// Declares every top-level class and local function of the file before its first statement,
    /// so a function body reaches one written further down. Their declarations then assign the
    /// local instead of opening a new one.
    /// </summary>
    private void EmitHoistedNames(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Stmt> body)
    {
        _hoistedNames.Clear();
        _overloadVariants.Clear();
        _overloadDispatchers.Clear();
        _boundExtensions.Clear();
        foreach (var extend in body.OfType<ExtendDecl>().Where(extend => !extend.IsDeclare))
        {
            foreach (var member in extend.Methods.Where(member => member.LuaName.Length > 0))
                _hoistedNames.Add(member.LuaName);
        }
        foreach (var group in FunctionOverloadGroups(ctx, pkg, body))
        {
            foreach (var variant in RegisterOverloadVariants(ctx, pkg, group) ?? [])
                _hoistedNames.Add(variant);
        }

        foreach (var stmt in body)
        {
            var name = (stmt is ExportStmt export ? export.Declaration : stmt) switch
            {
                ClassDecl { IsDeclare: false } cd => cd.Name,
                LocalFunctionDecl lfd => lfd.Name,
                _ => null,
            };
            if (name != null) _hoistedNames.Add(ResolveName(ctx, pkg, name));
        }

        if (_hoistedNames.Count == 0) return;

        gen.Write("local " + string.Join(", ", _hoistedNames));
        gen.NewLine();
        gen.WriteSemicolon();
    }

    /// <summary>Whether a top-level declaration of <paramref name="name"/> assigns its hoisted local.</summary>
    private bool IsHoisted(string name) => _stmtListDepth == 1 && _hoistedNames.Contains(name);

    /// <summary>
    /// One exported binding: the name importers write, and the name it carries in the emitted
    /// chunk. The two differ once mangling renames top-level bindings, and the export table has to
    /// keep the first as its key or no importer could find it.
    /// </summary>
    private readonly record struct ExportName(string Source, string Emitted);

    private void CollectExportNames(PassContext ctx, PackageContext pkg, List<Stmt> stmts, List<ExportName> names)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is ExtendDecl { IsExported: true, IsDeclare: false } exportedExtend)
            {
                foreach (var member in exportedExtend.Methods.Where(member => member.LuaName.Length > 0))
                    names.Add(new ExportName(member.LuaName, member.LuaName));
            }

            if (stmt is ImportStmt { IsReExport: true } reExport)
            {
                foreach (var spec in reExport.Specifiers)
                {
                    var exported = spec.Alias ?? spec.Name;
                    names.Add(new ExportName(exported.Name, ResolveName(ctx, pkg, exported)));
                }
            }

            if (stmt is not ExportStmt export) continue;
            switch (export.Declaration)
            {
                case FunctionDecl fd:
                    if (fd.NamePath.Count > 0)
                        names.Add(new ExportName(fd.NamePath[0].Name, ResolveName(ctx, pkg, fd.NamePath[0])));
                    break;
                case LocalFunctionDecl lfd:
                    names.Add(new ExportName(lfd.Name.Name, ResolveName(ctx, pkg, lfd.Name)));
                    break;
                case LocalDecl ld:
                    foreach (var v in ld.Variables)
                        names.Add(new ExportName(v.Name.Name, ResolveName(ctx, pkg, v.Name)));
                    break;
                case EnumDecl ed:
                    names.Add(new ExportName(ed.Name.Name, ResolveName(ctx, pkg, ed.Name)));
                    break;
                case ClassDecl cd:
                    names.Add(new ExportName(cd.Name.Name, ResolveName(ctx, pkg, cd.Name)));
                    break;
            }
        }
    }

    #region Statements

    private void EmitStmtList(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Stmt> stmts)
    {
        _stmtListDepth++;
        var emitted = new HashSet<int>();
        for (var i = 0; i < stmts.Count; i++)
        {
            if (emitted.Contains(i)) continue;
            gen.FlushHoisted();

            var stmt = stmts[i];
            var funcName = FunctionDeclName(stmt);
            if (funcName != null)
            {
                var group = new List<(Stmt stmt, int idx)> { (stmt, i) };
                for (var j = i + 1; j < stmts.Count; j++)
                {
                    if (FunctionDeclName(stmts[j])?.Name == funcName.Name)
                    {
                        group.Add((stmts[j], j));
                        emitted.Add(j);
                    }
                }

                if (group.Count > 1)
                {
                    EmitOverloadedFunction(ctx, pkg, gen, ResolveName(ctx, pkg, funcName), group.Select(g => g.stmt).ToList());
                    continue;
                }
            }

            EmitStmt(ctx, pkg, gen, stmt);
            if (_stmtListDepth == 1) EmitReflectionFor(ctx, pkg, gen, stmt);
        }
        gen.FlushHoisted();
        _stmtListDepth--;
    }

    /// <summary>
    /// The name a plain function declaration binds; overloads share it in the source even when
    /// mangling gives each of them a name of its own.
    /// </summary>
    private static NameRef? FunctionDeclName(Stmt stmt)
    {
        var actual = stmt is ExportStmt es ? es.Declaration : stmt;
        return actual switch
        {
            FunctionDecl { NamePath.Count: 1, MethodName: null } fd => fd.NamePath[0],
            LocalFunctionDecl lfd => lfd.Name,
            _ => null
        };
    }

    private bool ShouldStrip(PassContext ctx, PackageContext pkg, Stmt stmt)
    {
        if (!ctx.Config.Code.StripUnused) return false;
        switch (stmt)
        {
            case LocalFunctionDecl lfd:
                return pkg.Syms.GetByID(lfd.Name.Sym, out var lfSym) && lfSym.Flags.HasFlag(SymbolFlags.Unused);
            case LocalDecl ld:
                return ld.Variables.All(v => pkg.Syms.GetByID(v.Name.Sym, out var vSym) && vSym.Flags.HasFlag(SymbolFlags.Unused));
            case ImportStmt import:
                if (import.Kind == ImportKind.SideEffect) return false;
                if (import.Alias != null)
                    return pkg.Syms.GetByID(import.Alias.Sym, out var aSym) && aSym.Flags.HasFlag(SymbolFlags.Unused);
                return import.Specifiers.Count > 0 && import.Specifiers.All(s =>
                {
                    var nr = s.Alias ?? s.Name;
                    return pkg.Syms.GetByID(nr.Sym, out var sSym) && sSym.Flags.HasFlag(SymbolFlags.Unused);
                });
            default:
                return false;
        }
    }

    private void EmitStmt(PassContext ctx, PackageContext pkg, LuaGenerator gen, Stmt stmt)
    {
        if (ShouldStrip(ctx, pkg, stmt)) return;

        switch (stmt)
        {
            case FunctionDecl fd:
                EmitFunctionDecl(ctx, pkg, gen, fd);
                break;
            case LocalFunctionDecl lfd:
                EmitLocalFunctionDecl(ctx, pkg, gen, lfd);
                break;
            case LocalDecl ld:
                EmitLocalDecl(ctx, pkg, gen, ld);
                break;
            case AssignStmt assign:
                EmitAssign(ctx, pkg, gen, assign);
                break;
            case ExprStmt exprStmt:
                EmitExprStmt(ctx, pkg, gen, exprStmt);
                break;
            case DoBlockStmt doBlock:
                gen.BeginBlock("do");
                EmitStmtList(ctx, pkg, gen, doBlock.Body);
                gen.EndBlock();
                break;
            case WhileStmt ws:
                EmitWhileLoop(ctx, pkg, gen, ws);
                break;
            case RepeatStmt rs:
                EmitRepeatLoop(ctx, pkg, gen, rs);
                break;
            case IfStmt ifStmt:
                EmitIf(ctx, pkg, gen, ifStmt);
                break;
            case NumericForStmt nf:
                EmitNumericForWithLabel(ctx, pkg, gen, nf);
                break;
            case GenericForStmt gf:
                EmitGenericForWithLabel(ctx, pkg, gen, gf);
                break;
            case ReturnStmt ret:
                EmitReturn(ctx, pkg, gen, ret);
                break;
            case TryStmt tryStmt:
                EmitTry(ctx, pkg, gen, tryStmt);
                break;
            case BreakStmt bs:
                EmitBreak(ctx, pkg, gen, bs);
                break;
            case ContinueStmt:
                EmitContinue(gen);
                break;
            case DeferStmt ds:
                EmitDefer(ctx, pkg, gen, ds);
                break;
            case GuardStmt gs:
                EmitGuard(ctx, pkg, gen, gs);
                break;
            case GotoStmt gs:
                if (gen.Features.HasGoto)
                    gen.WriteLine("goto " + gs.LabelName.Name);
                break;
            case LabelStmt ls:
                if (gen.Features.HasGoto)
                    gen.WriteLine("::" + ls.Name.Name + "::");
                break;
            case ImportStmt import:
                EmitImport(ctx, pkg, gen, import);
                break;
            case ExportStmt export:
                EmitStmt(ctx, pkg, gen, export.Declaration);
                break;
            case DeclareFunctionDecl:
            case DeclareVariableDecl:
            case DeclareModuleDecl:
                break;
            case EnumDecl ed:
                if (!ed.IsDeclare)
                    EmitEnumDecl(ctx, pkg, gen, ed);
                break;
            case ClassDecl cd:
                if (!cd.IsDeclare)
                    EmitClassDecl(ctx, pkg, gen, cd);
                break;
            case InterfaceDecl:
                break;
            case ExtendDecl ed:
                if (!ed.IsDeclare)
                    EmitExtendDecl(ctx, pkg, gen, ed);
                break;
            case MatchStmt ms:
                EmitMatchStmt(ctx, pkg, gen, ms);
                break;
        }
    }

    private void EmitEnumDecl(PassContext ctx, PackageContext pkg, LuaGenerator gen, EnumDecl ed)
    {
        var hasStringValues = ed.Members.Any(m => m.Value is StringLiteralExpr);
        gen.Write("local ");
        gen.Write(ResolveName(ctx, pkg, ed.Name));
        gen.Write(" = { ");
        long autoIndex = 0;
        for (var i = 0; i < ed.Members.Count; i++)
        {
            if (i > 0) gen.Write(", ");
            var m = ed.Members[i];
            gen.Write(m.Name.Name);
            gen.Write(" = ");
            switch (m.Value)
            {
                case NumberLiteralExpr nl:
                    gen.Write(nl.Raw);
                    if (long.TryParse(nl.Raw, out var parsed)) autoIndex = parsed + 1;
                    else autoIndex++;
                    break;
                case StringLiteralExpr sl:
                    gen.Write("\"");
                    gen.Write(sl.Value);
                    gen.Write("\"");
                    autoIndex++;
                    break;
                default:
                    if (hasStringValues)
                    {
                        gen.Write("\"");
                        gen.Write(m.Name.Name);
                        gen.Write("\"");
                    }
                    else
                    {
                        gen.Write(autoIndex.ToString());
                        autoIndex++;
                    }
                    break;
            }
        }
        gen.Write(" }");
        gen.NewLine();
        gen.WriteSemicolon();
    }

    /// <summary>
    /// Emits a class: its table, accessors, <c>new</c>, and its methods. A class that declares no
    /// constructor of its own is constructed through the one it inherits, so its generated
    /// <c>new</c> takes a variadic parameter list and forwards it to the base unchanged.
    /// </summary>
    /// <summary>
    /// Records the interfaces <paramref name="cd"/> implements on the class table, so an `is` test
    /// against an interface has something to look up at runtime.
    /// </summary>
    private void EmitImplementedInterfaces(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd, string className)
    {
        if (cd.Name.Sym == SymID.Invalid
            || !pkg.Syms.GetByID(cd.Name.Sym, out var sym)
            || !pkg.Types.GetByID(sym.Type, out var declared)
            || declared is not ClassType classType)
        {
            return;
        }

        var names = Nebra.IR.Type.ImplementedInterfaces(classType)
            .Select(iface => iface.Name)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            return;
        }

        gen.Write(className);
        gen.Write(".__interfaces = { ");
        for (var i = 0; i < names.Count; i++)
        {
            if (i > 0) gen.Write(", ");
            gen.Write("[\"");
            gen.Write(names[i]);
            gen.Write("\"] = true");
        }
        gen.Write(" }");
        gen.NewLine();
        gen.WriteSemicolon();
    }

    /// <summary>
    /// Rewrites either spelling of unpack to the chunk-level alias, which resolves to whichever of
    /// <c>table.unpack</c> and <c>unpack</c> the target provides.
    /// </summary>
    private bool TryEmitUnpack(PackageContext pkg, LuaGenerator gen, Expr expr)
    {
        if (!IsExternalUnpack(pkg, expr))
        {
            return false;
        }

        gen.Write(gen.GetUnpackHelper());
        return true;
    }

    private static bool IsExternalUnpack(PackageContext pkg, Expr expr)
    {
        if (expr is NameExpr name)
        {
            return name.Name.Name == "unpack" && IsExternalSymbol(pkg, name.Name);
        }

        if (expr is DotAccessExpr { IsOptional: false } dot)
        {
            return dot.FieldName.Name == "unpack"
                && dot.Object is NameExpr { Name.Name: "table" } owner
                && IsExternalSymbol(pkg, owner.Name);
        }

        return false;
    }

    private static bool IsExternalSymbol(PackageContext pkg, NameRef name)
    {
        return name.Sym != SymID.Invalid
            && pkg.Syms.GetByID(name.Sym, out var sym)
            && sym.Flags.HasFlag(SymbolFlags.External);
    }

    /// <summary>
    /// Emits a class with the name of its base class at hand for the <c>super:method()</c> calls
    /// of its methods.
    /// </summary>
    private void EmitClassDecl(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd)
    {
        var enclosingSuper = _superClassName;
        var enclosingHostInherited = _hostInheritedClass;
        _superClassName = cd.BaseClass != null ? ResolveName(ctx, pkg, cd.BaseClass) : null;
        _hostInheritedClass = HostInheritedClass(pkg, cd);
        EmitClassDeclBody(ctx, pkg, gen, cd);
        _superClassName = enclosingSuper;
        _hostInheritedClass = enclosingHostInherited;
    }

    private void EmitClassDeclBody(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd)
    {
        var className = ResolveName(ctx, pkg, cd.Name);
        if (pkg.Scopes.EnclosingScope(cd.ID, out var classScope))
            _staticMemberTables[classScope] = className;
        var hasAccessors = cd.Accessors.Count > 0;
        var hasBase = cd.BaseClass != null;
        var baseName = hasBase ? ResolveName(ctx, pkg, cd.BaseClass!) : null;

        if (!IsHoisted(className)) gen.Write("local ");
        gen.Write(className);
        if (_hostInheritedClass != null)
        {
            gen.Write(" = ");
            gen.Write(ExpandInheritTemplate(_hostInheritedClass, baseName!, cd.Name.Name));
        }
        else if (hasBase)
        {
            gen.Write(" = setmetatable({}, { __index = ");
            gen.Write(baseName!);
            gen.Write(" })");
        }
        else
        {
            gen.Write(" = {}");
        }
        gen.NewLine();
        gen.WriteSemicolon();

        var operatorClass = pkg.Syms.GetByID(cd.Name.Sym, out var operatorSymbol)
                            && ctx.Types.GetByID(operatorSymbol.Type, out var operatorType)
            ? operatorType as ClassType
            : null;
        if (_hostInheritedClass == null)
        {
            EmitClassMetatable(gen, cd, operatorClass, className, hasAccessors);
        }

        EmitImplementedInterfaces(ctx, pkg, gen, cd, className);

        foreach (var accessor in cd.Accessors)
        {
            var prefix = accessor.Kind == AccessorKind.Getter ? "__get_" : "__set_";
            gen.Write("function ");
            gen.Write(className);
            gen.Write(".");
            gen.Write(prefix);
            gen.Write(accessor.Name.Name);
            gen.Write("(self");
            foreach (var p in accessor.Parameters)
            {
                gen.Write(", ");
                gen.Write(ResolveName(ctx, pkg, p.Name));
            }
            gen.Write(")");
            gen.NewLine();
            gen.Indent();
            EmitFuncBodyContent(ctx, pkg, gen, accessor.Parameters, accessor.Body, accessor.ReturnStmt, false);
            gen.Dedent();
            gen.WriteLine("end");
            gen.WriteSemicolon();
        }

        EmitConstructors(ctx, pkg, gen, cd, operatorClass, className, baseName, hasAccessors);

        var declaredType = pkg.Syms.GetByID(cd.Name.Sym, out var declaredSymbol)
                           && ctx.Types.GetByID(declaredSymbol.Type, out var declared)
            ? declared as ClassType
            : null;
        var splitEmitted = new HashSet<(string, bool)>();

        foreach (var method in cd.Methods)
        {
            if (method.IsStatic) continue;
            if (declaredType != null && OverloadSignature.IsSplit(declaredType, method.Name.Name, false))
            {
                if (splitEmitted.Add((method.Name.Name, false)))
                    EmitSplitMethods(ctx, pkg, gen, cd, declaredType, className, baseName, method.Name.Name, false);
                continue;
            }

            gen.Write("function ");
            gen.Write(className);
            gen.Write(":");
            gen.Write(method.Name.Name);
            gen.Write("(");
            EmitParamList(ctx, pkg, gen, method.Parameters);
            EmitAsyncDoneParam(gen, method.IsAsync, method.Parameters.Count > 0);
            gen.Write(")");
            gen.NewLine();
            gen.Indent();
            if (method.IsAbstract)
            {
                gen.Write("error(\"Abstract method '");
                gen.Write(method.Name.Name);
                gen.Write("' must be implemented\")");
                gen.NewLine();
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

        if (_hostInheritedClass != null)
        {
            EmitHostOperatorHooks(gen, cd, _hostInheritedClass, className);
        }

        // Inherited interface default methods: materialise their bodies on this class.
        if (pkg.Syms.GetByID(cd.Name.Sym, out var clsSym)
            && ctx.Types.GetByID(clsSym.Type, out var clsT) && clsT is ClassType classType
            && classType.DefaultsToEmit.Count > 0)
        {
            foreach (var (mName, node) in classType.DefaultsToEmit.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                gen.Write("function ");
                gen.Write(className);
                gen.Write(":");
                gen.Write(mName);
                gen.Write("(");
                EmitParamList(ctx, pkg, gen, node.Parameters);
                EmitAsyncDoneParam(gen, node.IsAsync, node.Parameters.Count > 0);
                gen.Write(")");
                gen.NewLine();
                gen.Indent();
                EmitFuncBodyContent(ctx, pkg, gen, node.Parameters, node.Body!, node.ReturnStmt, node.IsAsync);
                gen.Dedent();
                gen.WriteLine("end");
                gen.WriteSemicolon();
            }
        }

        foreach (var method in cd.Methods)
        {
            if (!method.IsStatic) continue;
            if (declaredType != null && OverloadSignature.IsSplit(declaredType, method.Name.Name, true))
            {
                if (splitEmitted.Add((method.Name.Name, true)))
                    EmitSplitMethods(ctx, pkg, gen, cd, declaredType, className, baseName, method.Name.Name, true);
                continue;
            }

            gen.Write("function ");
            gen.Write(className);
            gen.Write(".");
            gen.Write(method.Name.Name);
            gen.Write("(");
            EmitParamList(ctx, pkg, gen, method.Parameters);
            EmitAsyncDoneParam(gen, method.IsAsync, method.Parameters.Count > 0);
            gen.Write(")");
            gen.NewLine();
            gen.Indent();
            EmitFuncBodyContent(ctx, pkg, gen, method.Parameters, method.Body, method.ReturnStmt, method.IsAsync);
            gen.Dedent();
            gen.WriteLine("end");
            gen.WriteSemicolon();
        }

        // Static fields
        foreach (var field in cd.Fields)
        {
            if (!field.IsStatic) continue;
            gen.Write(className);
            gen.Write(".");
            gen.Write(field.Name.Name);
            gen.Write(" = ");
            if (field.DefaultValue != null)
                EmitExpr(ctx, pkg, gen, field.DefaultValue);
            else
                gen.Write("nil");
            gen.NewLine();
            gen.WriteSemicolon();
        }

        if (hasBase && _hostInheritedClass == null)
        {
            var inherited = InheritedMetamethods(operatorClass?.BaseClass);
            if (inherited.Count > 0)
            {
                gen.Write("for _, __k in ipairs({" + string.Join(",", inherited.Select(name => $"\"{name}\"")) + "}) do ");
                gen.Write("if rawget(");
                gen.Write(baseName!);
                gen.Write(", __k) and not rawget(");
                gen.Write(className);
                gen.Write(", __k) then ");
                gen.Write(className);
                gen.Write("[__k] = ");
                gen.Write(baseName!);
                gen.Write("[__k] end end");
                gen.NewLine();
                gen.WriteSemicolon();
            }
        }
    }

    /// <summary>Makes the table of a class the metatable of its instances and gives it its name.</summary>
    private void EmitClassMetatable(LuaGenerator gen, ClassDecl cd, ClassType? operatorClass, string className, bool hasAccessors)
    {
        if (operatorClass != null && (HasClassOperator(operatorClass, "__op_index") || HasClassOperator(operatorClass, "__op_newindex")))
            EmitDeclaredFieldSet(gen, className, operatorClass);

        if (!hasAccessors && operatorClass != null && HasClassOperator(operatorClass, "__op_index"))
        {
            EmitIndexMetamethod(gen, className);
        }
        else if (!hasAccessors)
        {
            gen.Write(className);
            gen.Write(".__index = ");
            gen.Write(className);
            gen.NewLine();
            gen.WriteSemicolon();
        }

        if (!hasAccessors && operatorClass != null && HasClassOperator(operatorClass, "__op_newindex"))
            EmitNewIndexMetamethod(gen, className);
        if (operatorClass != null && HasClassOperator(operatorClass, "__op_call"))
            EmitCallMetamethod(gen, className);

        gen.Write(className);
        gen.Write(".__name = \"");
        gen.Write(cd.Name.Name);
        gen.Write("\"");
        gen.NewLine();
        gen.WriteSemicolon();
    }

    /// <summary>
    /// Emits the constructors of a class: one <c>new</c>, or for overloaded constructors each one
    /// under its variant name, such as <c>new__string</c>, and a <c>new</c> that dispatches to the
    /// one whose parameters fit the arguments it receives.
    /// </summary>
    private void EmitConstructors(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd, ClassType? operatorClass,
        string className, string? baseName, bool hasAccessors)
    {
        var enclosingTryReturn = _tryReturnHelper;
        _tryReturnHelper = null;
        EmitConstructorOverloads(ctx, pkg, gen, cd, operatorClass, className, baseName, hasAccessors);
        _tryReturnHelper = enclosingTryReturn;
    }

    private void EmitConstructorOverloads(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd, ClassType? operatorClass,
        string className, string? baseName, bool hasAccessors)
    {
        if (_hostInheritedClass != null)
        {
            EmitHostInheritedConstructors(ctx, pkg, gen, cd, _hostInheritedClass, className, baseName!);
            return;
        }

        var overloads = operatorClass?.ConstructorOverloads ?? [];
        if (cd.Constructors.Count < 2 || overloads.Count != cd.Constructors.Count || !OverloadSignature.AreDistinct(overloads, false))
        {
            EmitConstructor(ctx, pkg, gen, cd, cd.Constructor, operatorClass, className + ".new", className, baseName, hasAccessors);
            return;
        }

        var branches = new List<OverloadBranch>();
        for (var i = 0; i < cd.Constructors.Count; i++)
        {
            var variant = className + "." + OverloadSignature.VariantName("new", overloads[i], false);
            EmitConstructor(ctx, pkg, gen, cd, cd.Constructors[i], operatorClass, variant, className, baseName, hasAccessors);
            branches.Add(new OverloadBranch(overloads[i], false, variant + "(...)"));
        }

        EmitOverloadDispatcher(gen, $"function {className}.new(...)", "new", branches);
    }

    /// <summary>
    /// Emits <paramref name="ctor"/>, or the constructor of a class declaring none, as the Lua
    /// function <paramref name="functionName"/>.
    /// </summary>
    private void EmitConstructor(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd, ClassConstructorNode? ctor,
        ClassType? operatorClass, string functionName, string className, string? baseName, bool hasAccessors)
    {
        var hasBase = cd.BaseClass != null;
        gen.Write("function ");
        gen.Write(functionName);
        gen.Write("(");
        if (ctor != null)
        {
            for (var i = 0; i < ctor.Parameters.Count; i++)
            {
                if (i > 0) gen.Write(", ");
                gen.Write(ResolveName(ctx, pkg, ctor.Parameters[i].Name));
            }
        }
        else if (hasBase && InheritsConstructorArguments(operatorClass?.BaseClass))
        {
            gen.Write("...");
        }
        gen.Write(")");
        gen.NewLine();
        gen.Indent();

        if (ctor != null)
        {
            EmitDefaultParamPreamble(ctx, pkg, gen, ctor.Parameters);
        }

        if (TryCollectCollapsibleInits(cd, ctor, hasBase, hasAccessors, out var inits))
        {
            EmitCollapsedConstructor(ctx, pkg, gen, className, inits);
        }
        else if (hasBase && OnlyWrapsBaseConstructor(cd, ctor))
        {
            var forwarded = ctor == null && InheritsConstructorArguments(operatorClass?.BaseClass) ? "..." : "";
            var metatable = ClassNeedsProxy(ctx, pkg, cd) ? $"{gen.GetClassProxyHelper()}({className}, {baseName})" : className;
            gen.WriteLine($"return setmetatable({baseName}.new({forwarded}), {metatable})");
            gen.WriteSemicolon();
        }
        else
        {
            var hasSuperCall = hasBase && ctor != null && HasSuperCall(ctor.Body);
            if (hasBase)
            {
                if (!hasSuperCall)
                {
                    gen.Write("local self = ");
                    gen.Write(baseName!);
                    gen.Write(ctor == null && InheritsConstructorArguments(operatorClass?.BaseClass) ? ".new(...)" : ".new()");
                    gen.NewLine();
                    gen.WriteSemicolon();
                    gen.Write("setmetatable(self, ");
                    gen.Write(ClassNeedsProxy(ctx, pkg, cd) ? $"{gen.GetClassProxyHelper()}({className}, {baseName})" : className);
                    gen.Write(")");
                    gen.NewLine();
                    gen.WriteSemicolon();
                }
            }
            else
            {
                if (hasAccessors)
                {
                    var proxyHelper = gen.GetClassProxyHelper();
                    gen.Write("local self = setmetatable({}, ");
                    gen.Write(proxyHelper);
                    gen.Write("(");
                    gen.Write(className);
                    gen.Write(", nil))");
                }
                else
                {
                    gen.Write("local self = setmetatable({}, ");
                    gen.Write(className);
                    gen.Write(")");
                }
                gen.NewLine();
                gen.WriteSemicolon();
            }

            if (!hasSuperCall)
            {
                EmitInstanceFieldDefaults(ctx, pkg, gen, cd);
            }

            if (ctor != null)
            {
                EmitClassConstructorBody(ctx, pkg, gen, cd, ctor);
            }

            gen.WriteLine("return self");
            gen.WriteSemicolon();
        }

        gen.Dedent();
        gen.WriteLine("end");
        gen.WriteSemicolon();
    }

    private void EmitClassConstructorBody(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd, ClassConstructorNode ctor)
    {
        var className = ResolveName(ctx, pkg, cd.Name);
        var hasBase = cd.BaseClass != null;
        var baseName = hasBase ? ResolveName(ctx, pkg, cd.BaseClass!) : null;
        var needsProxy = ClassNeedsProxy(ctx, pkg, cd);

        foreach (var stmt in ctor.Body)
        {
            if (stmt is ExprStmt { Expression: SuperCallExpr superCall })
            {
                if (hasBase)
                {
                    gen.Write("local self = ");
                    gen.Write(baseName!);
                    gen.Write("." + (superCall.OverloadName ?? "new") + "(");
                    for (var i = 0; i < superCall.Arguments.Count; i++)
                    {
                        if (i > 0) gen.Write(", ");
                        EmitExpr(ctx, pkg, gen, superCall.Arguments[i]);
                    }
                    gen.Write(")");
                    gen.NewLine();
                    gen.WriteSemicolon();

                    if (needsProxy)
                    {
                        var proxyHelper = gen.GetClassProxyHelper();
                        gen.Write("setmetatable(self, ");
                        gen.Write(proxyHelper);
                        gen.Write("(");
                        gen.Write(className);
                        gen.Write(", ");
                        gen.Write(baseName!);
                        gen.Write("))");
                    }
                    else
                    {
                        gen.Write("setmetatable(self, ");
                        gen.Write(className);
                        gen.Write(")");
                    }
                    gen.NewLine();
                    gen.WriteSemicolon();

                    // Now that `self` exists, emit the child class's instance field defaults.
                    EmitInstanceFieldDefaults(ctx, pkg, gen, cd);
                }
                continue;
            }
            EmitStmt(ctx, pkg, gen, stmt);
        }
        if (ctor.ReturnStmt != null) EmitReturn(ctx, pkg, gen, ctor.ReturnStmt);
    }

    private void EmitInstanceFieldDefaults(PassContext ctx, PackageContext pkg, LuaGenerator gen, ClassDecl cd)
    {
        foreach (var field in cd.Fields)
        {
            if (field.IsStatic) continue;
            if (field.DefaultValue == null) continue;
            gen.Write("self.");
            gen.Write(field.Name.Name);
            gen.Write(" = ");
            EmitExpr(ctx, pkg, gen, field.DefaultValue);
            gen.NewLine();
            gen.WriteSemicolon();
        }
    }

    /// <summary>
    /// Determines whether a class's constructor collapses to a single
    /// <c>return setmetatable({ ... }, Class)</c> — the fast path that creates the instance table
    /// with a known field set and skips the intermediate <c>self</c> local and metatable writes.
    /// Only safe when the class has no base class and no accessors, and the field defaults plus the
    /// constructor body are exactly a straight run of distinct <c>self.field = value</c> assignments
    /// whose values never read <c>self</c> or capture it in a closure. Returns the ordered field
    /// initializers via <paramref name="inits"/> when collapsible.
    /// </summary>
    private static bool TryCollectCollapsibleInits(ClassDecl cd, ClassConstructorNode? ctor, bool hasBase, bool hasAccessors, out List<(string Field, Expr Value)> inits)
    {
        inits = [];
        if (hasBase || hasAccessors) return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in cd.Fields)
        {
            if (field.IsStatic || field.DefaultValue == null) continue;
            if (ContainsSelfOrClosure(field.DefaultValue)) return false;
            if (!seen.Add(field.Name.Name)) return false;
            inits.Add((field.Name.Name, field.DefaultValue));
        }

        if (ctor != null)
        {
            if (ctor.ReturnStmt != null) return false;
            foreach (var stmt in ctor.Body)
            {
                if (stmt is not AssignStmt { Targets.Count: 1, Values.Count: 1 } asn) return false;
                if (asn.Targets[0] is not DotAccessExpr { IsOptional: false, Object: NameExpr { Name.Name: "self" } } dot) return false;
                if (ContainsSelfOrClosure(asn.Values[0])) return false;
                if (!seen.Add(dot.FieldName.Name)) return false;
                inits.Add((dot.FieldName.Name, asn.Values[0]));
            }
        }

        return true;
    }

    private void EmitCollapsedConstructor(PassContext ctx, PackageContext pkg, LuaGenerator gen, string className, List<(string Field, Expr Value)> inits)
    {
        gen.Write("return setmetatable({");
        if (inits.Count > 0)
        {
            gen.NewLine();
            gen.Indent();
            for (var i = 0; i < inits.Count; i++)
            {
                gen.Write(inits[i].Field);
                gen.Write(" = ");
                EmitExpr(ctx, pkg, gen, inits[i].Value);
                if (i < inits.Count - 1) gen.Write(",");
                gen.NewLine();
            }
            gen.Dedent();
        }
        gen.Write("}, ");
        gen.Write(className);
        gen.Write(")");
        gen.NewLine();
        gen.WriteSemicolon();
    }

    /// <summary>
    /// True when an expression reads <c>self</c> or contains a nested function that might capture
    /// it — either of which makes the constructor unsafe to collapse into a table literal (where
    /// <c>self</c> is not yet bound). Unknown expression shapes conservatively return true.
    /// </summary>
    private static bool ContainsSelfOrClosure(Expr e)
    {
        switch (e)
        {
            case null: return false;
            case NilLiteralExpr or BoolLiteralExpr or NumberLiteralExpr or StringLiteralExpr or VarargExpr: return false;
            case NameExpr ne: return ne.Name.Name == "self";
            case ParenExpr p: return ContainsSelfOrClosure(p.Inner);
            case BinaryExpr b: return ContainsSelfOrClosure(b.Left) || ContainsSelfOrClosure(b.Right);
            case UnaryExpr u: return ContainsSelfOrClosure(u.Operand);
            case DotAccessExpr d: return ContainsSelfOrClosure(d.Object);
            case IndexAccessExpr ix: return ContainsSelfOrClosure(ix.Object) || ContainsSelfOrClosure(ix.Index);
            case FunctionCallExpr c: return ContainsSelfOrClosure(c.Callee) || c.Arguments.Any(ContainsSelfOrClosure);
            case MethodCallExpr m: return ContainsSelfOrClosure(m.Object) || m.Arguments.Any(ContainsSelfOrClosure);
            case NonNilAssertExpr n: return ContainsSelfOrClosure(n.Inner);
            case IncDecExpr id: return ContainsSelfOrClosure(id.Target);
            case TypeCheckExpr tc: return ContainsSelfOrClosure(tc.Inner);
            case TypeCastExpr tc: return ContainsSelfOrClosure(tc.Inner);
            case TypeOfExpr to: return ContainsSelfOrClosure(to.Inner);
            case InstanceOfExpr io: return ContainsSelfOrClosure(io.Inner);
            case AwaitExpr a: return ContainsSelfOrClosure(a.Expression);
            case NewExpr nw: return nw.Arguments.Any(ContainsSelfOrClosure);
            case InterpolatedStringExpr istr: return istr.Parts.OfType<InterpExprPart>().Any(pt => ContainsSelfOrClosure(pt.Expression));
            case TableConstructorExpr tbl: return tbl.Fields.Any(f => ContainsSelfOrClosure(f.Value) || (f.Key != null && ContainsSelfOrClosure(f.Key)));
            default: return true;
        }
    }

    private bool ClassNeedsProxy(PassContext ctx, PackageContext pkg, ClassDecl cd)
    {
        if (cd.Accessors.Count > 0) return true;
        if (cd.Name.Sym != SymID.Invalid
            && pkg.Syms.GetByID(cd.Name.Sym, out var sym)
            && pkg.Types.GetByID(sym.Type, out var t) && t is ClassType ct)
        {
            var parent = ct.BaseClass;
            while (parent != null)
            {
                if (parent.Getters.Count > 0 || parent.Setters.Count > 0) return true;
                parent = parent.BaseClass;
            }
        }
        return false;
    }

    private bool HasSuperCall(List<Stmt> stmts)
    {
        return stmts.Any(s => s is ExprStmt { Expression: SuperCallExpr });
    }

    private void EmitNewExpr(PassContext ctx, PackageContext pkg, LuaGenerator gen, NewExpr ne)
    {
        var record = ctx.Cache.ContainsKey(InferTypesPass.GenericTypeChecksKey) ? TypeArgumentRecord(ctx, ne.Type) : null;
        if (record == null)
        {
            EmitConstruction(ctx, pkg, gen, ne);
            return;
        }

        gen.Write(gen.GetGenericRecordHelper() + "(");
        EmitConstruction(ctx, pkg, gen, ne);
        gen.Write(", " + record + ")");
    }

    private void EmitConstruction(PassContext ctx, PackageContext pkg, LuaGenerator gen, NewExpr ne)
    {
        var className = ResolveName(ctx, pkg, ne.ClassName);

        var template = LookupCtorTemplate(ctx, pkg, ne.ClassName);
        if (template != null)
        {
            var idx = template.IndexOf("$args", StringComparison.Ordinal);
            if (idx >= 0)
            {
                gen.Write(template[..idx].Replace("$class", className));
                for (var i = 0; i < ne.Arguments.Count; i++)
                {
                    if (i > 0) gen.Write(", ");
                    EmitExpr(ctx, pkg, gen, ne.Arguments[i]);
                }
                gen.Write(template[(idx + "$args".Length)..].Replace("$class", className));
                return;
            }
            
            gen.Write(template.Replace("$class", className));
            gen.Write("(");
            for (var i = 0; i < ne.Arguments.Count; i++)
            {
                if (i > 0) gen.Write(", ");
                EmitExpr(ctx, pkg, gen, ne.Arguments[i]);
            }
            gen.Write(")");
            return;
        }

        gen.Write(className);
        gen.Write("." + (ne.OverloadName ?? "new") + "(");
        for (var i = 0; i < ne.Arguments.Count; i++)
        {
            if (i > 0) gen.Write(", ");
            EmitExpr(ctx, pkg, gen, ne.Arguments[i]);
        }
        gen.Write(")");
    }

    private static string? LookupCtorTemplate(PassContext ctx, PackageContext pkg, NameRef className)
    {
        if (className.Sym == SymID.Invalid) return null;
        if (!pkg.Syms.GetByID(className.Sym, out var sym)) return null;
        if (!ctx.Types.GetByID(sym.Type, out var rawType)) return null;
        if (rawType is not ClassType classType) return null;
        return classType.EffectiveInheritTemplate != null ? classType.NearestCtorTemplate : classType.CtorTemplate;
    }

    #endregion

    #region Declarations

    /// <summary>
    /// Emits overloads that share their parameter types, which only a side split allows, as one
    /// function holding every body.
    /// </summary>
    private void EmitInlineOverloads(PassContext ctx, PackageContext pkg, LuaGenerator gen, string name, List<Stmt> overloads)
    {
        var isLocal = overloads.Any(s =>
        {
            var actual = s is ExportStmt es ? es.Declaration : s;
            return actual is LocalFunctionDecl;
        });

        gen.Write(isLocal && !IsHoisted(name) ? "local function " : "function ");
        gen.Write(name);
        gen.Write("(...)");
        gen.NewLine();
        gen.Indent();
        gen.WriteLine("local __n = select(\"#\", ...)");
        gen.WriteSemicolon();
        gen.WriteLine("local __args = {...}");
        gen.WriteSemicolon();

        var first = true;
        foreach (var stmt in overloads)
        {
            var actual = stmt is ExportStmt es ? es.Declaration : (Decl)stmt;
            List<Parameter> parameters;
            List<Stmt> body;
            ReturnStmt? returnStmt;

            switch (actual)
            {
                case FunctionDecl fd:
                    parameters = fd.Parameters;
                    body = fd.Body;
                    returnStmt = fd.ReturnStmt;
                    break;
                case LocalFunctionDecl lfd:
                    parameters = lfd.Parameters;
                    body = lfd.Body;
                    returnStmt = lfd.ReturnStmt;
                    break;
                default:
                    continue;
            }

            var regularParams = parameters.Where(p => !p.IsVararg).ToList();

            gen.Write(first ? "if " : "elseif ");
            first = false;

            var conditions = new List<string> { $"__n == {regularParams.Count}" };
            foreach (var param in regularParams)
            {
                var luaType = GetLuaTypeCheck(ctx, pkg, param);
                if (luaType != null)
                {
                    var idx = regularParams.IndexOf(param) + 1;
                    conditions.Add($"type(__args[{idx}]) == \"{luaType}\"");
                }
            }

            gen.Write(string.Join(" and ", conditions));
            gen.Write(" then");
            gen.NewLine();
            gen.Indent();

            for (var i = 0; i < regularParams.Count; i++)
            {
                gen.Write("local ");
                gen.Write(ResolveName(ctx, pkg, regularParams[i].Name));
                gen.Write(" = __args[");
                gen.Write((i + 1).ToString());
                gen.WriteLine("]");
                gen.WriteSemicolon();
            }

            EmitStmtList(ctx, pkg, gen, body);
            if (returnStmt != null)
                EmitReturn(ctx, pkg, gen, returnStmt);

            gen.Dedent();
        }

        gen.Write("else");
        gen.NewLine();
        gen.Indent();
        gen.WriteLine("error(\"no matching overload for '\" .. \"" + name + "\" .. \"'\")");
        gen.WriteSemicolon();
        gen.Dedent();
        gen.WriteLine("end");
        gen.EndBlock();
    }

    private string? GetLuaTypeCheck(PassContext ctx, PackageContext pkg, Parameter param)
    {
        if (param.TypeAnnotation == null || param.TypeAnnotation.ResolvedType == TypID.Invalid)
            return null;

        if (!pkg.Types.GetByID(param.TypeAnnotation.ResolvedType, out var typ))
            return null;

        return typ.Kind switch
        {
            TypeKind.PrimitiveNumber => "number",
            TypeKind.PrimitiveString => "string",
            TypeKind.PrimitiveBool => "boolean",
            TypeKind.Function => "function",
            TypeKind.TableArray or TypeKind.TableMap or TypeKind.Struct => "table",
            _ => null
        };
    }

    private void EmitFunctionDecl(PassContext ctx, PackageContext pkg, LuaGenerator gen, FunctionDecl fd)
    {
        gen.Write("function ");
        EmitNamePath(ctx, pkg, gen, fd.NamePath);
        if (fd.MethodName != null)
        {
            gen.Write(":");
            gen.Write(fd.MethodName.Name);
        }
        gen.Write("(");
        EmitParamList(ctx, pkg, gen, fd.Parameters);
        if (fd.IsAsync)
        {
            if (fd.Parameters.Count > 0) gen.Write(", ");
            gen.Write("__done");
        }
        gen.Write(")");
        gen.NewLine();
        gen.Indent();
        EmitFuncBodyContent(ctx, pkg, gen, fd.Parameters, fd.Body, fd.ReturnStmt, fd.IsAsync);
        gen.EndBlock();
    }

    private void EmitLocalFunctionDecl(PassContext ctx, PackageContext pkg, LuaGenerator gen, LocalFunctionDecl lfd)
    {
        var functionName = ResolveName(ctx, pkg, lfd.Name);
        gen.Write(IsHoisted(functionName) ? "function " : "local function ");
        gen.Write(functionName);
        gen.Write("(");
        EmitParamList(ctx, pkg, gen, lfd.Parameters);
        if (lfd.IsAsync)
        {
            if (lfd.Parameters.Count > 0) gen.Write(", ");
            gen.Write("__done");
        }
        gen.Write(")");
        gen.NewLine();
        gen.Indent();
        EmitFuncBodyContent(ctx, pkg, gen, lfd.Parameters, lfd.Body, lfd.ReturnStmt, lfd.IsAsync);
        gen.EndBlock();
    }

    private void EmitLocalDecl(PassContext ctx, PackageContext pkg, LuaGenerator gen, LocalDecl ld)
    {
        var deepFreeze = ctx.Config.Rules.DeepFreeze && !ld.IsMutable;

        gen.Write("local ");
        for (var i = 0; i < ld.Variables.Count; i++)
        {
            if (i > 0) gen.Write(", ");
            var v = ld.Variables[i];
            gen.Write(ResolveName(ctx, pkg, v.Name));
            if (gen.Features.HasConstLocal && v.Attribute == "const")
                gen.Write(" <const>");
            else if (gen.Features.HasCloseLocal && v.Attribute == "close")
                gen.Write(" <close>");
        }

        if (ld.Values.Count > 0)
        {
            gen.Write(" = ");
            if (deepFreeze && ld.Values.Any(v => v is TableConstructorExpr))
            {
                for (var i = 0; i < ld.Values.Count; i++)
                {
                    if (i > 0) gen.Write(", ");
                    if (ld.Values[i] is TableConstructorExpr)
                    {
                        gen.Write($"{gen.GetFreezeHelper()}(");
                        EmitExpr(ctx, pkg, gen, ld.Values[i]);
                        gen.Write(")");
                        
                    }
                    else
                    {
                        EmitExpr(ctx, pkg, gen, ld.Values[i]);
                    }
                }
            }
            else
            {
                EmitExprList(ctx, pkg, gen, ld.Values);
            }
        }
        gen.NewLine();
        gen.WriteSemicolon();
    }

    #endregion

    #region Assignment

    private void EmitAssign(PassContext ctx, PackageContext pkg, LuaGenerator gen, AssignStmt assign)
    {
        if (assign.Targets.Any(target => target is IndexAccessExpr { Extension: not null }))
        {
            EmitAssignThroughExtensions(ctx, pkg, gen, assign);
            return;
        }

        for (var i = 0; i < assign.Targets.Count; i++)
        {
            if (i > 0) gen.Write(", ");
            EmitExpr(ctx, pkg, gen, assign.Targets[i]);
        }
        gen.Write(" = ");
        EmitExprList(ctx, pkg, gen, assign.Values);
        gen.NewLine();
        gen.WriteSemicolon();
    }

    #endregion

    #region Control Flow

    private void EmitIf(PassContext ctx, PackageContext pkg, LuaGenerator gen, IfStmt ifStmt)
    {
        gen.Write("if ");
        EmitExpr(ctx, pkg, gen, ifStmt.Condition);
        gen.Write(" then");
        gen.NewLine();
        gen.Indent();
        EmitStmtList(ctx, pkg, gen, ifStmt.Body);
        gen.Dedent();

        foreach (var elseIf in ifStmt.ElseIfs)
        {
            gen.Write("elseif ");
            EmitExpr(ctx, pkg, gen, elseIf.Condition);
            gen.Write(" then");
            gen.NewLine();
            gen.Indent();
            EmitStmtList(ctx, pkg, gen, elseIf.Body);
            gen.Dedent();
        }

        if (ifStmt.ElseBody != null)
        {
            gen.WriteLine("else");
            gen.Indent();
            EmitStmtList(ctx, pkg, gen, ifStmt.ElseBody);
            gen.Dedent();
        }

        gen.WriteLine("end");
    }

    // EmitNumericFor and EmitGenericFor have been replaced by
    // EmitNumericForWithLabel and EmitGenericForWithLabel above.

    private void EmitIteratorMethodCall(PassContext ctx, PackageContext pkg, LuaGenerator gen, Expr iterated)
    {
        var needsParentheses = iterated is not (NameExpr or DotAccessExpr or IndexAccessExpr or FunctionCallExpr or MethodCallExpr or ParenExpr);
        if (needsParentheses) gen.Write("(");
        EmitExpr(ctx, pkg, gen, iterated);
        if (needsParentheses) gen.Write(")");
        gen.Write(":iterator()");
    }

    private void EmitGenericForIterators(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Expr> iterators)
    {
        for (var i = 0; i < iterators.Count; i++)
        {
            if (i > 0) gen.Write(", ");
            var iter = iterators[i];
            if (gen.IndexBase != 1 && iter is FunctionCallExpr call && IsIpairsCall(ctx, pkg, call))
            {
                var helper = gen.GetIpairsHelper();
                gen.Write(helper);
                gen.Write("(");
                EmitExprList(ctx, pkg, gen, call.Arguments);
                gen.Write(")");
            }
            else
            {
                EmitExpr(ctx, pkg, gen, iter);
            }
        }
    }

    // --- Loop helpers (continue / break N) ---

    private int PushLoop()
    {
        var label = ++_loopLabelCounter;
        _loopLabelStack.Push(label);
        return label;
    }

    private void PopLoop() => _loopLabelStack.Pop();

    private bool LoopBodyNeedsContinueLabel(List<Stmt> body)
    {
        foreach (var s in body)
        {
            if (s is ContinueStmt) return true;
            if (s is IfStmt ifs)
            {
                if (LoopBodyNeedsContinueLabel(ifs.Body)) return true;
                foreach (var ei in ifs.ElseIfs)
                    if (LoopBodyNeedsContinueLabel(ei.Body)) return true;
                if (ifs.ElseBody != null && LoopBodyNeedsContinueLabel(ifs.ElseBody)) return true;
            }
            if (s is DoBlockStmt db && LoopBodyNeedsContinueLabel(db.Body)) return true;
            if (s is MatchStmt ms && ms.Arms.Any(a => LoopBodyNeedsContinueLabel(a.Body))) return true;
        }
        return false;
    }

    private void EmitLoopBody(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Stmt> body, int label)
    {
        var needsContinue = LoopBodyNeedsContinueLabel(body);
        if (needsContinue && !gen.Features.HasGoto)
        {
            gen.BeginBlock("repeat");
            EmitStmtList(ctx, pkg, gen, body);
            gen.Dedent();
            gen.WriteLine("until true");
            gen.Indent();
            gen.Dedent();
        }
        else
        {
            EmitStmtList(ctx, pkg, gen, body);
            if (needsContinue) gen.WriteLine($"::__continue_{label}::");
        }
    }

    private void EmitWhileLoop(PassContext ctx, PackageContext pkg, LuaGenerator gen, WhileStmt ws)
    {
        var label = PushLoop();
        gen.Write("while ");
        EmitExpr(ctx, pkg, gen, ws.Condition);
        gen.BeginBlock(" do");
        EmitLoopBody(ctx, pkg, gen, ws.Body, label);
        gen.EndBlock();
        EmitBreakLabel(gen, label);
        PopLoop();
    }

    private void EmitRepeatLoop(PassContext ctx, PackageContext pkg, LuaGenerator gen, RepeatStmt rs)
    {
        var label = PushLoop();
        gen.BeginBlock("repeat");
        EmitLoopBody(ctx, pkg, gen, rs.Body, label);
        gen.Dedent();
        gen.Write("until ");
        EmitExpr(ctx, pkg, gen, rs.Condition);
        gen.NewLine();
        gen.Indent();
        gen.Dedent();
        EmitBreakLabel(gen, label);
        PopLoop();
    }

    private void EmitNumericForWithLabel(PassContext ctx, PackageContext pkg, LuaGenerator gen, NumericForStmt nf)
    {
        var label = PushLoop();
        gen.Write("for ");
        gen.Write(ResolveName(ctx, pkg, nf.VarName));
        gen.Write(" = ");
        EmitExpr(ctx, pkg, gen, nf.Start);
        gen.Write(", ");
        EmitExpr(ctx, pkg, gen, nf.Limit);
        if (nf.Step != null)
        {
            gen.Write(", ");
            EmitExpr(ctx, pkg, gen, nf.Step);
        }
        gen.Write(" do");
        gen.NewLine();
        gen.Indent();
        EmitLoopBody(ctx, pkg, gen, nf.Body, label);
        gen.EndBlock();
        EmitBreakLabel(gen, label);
        PopLoop();
    }

    private void EmitGenericForWithLabel(PassContext ctx, PackageContext pkg, LuaGenerator gen, GenericForStmt gf)
    {
        var label = PushLoop();
        if (gf is { RunsOverRange: true, Iterators: [BinaryExpr range] })
        {
            gen.Write($"for {ResolveName(ctx, pkg, gf.VarNames[0])} = ");
            EmitExpr(ctx, pkg, gen, range.Left);
            gen.Write(", ");
            EmitExpr(ctx, pkg, gen, range.Right);
            gen.Write(" do");
            gen.NewLine();
            gen.Indent();
            EmitLoopBody(ctx, pkg, gen, gf.Body, label);
            gen.EndBlock();
            EmitBreakLabel(gen, label);
            PopLoop();
            return;
        }

        gen.Write("for ");
        for (var i = 0; i < gf.VarNames.Count; i++)
        {
            if (i > 0) gen.Write(", ");
            gen.Write(ResolveName(ctx, pkg, gf.VarNames[i]));
        }
        gen.Write(" in ");
        if (gf.CallsIteratorMethod)
            EmitIteratorMethodCall(ctx, pkg, gen, gf.Iterators[0]);
        else if (gf.ChecksIteratorAtRuntime)
        {
            gen.Write(gen.GetIterateHelper() + "(");
            EmitExpr(ctx, pkg, gen, gf.Iterators[0]);
            gen.Write(")");
        }
        else
            EmitGenericForIterators(ctx, pkg, gen, gf.Iterators);
        gen.Write(" do");
        gen.NewLine();
        gen.Indent();
        EmitLoopBody(ctx, pkg, gen, gf.Body, label);
        gen.EndBlock();
        EmitBreakLabel(gen, label);
        PopLoop();
    }

    private void EmitBreakLabel(LuaGenerator gen, int label)
    {
        if (_neededBreakLabels.Remove(label) && gen.Features.HasGoto)
            gen.WriteLine($"::__break_{label}::");
    }

    private void EmitBreak(PassContext ctx, PackageContext pkg, LuaGenerator gen, BreakStmt bs)
    {
        if (bs.Depth <= 1 || !gen.Features.HasGoto)
        {
            gen.WriteLine("break");
            gen.WriteSemicolon();
            return;
        }

        var stack = _loopLabelStack.ToArray();
        var targetIdx = bs.Depth - 1;
        if (targetIdx < stack.Length)
        {
            var targetLabel = stack[targetIdx];
            _neededBreakLabels.Add(targetLabel);
            gen.WriteLine($"goto __break_{targetLabel}");
        }
        else
        {
            gen.WriteLine("break");
            gen.WriteSemicolon();
        }
    }

    private void EmitContinue(LuaGenerator gen)
    {
        if (!gen.Features.HasGoto)
        {
            gen.WriteLine("break");
            gen.WriteSemicolon();
            return;
        }

        if (_loopLabelStack.Count > 0)
            gen.WriteLine($"goto __continue_{_loopLabelStack.Peek()}");
    }


    private void EmitGuard(PassContext ctx, PackageContext pkg, LuaGenerator gen, GuardStmt gs)
    {
        gen.Write("if not (");
        EmitExpr(ctx, pkg, gen, gs.Condition);
        gen.Write(") then");
        gen.NewLine();
        gen.Indent();
        var ret = new ReturnStmt(NodeID.Invalid, TextSpan.Empty, []);
        if (gs.ElseExpr != null)
        {
            ret.Values.Add(gs.ElseExpr);
        }
        EmitReturn(ctx, pkg, gen, ret);
        gen.EndBlock();
    }


    /// <summary>
    /// Records a deferred call on the enclosing function's list. The function and its arguments
    /// are evaluated here, as Go does; a deferred block is a closure and sees its variables as they
    /// are when it runs.
    /// </summary>
    private void EmitDefer(PassContext ctx, PackageContext pkg, LuaGenerator gen, DeferStmt ds)
    {
        if (_deferDepth == 0) return;

        switch (ds.Call)
        {
            case FunctionCallExpr call:
                gen.Write($"{gen.GetDeferHelper()}(__defers, ");
                EmitExpr(ctx, pkg, gen, call.Callee);
                EmitDeferredArguments(ctx, pkg, gen, call.Arguments);
                gen.WriteLine(")");
                break;
            case MethodCallExpr { ExtensionTargetType: { } extensionTarget } extension:
                gen.Write($"{gen.GetDeferHelper()}(__defers, {extension.Extension?.LuaName ?? ExtensionFnName(ctx, extensionTarget, extension.MethodName.Name)}, ");
                EmitExpr(ctx, pkg, gen, extension.Object);
                EmitDeferredArguments(ctx, pkg, gen, extension.Arguments);
                gen.WriteLine(")");
                break;
            case MethodCallExpr method:
            {
                var receiver = gen.FreshTemp("__receiver");
                gen.Write($"do local {receiver} = ");
                EmitExpr(ctx, pkg, gen, method.Object);
                gen.Write($"; {gen.GetDeferHelper()}(__defers, {receiver}.{CalledMethodName(method)}, {receiver}");
                EmitDeferredArguments(ctx, pkg, gen, method.Arguments);
                gen.WriteLine(") end");
                break;
            }
            case null when ds.Block != null:
                gen.WriteLine("__defers[#__defers + 1] = function()");
                gen.Indent();
                EmitStmtList(ctx, pkg, gen, ds.Block);
                gen.Dedent();
                gen.WriteLine("end");
                break;
        }

        gen.WriteSemicolon();
    }

    private void EmitDeferredArguments(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Expr> arguments)
    {
        foreach (var argument in arguments)
        {
            gen.Write(", ");
            EmitExpr(ctx, pkg, gen, argument);
        }
    }

    private bool IsIpairsCall(PassContext ctx, PackageContext pkg, FunctionCallExpr call)
    {
        if (call.Callee is NameExpr nameExpr)
        {
            var resolved = ResolveName(ctx, pkg, nameExpr.Name);
            return resolved == "ipairs";
        }
        return false;
    }

    private void EmitReturn(PassContext ctx, PackageContext pkg, LuaGenerator gen, ReturnStmt ret)
    {
        var marker = _tryReturnHelper != null ? _tryReturnHelper + "_return" : null;
        if (ret.Values.Count == 0)
        {
            gen.WriteLine(marker != null ? "return " + marker : "return");
        }
        else
        {
            gen.Write(marker != null ? "return " + marker + ", " : "return ");
            EmitExprList(ctx, pkg, gen, ret.Values);
            gen.NewLine();
        }
        gen.WriteSemicolon();
    }

    #endregion

    #region Import / Export

    private void EmitImport(PassContext ctx, PackageContext pkg, LuaGenerator gen, ImportStmt import)
    {
        if (import.IsTypeOnly || import.IsTypeImport) return;

        var runtimeGlobal = RuntimeGlobalOf(ctx, import);
        var modExpr = runtimeGlobal ?? gen.EmitImport(ImportPathOf(ctx, import));
        var extensions = ImportedExtensions(ctx, import);

        switch (import.Kind)
        {
            case ImportKind.SideEffect when extensions.Count > 0:
            {
                var temp = gen.FreshTemp("_mod");
                gen.WriteLine($"local {temp} = {(runtimeGlobal != null ? $"assert({RequireGlobal(ctx, import)})" : modExpr)}");
                EmitExtensionBindings(gen, temp, extensions);
                break;
            }
            case ImportKind.SideEffect when runtimeGlobal != null:
                gen.WriteLine($"assert({RequireGlobal(ctx, import)})");
                break;
            case ImportKind.SideEffect:
                gen.WriteLine(modExpr);
                break;
            case ImportKind.Default:
            case ImportKind.Namespace:
            {
                var alias = import.Alias != null
                    ? ResolveName(ctx, pkg, import.Alias)
                    : import.Module.Name;
                gen.Write("local ");
                gen.Write(alias);
                gen.Write(" = ");
                gen.WriteLine(modExpr);
                EmitExtensionBindings(gen, alias, extensions);
                break;
            }
            case ImportKind.Named:
            {
                var runtimeSpecs = import.Specifiers.Where(spec => !IsCompileTimeOnly(ctx, pkg, spec)).ToList();
                if (runtimeSpecs.Count == 0 && extensions.Count == 0)
                {
                    gen.WriteLine(modExpr);
                    break;
                }

                var temp = gen.FreshTemp("_mod");
                gen.Write("local ");
                gen.Write(temp);
                gen.Write(" = ");
                gen.WriteLine(modExpr);

                foreach (var spec in runtimeSpecs)
                {
                    var localName = spec.Alias != null
                        ? ResolveName(ctx, pkg, spec.Alias)
                        : ResolveName(ctx, pkg, spec.Name);
                    gen.Write("local ");
                    gen.Write(localName);
                    gen.Write(" = ");
                    gen.Write(temp);
                    gen.Write(".");
                    gen.WriteLine(spec.Name.Name);
                }
                EmitExtensionBindings(gen, temp, extensions);
                break;
            }
        }
        gen.WriteSemicolon();
    }

    /// <summary>
    /// The global an import reads instead of loading a file, when it imports the root module of a
    /// package that declares a <c>runtime_global</c>. A missing global fails with the package's
    /// name rather than as an index of <c>nil</c> wherever an imported name is used.
    /// </summary>
    private string? RuntimeGlobalOf(PassContext ctx, ImportStmt import)
    {
        return ResolvedImport(ctx, import) is { RuntimeGlobal: not null, IsPackageRoot: true } ? RequireGlobal(ctx, import) : null;
    }

    private string RequireGlobal(PassContext ctx, ImportStmt import)
    {
        var resolved = ResolvedImport(ctx, import)!;
        var message = $"the package '{resolved.RuntimePackage}' is not loaded: the global {resolved.RuntimeGlobal} is missing";
        return $"{resolved.RuntimeGlobal} or error(\"{EscapeLuaString(message)}\", 0)";
    }

    /// <summary>
    /// The path an import loads its module by. With <c>import_path = "root"</c>, one of the
    /// project's own modules is named by its path from the source root, which the output mirrors,
    /// so every importer names it the same way; anything else keeps the path as written.
    /// </summary>
    private string ImportPathOf(PassContext ctx, ImportStmt import)
    {
        if (ctx.Config.Code.ImportPath != "root") return import.Module.Name;
        if (ResolvedImport(ctx, import) is not { Kind: ModuleKind.NebraSource, FilePath: { } filePath }) return import.Module.Name;

        var sourceRoot = Path.GetFullPath(Path.Combine(ctx.Config.ProjectRoot, ctx.Config.Source));
        var relative = Path.GetRelativePath(sourceRoot, Path.GetFullPath(filePath));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return import.Module.Name;

        return Path.ChangeExtension(relative, null).Replace(Path.DirectorySeparatorChar, '/');
    }

    private ResolvedModule? ResolvedImport(PassContext ctx, ImportStmt import)
    {
        return ctx.Cache.TryGetValue($"import_resolved:{_currentFile}:{import.Module.Name}", out var entry) ? entry as ResolvedModule : null;
    }

    /// <summary>
    /// Reports whether an imported name exists only at compile time. An interface has no runtime
    /// representation, so binding it would read a field the exporting module never wrote, and in a
    /// module that exports nothing else <c>require</c> returns <c>true</c> and the read fails
    /// outright. The module is still loaded, so its side effects are unaffected.
    /// </summary>
    private static bool IsCompileTimeOnly(PassContext ctx, PackageContext pkg, ImportSpecifier spec)
    {
        if (spec.Name.Sym == SymID.Invalid) return false;
        if (!pkg.Syms.GetByID(spec.Name.Sym, out var symbol)) return false;
        if (symbol.Kind == SymbolKind.Interface) return true;

        return ctx.Types.GetByID(symbol.Type, out var type) && type is InterfaceType;
    }

    #endregion

    #region Expressions

    private void EmitExprList(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Expr> exprs)
    {
        for (var i = 0; i < exprs.Count; i++)
        {
            if (i > 0) gen.Write(", ");
            EmitExpr(ctx, pkg, gen, exprs[i]);
        }
    }

    private void EmitExprStmt(PassContext ctx, PackageContext pkg, LuaGenerator gen, ExprStmt exprStmt)
    {
        if (exprStmt.Expression is IncDecExpr incDec)
        {
            EmitExpr(ctx, pkg, gen, incDec.Target);
            gen.Write(incDec.IsIncrement ? " = " : " = ");
            EmitExpr(ctx, pkg, gen, incDec.Target);
            gen.Write(incDec.IsIncrement ? " + 1" : " - 1");
            gen.NewLine();
            gen.WriteSemicolon();
            return;
        }

        if (StartsWithParenthesis(exprStmt.Expression)) gen.Write(";");
        EmitExpr(ctx, pkg, gen, exprStmt.Expression);
        gen.NewLine();
        gen.WriteSemicolon();
    }

    /// <summary>
    /// Whether the Lua of a statement opens with a parenthesis. Lua would read it as a call of
    /// whatever the previous statement ended with, so such a statement is started with <c>;</c>.
    /// </summary>
    private static bool StartsWithParenthesis(Expr expr)
    {
        return expr switch
        {
            ParenExpr => true,
            AwaitExpr { Expression: MethodCallExpr } => true,
            MethodCallExpr call => StartsWithParenthesis(call.Object),
            FunctionCallExpr call => StartsWithParenthesis(call.Callee),
            DotAccessExpr access => StartsWithParenthesis(access.Object),
            IndexAccessExpr access => StartsWithParenthesis(access.Object),
            _ => false,
        };
    }

    private void EmitExpr(PassContext ctx, PackageContext pkg, LuaGenerator gen, Expr expr)
    {
        switch (expr)
        {
            case NilLiteralExpr:
                gen.Write("nil");
                break;
            case BoolLiteralExpr b:
                gen.Write(b.Value ? "true" : "false");
                break;
            case NumberLiteralExpr n:
                gen.Write(n.Raw);
                break;
            case StringLiteralExpr s:
                EmitString(ctx, pkg, gen, s);
                break;
            case VarargExpr:
                gen.Write("...");
                break;
            case NameExpr { Enum: { } shorthandEnum, EnumMember: { } shorthandMember }:
                gen.Write(EnumMemberValue(shorthandEnum, shorthandMember));
                break;
            case NameExpr name:
                if (_overloadDispatchers.TryGetValue(name.Name.Sym, out var dispatcher))
                {
                    gen.Write(name.CallsResolvedOverload ? _overloadVariants[name.Name.Sym] : dispatcher);
                }
                else if (!TryEmitUnpack(pkg, gen, expr))
                {
                    gen.Write(ResolveName(ctx, pkg, name.Name));
                }
                break;
            case ParenExpr paren:
                gen.Write("(");
                if (!TryEmitFolded(gen, paren.Inner)) EmitExpr(ctx, pkg, gen, paren.Inner);
                gen.Write(")");
                break;
            case BinaryExpr bin:
                EmitBinary(ctx, pkg, gen, bin);
                break;
            case UnaryExpr un:
                EmitUnary(ctx, pkg, gen, un);
                break;
            case FunctionDefExpr fd:
                EmitFunctionDef(ctx, pkg, gen, fd);
                break;
            case DotAccessExpr dot:
                if (dot.IsOptional)
                {
                    EmitOptionalDotAccess(ctx, pkg, gen, dot);
                }
                else if (dot.Extension != null)
                {
                    EmitExtensionCall(ctx, pkg, gen, dot.Extension, dot.Object, []);
                }
                else if (!TryEmitUnpack(pkg, gen, expr))
                {
                    EmitExpr(ctx, pkg, gen, dot.Object);
                    gen.Write(".");
                    gen.Write(dot.OverloadName ?? dot.FieldName.Name);
                }
                break;
            case IndexAccessExpr idx:
                EmitIndexAccess(ctx, pkg, gen, idx);
                break;
            case FunctionCallExpr call:
                if (call.IsOptional)
                    EmitOptionalFunctionCall(ctx, pkg, gen, call);
                else
                    EmitFunctionCall(ctx, pkg, gen, call);
                break;
            case MethodCallExpr mc:
                EmitMethodCall(ctx, pkg, gen, mc);
                break;
            case TableConstructorExpr tc:
                EmitTableConstructor(ctx, pkg, gen, tc);
                break;
            case InterpolatedStringExpr interp:
                EmitInterpolatedString(ctx, pkg, gen, interp);
                break;
            case NonNilAssertExpr nna:
                EmitExpr(ctx, pkg, gen, nna.Inner);
                break;
            case IncDecExpr incDec:
                EmitIncDec(ctx, pkg, gen, incDec);
                break;
            case TypeCastExpr tcast:
                EmitExpr(ctx, pkg, gen, tcast.Inner);
                break;
            case TypeCheckExpr tchk:
                EmitTypeCheck(ctx, pkg, gen, tchk);
                break;
            case TypeOfExpr tof:
                EmitTypeOf(ctx, pkg, gen, tof);
                break;
            case InstanceOfExpr iof:
                EmitInstanceOf(ctx, pkg, gen, iof);
                break;
            case MatchExpr me:
                EmitMatchExpr(ctx, pkg, gen, me);
                break;
            case AwaitExpr aw:
                EmitAwaitExpr(ctx, pkg, gen, aw);
                break;
            case NewExpr ne:
                EmitNewExpr(ctx, pkg, gen, ne);
                break;
            case SuperCallExpr:
                break;
        }
    }

    private void EmitTypeCheck(PassContext ctx, PackageContext pkg, LuaGenerator gen, TypeCheckExpr tchk)
    {
        var targetID = tchk.TargetType.ResolvedType;
        if (!pkg.Types.GetByID(targetID, out var targetType))
        {
            gen.Write("false");
            return;
        }

        switch (targetType)
        {
            case { Kind: TypeKind.PrimitiveNil }:
                gen.Write("(");
                EmitExpr(ctx, pkg, gen, tchk.Inner);
                gen.Write(" == nil)");
                break;
            case { Kind: TypeKind.PrimitiveString }:
                EmitTypeOfCheck(ctx, pkg, gen, tchk.Inner, "string");
                break;
            case { Kind: TypeKind.PrimitiveNumber }:
                EmitTypeOfCheck(ctx, pkg, gen, tchk.Inner, "number");
                break;
            case { Kind: TypeKind.PrimitiveBool }:
                EmitTypeOfCheck(ctx, pkg, gen, tchk.Inner, "boolean");
                break;
            case { Kind: TypeKind.PrimitiveAny }:
                gen.Write("(");
                EmitExpr(ctx, pkg, gen, tchk.Inner);
                gen.Write(" ~= nil)");
                break;
            case TableArrayType:
            case TableMapType:
            case StructType:
                EmitTypeOfCheck(ctx, pkg, gen, tchk.Inner, "table");
                break;
            case FunctionType:
                EmitTypeOfCheck(ctx, pkg, gen, tchk.Inner, "function");
                break;
            case ClassType { NearestIsTemplate: { } isTemplate } templated:
                EmitIsTemplate(ctx, pkg, gen, isTemplate, tchk.Inner, PatternTypeName(ctx, pkg, tchk.TargetType, templated.Name));
                break;
            case ClassType { GenericDefinition: not null } instance when RuntimeArguments(instance.TypeArguments) is { } arguments:
            {
                gen.Write(gen.GetGenericClassCheckHelper() + "(");
                EmitExpr(ctx, pkg, gen, tchk.Inner);
                gen.Write($", {PatternTypeName(ctx, pkg, tchk.TargetType, instance.Name)}, \"{instance.Name}\", {arguments})");
                break;
            }
            case InterfaceType { IsContract: true } contract:
                EmitContractCheck(ctx, pkg, gen, contract, tchk.Inner);
                break;
            case InterfaceType { GenericDefinition: not null } instance when RuntimeArguments(instance.TypeArguments) is { } arguments:
            {
                gen.Write(gen.GetGenericInterfaceCheckHelper() + "(");
                EmitExpr(ctx, pkg, gen, tchk.Inner);
                gen.Write($", \"{instance.Name}\", {arguments})");
                break;
            }
            case ClassType ct:
            {
                gen.Write(gen.GetInstanceOfHelper());
                gen.Write("(");
                EmitExpr(ctx, pkg, gen, tchk.Inner);
                gen.Write(", ");
                gen.Write(PatternTypeName(ctx, pkg, tchk.TargetType, ct.Name));
                gen.Write(")");
                break;
            }
            case InterfaceType it:
            {
                gen.Write(gen.GetImplementsHelper());
                gen.Write("(");
                EmitExpr(ctx, pkg, gen, tchk.Inner);
                gen.Write(", \"");
                gen.Write(it.Name);
                gen.Write("\")");
                break;
            }
            case EnumType et:
            {
                var enumName = PatternTypeName(ctx, pkg, tchk.TargetType, et.Name);
                gen.Write("(function() local __v = (");
                EmitExpr(ctx, pkg, gen, tchk.Inner);
                gen.Write("); for _, __m in pairs(");
                gen.Write(enumName);
                gen.Write(") do if __m == __v then return true end end return false end)()");
                break;
            }
            default:
                gen.Write("false");
                break;
        }
    }

    private void EmitTypeOf(PassContext ctx, PackageContext pkg, LuaGenerator gen, TypeOfExpr tof)
    {
        var innerType = tof.Inner.Type;
        if (innerType != TypID.Invalid && pkg.Types.GetByID(innerType, out var t))
        {
            switch (t)
            {
                case { Kind: TypeKind.PrimitiveString }:
                    gen.Write("\"string\"");
                    return;
                case { Kind: TypeKind.PrimitiveNumber }:
                    gen.Write("\"number\"");
                    return;
                case { Kind: TypeKind.PrimitiveBool }:
                    gen.Write("\"boolean\"");
                    return;
                case { Kind: TypeKind.PrimitiveNil }:
                    gen.Write("\"nil\"");
                    return;
                case ClassType ct:
                    gen.Write("\"");
                    gen.Write(ct.Name);
                    gen.Write("\"");
                    return;
                case EnumType et:
                    gen.Write("\"");
                    gen.Write(et.Name);
                    gen.Write("\"");
                    return;
            }
        }

        var helper = gen.GetTypeOfHelper();
        gen.Write(helper);
        gen.Write("(");
        EmitExpr(ctx, pkg, gen, tof.Inner);
        gen.Write(")");
    }

    /// <summary>
    /// The emitted name of the type a match arm tests against. The declaration's own name is only
    /// correct while nothing renames it, so the reference is resolved the same way every other use
    /// of that type is.
    /// </summary>
    private string PatternTypeName(PassContext ctx, PackageContext pkg, TypeRef? typeRef, string fallback)
    {
        if (typeRef is NamedTypeRef named) return ResolveName(ctx, pkg, named.Name);
        if (typeRef is GenericTypeRef generic) return ResolveName(ctx, pkg, generic.Name);

        return fallback;
    }

    private void EmitInstanceOf(PassContext ctx, PackageContext pkg, LuaGenerator gen, InstanceOfExpr iof)
    {
        if (iof.TargetType != null && ctx.Types.GetByID(iof.TargetType.ResolvedType, out var templatedTarget)
            && templatedTarget is ClassType { NearestIsTemplate: { } isTemplate })
        {
            EmitIsTemplate(ctx, pkg, gen, isTemplate, iof.Inner, ResolveName(ctx, pkg, iof.ClassName));
            return;
        }

        if (iof.TargetType != null && ctx.Types.GetByID(iof.TargetType.ResolvedType, out var target)
            && target is ClassType { GenericDefinition: not null } instance && RuntimeArguments(instance.TypeArguments) is { } arguments)
        {
            gen.Write(gen.GetGenericClassCheckHelper() + "(");
            EmitExpr(ctx, pkg, gen, iof.Inner);
            gen.Write($", {ResolveName(ctx, pkg, iof.ClassName)}, \"{instance.Name}\", {arguments})");
            return;
        }

        var helper = gen.GetInstanceOfHelper();
        gen.Write(helper);
        gen.Write("(");
        EmitExpr(ctx, pkg, gen, iof.Inner);
        gen.Write(", ");
        gen.Write(ResolveName(ctx, pkg, iof.ClassName));
        gen.Write(")");
    }

    private void EmitTypeOfCheck(PassContext ctx, PackageContext pkg, LuaGenerator gen, Expr inner, string typeName)
    {
        gen.Write("(type(");
        EmitExpr(ctx, pkg, gen, inner);
        gen.Write(") == \"");
        gen.Write(typeName);
        gen.Write("\")");
    }

    private void EmitMatchStmt(PassContext ctx, PackageContext pkg, LuaGenerator gen, MatchStmt ms)
    {
        var scrutineeTemp = gen.FreshTemp();
        gen.Write("local " + scrutineeTemp + " = ");
        EmitExpr(ctx, pkg, gen, ms.Scrutinee);
        gen.NewLine();

        var openedIf = false;
        for (var i = 0; i < ms.Arms.Count; i++)
        {
            var arm = ms.Arms[i];
            var isWildcard = arm.Pattern.Kind == MatchPatternKind.Wildcard;
            var unconditionalWildcard = isWildcard && arm.Guard == null;

            if (unconditionalWildcard && !openedIf)
            {
                gen.Write("do");
                gen.NewLine();
                gen.Indent();
                EmitStmtList(ctx, pkg, gen, arm.Body);
                gen.Dedent();
                gen.WriteLine("end");
                return;
            }

            if (unconditionalWildcard)
            {
                gen.WriteLine("else");
                gen.Indent();
            }
            else
            {
                gen.Write(openedIf ? "elseif " : "if ");
                openedIf = true;
                if (isWildcard)
                {
                    gen.Write("(");
                    EmitExpr(ctx, pkg, gen, arm.Guard!);
                    gen.Write(")");
                }
                else
                {
                    EmitMatchCondition(ctx, pkg, gen, arm.Pattern, scrutineeTemp);
                    if (arm.Guard != null)
                    {
                        gen.Write(" and (");
                        EmitExpr(ctx, pkg, gen, arm.Guard);
                        gen.Write(")");
                    }
                }
                gen.Write(" then");
                gen.NewLine();
                gen.Indent();
            }

            if (arm.Pattern.Kind == MatchPatternKind.TypeBinding && arm.Pattern.Binding != null)
            {
                gen.Write("local " + ResolveName(ctx, pkg, arm.Pattern.Binding) + " = " + scrutineeTemp);
                gen.NewLine();
            }

            EmitStmtList(ctx, pkg, gen, arm.Body);
            gen.Dedent();
        }
        if (openedIf) gen.WriteLine("end");
    }

    private void EmitMatchExpr(PassContext ctx, PackageContext pkg, LuaGenerator gen, MatchExpr me)
    {
        var scrutineeTemp = gen.FreshTemp("_ms");

        gen.Write("(function()");
        gen.NewLine();
        gen.Indent();
        gen.Write("local " + scrutineeTemp + " = ");
        EmitExpr(ctx, pkg, gen, me.Scrutinee);
        gen.NewLine();

        var openedIf = false;
        for (var i = 0; i < me.Arms.Count; i++)
        {
            var arm = me.Arms[i];
            var isWildcard = arm.Pattern.Kind == MatchPatternKind.Wildcard;
            var unconditionalWildcard = isWildcard && arm.Guard == null;

            if (unconditionalWildcard && !openedIf)
            {
                gen.Write("do");
                gen.NewLine();
                gen.Indent();
            }
            else if (unconditionalWildcard)
            {
                gen.WriteLine("else");
                gen.Indent();
            }
            else
            {
                gen.Write(openedIf ? "elseif " : "if ");
                openedIf = true;
                if (isWildcard)
                {
                    gen.Write("(");
                    EmitExpr(ctx, pkg, gen, arm.Guard!);
                    gen.Write(")");
                }
                else
                {
                    EmitMatchCondition(ctx, pkg, gen, arm.Pattern, scrutineeTemp);
                    if (arm.Guard != null)
                    {
                        gen.Write(" and (");
                        EmitExpr(ctx, pkg, gen, arm.Guard);
                        gen.Write(")");
                    }
                }
                gen.Write(" then");
                gen.NewLine();
                gen.Indent();
            }

            if (arm.Pattern.Kind == MatchPatternKind.TypeBinding && arm.Pattern.Binding != null)
            {
                gen.Write("local " + ResolveName(ctx, pkg, arm.Pattern.Binding) + " = " + scrutineeTemp);
                gen.NewLine();
            }

            gen.Write("return ");
            EmitExpr(ctx, pkg, gen, arm.Value);
            gen.NewLine();
            gen.Dedent();
        }

        gen.WriteLine("end");
        gen.Dedent();
        gen.Write("end)()");
    }

    private void EmitMatchCondition(PassContext ctx, PackageContext pkg, LuaGenerator gen, MatchPattern pattern, string scrutineeTemp)
    {
        switch (pattern.Kind)
        {
            case MatchPatternKind.Value:
                gen.Write(scrutineeTemp + " == ");
                EmitExpr(ctx, pkg, gen, pattern.ValueExpr!);
                break;
            case MatchPatternKind.TypeBinding:
                if (pattern.TypeRef != null)
                {
                    var typeId = pattern.TypeRef.ResolvedType;
                    if (pkg.Types.GetByID(typeId, out var t))
                    {
                        switch (t)
                        {
                            case ClassType { NearestIsTemplate: { } isTemplate } templated:
                                gen.Write(ExpandIsTemplate(isTemplate, scrutineeTemp, PatternTypeName(ctx, pkg, pattern.TypeRef, templated.Name)));
                                break;
                            case ClassType ct:
                            {
                                var helper = gen.GetInstanceOfHelper();
                                gen.Write(helper + "(" + scrutineeTemp + ", "
                                          + PatternTypeName(ctx, pkg, pattern.TypeRef, ct.Name) + ")");
                                break;
                            }
                            case InterfaceType { IsContract: true } contract:
                                gen.Write($"{ContractValidator(gen, contract)}({scrutineeTemp})");
                                break;
                            case InterfaceType it:
                            {
                                var helper = gen.GetInstanceOfHelper();
                                gen.Write(helper + "(" + scrutineeTemp + ", "
                                          + PatternTypeName(ctx, pkg, pattern.TypeRef, it.Name) + ")");
                                break;
                            }
                            default:
                            {
                                var luaType = t.Kind switch
                                {
                                    TypeKind.PrimitiveString => "string",
                                    TypeKind.PrimitiveNumber => "number",
                                    TypeKind.PrimitiveBool => "boolean",
                                    TypeKind.PrimitiveNil => "nil",
                                    TypeKind.TableArray or TypeKind.TableMap or TypeKind.Struct => "table",
                                    TypeKind.Function => "function",
                                    _ => "any"
                                };
                                if (luaType == "nil")
                                    gen.Write(scrutineeTemp + " == nil");
                                else if (luaType == "any")
                                    gen.Write(scrutineeTemp + " ~= nil");
                                else
                                    gen.Write("type(" + scrutineeTemp + ") == \"" + luaType + "\"");
                                break;
                            }
                        }
                    }
                    else
                    {
                        gen.Write("true");
                    }
                }
                else
                {
                    gen.Write("true");
                }
                break;
            case MatchPatternKind.Wildcard:
                gen.Write("true");
                break;
        }
    }

    private void EmitInterpolatedString(PassContext ctx, PackageContext pkg, LuaGenerator gen, InterpolatedStringExpr interp)
    {
        if (interp.Parts.Count == 0)
        {
            gen.Write("\"\"");
            return;
        }

        var first = true;
        foreach (var part in interp.Parts)
        {
            if (!first) gen.Write(" .. ");
            first = false;

            switch (part)
            {
                case InterpTextPart text:
                    gen.Write("\"");
                    gen.Write(EscapeLuaString(text.Text));
                    gen.Write("\"");
                    break;
                case InterpExprPart exprPart:
                    if (exprPart.Expression is StringLiteralExpr)
                    {
                        EmitExpr(ctx, pkg, gen, exprPart.Expression);
                    }
                    else
                    {
                        gen.Write("tostring(");
                        EmitExpr(ctx, pkg, gen, exprPart.Expression);
                        gen.Write(")");
                    }
                    break;
            }
        }
    }

    private void EmitString(PassContext ctx, PackageContext pkg, LuaGenerator gen, StringLiteralExpr s)
    {
        gen.Write("\"");
        gen.Write(EscapeLuaString(s.Value));
        gen.Write("\"");
    }

    private void EmitBinary(PassContext ctx, PackageContext pkg, LuaGenerator gen, BinaryExpr bin)
    {
        if (bin.IsRange)
        {
            gen.Write(gen.GetRangeHelper() + "(");
            EmitExpr(ctx, pkg, gen, bin.Left);
            gen.Write(", ");
            EmitExpr(ctx, pkg, gen, bin.Right);
            gen.Write(")");
            return;
        }

        if (bin.Extension != null)
        {
            EmitExtensionCall(ctx, pkg, gen, bin.Extension, bin.Left, [bin.Right]);
            return;
        }

        if (TryEmitFolded(gen, bin)) return;

        if (bin.Op == BinaryOp.NilCoalesce)
        {
            EmitNilCoalesce(ctx, pkg, gen, bin);
            return;
        }

        var isConcatOp = gen.IsConfiguredConcatOp(bin.Op) && bin.Op != BinaryOp.Concat;
        var isStringContext = isConcatOp && IsStringTyped(ctx, pkg, bin);

        if (isStringContext)
        {
            var helper = gen.GetConcatHelper();
            gen.Write(helper);
            gen.Write("(");
            EmitExpr(ctx, pkg, gen, bin.Left);
            gen.Write(", ");
            EmitExpr(ctx, pkg, gen, bin.Right);
            gen.Write(")");
            return;
        }

        if (bin.Op == BinaryOp.FloorDiv && !gen.Features.HasFloorDiv)
        {
            var helper = gen.GetFloorDivHelper();
            gen.Write(helper);
            gen.Write("(");
            EmitExpr(ctx, pkg, gen, bin.Left);
            gen.Write(", ");
            EmitExpr(ctx, pkg, gen, bin.Right);
            gen.Write(")");
            return;
        }

        if (gen.IsBitwiseOp(bin.Op) && gen.Features.BitwiseStyle == BitwiseStyle.BitLib)
        {
            gen.Write(gen.BinaryOpToLua(bin.Op));
            gen.Write("(");
            EmitExpr(ctx, pkg, gen, bin.Left);
            gen.Write(", ");
            EmitExpr(ctx, pkg, gen, bin.Right);
            gen.Write(")");
            return;
        }

        if (gen.IsBitwiseOp(bin.Op) && !gen.Features.HasBitwise)
            return;

        EmitExpr(ctx, pkg, gen, bin.Left);
        gen.Write(" ");
        gen.Write(gen.BinaryOpToLua(bin.Op));
        gen.Write(" ");
        EmitExpr(ctx, pkg, gen, bin.Right);
    }

    private void EmitOptionalDotAccess(PassContext ctx, PackageContext pkg, LuaGenerator gen, DotAccessExpr dot)
    {
        gen.Write("(function() local __v = (");
        EmitExpr(ctx, pkg, gen, dot.Object);
        gen.Write("); if __v == nil then return nil else return ");
        gen.Write(dot.Extension != null ? $"{dot.Extension.LuaName}(__v)" : "__v." + dot.FieldName.Name);
        gen.Write(" end end)()");
    }

    private void EmitOptionalFunctionCall(PassContext ctx, PackageContext pkg, LuaGenerator gen, FunctionCallExpr call)
    {
        gen.Write("(function() local __f = (");
        EmitExpr(ctx, pkg, gen, call.Callee);
        gen.Write(call.Extension != null
            ? $"); if __f == nil then return nil else return {call.Extension.LuaName}(__f" + (call.Arguments.Count > 0 ? ", " : "")
            : "); if __f == nil then return nil else return __f(");
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            if (i > 0) gen.Write(", ");
            EmitExpr(ctx, pkg, gen, call.Arguments[i]);
        }
        gen.Write(") end end)()");
    }

    private void EmitNilCoalesce(PassContext ctx, PackageContext pkg, LuaGenerator gen, BinaryExpr bin)
    {
        gen.Write("(function() local __v = (");
        EmitExpr(ctx, pkg, gen, bin.Left);
        gen.Write("); if __v ~= nil then return __v else return (");
        EmitExpr(ctx, pkg, gen, bin.Right);
        gen.Write(") end end)()");
    }

    private void EmitUnary(PassContext ctx, PackageContext pkg, LuaGenerator gen, UnaryExpr un)
    {
        if (un.Extension != null)
        {
            EmitExtensionCall(ctx, pkg, gen, un.Extension, un.Operand, []);
            return;
        }

        if (TryEmitFolded(gen, un)) return;

        if (un.Op == UnaryOp.BitwiseNot && gen.Features.BitwiseStyle == BitwiseStyle.BitLib)
        {
            gen.Write("bit.bnot(");
            EmitExpr(ctx, pkg, gen, un.Operand);
            gen.Write(")");
            return;
        }

        if (un.Op == UnaryOp.BitwiseNot && !gen.Features.HasBitwise)
            return;

        gen.Write(gen.UnaryOpToLua(un.Op));
        var needsParens = un.Operand is BinaryExpr or UnaryExpr;
        if (needsParens) gen.Write("(");
        EmitExpr(ctx, pkg, gen, un.Operand);
        if (needsParens) gen.Write(")");
    }

    private void EmitIncDec(PassContext ctx, PackageContext pkg, LuaGenerator gen, IncDecExpr incDec)
    {
        var helper = gen.GetIncDecHelper(incDec.IsPre, incDec.IsIncrement);
        gen.Write(helper);
        gen.Write("(function() return ");
        EmitExpr(ctx, pkg, gen, incDec.Target);
        gen.Write(" end, function(__v) ");
        EmitExpr(ctx, pkg, gen, incDec.Target);
        gen.Write(" = __v end)");
    }

    private void EmitFunctionDef(PassContext ctx, PackageContext pkg, LuaGenerator gen, FunctionDefExpr fd)
    {
        gen.Write("function(");
        EmitParamList(ctx, pkg, gen, fd.Parameters);
        if (fd.IsAsync)
        {
            if (fd.Parameters.Count > 0) gen.Write(", ");
            gen.Write("__done");
        }
        gen.Write(")");
        gen.NewLine();
        gen.Indent();
        EmitFuncBodyContent(ctx, pkg, gen, fd.Parameters, fd.Body, fd.ReturnStmt, fd.IsAsync);
        gen.Dedent();
        gen.Write("end");
    }

    private void EmitAwaitExpr(PassContext ctx, PackageContext pkg, LuaGenerator gen, AwaitExpr aw)
    {
        var inner = aw.Expression;
        if (inner is FunctionCallExpr fc)
        {
            gen.Write("coroutine.yield({");
            EmitExpr(ctx, pkg, gen, fc.Callee);
            foreach (var arg in fc.Arguments)
            {
                gen.Write(", ");
                EmitExpr(ctx, pkg, gen, arg);
            }
            var passed = PadToCallback(gen, aw, fc.Arguments.Count);
            gen.Write($", n = {passed + 1}");
            gen.Write("})");
        }
        else if (inner is MethodCallExpr mc)
        {
            var tmp = gen.FreshTemp("_t");
            gen.Write($"(function() local {tmp} = ");
            EmitExpr(ctx, pkg, gen, mc.Object);
            var method = mc.IsSuper ? $"{_superClassName}.{CalledMethodName(mc)}" : $"{tmp}.{CalledMethodName(mc)}";
            gen.Write($"; return coroutine.yield({{{method}, {tmp}");
            foreach (var arg in mc.Arguments)
            {
                gen.Write(", ");
                EmitExpr(ctx, pkg, gen, arg);
            }
            var passed = PadToCallback(gen, aw, mc.Arguments.Count);
            gen.Write($", n = {passed + 2}");
            gen.Write("}) end)()");
        }
        else
        {
            gen.Write("coroutine.yield({");
            EmitExpr(ctx, pkg, gen, inner);
            gen.Write(", n = 1})");
        }
    }

    /// <summary>
    /// Passes <c>nil</c> for the optional arguments an awaited call leaves out before its callback,
    /// and returns how many arguments precede the callback.
    /// </summary>
    private static int PadToCallback(LuaGenerator gen, AwaitExpr aw, int written)
    {
        for (var i = written; i < aw.CallbackPosition; i++)
            gen.Write(", nil");
        return Math.Max(written, aw.CallbackPosition);
    }

    private void EmitIndexAccess(PassContext ctx, PackageContext pkg, LuaGenerator gen, IndexAccessExpr idx)
    {
        if (idx.Extension != null)
        {
            EmitExtensionCall(ctx, pkg, gen, idx.Extension, idx.Object, [idx.Index]);
            return;
        }

        EmitExpr(ctx, pkg, gen, idx.Object);
        gen.Write("[");
        if (gen.IndexBase != 1 && !idx.UsesIndexOperator && MayBeNumber(ctx, pkg, idx.Index))
        {
            gen.Write(gen.AdjustIndex(ExprToInline(ctx, pkg, gen, idx.Index)));
        }
        else
        {
            EmitExpr(ctx, pkg, gen, idx.Index);
        }
        gen.Write("]");
    }

    /// <summary>
    /// Whether a key may be a number, which the configured index base shifts. A key whose type
    /// excludes numbers, such as the string in <c>t["name"]</c>, is written as it is.
    /// </summary>
    private static bool MayBeNumber(PassContext ctx, PackageContext pkg, Expr key)
    {
        return !pkg.Types.GetByID(key.Type, out var type) || MayBeNumber(pkg, type);
    }

    /// <summary>
    /// Whether a key of <paramref name="type"/> may be a number at runtime, so a list index needs
    /// shifting: a string literal or <c>keyof</c> never is, and a type parameter is what its bound is.
    /// </summary>
    private static bool MayBeNumber(PackageContext pkg, IR.Type type)
    {
        return type switch
        {
            UnionType union => union.Types.Any(member => MayBeNumber(pkg, member)),
            LiteralType literal => literal.BaseType.Kind == TypeKind.PrimitiveNumber,
            KeyOfType => false,
            TypeParameterType { ExtendsBound: { } bound } when pkg.Types.GetByID(bound, out var boundType) => MayBeNumber(pkg, boundType),
            _ => type.Kind is TypeKind.PrimitiveNumber or TypeKind.PrimitiveAny or TypeKind.TypeParameter or TypeKind.Enum or TypeKind.IndexedAccess,
        };
    }

    private void EmitFunctionCall(PassContext ctx, PackageContext pkg, LuaGenerator gen, FunctionCallExpr call)
    {
        if (TryEmitReflectOf(ctx, pkg, gen, call)) return;
        if (call.Extension != null)
        {
            EmitExtensionCall(ctx, pkg, gen, call.Extension, call.Callee, call.Arguments);
            return;
        }

        EmitExpr(ctx, pkg, gen, call.Callee);
        gen.Write("(");
        EmitExprList(ctx, pkg, gen, call.Arguments);
        gen.Write(")");
    }

    private void EmitMethodCall(PassContext ctx, PackageContext pkg, LuaGenerator gen, MethodCallExpr mc)
    {
        // Extension method: lower `receiver:m(args)` to `__ext_<type>_m(receiver, args)`.
        if (mc.ExtensionTargetType is { } extTarget)
        {
            gen.Write(mc.Extension?.LuaName ?? ExtensionFnName(ctx, extTarget, mc.MethodName.Name));
            gen.Write("(");
            EmitExpr(ctx, pkg, gen, mc.Object);
            if (mc.Arguments.Count > 0)
            {
                gen.Write(", ");
                EmitExprList(ctx, pkg, gen, mc.Arguments);
            }
            gen.Write(")");
            return;
        }

        if (mc.IsSuper && CallsHostMethod(mc))
        {
            EmitExpr(ctx, pkg, gen, mc.Object);
            gen.Write($".Super:{CalledMethodName(mc)}(");
            EmitExprList(ctx, pkg, gen, mc.Arguments);
            gen.Write(")");
            return;
        }

        if (mc.IsSuper)
        {
            gen.Write($"{_superClassName}.{CalledMethodName(mc)}(");
            EmitExpr(ctx, pkg, gen, mc.Object);
            if (mc.Arguments.Count > 0)
            {
                gen.Write(", ");
                EmitExprList(ctx, pkg, gen, mc.Arguments);
            }
            gen.Write(")");
            return;
        }

        EmitExpr(ctx, pkg, gen, mc.Object);
        gen.Write(":");
        gen.Write(CalledMethodName(mc));
        gen.Write("(");
        EmitExprList(ctx, pkg, gen, mc.Arguments);
        gen.Write(")");
    }

    /// <summary>The method a call reaches: the overload it picked at compile time, or the method's plain name.</summary>
    private static string CalledMethodName(MethodCallExpr call) => call.OverloadName ?? call.MethodName.Name;

    private static string ExtensionFnName(PassContext ctx, TypID targetType, string method)
    {
        ctx.Types.GetByID(targetType, out var t);
        var key = t != null ? t.PortableKey : targetType.ToString();
        var sanitized = new string(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return $"__ext_{sanitized}_{method}";
    }

    // A valid extend target is a resolved, concrete type — not a parse-error null or bare `any`.
    private static bool IsExtendableTarget(PassContext ctx, ExtendDecl ed)
        => ed.TargetType != null
           && ed.TargetType.ResolvedType != TypID.Invalid
           && ed.TargetType.ResolvedType != ctx.Types.PrimAny.ID;

    /// <summary>
    /// Emits the members of an <c>extend</c> block as local functions under their generated names.
    /// At the top level they assign the locals declared before the first statement, so a use above
    /// the block reaches them; an exported block also hands them to its importers through the
    /// module's export table.
    /// </summary>
    private void EmitExtendDecl(PassContext ctx, PackageContext pkg, LuaGenerator gen, ExtendDecl ed)
    {
        if (ed.IsDeclare || !IsExtendableTarget(ctx, ed)) return;
        foreach (var m in ed.Methods.Where(member => member.LuaName.Length > 0))
        {
            gen.Write(IsHoisted(m.LuaName) ? "function " : "local function ");
            gen.Write(m.LuaName);
            gen.Write("(self");
            if (m.Parameters.Count > 0)
            {
                gen.Write(", ");
                EmitParamList(ctx, pkg, gen, m.Parameters);
            }
            EmitAsyncDoneParam(gen, m.IsAsync, true);
            gen.Write(")");
            gen.NewLine();
            gen.Indent();
            EmitFuncBodyContent(ctx, pkg, gen, m.Parameters, m.Body, m.ReturnStmt, m.IsAsync);
            gen.Dedent();
            gen.WriteLine("end");
            gen.WriteSemicolon();
        }
    }

    /// <summary>
    /// Emits a use of an extension getter or operator: a call of its function with
    /// <paramref name="receiver"/> as <c>self</c>, followed by <paramref name="arguments"/>.
    /// </summary>
    private void EmitExtensionCall(PassContext ctx, PackageContext pkg, LuaGenerator gen, ExtensionMethodNode extension,
        Expr receiver, List<Expr> arguments)
    {
        gen.Write(extension.LuaName);
        gen.Write("(");
        EmitExpr(ctx, pkg, gen, receiver);
        foreach (var argument in arguments)
        {
            gen.Write(", ");
            EmitExpr(ctx, pkg, gen, argument);
        }
        gen.Write(")");
    }

    /// <summary>
    /// The extensions this file uses that <paramref name="import"/> brings in, each named once per
    /// file, which the import binds to locals of the same name.
    /// </summary>
    private List<ExtensionMethodNode> ImportedExtensions(PassContext ctx, ImportStmt import)
    {
        if (_currentFile == null || import.IsTypeOnly || import.IsTypeImport) return [];
        if (!ctx.Cache.TryGetValue(InferTypesPass.UsedExtensionsKey(_currentFile), out var cached) || cached is not HashSet<ExtensionMethodNode> used) return [];

        var resolved = ResolvedImport(ctx, import);
        return used
            .Where(member => !member.IsGlobal && member.DeclaringFile != null
                && !string.Equals(Path.GetFullPath(member.DeclaringFile), Path.GetFullPath(_currentFile), StringComparison.Ordinal)
                && InferTypesPass.ExtensionComesFrom(member, import, resolved)
                && _boundExtensions.Add(member.LuaName))
            .OrderBy(member => member.LuaName, StringComparer.Ordinal)
            .ToList();
    }

    private static void EmitExtensionBindings(LuaGenerator gen, string module, List<ExtensionMethodNode> extensions)
    {
        foreach (var extension in extensions)
            gen.WriteLine($"local {extension.LuaName} = {module}.{extension.LuaName}");
    }

    private void EmitTableConstructor(PassContext ctx, PackageContext pkg, LuaGenerator gen, TableConstructorExpr tc)
    {
        if (tc.Fields.Count == 0)
        {
            gen.Write("{}");
            return;
        }

        gen.Write("{");
        if (!gen.Minify) gen.NewLine();
        gen.Indent();

        for (var i = 0; i < tc.Fields.Count; i++)
        {
            var field = tc.Fields[i];
            switch (field.Kind)
            {
                case TableFieldKind.Named:
                    gen.Write(field.Name!.Name);
                    gen.Write(" = ");
                    EmitExpr(ctx, pkg, gen, field.Value);
                    break;
                case TableFieldKind.Bracket:
                    gen.Write("[");
                    if (gen.IndexBase != 1 && MayBeNumber(ctx, pkg, field.Key!))
                        gen.Write(gen.AdjustIndex(ExprToInline(ctx, pkg, gen, field.Key!)));
                    else
                        EmitExpr(ctx, pkg, gen, field.Key!);
                    gen.Write("] = ");
                    EmitExpr(ctx, pkg, gen, field.Value);
                    break;
                case TableFieldKind.Positional:
                    EmitExpr(ctx, pkg, gen, field.Value);
                    break;
            }

            if (i < tc.Fields.Count - 1)
                gen.Write(",");
            if (!gen.Minify) gen.NewLine();
        }

        gen.Dedent();
        gen.Write("}");
    }

    #endregion

    #region Helpers

    private void EmitParamList(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Parameter> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0) gen.Write(", ");
            var param = parameters[i];
            if (param.IsVararg)
            {
                gen.Write("...");
            }
            else
            {
                gen.Write(ResolveName(ctx, pkg, param.Name));
            }
        }
    }

    private void EmitNamedVarargPreamble(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Parameter> parameters)
    {
        var vararg = parameters.FirstOrDefault(p => p.IsVararg);
        if (vararg == null || vararg.Name.Name == "...") return;
        gen.Write("local ");
        gen.Write(ResolveName(ctx, pkg, vararg.Name));
        gen.WriteLine(" = {...}");
        gen.WriteSemicolon();
    }

    private void EmitDefaultParamPreamble(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Parameter> parameters)
    {
        foreach (var param in parameters)
        {
            if (param.IsVararg || param.DefaultValue == null) continue;
            var name = ResolveName(ctx, pkg, param.Name);
            gen.Write("if ");
            gen.Write(name);
            gen.Write(" == nil then ");
            gen.Write(name);
            gen.Write(" = ");
            EmitExpr(ctx, pkg, gen, param.DefaultValue);
            gen.WriteLine(" end");
            gen.WriteSemicolon();
        }
    }

    private static void EmitAsyncDoneParam(LuaGenerator gen, bool isAsync, bool hasPrecedingParams)
    {
        if (!isAsync) return;
        if (hasPrecedingParams) gen.Write(", ");
        gen.Write("__done");
    }

    private void EmitFuncBodyContent(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Parameter> parameters, List<Stmt> body, ReturnStmt? returnStmt, bool isAsync)
    {
        var enclosingTryReturn = _tryReturnHelper;
        _tryReturnHelper = null;
        EmitFuncBodyContentCore(ctx, pkg, gen, parameters, body, returnStmt, isAsync);
        _tryReturnHelper = enclosingTryReturn;
    }

    private void EmitFuncBodyContentCore(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Parameter> parameters, List<Stmt> body, ReturnStmt? returnStmt, bool isAsync)
    {
        if (isAsync)
        {
            var driverName = gen.GetAsyncDriverHelper();
            gen.WriteLine("local __co = coroutine.create(function()");
            gen.Indent();
            EmitDefaultParamPreamble(ctx, pkg, gen, parameters);
            EmitNamedVarargPreamble(ctx, pkg, gen, parameters);
            EmitBodyStatements(ctx, pkg, gen, parameters, body, returnStmt);
            gen.EndBlock("end)");
            gen.WriteLine($"{driverName}(__co, __done)");
        }
        else
        {
            EmitDefaultParamPreamble(ctx, pkg, gen, parameters);
            EmitNamedVarargPreamble(ctx, pkg, gen, parameters);
            EmitBodyStatements(ctx, pkg, gen, parameters, body, returnStmt);
        }
    }

    /// <summary>
    /// Emits the statements of a function body. A body with a <c>defer</c> keeps a list of the
    /// calls deferred while it runs and runs it in reverse on every way out: the statements run in
    /// a <c>pcall</c>, so the list also runs when they raise an error, which is raised again after.
    /// </summary>
    private void EmitBodyStatements(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Parameter> parameters, List<Stmt> body, ReturnStmt? returnStmt)
    {
        if (!DeferStmt.AnyIn(body))
        {
            EmitStmtList(ctx, pkg, gen, body);
            if (returnStmt != null) EmitReturn(ctx, pkg, gen, returnStmt);
            return;
        }

        var vararg = parameters.Any(parameter => parameter.IsVararg) ? "..." : "";
        gen.WriteLine("local __defers = {}");
        gen.WriteLine($"return {gen.GetDeferFinishHelper()}(__defers, pcall(function({vararg})");
        gen.Indent();
        _deferDepth++;
        EmitStmtList(ctx, pkg, gen, body);
        if (returnStmt != null) EmitReturn(ctx, pkg, gen, returnStmt);
        _deferDepth--;
        gen.Dedent();
        gen.WriteLine(vararg.Length > 0 ? "end, ...))" : "end))");
    }

    private void EmitNamePath(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<NameRef> namePath)
    {
        for (var i = 0; i < namePath.Count; i++)
        {
            if (i > 0) gen.Write(".");
            if (i == 0)
                gen.Write(ResolveName(ctx, pkg, namePath[i]));
            else
                gen.Write(namePath[i].Name);
        }
    }

    private string ResolveName(PassContext ctx, PackageContext pkg, NameRef nameRef)
    {
        if (nameRef.Sym == SymID.Invalid)
            return nameRef.Name;

        if (StaticMemberTable(pkg, nameRef.Sym) is { } table)
            return $"{table}.{nameRef.Name}";

        if (ctx.Names.GetMangled(nameRef.Sym, out var mangled))
            return mangled;

        if (ctx.Names.GetOriginal(nameRef.Sym, out var original))
            return original;

        return nameRef.Name;
    }

    /// <summary>
    /// The emitted name of the class table a static member is stored on, when <paramref name="sym"/>
    /// is a static member reached by its bare name inside its class.
    /// </summary>
    private string? StaticMemberTable(PackageContext pkg, SymID sym)
    {
        if (!pkg.Syms.GetByID(sym, out var symbol) || !symbol.Flags.HasFlag(SymbolFlags.Static)) return null;
        return _staticMemberTables.GetValueOrDefault(symbol.Owner);
    }

    /// <summary>
    /// Whether a configured concatenation operator joins strings here: an operand is a string and
    /// so is the result, which it is not when a class operator takes the string operand.
    /// </summary>
    private bool IsStringTyped(PassContext ctx, PackageContext pkg, BinaryExpr bin)
    {
        return IsStringType(ctx, pkg, bin.Type)
               && (IsStringType(ctx, pkg, bin.Left.Type) || IsStringType(ctx, pkg, bin.Right.Type));
    }

    private bool IsStringType(PassContext ctx, PackageContext pkg, TypID t)
    {
        return t == ctx.Types.PrimString.ID;
    }

    private string ExprToInline(PassContext ctx, PackageContext pkg, LuaGenerator gen, Expr expr)
    {
        var sub = new LuaGenerator(gen.Config);
        EmitExpr(ctx, pkg, sub, expr);
        return sub.Finish();
    }

    /// <summary>
    /// The Lua a member of <paramref name="enumType"/> written without its enum, as <c>RED</c>,
    /// compiles to: its value, so the enum does not have to be in scope where it is used. A
    /// declared enum does not carry its values, so its member is read from the enum's table.
    /// </summary>
    private static string EnumMemberValue(EnumType enumType, EnumType.Member member)
    {
        if (enumType.IsDeclared || member.Value == null) return enumType.Name + "." + member.Name;
        return enumType.BaseType.Kind == TypeKind.PrimitiveString
            ? "\"" + EscapeLuaString(member.Value.ToString()!) + "\""
            : member.Value.ToString()!;
    }

    private static string EscapeLuaString(string s)
    {
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t")
            .Replace("\0", "\\0");
    }

    #endregion
}
