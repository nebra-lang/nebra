using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Handlers;

public sealed class SignatureHelpHandler(NebraWorkspace workspace) : SignatureHelpHandlerBase
{
    public override async Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var annotationHelp = TryAnnotationSignatureHelp(result, request.Position);
        if (annotationHelp != null) return annotationHelp;

        var callNode = NodeFinder.FindEnclosingCall(result.Hir, line, col);
        if (callNode == null) return null;

        var calleeSym = SymID.Invalid;
        List<Expr> arguments;
        FunctionType? ft = null;
        string calleeLabel = "fn";

        switch (callNode)
        {
            case FunctionCallExpr fce:
                if (fce.Callee is NameExpr ne) calleeSym = ne.Name.Sym;
                if (calleeSym == SymID.Invalid && fce.Callee is DotAccessExpr fcDot)
                {
                    ft = ResolveMemberAsFunction(result, fcDot.Object, fcDot.FieldName.Name, isStatic: true);
                    if (ft != null) calleeLabel = fcDot.FieldName.Name;
                }
                break;
            case MethodCallExpr mce:
                calleeSym = mce.MethodName.Sym;
                arguments = mce.Arguments;
                if (calleeSym == SymID.Invalid)
                {
                    ft = ResolveMemberAsFunction(result, mce.Object, mce.MethodName.Name, isStatic: false);
                    if (ft != null) calleeLabel = mce.MethodName.Name;
                }
                break;
            default:
                return null;
        }

        Symbol? sym = null;
        if (ft == null)
        {
            if (calleeSym == SymID.Invalid || !result.Syms.GetByID(calleeSym, out var s))
                return null;
            sym = s;
            if (!result.Types.GetByID(sym.Type, out var typ) || typ is not FunctionType bound)
                return null;
            ft = bound;
            calleeLabel = sym.Name;
        }

        var activeParam = CountActiveParam(result.SourceText, request.Position);

        var paramInfos = new List<ParameterInformation>();
        List<Parameter>? declParams = null;
        Doc.DocComment? doc = null;

        if (sym != null && sym.DeclaringNode != NodeID.Invalid && result.NodeRegistry.TryGetValue(sym.DeclaringNode, out var declNode))
        {
            declParams = declNode switch
            {
                FunctionDecl fd => fd.Parameters,
                LocalFunctionDecl lfd => lfd.Parameters,
                _ => null
            };
            if (declNode is Decl declWithDoc) doc = declWithDoc.Doc;
        }

