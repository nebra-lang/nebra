using System.Text;
using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

public sealed class DeclGenPass() : Pass(PassName, PassScope.PerBuild, true)
{
    public const string PassName = "DeclGen";

    public override bool Run(PassContext context)
    {
        if (!context.Config.GenerateDeclarations) return true;

        var sourceRoot = Path.GetFullPath(Path.Combine(context.Config.ProjectRoot, context.Config.Source));
        var sourceRootWithSep = sourceRoot + Path.DirectorySeparatorChar;
        var sb = new StringBuilder();
        var hasContent = false;

        foreach (var pkg in context.Pkgs)
        {
            foreach (var file in pkg.Files)
            {
                if (file.IsDeclarationFile) continue;
                // Empty as well as null: the REPL compiles a synthetic file that has no path, and
                // Path.GetFullPath throws on an empty string.
                if (string.IsNullOrEmpty(file.Filename)) continue;

                var fullPath = Path.GetFullPath(file.Filename);
                if (!fullPath.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)
                    && !fullPath.StartsWith(sourceRootWithSep, StringComparison.OrdinalIgnoreCase))
                    continue;

                var exports = CollectExports(file);
                var extensions = CollectExtensions(file);
                if (exports.Count == 0 && extensions.Count == 0) continue;

                var modulePath = DeriveModulePath(file, sourceRoot);
                if (hasContent) sb.AppendLine();
                EmitModuleDeclaration(sb, context, pkg, modulePath, exports, extensions);
                hasContent = true;
            }
        }

        if (hasContent)
            context.Cache["GeneratedDeclarations"] = sb.ToString();

