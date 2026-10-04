using Nebra.Diagnostics;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using NebraSymbolKind = Nebra.IR.SymbolKind;
using NebraType = Nebra.IR.Type;

namespace Nebra.LPS.Handlers;

public sealed class CompletionHandler(NebraWorkspace workspace) : CompletionHandlerBase
{
    private static readonly string[] Keywords =
    [
        "and", "break", "do", "else", "elseif", "end", "enum", "false", "for",
        "function", "goto", "if", "in", "local", "nil", "not", "or",
        "repeat", "return", "then", "true", "until", "while",
        "as", "async", "await", "case", "class", "constructor", "declare", "export", "extends", "from",
        "abstract", "implements", "import", "interface", "match", "meta", "module", "mut", "new", "override",
        "protected", "static", "super", "when", "typeof", "instanceof",
        "defer", "guard", "continue"
    ];

    public override async Task<CompletionList> Handle(CompletionParams request, CancellationToken ct)
    {
        var items = new List<CompletionItem>();

        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result != null)
        {
            var line = request.Position.Line + 1;
            var col = request.Position.Character + 1;

            var annotationItems = TryAnnotationCompletion(result, request.Position);
            if (annotationItems != null)
                return new CompletionList(annotationItems);

            var importItems = TryImportPathCompletion(result, request.Position);
            if (importItems != null)
                return new CompletionList(importItems);

            var importSpecItems = TryImportSpecifierCompletion(result, request.Position);
            if (importSpecItems != null)
                return new CompletionList(importSpecItems);

            var memberItems = TryMemberCompletion(result, request.Position);
            if (memberItems != null)
            {
                return new CompletionList(memberItems);
            }

            var typeContext = IsTypeAnnotationContext(result, request.Position);

            if (!typeContext)
            {
                foreach (var kw in Keywords)
                {
                    items.Add(new CompletionItem
                    {
                        Label = kw,
                        Kind = CompletionItemKind.Keyword
                    });
                }
            }
            else
            {
                foreach (var prim in new[] { "string", "number", "boolean", "any", "void", "nil", "never", "thread", "userdata" })
                {
                    items.Add(new CompletionItem
                    {
                        Label = prim,
                        Kind = CompletionItemKind.TypeParameter,
                        Detail = "primitive type"
                    });
                }
            }

            var containingClass = FindContainingClass(result.Hir, line, col);
            if (containingClass != null && !typeContext)
                AppendSelfMemberSnippets(result, containingClass, items);

            var node = NodeFinder.Find(result.Hir, line, col);
            var scopeId = result.Package.Root;
            if (node != null)
                result.Scopes.EnclosingScope(node.ID, out scopeId);

            var symbols = workspace.CollectVisibleSymbols(result, scopeId);
            foreach (var sym in symbols)
            {
                if (typeContext && !IsTypeKind(sym.Kind))
                    continue;

                var typeStr = workspace.FormatType(result.Types, sym.Type);
                var kind = sym.Kind switch
                {
                    NebraSymbolKind.Function => CompletionItemKind.Function,
                    NebraSymbolKind.Enum => CompletionItemKind.Enum,
                    NebraSymbolKind.Class => CompletionItemKind.Class,
                    NebraSymbolKind.Interface => CompletionItemKind.Interface,
                    NebraSymbolKind.TypeParam => CompletionItemKind.TypeParameter,
                    _ => CompletionItemKind.Variable
                };
                items.Add(new CompletionItem
                {
                    Label = sym.Name,
                    Kind = kind,
                    Detail = typeStr,
                    Documentation = BuildSymbolDocumentation(result, sym),
                    Tags = IsDeprecated(result, sym) ? new Container<CompletionItemTag>(CompletionItemTag.Deprecated) : null
                });
            }
        }
        else
        {
            foreach (var kw in Keywords)
            {
                items.Add(new CompletionItem
                {
                    Label = kw,
                    Kind = CompletionItemKind.Keyword
                });
            }
        }