        var regularIdx = 0;
        if (declParams != null)
        {
            foreach (var dp in declParams)
            {
                if (dp.IsVararg)
                {
                    var vaType = ft.VarargType != null
                        ? workspace.FormatType(result.Types, ft.VarargType)
                        : "any";
                    var vaName = dp.Name.Name != "..." ? dp.Name.Name : "...";
                    paramInfos.Add(new ParameterInformation
                    {
                        Label = new ParameterInformationLabel($"...{vaName}: {vaType}")
                    });
                }
                else
                {
                    var pType = regularIdx < ft.ParamTypes.Count
                        ? workspace.FormatType(result.Types, ft.ParamTypes[regularIdx])
                        : "any";
                    var label2 = $"{dp.Name.Name}: {pType}";
                    if (dp.DefaultValue != null)
                        label2 += " = ...";
                    var pi = new ParameterInformation
                    {
                        Label = new ParameterInformationLabel(label2)
                    };
                    var pdesc = Doc.DocMarkdown.ParamDescription(doc, dp.Name.Name);
                    if (pdesc != null)
                        pi = new ParameterInformation
                        {
                            Label = pi.Label,
                            Documentation = new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = pdesc })
                        };
                    paramInfos.Add(pi);
                    regularIdx++;
                }
            }
        }
        else
        {
            var skipFirst = callNode is MethodCallExpr
                && ft.ParamTypes.Count > 0 && ft.ParamNames.Count > 0
                && ft.ParamNames[0] == "self";
            for (var i = skipFirst ? 1 : 0; i < ft.ParamTypes.Count; i++)
            {
                var pType = workspace.FormatType(result.Types, ft.ParamTypes[i]);
                var defaultHint = ft.DefaultParams.Contains(i) ? " = ..." : "";
                var argName = $"arg{i}";
                if (ft.ParamNames != null && i < ft.ParamNames.Count && !string.IsNullOrEmpty(ft.ParamNames[i]))
                    argName = ft.ParamNames[i];
                paramInfos.Add(new ParameterInformation
                {
                    Label = new ParameterInformationLabel($"{argName}: {pType}{defaultHint}")
                });
            }
            if (ft.IsVararg)
            {
                var vaType = ft.VarargType != null
                    ? workspace.FormatType(result.Types, ft.VarargType)
                    : "any";
                paramInfos.Add(new ParameterInformation
                {
                    Label = new ParameterInformationLabel($"...: {vaType}")
                });
            }
        }

        var retType = workspace.FormatType(result.Types, ft.ReturnType);
        var paramStr = string.Join(", ", paramInfos.Select(p => p.Label.Label));
        var label = $"{calleeLabel}({paramStr}) -> {retType}";

        var sigInfo = new SignatureInformation
        {
            Label = label,
            Parameters = new Container<ParameterInformation>(paramInfos),
            Documentation = doc != null && !doc.IsEmpty
                ? new StringOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = Doc.DocMarkdown.Render(doc)
                })
                : null
        };

        return new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(sigInfo),
            ActiveSignature = 0,
            ActiveParameter = Math.Min(activeParam, Math.Max(0, paramInfos.Count - 1))
        };
    }

    /// <summary>
    /// Looks up a method/field-as-function on the receiver expression's type,
    /// walking class inheritance and interface base graphs. Returns the
    /// <see cref="FunctionType"/> when the member exists, null otherwise.
    /// Used so signature help works on calls whose callee has no bound
    /// symbol — e.g. <c>player:GetValue(...)</c> inheriting from
    /// <c>Entity</c>, or <c>Events.CallRemote(...)</c> with side-overloaded
    /// dispatch.
    /// </summary>
    private FunctionType? ResolveMemberAsFunction(AnalysisResult result, Expr receiver, string name, bool isStatic)
    {
        if (receiver.Type == TypID.Invalid) return null;
        if (!result.Types.GetByID(receiver.Type, out var rt)) return null;

        if (rt is UnionType u)
        {
            foreach (var m in u.Types)
                if (m.Kind != TypeKind.PrimitiveNil) { rt = m; break; }
        }

        if (rt is ClassType ct)
        {
            for (var cur = ct; cur != null; cur = cur.BaseClass)
            {
                if (isStatic && cur.StaticMethods.TryGetValue(name, out var sm)) return sm;
                if (!isStatic && cur.Methods.TryGetValue(name, out var im)) return im;
            }

            if (isStatic) return null;

            foreach (var iface in Nebra.IR.Type.ImplementedInterfaces(ct))
            {
                if (iface.Methods.TryGetValue(name, out var im)) return im;
            }
            return null;
        }
        if (rt is InterfaceType ift)
        {
            var visited = new HashSet<InterfaceType>();
            var queue = new Queue<InterfaceType>();
            queue.Enqueue(ift);
            while (queue.TryDequeue(out var cur))
            {
                if (!visited.Add(cur)) continue;
                if (cur.Methods.TryGetValue(name, out var m)) return m;
                foreach (var b in cur.BaseInterfaces) queue.Enqueue(b);
            }
            return null;
        }
        if (rt is StructType st)
        {
            var field = st.Fields.FirstOrDefault(f => f.Name.Name == name);
            return field?.Type as FunctionType;
        }
        return null;
    }

    /// <summary>
    /// Provides signature help for annotation invocations. Walks the HIR for an
    /// <see cref="Annotation"/> whose argument list contains the cursor, looks
    /// the annotation up in <see cref="NebraWorkspace.GetAnnotationMeta"/>, and
    /// renders the <c>meta.params</c> spec as a <see cref="SignatureInformation"/>.
    /// Returns <c>null</c> when the cursor isn't inside an annotation call.
    /// </summary>
    private SignatureHelp? TryAnnotationSignatureHelp(AnalysisResult result, OmniSharp.Extensions.LanguageServer.Protocol.Models.Position pos)
    {
        var line = pos.Line + 1;
        var col = pos.Character + 1;
        var annotation = FindEnclosingAnnotation(result.Hir, line, col);
        if (annotation == null) return null;

        var meta = workspace.GetAnnotationMeta(annotation.Name.Name);
        if (meta == null) return null;

        var paramInfos = new List<ParameterInformation>();
        foreach (var p in meta.Parameters)
        {
            var label = $"{p.Name}: {p.TypeName}";
            if (!p.Required)
                label += " = " + (p.DefaultValue?.ToString() ?? "nil");
            paramInfos.Add(new ParameterInformation
            {
                Label = new ParameterInformationLabel(label)
            });
        }

        var paramStr = string.Join(", ", paramInfos.Select(p => p.Label.Label));
        var label2 = $"@{meta.Name}({paramStr})";

        var activeParam = ActiveAnnotationParam(annotation, pos);

        return new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(new SignatureInformation
            {
                Label = label2,
                Parameters = new Container<ParameterInformation>(paramInfos),
                Documentation = new StringOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = $"target: `{string.Join(" | ", meta.Targets)}`  \nsource: `{System.IO.Path.GetFileName(meta.SourcePath)}`"
                })
            }),
            ActiveSignature = 0,
            ActiveParameter = Math.Min(activeParam, Math.Max(0, paramInfos.Count - 1))
        };
    }

    /// <summary>
    /// Determines the active parameter index inside an annotation call. If the
    /// argument under the cursor uses named syntax (<c>message = …</c>), match
    /// by name; otherwise fall back to counting commas.
    /// </summary>
    private static int ActiveAnnotationParam(Annotation ann, OmniSharp.Extensions.LanguageServer.Protocol.Models.Position pos)
    {
        var line = pos.Line + 1;
        var col = pos.Character + 1;
        for (var i = 0; i < ann.Args.Count; i++)
        {
            var arg = ann.Args[i];
            if (arg.Span.StartLn <= line && line <= arg.Span.EndLn &&
                (line != arg.Span.StartLn || col >= arg.Span.StartCol) &&
                (line != arg.Span.EndLn || col <= arg.Span.EndCol + 1))
            {
                return i;
            }
        }
        return ann.Args.Count;
    }

    private static Annotation? FindEnclosingAnnotation(IRScript hir, int line, int col)
    {
        Annotation? Check(List<Annotation> anns)
        {
            foreach (var a in anns)
            {
                if (a.Span.StartLn <= line && line <= a.Span.EndLn &&
                    (line != a.Span.StartLn || col >= a.Span.StartCol) &&
                    (line != a.Span.EndLn || col <= a.Span.EndCol + 1))
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
                _ => null
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
                _ => null
            };
            if (found != null) return found;

            if (decl is ClassDecl cls)
            {
                foreach (var f in cls.Fields) { found = Check(f.Annotations); if (found != null) return found; }
                foreach (var m in cls.Methods) { found = Check(m.Annotations); if (found != null) return found; }
            }
            if (decl is InterfaceDecl iface)
            {
                foreach (var f in iface.Fields) { found = Check(f.Annotations); if (found != null) return found; }
                foreach (var m in iface.Methods) { found = Check(m.Annotations); if (found != null) return found; }
            }
            if (decl is EnumDecl en)
                foreach (var m in en.Members) { found = Check(m.Annotations); if (found != null) return found; }
        }
        return null;
    }

    private static int CountActiveParam(string sourceText, OmniSharp.Extensions.LanguageServer.Protocol.Models.Position pos)
    {
        var lines = sourceText.Split('\n');
        if (pos.Line < 0 || pos.Line >= lines.Length) return 0;
        var lineText = lines[pos.Line].TrimEnd('\r');
        var cursor = Math.Min(pos.Character, lineText.Length);

        var depth = 0;
        var commas = 0;

        for (var i = cursor - 1; i >= 0; i--)
        {
            var ch = lineText[i];
            switch (ch)
            {
                case ')' or ']' or '}':
                    depth++;
                    break;
                case '(' or '[' or '{':
                    if (depth == 0) return commas;
                    depth--;
                    break;
                case ',' when depth == 0:
                    commas++;
                    break;
            }
        }

        return commas;
    }

    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(
        SignatureHelpCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SignatureHelpRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra"),
            TriggerCharacters = new Container<string>("(", ",", "@", "=", " ")
        };
    }
}