        return true;
    }

    private static string DeriveModulePath(PreparsedFile file, string sourceRoot)
    {
        if (file.Filename == null) return "unknown";

        var fullPath = Path.GetFullPath(file.Filename);
        var fullRoot = Path.GetFullPath(sourceRoot);

        string relative;
        if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            relative = Path.GetRelativePath(fullRoot, fullPath);
        }
        else
        {
            relative = Path.GetFileName(fullPath);
        }

        relative = Path.ChangeExtension(relative, null);
        return relative.Replace('\\', '/');
    }

    private sealed record ExportedSymbol(string Name, NameRef NameRef, Decl Declaration);

    private static List<ExportedSymbol> CollectExports(PreparsedFile file)
    {
        var exports = new List<ExportedSymbol>();

        foreach (var stmt in file.Hir.Body)
        {
            if (stmt is not ExportStmt export) continue;

            switch (export.Declaration)
            {
                case FunctionDecl fd when fd.NamePath.Count > 0:
                    exports.Add(new ExportedSymbol(fd.NamePath[0].Name, fd.NamePath[0], fd));
                    break;
                case LocalFunctionDecl lfd:
                    exports.Add(new ExportedSymbol(lfd.Name.Name, lfd.Name, lfd));
                    break;
                case LocalDecl ld:
                    foreach (var v in ld.Variables)
                        exports.Add(new ExportedSymbol(v.Name.Name, v.Name, ld));
                    break;
                case EnumDecl ed:
                    exports.Add(new ExportedSymbol(ed.Name.Name, ed.Name, ed));
                    break;
                case ClassDecl cd:
                    exports.Add(new ExportedSymbol(cd.Name.Name, cd.Name, cd));
                    break;
                case InterfaceDecl ifd:
                    exports.Add(new ExportedSymbol(ifd.Name.Name, ifd.Name, ifd));
                    break;
            }
        }

        return exports;
    }

    /// <summary>
    /// Collects the <c>extend</c> blocks declared at the top level of <paramref name="file"/>.
    /// Extensions are not exported by name - they register against the type they extend - so they
    /// are gathered separately from the export list.
    /// </summary>
    private static List<ExtendDecl> CollectExtensions(PreparsedFile file)
    {
        return file.Hir.Body.OfType<ExtendDecl>().Where(ed => !ed.IsDeclare).ToList();
    }

    private void EmitModuleDeclaration(StringBuilder sb, PassContext ctx, PackageContext pkg,
        string modulePath, List<ExportedSymbol> exports, List<ExtendDecl> extensions)
    {
        sb.AppendLine($"declare module \"{modulePath}\"");

        foreach (var export in exports)
        {
            EmitDoc(sb, "    ", export.Declaration.Doc);
            switch (export.Declaration)
            {
                case FunctionDecl fd:
                    EmitFunctionDeclaration(sb, ctx, pkg, export.Name, fd);
                    break;
                case LocalFunctionDecl lfd:
                    EmitLocalFunctionDeclaration(sb, ctx, pkg, lfd);
                    break;
                case LocalDecl:
                    EmitVarDeclaration(sb, ctx, pkg, export.NameRef);
                    break;
                case EnumDecl ed:
                    EmitEnumDeclaration(sb, ctx, pkg, ed);
                    break;
                case ClassDecl cd:
                    EmitClassDeclaration(sb, ctx, pkg, cd);
                    break;
                case InterfaceDecl ifd:
                    EmitInterfaceDeclaration(sb, ctx, pkg, ifd);
                    break;
            }
        }

        foreach (var extension in extensions)
        {
            EmitExtendDeclaration(sb, ctx, pkg, extension);
        }

        sb.AppendLine("end");
    }

    /// <summary>
    /// Writes an <c>extend</c> block carrying method signatures only, which is the form a
    /// declaration file accepts.
    /// </summary>
    private void EmitExtendDeclaration(StringBuilder sb, PassContext ctx, PackageContext pkg, ExtendDecl ed)
    {
        if (ed.TargetType == null) return;
        if (!ctx.Types.GetByID(ed.TargetType.ResolvedType, out var target)) return;

        sb.AppendLine($"    extend {FormatType(ctx, target)}");
        foreach (var method in ed.Methods)
        {
            sb.Append("        ");
            if (method.IsAsync) sb.Append("async ");
            sb.Append($"function {method.Name.Name}(");
            EmitParams(sb, ctx, pkg, method.Parameters);
            sb.Append(')');
            EmitReturnType(sb, ctx, pkg, method.ReturnType);
            sb.AppendLine();
        }
        sb.AppendLine("    end");
    }

    /// <summary>
    /// Re-serialises a parsed <see cref="Doc.DocComment"/> back into LuaCATS
    /// <c>---</c> lines so generated <c>.d.neb</c> declaration files preserve
    /// the documentation alongside the types. Called before every emitted
    /// declaration; a no-op when <paramref name="doc"/> is null/empty.
    /// </summary>
    private static void EmitDoc(StringBuilder sb, string indent, Nebra.Doc.DocComment? doc)
    {
        if (doc == null || doc.IsEmpty) return;

        void AppendLines(string text)
        {
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                sb.Append(indent).Append("---");
                if (line.Length > 0) sb.Append(' ').Append(line);
                sb.AppendLine();
            }
        }

        if (!string.IsNullOrEmpty(doc.Summary)) AppendLines(doc.Summary);
        if (!string.IsNullOrEmpty(doc.Remarks))
        {
            sb.Append(indent).AppendLine("---");
            AppendLines(doc.Remarks);
        }

        foreach (var p in doc.Params)
        {
            sb.Append(indent).Append("---@param ").Append(p.Name);
            if (p.Optional) sb.Append('?');
            if (!string.IsNullOrEmpty(p.TypeText)) sb.Append(' ').Append(p.TypeText);
            if (!string.IsNullOrEmpty(p.Description)) sb.Append(' ').Append(p.Description);
            sb.AppendLine();
        }

        foreach (var r in doc.Returns)
        {
            sb.Append(indent).Append("---@return ");
            sb.Append(string.IsNullOrEmpty(r.TypeText) ? "any" : r.TypeText);
            if (!string.IsNullOrEmpty(r.Name)) sb.Append(' ').Append(r.Name);
            if (!string.IsNullOrEmpty(r.Description)) sb.Append(" #").Append(r.Description);
            sb.AppendLine();
        }

        foreach (var g in doc.Generics)
        {
            sb.Append(indent).Append("---@generic ").Append(g.Name);
            if (!string.IsNullOrEmpty(g.Bound)) sb.Append(": ").Append(g.Bound);
            sb.AppendLine();
        }

        foreach (var o in doc.Overloads)
            sb.Append(indent).Append("---@overload ").AppendLine(o.Raw);

        foreach (var s in doc.See)
            sb.Append(indent).Append("---@see ").AppendLine(s);

        if (doc.Async) sb.Append(indent).AppendLine("---@async");
        if (doc.NoDiscard) sb.Append(indent).AppendLine("---@nodiscard");
        if (!string.IsNullOrEmpty(doc.Since)) sb.Append(indent).Append("---@since ").AppendLine(doc.Since);
        if (doc.Deprecated)
        {
            sb.Append(indent).Append("---@deprecated");
            if (!string.IsNullOrEmpty(doc.DeprecatedReason)) sb.Append(' ').Append(doc.DeprecatedReason);
            sb.AppendLine();
        }
        if (doc.Visibility != Nebra.Doc.DocVisibility.Default)
            sb.Append(indent).Append("---@").AppendLine(doc.Visibility.ToString().ToLowerInvariant());
    }

    private void EmitEnumDeclaration(StringBuilder sb, PassContext ctx, PackageContext pkg, EnumDecl ed)
    {
        sb.Append("    enum ");
        sb.Append(ed.Name.Name);
        sb.AppendLine();
        foreach (var member in ed.Members)
        {
            EmitDoc(sb, "        ", member.Doc);
            sb.Append("        ");
            sb.AppendLine(member.Name.Name);
        }
        sb.AppendLine("    end");
    }

    /// <summary>
    /// Emits an exported function as a member of the surrounding <c>declare module</c>. Members
    /// are written without the <c>declare</c> keyword: inside a module block the grammar expects
    /// <c>function name(...)</c>, and a stray <c>declare</c> makes the parser close the module
    /// early and hoist the function out of it, where the module's own types are not in scope.
    /// </summary>
    private void EmitFunctionDeclaration(StringBuilder sb, PassContext ctx, PackageContext pkg,
        string exportName, FunctionDecl fd)
    {
        sb.Append("    function ");
        sb.Append(exportName);

        if (fd.MethodName != null)
        {
            sb.Append(':');
            sb.Append(fd.MethodName.Name);
        }

        sb.Append('(');
        EmitParams(sb, ctx, pkg, fd.Parameters);
        sb.Append(')');
        EmitReturnType(sb, ctx, pkg, fd.NamePath[0]);
        sb.AppendLine();
    }

    private void EmitLocalFunctionDeclaration(StringBuilder sb, PassContext ctx, PackageContext pkg,
        LocalFunctionDecl lfd)
    {
        sb.Append("    function ");
        sb.Append(lfd.Name.Name);
        sb.Append('(');
        EmitParams(sb, ctx, pkg, lfd.Parameters);
        sb.Append(')');
        EmitReturnType(sb, ctx, pkg, lfd.Name);
        sb.AppendLine();
    }

    private void EmitVarDeclaration(StringBuilder sb, PassContext ctx, PackageContext pkg, NameRef nameRef)
    {
        sb.Append("    ");
        sb.Append(nameRef.Name);
        sb.Append(": ");
        sb.AppendLine(FormatSymType(ctx, pkg, nameRef));
    }

    private void EmitClassDeclaration(StringBuilder sb, PassContext ctx, PackageContext pkg, ClassDecl cd)
    {
        sb.Append("    ");
        if (cd.IsAbstract) sb.Append("abstract ");
        sb.Append("class ");
        sb.Append(cd.Name.Name);
        if (cd.BaseClass != null)
        {
            sb.Append(" extends ");
            sb.Append(cd.BaseClass.Name);
        }
        if (cd.Interfaces.Count > 0)
        {
            sb.Append(" implements ");
            sb.Append(string.Join(", ", cd.Interfaces.Select(i => i.Name)));
        }
        sb.AppendLine();

        foreach (var field in cd.Fields)
        {
            if (field.IsLocal) continue;
            EmitDoc(sb, "        ", field.Doc);
            sb.Append("        ");
            if (field.IsProtected) sb.Append("protected ");
            if (field.IsStatic) sb.Append("static ");
            sb.Append(field.Name.Name);
            if (field.TypeAnnotation != null)
            {
                sb.Append(": ");
                sb.Append(FormatAnnotatedType(ctx, pkg, field.TypeAnnotation, field.Name));
            }
            sb.AppendLine();
        }

        if (cd.Constructor != null)
        {
            EmitDoc(sb, "        ", cd.Constructor.Doc);
            sb.Append("        constructor(");
            EmitParams(sb, ctx, pkg, cd.Constructor.Parameters);
            sb.AppendLine(")");
        }

        foreach (var method in cd.Methods)
        {
            if (method.IsLocal) continue;
            EmitDoc(sb, "        ", method.Doc);
            sb.Append("        ");
            if (method.IsProtected) sb.Append("protected ");
            if (method.IsStatic) sb.Append("static ");
            if (method.IsOverride) sb.Append("override ");
            if (method.IsAbstract) sb.Append("abstract ");
            if (method.IsAsync) sb.Append("async ");
            sb.Append("function ");
            sb.Append(method.Name.Name);
            sb.Append('(');
            EmitParams(sb, ctx, pkg, method.Parameters);
            sb.Append(')');
            EmitReturnType(sb, ctx, pkg, method.ReturnType);
            sb.AppendLine();
        }

        foreach (var accessor in cd.Accessors)
        {
            sb.Append("        ");
            sb.Append(accessor.Kind == AccessorKind.Getter ? "get" : "set");
            sb.Append(' ');
            sb.Append(accessor.Name.Name);
            sb.Append('(');
            EmitParams(sb, ctx, pkg, accessor.Parameters);
            sb.Append(')');
            if (accessor.ReturnType != null)
                EmitReturnType(sb, ctx, pkg, accessor.ReturnType);
            sb.AppendLine();
        }

        sb.AppendLine("    end");
    }

    private void EmitInterfaceDeclaration(StringBuilder sb, PassContext ctx, PackageContext pkg, InterfaceDecl ifd)
    {
        sb.Append("    interface ");
        sb.Append(ifd.Name.Name);
        if (ifd.BaseInterfaces.Count > 0)
        {
            sb.Append(" extends ");
            sb.Append(string.Join(", ", ifd.BaseInterfaces.Select(i => i.Name)));
        }
        sb.AppendLine();

        foreach (var field in ifd.Fields)
        {
            EmitDoc(sb, "        ", field.Doc);
            sb.Append("        ");
            sb.Append(field.Name.Name);
            sb.Append(": ");
            sb.Append(FormatAnnotatedType(ctx, pkg, field.TypeAnnotation, field.Name));
            sb.AppendLine();
        }

        foreach (var method in ifd.Methods)
        {
            EmitDoc(sb, "        ", method.Doc);
            sb.Append("        ");
            if (method.IsAsync) sb.Append("async ");
            sb.Append("function ");
            sb.Append(method.Name.Name);
            sb.Append('(');
            EmitParams(sb, ctx, pkg, method.Parameters);
            sb.Append(')');
            EmitReturnType(sb, ctx, pkg, method.ReturnType);
            sb.AppendLine();
        }

        sb.AppendLine("    end");
    }

    private void EmitParams(StringBuilder sb, PassContext ctx, PackageContext pkg, List<Parameter> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var p = parameters[i];
            if (p.IsVararg)
            {
                sb.Append("...");
                if (p.Name.Name != "...")
                    sb.Append(p.Name.Name);
                if (p.TypeAnnotation != null)
                {
                    sb.Append(": ");
                    sb.Append(FormatSymType(ctx, pkg, p.Name));
                }
            }
            else
            {
                sb.Append(p.Name.Name);
                sb.Append(": ");
                sb.Append(FormatSymType(ctx, pkg, p.Name));
                if (p.DefaultValue != null)
                    sb.Append(" = ...");
            }
        }
    }

    private void EmitReturnType(StringBuilder sb, PassContext ctx, PackageContext pkg, NameRef nameRef)
    {
        if (nameRef.Sym == SymID.Invalid) return;
        if (!pkg.Syms.GetByID(nameRef.Sym, out var sym)) return;
        if (!ctx.Types.GetByID(sym.Type, out var typ) || typ is not FunctionType ft) return;

        sb.Append(": ");
        if (ft.Predicate != null)
            sb.Append($"{ft.Predicate.ParamName} is {FormatType(ctx, ft.Predicate.TargetType)}");
        else
            sb.Append(FormatType(ctx, ft.ReturnType));
    }

    /// <summary>
    /// Emits a method/accessor return type straight from its resolved AST <see cref="TypeRef"/>.
    /// Class-method names are not declared as symbols, so the name-symbol lookup used for free
    /// functions yields nothing here and would drop the return type entirely.
    /// </summary>
    private void EmitReturnType(StringBuilder sb, PassContext ctx, PackageContext pkg, TypeRef? returnType)
    {
        if (returnType == null) return;
        if (returnType is TypePredicateRef pred && ctx.Types.GetByID(pred.TargetType.ResolvedType, out var target))
        {
            sb.Append($": {pred.ParamName.Name} is {FormatType(ctx, target)}");
            return;
        }
        if (returnType.ResolvedType == TypID.Invalid) return;
        if (!ctx.Types.GetByID(returnType.ResolvedType, out var typ)) return;

        sb.Append(": ");
        sb.Append(FormatType(ctx, typ));
    }

    /// <summary>
    /// Renders the type a declaration was annotated with. A class or interface field is a member
    /// rather than a scope symbol, so it has no symbol to read a type off; the resolved annotation
    /// is what carries it. Falls back to the symbol for anything that does have one.
    /// </summary>
    private string FormatAnnotatedType(PassContext ctx, PackageContext pkg, TypeRef? typeRef, NameRef nameRef)
    {
        if (typeRef != null && typeRef.ResolvedType != TypID.Invalid
            && ctx.Types.GetByID(typeRef.ResolvedType, out var resolved))
        {
            return FormatType(ctx, resolved);
        }

        return FormatSymType(ctx, pkg, nameRef);
    }

    private string FormatSymType(PassContext ctx, PackageContext pkg, NameRef nameRef)
    {
        if (nameRef.Sym == SymID.Invalid) return "any";
        if (!pkg.Syms.GetByID(nameRef.Sym, out var sym)) return "any";
        if (sym.Type == TypID.Invalid) return "any";
        if (!ctx.Types.GetByID(sym.Type, out var typ)) return "any";
        return FormatType(ctx, typ);
    }

    private string FormatType(PassContext ctx, Type typ)
    {
        return typ switch
        {
            FunctionType ft => $"({string.Join(", ", ft.ParamTypes.Select(p => FormatType(ctx, p)))}) -> {FormatType(ctx, ft.ReturnType)}",
            TableArrayType arr => $"{FormatType(ctx, arr.ElementType)}[]",
            TableMapType map => $"{{ [{FormatType(ctx, map.KeyType)}]: {FormatType(ctx, map.ValueType)} }}",
            UnionType union => string.Join(" | ", union.Types.Select(t => FormatType(ctx, t))),
            StructType st => $"{{ {string.Join(", ", st.Fields.Select(f => $"{f.Name.Name}: {FormatType(ctx, f.Type)}"))} }}",
            TupleType tuple => $"({string.Join(", ", tuple.Fields.Select(f => FormatType(ctx, f.Type)))})",
            VariadicType variadic => $"...{FormatType(ctx, variadic.ElementType)}",
            ClassType ct => ct.Name,
            InterfaceType it => it.Name,
            EnumType et => et.Name,
            _ => typ.Kind switch
            {
                TypeKind.PrimitiveNil => "nil",
                TypeKind.PrimitiveAny => "any",
                TypeKind.PrimitiveNumber => "number",
                TypeKind.PrimitiveBool => "boolean",
                TypeKind.PrimitiveString => "string",
                TypeKind.PrimitiveFunction => "function",
                TypeKind.PrimitiveThread => "thread",
                TypeKind.PrimitiveUserdata => "userdata",
                TypeKind.PrimitiveNever => "never",
                _ => "any"
            }
        };
    }
}
