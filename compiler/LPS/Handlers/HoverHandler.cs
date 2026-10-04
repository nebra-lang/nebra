using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using NebraSymbolKind = Nebra.IR.SymbolKind;

namespace Nebra.LPS.Handlers;

public sealed class HoverHandler(NebraWorkspace workspace) : HoverHandlerBase
{
    public override Task<Hover?> Handle(HoverParams request, CancellationToken ct)
    {
        var result = workspace.GetResult(request.TextDocument.Uri.ToString());
        if (result == null) return Task.FromResult<Hover?>(null);

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var hoveredNode = NodeFinder.Find(result.Hir, line, col);
        var nameRef = NodeFinder.FindNameRef(result.Hir, line, col);
        if (nameRef != null && nameRef.Sym != SymID.Invalid && result.Syms.GetByID(nameRef.Sym, out var sym))
        {
            var declaredType = sym.Type;
            var effectiveType = declaredType;
            if (hoveredNode is NameExpr ne && ne.Name == nameRef && ne.Type != TypID.Invalid)
            {
                effectiveType = ne.Type;
            }

            var typeStr = workspace.FormatType(result.Types, effectiveType);
            var kind = sym.Kind == NebraSymbolKind.Function ? "function" : "variable";
            string display;

            if (result.Types.GetByID(effectiveType, out var effTyp)
                && effTyp is InterfaceType or ClassType or EnumType)
            {
                display = workspace.FormatTypeBody(result, effTyp);
            }
            else if (sym.Kind == NebraSymbolKind.Function && result.Types.GetByID(sym.Type, out var typ) && typ is FunctionType ft)
            {
                List<Parameter>? declParams = null;
                if (sym.DeclaringNode != NodeID.Invalid && result.NodeRegistry.TryGetValue(sym.DeclaringNode, out var dn))
                {
                    declParams = dn switch
                    {
                        FunctionDecl fd => fd.Parameters,
                        LocalFunctionDecl lfd => lfd.Parameters,
                        _ => null
                    };
                }

                var paramParts = new List<string>();
                if (declParams != null)
                {
                    var ri = 0;
                    foreach (var dp in declParams)
                    {
                        if (dp.IsVararg)
                        {
                            var vaType = ft.VarargType != null
                                ? workspace.FormatType(result.Types, ft.VarargType)
                                : "any";
                            var vaName = dp.Name.Name != "..." ? dp.Name.Name : "";
                            paramParts.Add($"...{vaName}: {vaType}");
                        }
                        else
                        {
                            var pType = ri < ft.ParamTypes.Count
                                ? workspace.FormatType(result.Types, ft.ParamTypes[ri])
                                : "any";
                            var part = $"{dp.Name.Name}: {pType}";
                            if (dp.DefaultValue != null) part += " = ...";
                            paramParts.Add(part);
                            ri++;
                        }
                    }
                }
                else
                {
                    for (var i = 0; i < ft.ParamTypes.Count; i++)
                    {
                        var s = workspace.FormatType(result.Types, ft.ParamTypes[i]);
                        var suffix = ft.DefaultParams.Contains(i) ? " = ..." : "";
                        paramParts.Add(s + suffix);
                    }
                    if (ft.IsVararg)
                    {
                        var vaType = ft.VarargType != null
                            ? workspace.FormatType(result.Types, ft.VarargType)
                            : "any";
                        paramParts.Add($"...: {vaType}");
                    }
                }

                var ret = workspace.FormatType(result.Types, ft.ReturnType);
                display = $"(function) {sym.Name}({string.Join(", ", paramParts)}) -> {ret}";
            }
            else if (effectiveType != declaredType)
            {
                var declaredStr = workspace.FormatType(result.Types, declaredType);
                display = $"({kind}) {sym.Name}: {typeStr}\n-- narrowed from {declaredStr}";
            }
            else
            {
                display = $"({kind}) {sym.Name}: {typeStr}";
            }

            var typesLine = workspace.FormatTypeReferencesLine(result, effectiveType);
            var docMarkdown = TryRenderDocFor(result, sym);
            var hoverValue = $"```nebra\n{display}\n```";
            if (!string.IsNullOrEmpty(typesLine))
                hoverValue += $"\n\n{typesLine}";
            if (!string.IsNullOrEmpty(docMarkdown))
                hoverValue += $"\n\n---\n\n{docMarkdown}";

            return Task.FromResult<Hover?>(new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = hoverValue
                }),
                Range = NebraWorkspace.SpanToRange(nameRef.Span)
            });
        }

        if (nameRef != null && nameRef.Sym == SymID.Invalid)
        {
            var memberHover = TryMemberHover(result, hoveredNode, nameRef);
            if (memberHover != null) return Task.FromResult<Hover?>(memberHover);
        }

        var annotation = FindAnnotationAt(result.Hir, line, col);
        if (annotation != null)
        {
            var info = workspace.GetAnnotationInfo(annotation.Name.Name);
            var display = info ?? $"@{annotation.Name.Name}";
            return Task.FromResult<Hover?>(new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = $"```nebra\n{display}\n```"
                }),
                Range = NebraWorkspace.SpanToRange(annotation.Span)
            });
        }

        if (hoveredNode is Expr expr && expr.Type != TypID.Invalid)
        {
            var typeStr = workspace.FormatType(result.Types, expr.Type);
            var typesLine = workspace.FormatTypeReferencesLine(result, expr.Type);
            var value = $"```nebra\n{typeStr}\n```";
            if (!string.IsNullOrEmpty(typesLine)) value += $"\n\n{typesLine}";
            return Task.FromResult<Hover?>(new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = value
                }),
                Range = NebraWorkspace.SpanToRange(expr.Span)
            });
        }

        return Task.FromResult<Hover?>(null);
    }

    /// <summary>
    /// Builds the hover for a member access whose NameRef has no bound
    /// symbol (call-site method names and dot-access field names). Looks up
    /// the member on the receiver's type — walking the class inheritance
    /// chain and the interface base graph so e.g. <c>player:GetValue(...)</c>
    /// finds the method on <c>Entity</c>, <c>Player</c>'s parent class.
    /// </summary>
    private Hover? TryMemberHover(AnalysisResult result, Node? hoveredNode, NameRef nameRef)
    {
        var access = MemberResolver.FindAccess(hoveredNode, nameRef);
        if (access == null) return null;

        var receiver = access.Receiver;
        var memberName = access.Member.Name;
        var isMethodCall = hoveredNode is MethodCallExpr || hoveredNode is ExprStmt { Expression: MethodCallExpr };

        if (receiver.Type == TypID.Invalid) return null;
        if (!result.Types.GetByID(receiver.Type, out var recvType)) return null;

        recvType = UnwrapNullable(result, recvType);

        var (display, declNode, typesLine) = ResolveMemberDisplay(result, recvType, memberName, isMethodCall);
        if (display == null) return null;

        var hoverValue = $"```nebra\n{display}\n```";
        if (!string.IsNullOrEmpty(typesLine)) hoverValue += $"\n\n{typesLine}";
        if (declNode is Decl decl && decl.Doc != null)
        {
            var docMd = Doc.DocMarkdown.Render(decl.Doc);
            if (!string.IsNullOrEmpty(docMd)) hoverValue += $"\n\n---\n\n{docMd}";
        }

        return new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = hoverValue
            }),
            Range = NebraWorkspace.SpanToRange(nameRef.Span)
        };
    }

    private static IR.Type UnwrapNullable(AnalysisResult result, IR.Type t)
    {
        if (t is UnionType u)
        {
            foreach (var m in u.Types)
                if (m.Kind != TypeKind.PrimitiveNil) return UnwrapNullable(result, m);
        }
        return t;
    }

    /// <summary>
    /// Looks up a member on a class/interface (walking inheritance) and
    /// returns a hover-ready label, the optional source declaration node
    /// (for doc comments), and the workspace's "Type references" footer line.
    /// </summary>
    private (string? Display, Node? DeclNode, string? TypesLine) ResolveMemberDisplay(
        AnalysisResult result, IR.Type recvType, string name, bool isMethodCall)
    {
        if (recvType is ClassType ct)
        {
            for (var cur = ct; cur != null; cur = cur.BaseClass)
            {
                if (cur.Methods.TryGetValue(name, out var im))
                    return (FormatMember(result, ct.Name, name, im, isMethodLike: true),
                        null, workspace.FormatTypeReferencesLine(result, im.ID));
                if (cur.StaticMethods.TryGetValue(name, out var sm))
                    return (FormatMember(result, ct.Name, name, sm, isMethodLike: true, isStatic: true),
                        null, workspace.FormatTypeReferencesLine(result, sm.ID));
                if (cur.Getters.TryGetValue(name, out var g))
                    return ($"(getter) {ct.Name}.{name}: {workspace.FormatType(result.Types, g.ReturnType)}",
                        null, workspace.FormatTypeReferencesLine(result, g.ReturnType.ID));
                if (cur.InstanceFields.TryGetValue(name, out var f))
                    return ($"(field) {ct.Name}.{name}: {workspace.FormatType(result.Types, f.Type)}",
                        null, workspace.FormatTypeReferencesLine(result, f.Type.ID));
            }

            foreach (var iface in IR.Type.ImplementedInterfaces(ct))
            {
                if (iface.Methods.TryGetValue(name, out var im))
                    return (FormatMember(result, ct.Name, name, im, isMethodLike: true),
                        null, workspace.FormatTypeReferencesLine(result, im.ID));
                if (iface.Fields.TryGetValue(name, out var f))
                    return ($"(field) {ct.Name}.{name}: {workspace.FormatType(result.Types, f.Type)}",
                        null, workspace.FormatTypeReferencesLine(result, f.Type.ID));
            }
        }
        else if (recvType is InterfaceType ift)
        {
            var visited = new HashSet<InterfaceType>();
            var queue = new Queue<InterfaceType>();
            queue.Enqueue(ift);
            while (queue.TryDequeue(out var cur))
            {
                if (!visited.Add(cur)) continue;
                if (cur.Methods.TryGetValue(name, out var im))
                    return (FormatMember(result, ift.Name, name, im, isMethodLike: true),
                        null, workspace.FormatTypeReferencesLine(result, im.ID));
                if (cur.Fields.TryGetValue(name, out var f))
                    return ($"(field) {ift.Name}.{name}: {workspace.FormatType(result.Types, f.Type)}",
                        null, workspace.FormatTypeReferencesLine(result, f.Type.ID));
                foreach (var b in cur.BaseInterfaces) queue.Enqueue(b);
            }
        }
        else if (recvType is StructType st)
        {
            var field = st.Fields.FirstOrDefault(f => f.Name.Name == name);
            if (field != null)
                return ($"(field) {name}: {workspace.FormatType(result.Types, field.Type)}",
                    null, workspace.FormatTypeReferencesLine(result, field.Type.ID));
        }

        // Extension methods declared via `extend Type` — resolved on the type, its base
        // classes, or its implemented/extended interfaces (works for primitives too).
        var (extFn, extTarget) = IR.Type.ResolveExtension(recvType, name, result.Types.PrimFunction);
        if (extFn != null && extTarget != null)
        {
            var targetName = extTarget is ClassType ec ? ec.Name
                : extTarget is InterfaceType ei ? ei.Name
                : workspace.FormatType(result.Types, extTarget);
            return ("(extension) " + FormatMember(result, targetName, name, extFn, isMethodLike: true),
                null, workspace.FormatTypeReferencesLine(result, extFn.ID));
        }

        return (null, null, null);
    }

    private string FormatMember(AnalysisResult result, string ownerName, string memberName,
        FunctionType ft, bool isMethodLike, bool isStatic = false)
    {
        var prefix = isStatic ? "static " : "";
        var parts = new List<string>();
        // Class instance methods include a synthetic `self` first parameter
        // that lives in the FunctionType but is never written at the call
        // site; skip it when rendering so the hover matches what the user
        // actually types.
        var skipFirst = !isStatic && ft.ParamTypes.Count > 0 && ft.ParamNames.Count > 0
            && ft.ParamNames[0] == "self";
        for (var i = skipFirst ? 1 : 0; i < ft.ParamTypes.Count; i++)
        {
            var pName = i < ft.ParamNames.Count ? ft.ParamNames[i] : $"arg{i}";
            var pType = workspace.FormatType(result.Types, ft.ParamTypes[i]);
            var part = $"{pName}: {pType}";
            if (ft.DefaultParams.Contains(i)) part += " = ...";
            parts.Add(part);
        }
        if (ft.IsVararg)
        {
            var vaType = ft.VarargType != null ? workspace.FormatType(result.Types, ft.VarargType) : "any";
            parts.Add($"...: {vaType}");
        }
        var ret = workspace.FormatType(result.Types, ft.ReturnType);
        var sep = isMethodLike && !isStatic ? ":" : ".";
        return $"({prefix}method) {ownerName}{sep}{memberName}({string.Join(", ", parts)}) -> {ret}";
    }

    private static Annotation? FindAnnotationAt(IRScript hir, int line, int col)
    {
        Annotation? Check(List<Annotation> anns)
        {
            foreach (var a in anns)
            {
                if (a.Span.StartLn <= line && a.Span.EndLn >= line
                    && a.Span.StartCol <= col && a.Span.EndCol >= col)
                    return a;
            }
            return null;
        }

        foreach (var stmt in hir.Body)
        {
            var decl = stmt switch
            {
                ExportStmt ex => ex.Declaration,
                Decl d => d,
                _ => null,
            };
            if (decl == null) continue;

            Annotation? found = decl switch
            {
                FunctionDecl fd => Check(fd.Annotations),
                LocalFunctionDecl lfd => Check(lfd.Annotations),
                LocalDecl ld => Check(ld.Annotations),
                ClassDecl cd => Check(cd.Annotations),
                EnumDecl ed => Check(ed.Annotations),
                InterfaceDecl id => Check(id.Annotations),
                _ => null,
            };
            if (found != null) return found;

            if (decl is ClassDecl cls)
            {
                foreach (var f in cls.Fields) { found = Check(f.Annotations); if (found != null) return found; }
                foreach (var m in cls.Methods) { found = Check(m.Annotations); if (found != null) return found; }
            }
            if (decl is EnumDecl en)
            {
                foreach (var m in en.Members) { found = Check(m.Annotations); if (found != null) return found; }
            }
            if (decl is InterfaceDecl iface)
            {
                foreach (var f in iface.Fields) { found = Check(f.Annotations); if (found != null) return found; }
                foreach (var m in iface.Methods) { found = Check(m.Annotations); if (found != null) return found; }
            }
        }
        return null;
    }

    /// <summary>
    /// Looks up the <see cref="Doc.DocComment"/> attached to the declaration that
    /// owns the symbol and renders it for inclusion under the type-signature
    /// block of a hover. Walks class/interface/enum members so e.g. hovering a
    /// method picks up the comment on the method, not on the enclosing class.
    /// </summary>
    private static string TryRenderDocFor(AnalysisResult result, Symbol sym)
    {
        if (sym.DeclaringNode == NodeID.Invalid) return string.Empty;
        if (!result.NodeRegistry.TryGetValue(sym.DeclaringNode, out var node)) return string.Empty;

        Doc.DocComment? doc = node switch
        {
            Decl d => d.Doc,
            _ => null,
        };

        if (doc == null) return string.Empty;
        return Doc.DocMarkdown.Render(doc);
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability, ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra")
        };
    }
}
