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
        "protected", "sealed", "static", "super", "when", "typeof", "instanceof", "keyof",
        "defer", "guard", "continue", "try", "catch", "finally"
    ];

    /// <summary>The keywords an expression can use, offered between the braces of an interpolated string.</summary>
    private static readonly string[] ExpressionKeywords =
    [
        "and", "or", "not", "nil", "true", "false", "function", "new", "await", "typeof", "instanceof", "match", "as", "super"
    ];

    public override async Task<CompletionList> Handle(CompletionParams request, CancellationToken ct)
    {
        var items = new List<CompletionItem>();

        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result != null)
        {
            var line = request.Position.Line + 1;
            var col = request.Position.Character + 1;

            var reflectionItems = TryReflectionIdCompletion(result, request.Position);
            if (reflectionItems != null)
                return new CompletionList(reflectionItems);

            var metaItems = workspace.ProjectFor(result.FilePath).IsAnnotationFile(result.FilePath)
                ? TryAnnotationMetaCompletion(result, request.Position)
                : null;
            if (metaItems != null)
                return new CompletionList(metaItems);

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
                return new CompletionList(WithCallParentheses(memberItems, result, request.Position));
            }

            var lineText = LineText(result, request.Position.Line);
            var typePosition = TypePositionAtCursor.Find(lineText, request.Position.Character);
            var typeContext = typePosition != null;
            var stringAtCursor = StringAtCursor.Find(lineText, request.Position.Character);
            var openString = stringAtCursor.OpenStart;
            if (openString is { } template && lineText[template] == '`')
                return new CompletionList();

            var node = NodeFinder.Find(result.Hir, line, col);
            var scopeId = result.Package.Root;
            if (node != null)
                result.Scopes.EnclosingScope(node.ID, out scopeId);

            var symbols = workspace.CollectVisibleSymbols(result, scopeId);

            if (typePosition?.KeysOf is { } keysOf && FieldNameItems(result, symbols, keysOf, lineText, request.Position) is { Count: > 0 } keyItems)
                return new CompletionList(keyItems);
            if (typeContext && openString != null)
                return new CompletionList();

            var expected = typeContext || stringAtCursor.InInterpolation ? null : ExpectedTypeAtCursor.Find(result, line, openString is { } quote ? quote + 1 : col, symbols);
            if (expected != null)
            {
                var literals = LiteralCompletion.ValuesOf(expected, result.Types);
                if (literals.Count > 0)
                {
                    var literalItems = LiteralCompletion.Items(literals, workspace.FormatType(result.Types, expected.ID), lineText, request.Position);
                    if (openString != null) return new CompletionList(literalItems);
                    items.AddRange(literalItems);
                }
            }

            if (!typeContext)
            {
                foreach (var kw in stringAtCursor.InInterpolation ? ExpressionKeywords : Keywords)
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
                items.Add(new CompletionItem { Label = "keyof", Kind = CompletionItemKind.Keyword });
                foreach (var prim in new[] { "string", "number", "boolean", "any", "void", "nil", "never", "function", "thread", "userdata" })
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

            var symbolTypes = new Dictionary<string, NebraType>(StringComparer.Ordinal);
            foreach (var sym in symbols)
            {
                var kind = DeclaredKind(result, sym);
                if (typeContext && !IsTypeKind(kind))
                    continue;

                if (!IsTypeKind(kind) && result.Types.GetByID(sym.Type, out var symbolType))
                    symbolTypes.TryAdd(sym.Name, symbolType);

                var typeStr = workspace.FormatType(result.Types, sym.Type);
                items.Add(new CompletionItem
                {
                    Label = sym.Name,
                    Kind = AutoImportCompletion.KindOf(kind),
                    Detail = typeStr,
                    Documentation = BuildSymbolDocumentation(result, sym),
                    Tags = IsDeprecated(result, sym) ? new Container<CompletionItemTag>(CompletionItemTag.Deprecated) : null
                });
            }

            var visible = symbols.Select(sym => sym.Name).ToHashSet(StringComparer.Ordinal);
            items.AddRange(AutoImportCompletion.Items(result, workspace.LatestSnapshotFor(result.FilePath), request.Position, typeContext, visible));

            if (typeContext)
            {
                items = WithTypeArguments(items, result, symbols, lineText, request.Position);
            }

            if (expected != null)
            {
                items = RankByExpectedType(items, expected, symbolTypes, symbols);
            }

            if (!typeContext)
            {
                items = WithCallParentheses(items, result, request.Position);
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

    /// <summary>
    /// Orders <paramref name="items"/> by what fits <paramref name="expected"/>, the type a value
    /// written at the cursor should have: the members of an expected enum first, written without
    /// their enum where no declaration in scope takes the name, the first of them preselected;
    /// then the values and functions whose type or return type fits; then everything else.
    /// </summary>
    private static List<CompletionItem> RankByExpectedType(List<CompletionItem> items, NebraType expected,
        IReadOnlyDictionary<string, NebraType> symbolTypes, IReadOnlyList<Symbol> visible)
    {
        var declared = visible.Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);
        var members = EnumsIn(expected)
            .SelectMany(enumType => enumType.Members.Select(member => (Enum: enumType, Member: member)))
            .Select((entry, index) =>
            {
                var shorthand = !declared.Contains(entry.Member.Name);
                return new CompletionItem
                {
                    Label = shorthand ? entry.Member.Name : $"{entry.Enum.Name}.{entry.Member.Name}",
                    Kind = CompletionItemKind.EnumMember,
                    Detail = $"{entry.Enum.Name}.{entry.Member.Name}",
                    SortText = $"0_{index:D4}",
                    Preselect = index == 0
                };
            })
            .ToList();

        var ranked = items.Select(item => item with
        {
            SortText = item.SortText ?? RankOf(item, expected, symbolTypes) + item.Label,
            Preselect = members.Count == 0 && item.Preselect
        });
        return members.Concat(ranked).ToList();
    }

    private static string RankOf(CompletionItem item, NebraType expected, IReadOnlyDictionary<string, NebraType> symbolTypes)
    {
        if (item.Kind == CompletionItemKind.Keyword) return KeywordFits(item.Label, expected) ? "1_" : "3_";
        if (!symbolTypes.TryGetValue(item.Label, out var type)) return "2_";

        var value = type is FunctionType function && IsCallable(item) ? function.ReturnType : type;
        return ExpectedTypeAtCursor.Fits(value, expected) ? "1_" : "2_";
    }

    private static bool KeywordFits(string keyword, NebraType expected)
    {
        var members = expected is UnionType union ? union.Types : [expected];
        return keyword switch
        {
            "true" or "false" => members.Any(member => member.Kind == TypeKind.PrimitiveBool),
            "nil" => members.Any(member => member.Kind == TypeKind.PrimitiveNil),
            "new" => members.Any(member => member is ClassType),
            "function" => members.Any(member => member is FunctionType),
            _ => false,
        };
    }

    private static IEnumerable<EnumType> EnumsIn(NebraType expected)
    {
        return expected switch
        {
            EnumType enumType => [enumType],
            UnionType union => union.Types.OfType<EnumType>(),
            _ => [],
        };
    }

    /// <summary>
    /// The kind of declaration <paramref name="symbol"/> names: for an imported name, that of the
    /// declaration it was imported from, so an imported class completes as a class and in type
    /// positions, rather than as the variable the import binds.
    /// </summary>
    private static NebraSymbolKind DeclaredKind(AnalysisResult result, Symbol symbol)
    {
        return result.Snapshot.TryGetSymbol(result.Snapshot.Origin(symbol.ID), out var origin) ? origin.Kind : symbol.Kind;
    }

    /// <summary>
    /// Completes functions and methods as a call: their name followed by parentheses with the
    /// cursor between them, opening the signature help. Left alone when the cursor already sits
    /// before an opening parenthesis, or when an item brings its own text.
    /// </summary>
    private static List<CompletionItem> WithCallParentheses(List<CompletionItem> items, AnalysisResult result, Position position)
    {
        var lines = result.SourceText.Split('\n');
        var rest = position.Line < lines.Length ? lines[position.Line].TrimEnd('\r')[Math.Min(position.Character, lines[position.Line].TrimEnd('\r').Length)..] : "";
        if (rest.TrimStart().StartsWith('(')) return items;

        return items.Select(item => IsCallable(item) && item.InsertText == null && item.TextEdit == null
                ? item with
                {
                    InsertText = $"{item.Label}($0)",
                    InsertTextFormat = InsertTextFormat.Snippet,
                    Command = new Command
                    {
                        Name = "editor.action.triggerParameterHints",
                        Title = "Trigger parameter hints"
                    }
                }
                : item)
            .ToList();
    }

    private static bool IsCallable(CompletionItem item)
    {
        return item.Kind is CompletionItemKind.Method or CompletionItemKind.Function;
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

    private static string LineText(AnalysisResult result, int line)
    {
        var lines = result.SourceText.Split('\n');
        return line >= 0 && line < lines.Length ? lines[line].TrimEnd('\r') : "";
    }

    /// <summary>
    /// The field names of the type <paramref name="typeText"/> names, as string literals: the keys
    /// of <c>Pick&lt;User, |&gt;</c>, <c>Omit&lt;User, |&gt;</c> and <c>User[|]</c>.
    /// </summary>
    private List<CompletionItem>? FieldNameItems(AnalysisResult result, IReadOnlyList<Symbol> visible, string typeText, string lineText, Position position)
    {
        var name = typeText.Split('<')[0].Trim();
        var symbol = visible.FirstOrDefault(candidate => candidate.Name == name && IsTypeKind(DeclaredKind(result, candidate)));
        if (symbol == null || !result.Types.GetByID(symbol.Type, out var shape)) return null;

        return LiteralCompletion.Items(LiteralCompletion.FieldNamesOf(shape), $"field of {name}", lineText, position);
    }

    /// <summary>
    /// Completes a generic class or interface in a type position with its type argument list, the
    /// cursor inside it, unless a <c>&lt;</c> already follows the cursor.
    /// </summary>
    private static List<CompletionItem> WithTypeArguments(List<CompletionItem> items, AnalysisResult result, IReadOnlyList<Symbol> visible, string lineText, Position position)
    {
        var rest = lineText[Math.Min(position.Character, lineText.Length)..];
        if (rest.TrimStart().StartsWith('<')) return items;

        var generic = visible
            .Where(symbol => result.Types.GetByID(symbol.Type, out var type) && TypeParameterCount(type) > 0)
            .Select(symbol => symbol.Name)
            .ToHashSet(StringComparer.Ordinal);

        return items.Select(item => generic.Contains(item.Label) && item.InsertText == null && item.TextEdit == null
                ? item with { InsertText = $"{item.Label}<$0>", InsertTextFormat = InsertTextFormat.Snippet }
                : item)
            .ToList();
    }

    private static int TypeParameterCount(NebraType type)
    {
        return type switch
        {
            ClassType classType => classType.TypeParams.Count,
            InterfaceType interfaceType => interfaceType.TypeParams.Count,
            _ => 0,
        };
    }

    /// <summary>
    /// Detects whether the cursor position is preceded by a member-access operator (`.` or `?.`),
    /// resolves the receiver chain to a type, and returns its struct fields as completion items.
    /// Returns null if this is not a member-completion context.
    /// </summary>
    /// <summary>The extension getters visible on <paramref name="type"/> whose names are not taken yet.</summary>
    private IEnumerable<CompletionItem> ExtensionGetterItems(Nebra.IR.Type type, AnalysisResult result, HashSet<string> seen)
    {
        foreach (var getter in Nebra.IR.Type.EnumerateExtensionGetters(type, result.Types.PrimFunction))
        {
            if (getter.Signature == null || !seen.Add(getter.Name.Name)) continue;
            yield return new CompletionItem
            {
                Label = getter.Name.Name,
                Kind = CompletionItemKind.Property,
                Detail = "(extension) " + workspace.FormatType(result.Types, getter.Signature.ReturnType.ID)
            };
        }
    }

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

            classItems.AddRange(ExtensionGetterItems(classType, result, seen));

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
            extItems.AddRange(ExtensionGetterItems(finalType, result, extSeen));
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
    /// Completes an annotation name right after <c>@</c>, offering only the annotations that may
    /// be put on the declaration below, and the arguments inside <c>@Name(...)</c>: the
    /// parameters not given yet, or the sides for <c>@side</c>.
    /// </summary>
    private static List<CompletionItem>? TryAnnotationCompletion(AnalysisResult result, Position pos)
    {
        var call = AnnotationCatalog.CallAt(result.SourceText, pos.Line, pos.Character);
        if (call != null)
        {
            return AnnotationArgumentItems(result, call.Value.Name, call.Value.Arguments);
        }

        var lines = result.SourceText.Split('\n');
        if (pos.Line < 0 || pos.Line >= lines.Length) return null;

        var lineText = lines[pos.Line].TrimEnd('\r');
        var start = Math.Min(pos.Character, lineText.Length);
        while (start > 0 && (char.IsLetterOrDigit(lineText[start - 1]) || lineText[start - 1] == '_')) start--;
        if (start <= 0 || lineText[start - 1] != '@') return null;

        var target = AnnotationCatalog.TargetAt(result, pos.Line + 1);
        return AnnotationCatalog.All(result)
            .Where(entry => AnnotationCatalog.AppliesTo(entry, target))
            .Select(AnnotationItem)
            .ToList();
    }

    /// <summary>
    /// Completes the id inside <c>reflect.get("...")</c>, <c>reflect.ref("...")</c> and
    /// <c>reflect.implementorsOf("...")</c> with the ids the project's declarations are registered
    /// under, replacing what was typed of the id so far.
    /// </summary>
    private List<CompletionItem>? TryReflectionIdCompletion(AnalysisResult result, Position pos)
    {
        var lines = result.SourceText.Split('\n');
        if (pos.Line < 0 || pos.Line >= lines.Length) return null;

        var text = lines[pos.Line].TrimEnd('\r');
        var argument = ReflectionInfo.IdArgumentAt(text[..Math.Min(pos.Character, text.Length)]);
        if (argument == null) return null;

        var start = new Position(pos.Line, pos.Character - argument.Value.Typed.Length);
        return ReflectionInfo.Candidates(ReflectionInfo.Registry(workspace, result), argument.Value.Method)
            .Select(entry => new CompletionItem
            {
                Label = entry.Id,
                Kind = CompletionItemKind.Reference,
                Detail = $"{entry.Kind} {entry.Name}",
                FilterText = entry.Id,
                TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(start, pos),
                    NewText = entry.Id
                })
            })
            .ToList();
    }

    /// <summary>
    /// Completes the strings of an annotation file's <c>annotation</c> table: the declaration
    /// kinds inside <c>targets = { ... }</c> and the parameter types after <c>type =</c>.
    /// </summary>
    private static List<CompletionItem>? TryAnnotationMetaCompletion(AnalysisResult result, Position pos)
    {
        var lines = result.SourceText.Split('\n');
        if (pos.Line < 0 || pos.Line >= lines.Length) return null;

        var before = lines[pos.Line].TrimEnd('\r')[..Math.Min(pos.Character, lines[pos.Line].TrimEnd('\r').Length)];
        var quote = before.LastIndexOf('"');
        if (quote < 0 || before.Count(character => character == '"') % 2 == 0) return null;

        var prefix = before[..quote].TrimEnd();
        if (prefix.EndsWith("type =") || prefix.EndsWith("type="))
        {
            return new[] { "string", "number", "boolean", "table" }
                .Select(type => new CompletionItem { Label = type, Kind = CompletionItemKind.TypeParameter })
                .ToList();
        }

        if (!InsideTargetsList(lines, pos.Line, quote)) return null;

        return Enum.GetValues<Nebra.Compiler.Annotations.AnnotationTargetKind>()
            .Select(target => new CompletionItem
            {
                Label = target.ToString(),
                Kind = CompletionItemKind.EnumMember,
                Detail = AnnotationCatalog.Describe(target)
            })
            .ToList();
    }

    /// <summary>Whether the text before the given position sits in the braces opened after <c>targets =</c>.</summary>
    private static bool InsideTargetsList(string[] lines, int line, int character)
    {
        var text = string.Join("\n", lines.Take(line)) + "\n" + lines[line][..character];
        var open = text.LastIndexOf('{');
        if (open < 0 || text.IndexOf('}', open) >= 0) return false;

        var key = text[..open].TrimEnd();
        return key.EndsWith("targets =") || key.EndsWith("targets=");
    }

    private static CompletionItem AnnotationItem(AnnotationEntry entry)
    {
        var required = entry.Parameters.Where(parameter => parameter.Required).ToList();
        var snippet = entry.TakesNames || required.Count == 0
            ? entry.Name
            : $"{entry.Name}({string.Join(", ", required.Select((parameter, index) => $"{parameter.Name} = ${{{index + 1}:{DefaultSnippetValue(parameter)}}}"))})";

        var documentation = $"```nebra\n{AnnotationCatalog.Signature(entry)}\n```\n\nApplies to {AnnotationCatalog.TargetsText(entry)}";
        if (!string.IsNullOrEmpty(entry.Documentation)) documentation += $"\n\n{entry.Documentation}";

        return new CompletionItem
        {
            Label = entry.Name,
            Kind = CompletionItemKind.Function,
            Detail = AnnotationCatalog.Signature(entry),
            Documentation = new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = documentation }),
            InsertText = snippet,
            InsertTextFormat = snippet == entry.Name ? InsertTextFormat.PlainText : InsertTextFormat.Snippet
        };
    }

    private static List<CompletionItem>? AnnotationArgumentItems(AnalysisResult result, string name, List<string> arguments)
    {
        var entry = AnnotationCatalog.Find(result, name);
        if (entry == null) return null;

        if (entry.TakesNames)
        {
            return new[] { "client", "server", "shared" }
                .Where(side => !arguments.Contains(side))
                .Select(side => new CompletionItem { Label = side, Kind = CompletionItemKind.EnumMember })
                .ToList();
        }

        var given = arguments.Select(argument => argument.Split('=')[0].Trim()).ToHashSet(StringComparer.Ordinal);
        return entry.Parameters
            .Where(parameter => !given.Contains(parameter.Name))
            .Select(parameter => new CompletionItem
            {
                Label = parameter.Name,
                Kind = CompletionItemKind.Property,
                Detail = AnnotationCatalog.Parameter(parameter),
                InsertText = $"{parameter.Name} = ${{1:{DefaultSnippetValue(parameter)}}}",
                InsertTextFormat = InsertTextFormat.Snippet
            })
            .ToList();
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