        return new CompletionList(items);
    }

    private static bool IsTypeKind(NebraSymbolKind kind) =>
        kind is NebraSymbolKind.Class or NebraSymbolKind.Interface or NebraSymbolKind.Enum or NebraSymbolKind.TypeParam;

    private static StringOrMarkupContent? BuildSymbolDocumentation(AnalysisResult result, Symbol sym)
    {
        var doc = LookupDoc(result, sym);
        if (doc == null || doc.IsEmpty) return null;
        var rendered = Doc.DocMarkdown.Render(doc);
        if (string.IsNullOrEmpty(rendered)) return null;
        return new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = rendered });
    }

    private static bool IsDeprecated(AnalysisResult result, Symbol sym)
    {
        var doc = LookupDoc(result, sym);
        return doc?.Deprecated ?? false;
    }

    private static Doc.DocComment? LookupDoc(AnalysisResult result, Symbol sym)
    {
        if (sym.DeclaringNode == NodeID.Invalid) return null;
        if (!result.NodeRegistry.TryGetValue(sym.DeclaringNode, out var node)) return null;
        return node is Decl d ? d.Doc : null;
    }

    /// <summary>
    /// Returns the <see cref="ClassDecl"/> whose body span contains the given line/column,
    /// or null if the cursor is outside any class. Considers exported classes too.
    /// </summary>
    private static ClassDecl? FindContainingClass(IRScript hir, int line, int col)
    {
        foreach (var stmt in hir.Body)
        {
            ClassDecl? cd = stmt switch
            {
                ClassDecl c => c,
                ExportStmt e when e.Declaration is ClassDecl ce => ce,
                _ => null
            };
            if (cd != null && SpanContains(cd.Span, line, col))
                return cd;
        }
        return null;
    }

    private static bool SpanContains(TextSpan span, int line, int col)
    {
        var afterStart = span.StartLn < line || (span.StartLn == line && span.StartCol <= col);
        var beforeEnd = span.EndLn > line || (span.EndLn == line && span.EndCol >= col);
        return afterStart && beforeEnd;
    }

    /// <summary>
    /// Adds <c>self.&lt;member&gt;</c> snippet completions for instance fields, methods,
    /// getters and inherited members of the class containing the cursor. Helps avoid
    /// having to first type <c>self.</c> to get class member suggestions.
    /// </summary>
    private void AppendSelfMemberSnippets(AnalysisResult result, ClassDecl containingClass, List<CompletionItem> items)
    {
        if (containingClass.Name.Sym == SymID.Invalid) return;
        if (!result.Syms.GetByID(containingClass.Name.Sym, out var classSym)) return;
        if (!result.Types.GetByID(classSym.Type, out var t) || t is not ClassType classType) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = classType;
        while (current != null)
        {
            foreach (var (name, field) in current.InstanceFields)
            {
                if (!seen.Add(name)) continue;
                items.Add(new CompletionItem
                {
                    Label = "self." + name,
                    Kind = CompletionItemKind.Field,
                    Detail = workspace.FormatType(result.Types, field.Type.ID),
                    InsertText = "self." + name,
                    SortText = "0_" + name
                });
            }
            foreach (var (name, method) in current.Methods)
            {
                if (name.StartsWith("__")) continue;
                if (!seen.Add(name)) continue;
                items.Add(new CompletionItem
                {
                    Label = "self." + name,
                    Kind = CompletionItemKind.Method,
                    Detail = workspace.FormatType(result.Types, method.ID),
                    InsertText = "self." + name,
                    SortText = "0_" + name
                });
            }
            foreach (var (name, getter) in current.Getters)
            {
                if (!seen.Add(name)) continue;
                items.Add(new CompletionItem
                {
                    Label = "self." + name,
                    Kind = CompletionItemKind.Property,
                    Detail = workspace.FormatType(result.Types, getter.ReturnType.ID),
                    InsertText = "self." + name,
                    SortText = "0_" + name
                });
            }
            current = current.BaseClass;
        }
    }

    /// <summary>
    /// Checks whether the cursor sits in a type-annotation position, i.e. the prefix on the
    /// current line ends with <c>:</c> (optionally followed by whitespace and a partial name)
    /// inside a context where a type is expected — variable declarations, parameters, and
    /// function return types. Used to filter completion to types only.
    /// </summary>
    private static bool IsTypeAnnotationContext(AnalysisResult result, Position pos)
    {
        var lines = result.SourceText.Split('\n');
        if (pos.Line < 0 || pos.Line >= lines.Length) return false;
        var lineText = lines[pos.Line].TrimEnd('\r');
        var cursor = Math.Min(pos.Character, lineText.Length);

        var i = cursor;
        while (i > 0 && (char.IsLetterOrDigit(lineText[i - 1]) || lineText[i - 1] == '_')) i--;
        while (i > 0 && lineText[i - 1] == ' ') i--;

        if (i <= 0) return false;
        var c = lineText[i - 1];
        if (c != ':') return false;

        var prefix = lineText[..(i - 1)];
        var trimmed = prefix.TrimEnd();
        if (trimmed.EndsWith("::")) return false;

        return true;
    }

    /// <summary>
    /// Detects whether the cursor position is preceded by a member-access operator (`.` or `?.`),
    /// resolves the receiver chain to a type, and returns its struct fields as completion items.
    /// Returns null if this is not a member-completion context.
    /// </summary>
    private List<CompletionItem>? TryMemberCompletion(AnalysisResult result,
        OmniSharp.Extensions.LanguageServer.Protocol.Models.Position pos)
    {
        var lines = result.SourceText.Split('\n');
        if (pos.Line < 0 || pos.Line >= lines.Length) return null;
        var lineText = lines[pos.Line];
        if (lineText.EndsWith("\r")) lineText = lineText[..^1];

        var cursor = Math.Min(pos.Character, lineText.Length);

        var nameEnd = cursor;
        var i = cursor;
        while (i > 0 && (char.IsLetterOrDigit(lineText[i - 1]) || lineText[i - 1] == '_')) i--;
        var nameStart = i;

        if (i <= 0 || (lineText[i - 1] != '.' && lineText[i - 1] != ':')) return null;
        i--;
        if (i > 0 && lineText[i - 1] == '?') i--;

        var chainEnd = i;
        var j = i;
        while (true)
        {
            var segEnd = j;
            while (j > 0 && (char.IsLetterOrDigit(lineText[j - 1]) || lineText[j - 1] == '_')) j--;
            if (j == segEnd) return null;

            if (j > 0 && (lineText[j - 1] == '.' || lineText[j - 1] == ':'))
            {
                j--;
                if (j > 0 && lineText[j - 1] == '?') j--;
                continue;
            }

            break;
        }

        var chainText = lineText.Substring(j, chainEnd - j);
        if (string.IsNullOrEmpty(chainText)) return null;

        var segments = new List<(string Name, bool Optional)>();
        var k = 0;
        while (k < chainText.Length)
        {
            var optional = false;
            if (k > 0)
            {
                if (chainText[k] == '?' && k + 1 < chainText.Length && chainText[k + 1] == '.')
                {
                    optional = true;
                    k += 2;
                }
                else if (chainText[k] == '.' || chainText[k] == ':')
                {
                    k++;
                }
                else
                {
                    return null;
                }
            }

            var nStart = k;
            while (k < chainText.Length && (char.IsLetterOrDigit(chainText[k]) || chainText[k] == '_')) k++;
            if (k == nStart) return null;
            segments.Add((chainText.Substring(nStart, k - nStart), optional));
        }

        if (segments.Count == 0) return null;

        var lspLine = pos.Line + 1;
        var lspCol = Math.Max(1, pos.Character);
        var node = NodeFinder.Find(result.Hir, lspLine, lspCol);
        var scopeId = result.Package.Root;
        if (node != null) result.Scopes.EnclosingScope(node.ID, out scopeId);

        if (!result.Scopes.Lookup(scopeId, segments[0].Name, out var headSym)) return null;
        if (!result.Syms.GetByID(headSym, out var headSymbol)) return null;
        var currentTypeId = headSymbol.Type;

        for (var s = 1; s < segments.Count; s++)
        {
            currentTypeId = StripNil(result.Types, currentTypeId);
            if (!result.Types.GetByID(currentTypeId, out var t)) return null;
            switch (t)
            {
                case StructType st:
                {
                    var field = st.Fields.FirstOrDefault(f => f.Name.Name == segments[s].Name);
                    if (field == null) return null;
                    currentTypeId = field.Type.ID;
                    break;
                }
                case ClassType ct:
                {
                    if (!TryLookupClassMember(ct, segments[s].Name, out currentTypeId)) return null;
                    break;
                }
                case InterfaceType it:
                {
                    if (!TryLookupInterfaceMember(it, segments[s].Name, out currentTypeId)) return null;
                    break;
                }
                default:
                    return null;
            }
        }

        var finalTypeId = StripNil(result.Types, currentTypeId);
        if (!result.Types.GetByID(finalTypeId, out var finalType)) return null;

        if (finalType is EnumType enumType)
        {
            var items = new List<CompletionItem>();
            foreach (var m in enumType.Members)
            {
                items.Add(new CompletionItem
                {
                    Label = m.Name,
                    Kind = CompletionItemKind.EnumMember,
                    Detail = m.Value != null ? $"{enumType.Name}.{m.Name} = {m.Value}" : $"{enumType.Name}.{m.Name}"
                });
            }

            return items;
        }

        if (finalType is InterfaceType ifaceType)
        {
            var ifaceItems = new List<CompletionItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Walk(InterfaceType it)
            {
                foreach (var (name, field) in it.Fields)
                {
                    if (!seen.Add(name)) continue;
                    ifaceItems.Add(new CompletionItem
                    {
                        Label = name,
                        Kind = CompletionItemKind.Field,
                        Detail = workspace.FormatType(result.Types, field.Type.ID)
                    });
                }
                foreach (var (name, method) in it.Methods)
                {
                    if (name.StartsWith("__")) continue;
                    if (!seen.Add(name)) continue;
                    ifaceItems.Add(new CompletionItem
                    {
                        Label = name,
                        Kind = CompletionItemKind.Method,
                        Detail = workspace.FormatType(result.Types, method.ID)
                    });
                }
                foreach (var (name, method) in it.ExtensionMethods)
                {
                    if (name.StartsWith("__")) continue;
                    if (!seen.Add(name)) continue;
                    ifaceItems.Add(new CompletionItem
                    {
                        Label = name,
                        Kind = CompletionItemKind.Method,
                        Detail = "(extension) " + workspace.FormatType(result.Types, method.ID)
                    });
                }
                foreach (var bi in it.BaseInterfaces) Walk(bi);
            }
            Walk(ifaceType);
            return ifaceItems;
        }

        if (finalType is ClassType classType)
        {
            var classItems = new List<CompletionItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var cur = classType; cur != null; cur = cur.BaseClass)
            {
                foreach (var (name, field) in cur.InstanceFields)
                {
                    if (!seen.Add(name)) continue;
                    classItems.Add(new CompletionItem
                    {
                        Label = name,
                        Kind = CompletionItemKind.Field,
                        Detail = workspace.FormatType(result.Types, field.Type.ID)
                    });
                }

                foreach (var (name, method) in cur.Methods)
                {
                    if (name.StartsWith("__")) continue;
                    if (!seen.Add(name)) continue;
                    classItems.Add(new CompletionItem
                    {
                        Label = name,
                        Kind = CompletionItemKind.Method,
                        Detail = workspace.FormatType(result.Types, method.ID)
                    });
                }

                foreach (var (name, method) in cur.StaticMethods)
                {
                    if (!seen.Add(name)) continue;
                    classItems.Add(new CompletionItem
                    {
                        Label = name,
                        Kind = CompletionItemKind.Method,
                        Detail = workspace.FormatType(result.Types, method.ID)
                    });
                }

                foreach (var (name, getter) in cur.Getters)
                {
                    if (!seen.Add(name)) continue;
                    classItems.Add(new CompletionItem
                    {
                        Label = name,
                        Kind = CompletionItemKind.Property,
                        Detail = workspace.FormatType(result.Types, getter.ReturnType.ID)
                    });
                }
            }

            foreach (var iface in Nebra.IR.Type.ImplementedInterfaces(classType))
            {
                foreach (var (name, field) in iface.Fields)
                {
                    if (!seen.Add(name)) continue;
                    classItems.Add(new CompletionItem
                    {
                        Label = name,
                        Kind = CompletionItemKind.Field,
                        Detail = workspace.FormatType(result.Types, field.Type.ID)
                    });
                }

                foreach (var (name, method) in iface.Methods)
                {
                    if (name.StartsWith("__")) continue;
                    if (!seen.Add(name)) continue;
                    classItems.Add(new CompletionItem
                    {
                        Label = name,
                        Kind = CompletionItemKind.Method,
                        Detail = workspace.FormatType(result.Types, method.ID)
                    });
                }
            }

            foreach (var (name, method) in Nebra.IR.Type.EnumerateExtensions(classType, result.Types.PrimFunction))
            {
                if (name.StartsWith("__")) continue;
                if (!seen.Add(name)) continue;
                classItems.Add(new CompletionItem
                {
                    Label = name,
                    Kind = CompletionItemKind.Method,
                    Detail = "(extension) " + workspace.FormatType(result.Types, method.ID)
                });
            }

            if (classType.ConstructorType != null)
            {
                classItems.Add(new CompletionItem
                {
                    Label = "new",
                    Kind = CompletionItemKind.Constructor,
                    Detail = workspace.FormatType(result.Types, classType.ConstructorType.ID)
                });
            }

            return classItems;
        }

        if (finalType is not StructType finalStruct)
        {
            // Primitive (or other non-member) receiver: still offer extension methods
            // declared on it via `extend number` / `extend string`.
            var extItems = new List<CompletionItem>();
            var extSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, method) in Nebra.IR.Type.EnumerateExtensions(finalType, result.Types.PrimFunction))
            {
                if (name.StartsWith("__")) continue;
                if (!extSeen.Add(name)) continue;
                extItems.Add(new CompletionItem
                {
                    Label = name,
                    Kind = CompletionItemKind.Method,
                    Detail = "(extension) " + workspace.FormatType(result.Types, method.ID)
                });
            }
            return extItems;
        }

        var structItems = new List<CompletionItem>();
        foreach (var f in finalStruct.Fields)
        {
            structItems.Add(new CompletionItem
            {
                Label = f.Name.Name,
                Kind = f.Type is FunctionType ? CompletionItemKind.Method : CompletionItemKind.Field,
                Detail = workspace.FormatType(result.Types, f.Type.ID)
            });
        }

        return structItems;
    }

    private List<CompletionItem>? TryImportPathCompletion(AnalysisResult result,
        Position pos)
    {
        var lines = result.SourceText.Split('\n');
        if (pos.Line < 0 || pos.Line >= lines.Length) return null;
        var lineText = lines[pos.Line].TrimEnd('\r');

        var trimmed = lineText.TrimStart();
        if (!trimmed.StartsWith("import ") && !trimmed.StartsWith("from ")) return null;

        var cursor = Math.Min(pos.Character, lineText.Length);
        var quoteChar = '\0';
        var quoteStart = -1;
        for (var i = 0; i < cursor; i++)
        {
            if (lineText[i] == '"' || lineText[i] == '\'')
            {
                if (quoteChar == '\0')
                {
                    quoteChar = lineText[i];
                    quoteStart = i + 1;
                }
                else if (lineText[i] == quoteChar)
                {
                    quoteChar = '\0';
                    quoteStart = -1;
                }
            }
        }

        if (quoteChar == '\0' || quoteStart < 0) return null;

        var partial = lineText.Substring(quoteStart, cursor - quoteStart);
        var fileDir = Path.GetDirectoryName(result.FilePath);
        if (fileDir == null) return null;

        string searchDir;
        string prefix;
        var lastSlash = partial.LastIndexOfAny(['/', '\\']);
        if (lastSlash >= 0)
        {
            var relDir = partial[..(lastSlash + 1)];
            searchDir = Path.GetFullPath(Path.Combine(fileDir, relDir));
            prefix = relDir;
        }
        else
        {
            searchDir = fileDir;
            prefix = partial.StartsWith("./") ? "./" : "";
        }

        if (!Directory.Exists(searchDir)) return null;

        var items = new List<CompletionItem>();
        foreach (var dir in Directory.GetDirectories(searchDir))
        {
            var name = Path.GetFileName(dir);
            items.Add(new CompletionItem
            {
                Label = name,
                Kind = CompletionItemKind.Folder,
                InsertText = prefix + name + "/"
            });
        }

        foreach (var file in Directory.GetFiles(searchDir, "*.neb"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(result.FilePath),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            items.Add(new CompletionItem
            {
                Label = name,
                Kind = CompletionItemKind.File,
                InsertText = prefix + name
            });
        }

        foreach (var file in Directory.GetFiles(searchDir, "*.d.neb"))
        {
            var name = Path.GetFileName(file);
            var baseName = name[..^6];
            items.Add(new CompletionItem
            {
                Label = baseName,
                Kind = CompletionItemKind.File,
                InsertText = prefix + baseName
            });
        }

        return items;
    }

    private List<CompletionItem>? TryImportSpecifierCompletion(AnalysisResult result,
        OmniSharp.Extensions.LanguageServer.Protocol.Models.Position pos)
    {
        var lines = result.SourceText.Split('\n');
        if (pos.Line < 0 || pos.Line >= lines.Length) return null;
        var lineText = lines[pos.Line].TrimEnd('\r');

        var trimmed = lineText.TrimStart();
        if (!trimmed.StartsWith("import ")) return null;

        var cursor = Math.Min(pos.Character, lineText.Length);
        var braceIdx = lineText.IndexOf('{');
        var closeBraceIdx = lineText.IndexOf('}');
        if (braceIdx < 0 || cursor <= braceIdx || (closeBraceIdx >= 0 && cursor > closeBraceIdx))
            return null;

        string? moduleName = null;
        foreach (var stmt in result.Hir.Body)
        {
            if (stmt is not ImportStmt import) continue;
            if (import.Span.StartLn != pos.Line + 1) continue;
            moduleName = import.Module.Name;
            break;
        }

        if (moduleName == null) return null;

        var exports = workspace.CollectExportsFromModule(result, moduleName);
        if (exports == null) return null;

        var items = new List<CompletionItem>();
        foreach (var (name, info) in exports)
        {
            var typeStr = workspace.FormatType(result.Types, info.Type.ID);
            items.Add(new CompletionItem
            {
                Label = name,
                Kind = info.SymKind == IR.SymbolKind.Function
                    ? CompletionItemKind.Function
                    : CompletionItemKind.Variable,
                Detail = typeStr
            });
        }

        return items;
    }

    /// <summary>
    /// Detects whether the cursor is in an annotation context (after <c>@</c>) and returns
    /// completion items for known annotation names discovered from <c>Config.Annotations</c>.
    /// </summary>
    private List<CompletionItem>? TryAnnotationCompletion(AnalysisResult result,
        Position pos)
    {
        var lines = result.SourceText.Split('\n');
        if (pos.Line < 0 || pos.Line >= lines.Length) return null;
        var lineText = lines[pos.Line].TrimEnd('\r');
        var cursor = Math.Min(pos.Character, lineText.Length);

        var i = cursor;
        while (i > 0 && (char.IsLetterOrDigit(lineText[i - 1]) || lineText[i - 1] == '_')) i--;
        if (i <= 0 || lineText[i - 1] != '@') return null;

        var metas = workspace.GetAnnotationMetas();
        if (metas.Count == 0) return null;

        var items = new List<CompletionItem>();
        foreach (var meta in metas)
        {
            var paramLabels = meta.Parameters.Select(p =>
            {
                var s = $"{p.Name}: {p.TypeName}";
                return p.Required ? s : s + "?";
            });
            var paramDisplay = meta.Parameters.Count == 0 ? "" : "(" + string.Join(", ", paramLabels) + ")";

            string insertText;
            InsertTextFormat insertFormat;
            if (meta.Parameters.Count == 0)
            {
                insertText = meta.Name;
                insertFormat = InsertTextFormat.PlainText;
            }
            else
            {
                var snippetArgs = meta.Parameters
                    .Select((p, i) => $"{p.Name} = ${{{i + 1}:{DefaultSnippetValue(p)}}}");
                insertText = $"{meta.Name}({string.Join(", ", snippetArgs)})";
                insertFormat = InsertTextFormat.Snippet;
            }

            items.Add(new CompletionItem
            {
                Label = meta.Name,
                Kind = CompletionItemKind.Function,
                Detail = $"(annotation) @{meta.Name}{paramDisplay}",
                Documentation = new StringOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = $"```nebra\n{NebraWorkspace.FormatAnnotationSignature(meta)}\n```"
                }),
                InsertText = insertText,
                InsertTextFormat = insertFormat,
            });
        }

        return items;
    }

    private static string DefaultSnippetValue(Nebra.Compiler.Annotations.AnnotationParamSpec p)
    {
        return p.TypeName.ToLowerInvariant() switch
        {
            "string" => "\"\"",
            "number" => "0",
            "boolean" or "bool" => "false",
            "table" => "{}",
            "nil" => "nil",
            _ => "..."
        };
    }

    /// <summary>
    /// Walks the class inheritance chain looking for a member named
    /// <paramref name="name"/>. Returns its type via <paramref name="typeId"/>
    /// when found. Required so a chain like <c>player.GetValue</c> resolves
    /// even when <c>GetValue</c> is defined on <c>Entity</c>, the base class
    /// of <c>Player</c>.
    /// </summary>
    private static bool TryLookupClassMember(ClassType ct, string name, out TypID typeId)
    {
        for (var cur = ct; cur != null; cur = cur.BaseClass)
        {
            if (cur.InstanceFields.TryGetValue(name, out var f)) { typeId = f.Type.ID; return true; }
            if (cur.Methods.TryGetValue(name, out var m)) { typeId = m.ID; return true; }
            if (cur.Getters.TryGetValue(name, out var g)) { typeId = g.ReturnType.ID; return true; }
            if (cur.StaticMethods.TryGetValue(name, out var sm)) { typeId = sm.ID; return true; }
        }
        typeId = TypID.Invalid;
        return false;
    }

    /// <summary>
    /// Walks the interface inheritance chain (breadth-first across BaseInterfaces)
    /// looking for a member named <paramref name="name"/>. Companion to
    /// <see cref="TryLookupClassMember"/>.
    /// </summary>
    private static bool TryLookupInterfaceMember(InterfaceType it, string name, out TypID typeId)
    {
        var visited = new HashSet<InterfaceType>();
        var queue = new Queue<InterfaceType>();
        queue.Enqueue(it);
        while (queue.TryDequeue(out var cur))
        {
            if (!visited.Add(cur)) continue;
            if (cur.Fields.TryGetValue(name, out var f)) { typeId = f.Type.ID; return true; }
            if (cur.Methods.TryGetValue(name, out var m)) { typeId = m.ID; return true; }
            foreach (var b in cur.BaseInterfaces) queue.Enqueue(b);
        }
        typeId = TypID.Invalid;
        return false;
    }

    private static TypID StripNil(TypeTable types, TypID id)
    {
        if (!types.GetByID(id, out var t)) return id;
        if (t is UnionType u)
        {
            var nonNil = u.Types.Where(m => m.Kind != TypeKind.PrimitiveNil).ToList();
            if (nonNil.Count == 1) return nonNil[0].ID;
            if (nonNil.Count > 1) return types.UnionOf(nonNil);
        }

        return id;
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken ct)
        => Task.FromResult(request);

    protected override CompletionRegistrationOptions CreateRegistrationOptions(
        CompletionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra"),
            TriggerCharacters = new Container<string>(".", ":", "?", "/", "\"", "'", " ", "@"),
            ResolveProvider = false
        };
    }
}