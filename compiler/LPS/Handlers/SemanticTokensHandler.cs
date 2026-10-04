using Antlr4.Runtime;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using NebraSymbolKind = Nebra.IR.SymbolKind;

namespace Nebra.LPS.Handlers;

public sealed class SemanticTokensHandler(NebraWorkspace workspace) : SemanticTokensHandlerBase
{
    private const int TK_KEYWORD = 0;
    private const int TK_STRING = 1;
    private const int TK_NUMBER = 2;
    private const int TK_COMMENT = 3;
    private const int TK_VARIABLE = 4;
    private const int TK_FUNCTION = 5;
    private const int TK_OPERATOR = 6;
    private const int TK_TYPE = 7;
    private const int TK_ENUM_MEMBER = 8;
    private const int TK_TYPE_PARAMETER = 9;
    private const int TK_DECORATOR = 10;

    private static readonly string[] TokenTypes =
        ["keyword", "string", "number", "comment", "variable", "function", "operator", "type", "enumMember", "typeParameter", "decorator"];

    private static readonly string[] TokenModifiers = ["declaration", "readonly"];

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
        SemanticTokensCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra"),
            Legend = new SemanticTokensLegend
            {
                TokenTypes = new Container<SemanticTokenType>(
                    TokenTypes.Select(t => new SemanticTokenType(t))),
                TokenModifiers = new Container<SemanticTokenModifier>(
                    TokenModifiers.Select(m => new SemanticTokenModifier(m)))
            },
            Full = new SemanticTokensCapabilityRequestFull { Delta = false },
            Range = true
        };
    }

    protected override async Task Tokenize(SemanticTokensBuilder builder, ITextDocumentIdentifierParams request,
        CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return;

        var semanticOverrides = BuildSemanticOverrides(result);

        result.TokenStream.Fill();
        var tokens = result.TokenStream.GetTokens();

        foreach (var token in tokens)
        {
            if (token.Type == -1) continue;
            if (token.Channel != 0 && token.Type != NebraLexer.LONG_COMMENT && token.Type != NebraLexer.LINE_COMMENT)
                continue;

            var line = token.Line - 1;
            var col = token.Column;
            var length = token.Text?.Length ?? 0;
            if (length <= 0) continue;

            var key = (line, col);
            if (semanticOverrides.TryGetValue(key, out var semType))
            {
                builder.Push(line, col, length, semType, 0);
                continue;
            }

            var typeIndex = ClassifyToken(token.Type);
            if (typeIndex < 0) continue;

            builder.Push(line, col, length, typeIndex, 0);
        }

        return;
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
        ITextDocumentIdentifierParams request, CancellationToken ct)
    {
        return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
    }

    private static Dictionary<(int Line, int Col), int> BuildSemanticOverrides(AnalysisResult result)
    {
        var overrides = new Dictionary<(int, int), int>();
        var nameRefs = NodeFinder.CollectAllNameRefs(result.Hir);
        foreach (var nr in nameRefs)
        {
            if (nr.Sym == SymID.Invalid) continue;
            if (!result.Syms.GetByID(nr.Sym, out var sym)) continue;

            var tokenType = sym.Kind switch
            {
                NebraSymbolKind.Function => TK_FUNCTION,
                NebraSymbolKind.Enum => TK_TYPE,
                NebraSymbolKind.Class => TK_TYPE,
                NebraSymbolKind.Interface => TK_TYPE,
                NebraSymbolKind.TypeParam => TK_TYPE_PARAMETER,
                _ => -1
            };

            if (tokenType < 0) continue;

            var line = nr.Span.StartLn - 1;
            var col = nr.Span.StartCol - 1;
            overrides[(line, col)] = tokenType;
        }

        CollectEnumMemberTokens(result, result.Hir.Body, overrides);
        CollectAnnotationTokens(result, overrides);

        return overrides;
    }

    private static void CollectEnumMemberTokens(AnalysisResult result, List<Stmt> stmts, Dictionary<(int, int), int> overrides)
    {
        foreach (var stmt in stmts) CollectEnumMemberFromStmt(result, stmt, overrides);
    }

    private static void CollectEnumMemberFromStmt(AnalysisResult result, Stmt stmt, Dictionary<(int, int), int> overrides)
    {
        switch (stmt)
        {
            case EnumDecl ed:
                foreach (var m in ed.Members)
                {
                    var line = m.Name.Span.StartLn - 1;
                    var col = m.Name.Span.StartCol - 1;
                    overrides[(line, col)] = TK_ENUM_MEMBER;
                }
                break;
            case ExportStmt exp:
                CollectEnumMemberFromStmt(result, exp.Declaration, overrides);
                break;
            case FunctionDecl fd:
                CollectEnumMemberTokens(result, fd.Body, overrides);
                if (fd.ReturnStmt != null) CollectEnumMemberFromStmt(result, fd.ReturnStmt, overrides);
                break;
            case LocalFunctionDecl lfd:
                CollectEnumMemberTokens(result, lfd.Body, overrides);
                if (lfd.ReturnStmt != null) CollectEnumMemberFromStmt(result, lfd.ReturnStmt, overrides);
                break;
            case LocalDecl ld:
                foreach (var v in ld.Values) CollectEnumMemberFromExpr(result, v, overrides);
                break;
            case AssignStmt a:
                foreach (var t in a.Targets) CollectEnumMemberFromExpr(result, t, overrides);
                foreach (var v in a.Values) CollectEnumMemberFromExpr(result, v, overrides);
                break;
            case ExprStmt es:
                CollectEnumMemberFromExpr(result, es.Expression, overrides);
                break;
            case DoBlockStmt db:
                CollectEnumMemberTokens(result, db.Body, overrides);
                break;
            case WhileStmt ws:
                CollectEnumMemberFromExpr(result, ws.Condition, overrides);
                CollectEnumMemberTokens(result, ws.Body, overrides);
                break;
            case RepeatStmt rs:
                CollectEnumMemberTokens(result, rs.Body, overrides);
                CollectEnumMemberFromExpr(result, rs.Condition, overrides);
                break;
            case IfStmt ifs:
                CollectEnumMemberFromExpr(result, ifs.Condition, overrides);
                CollectEnumMemberTokens(result, ifs.Body, overrides);
                foreach (var ei in ifs.ElseIfs)
                {
                    CollectEnumMemberFromExpr(result, ei.Condition, overrides);
                    CollectEnumMemberTokens(result, ei.Body, overrides);
                }
                if (ifs.ElseBody != null) CollectEnumMemberTokens(result, ifs.ElseBody, overrides);
                break;
            case NumericForStmt nf:
                CollectEnumMemberFromExpr(result, nf.Start, overrides);
                CollectEnumMemberFromExpr(result, nf.Limit, overrides);
                if (nf.Step != null) CollectEnumMemberFromExpr(result, nf.Step, overrides);
                CollectEnumMemberTokens(result, nf.Body, overrides);
                break;
            case GenericForStmt gf:
                foreach (var iter in gf.Iterators) CollectEnumMemberFromExpr(result, iter, overrides);
                CollectEnumMemberTokens(result, gf.Body, overrides);
                break;
            case ReturnStmt ret:
                foreach (var v in ret.Values) CollectEnumMemberFromExpr(result, v, overrides);
                break;
        }
    }

    private static void CollectEnumMemberFromExpr(AnalysisResult result, Expr expr, Dictionary<(int, int), int> overrides)
    {
        switch (expr)
        {
            case DotAccessExpr dot:
                CollectEnumMemberFromExpr(result, dot.Object, overrides);
                if (dot.Object is NameExpr ne && ne.Name.Sym != SymID.Invalid &&
                    result.Syms.GetByID(ne.Name.Sym, out var sym) && sym.Kind == NebraSymbolKind.Enum)
                {
                    var line = dot.FieldName.Span.StartLn - 1;
                    var col = dot.FieldName.Span.StartCol - 1;
                    overrides[(line, col)] = TK_ENUM_MEMBER;
                }
                break;
            case ParenExpr pe:
                CollectEnumMemberFromExpr(result, pe.Inner, overrides);
                break;
            case BinaryExpr bin:
                CollectEnumMemberFromExpr(result, bin.Left, overrides);
                CollectEnumMemberFromExpr(result, bin.Right, overrides);
                break;
            case UnaryExpr un:
                CollectEnumMemberFromExpr(result, un.Operand, overrides);
                break;
            case IndexAccessExpr idx:
                CollectEnumMemberFromExpr(result, idx.Object, overrides);
                CollectEnumMemberFromExpr(result, idx.Index, overrides);
                break;
            case FunctionCallExpr call:
                CollectEnumMemberFromExpr(result, call.Callee, overrides);
                foreach (var a in call.Arguments) CollectEnumMemberFromExpr(result, a, overrides);
                break;
            case MethodCallExpr mc:
                CollectEnumMemberFromExpr(result, mc.Object, overrides);
                foreach (var a in mc.Arguments) CollectEnumMemberFromExpr(result, a, overrides);
                break;
            case FunctionDefExpr fd:
                CollectEnumMemberTokens(result, fd.Body, overrides);
                if (fd.ReturnStmt != null) CollectEnumMemberFromStmt(result, fd.ReturnStmt, overrides);
                break;
            case TableConstructorExpr tc:
                foreach (var f in tc.Fields)
                {
                    if (f.Key != null) CollectEnumMemberFromExpr(result, f.Key, overrides);
                    CollectEnumMemberFromExpr(result, f.Value, overrides);
                }
                break;
            case NonNilAssertExpr nna:
                CollectEnumMemberFromExpr(result, nna.Inner, overrides);
                break;
            case TypeCheckExpr tchk:
                CollectEnumMemberFromExpr(result, tchk.Inner, overrides);
                break;
            case TypeCastExpr tcast:
                CollectEnumMemberFromExpr(result, tcast.Inner, overrides);
                break;
        }
    }

    private static int ClassifyToken(int type)
    {
        return type switch
        {
            NebraLexer.AND or NebraLexer.BREAK or NebraLexer.DO or NebraLexer.ELSE or NebraLexer.ELSEIF
                or NebraLexer.END or NebraLexer.FALSE or NebraLexer.FOR or NebraLexer.FUNCTION
                or NebraLexer.GOTO or NebraLexer.IF or NebraLexer.IN or NebraLexer.LOCAL or NebraLexer.NIL
                or NebraLexer.NOT or NebraLexer.OR or NebraLexer.REPEAT or NebraLexer.RETURN
                or NebraLexer.THEN or NebraLexer.TRUE or NebraLexer.UNTIL or NebraLexer.WHILE
                or NebraLexer.AS or NebraLexer.IS or NebraLexer.DECLARE or NebraLexer.EXPORT or NebraLexer.FROM
                or NebraLexer.ASYNC or NebraLexer.AWAIT or NebraLexer.CASE or NebraLexer.ENUM
                or NebraLexer.IMPORT or NebraLexer.MATCH or NebraLexer.META or NebraLexer.MODULE
                or NebraLexer.MUT or NebraLexer.WHEN or NebraLexer.CLASS or NebraLexer.INTERFACE
                or NebraLexer.EXTENDS or NebraLexer.EXTEND or NebraLexer.IMPLEMENTS or NebraLexer.CONSTRUCTOR
                or NebraLexer.STATIC or NebraLexer.NEW or NebraLexer.SUPER
                or NebraLexer.ABSTRACT or NebraLexer.OVERRIDE or NebraLexer.PROTECTED
                or NebraLexer.TYPEOF or NebraLexer.INSTANCEOF
                or NebraLexer.DEFER or NebraLexer.GUARD or NebraLexer.CONTINUE
                => 0, // keyword

            NebraLexer.NORMAL_STRING or NebraLexer.CHAR_STRING or NebraLexer.LONG_STRING
                => 1, // string

            NebraLexer.INT or NebraLexer.HEX or NebraLexer.FLOAT or NebraLexer.HEX_FLOAT
                => 2, // number

            NebraLexer.LONG_COMMENT or NebraLexer.LINE_COMMENT
                => 3, // comment

            NebraLexer.AT => TK_DECORATOR, // annotation @

            NebraLexer.NAME => 4, // variable (default for identifiers)

            _ => -1
        };
    }

    /// <summary>
    /// Adds semantic overrides for annotation names (the NAME token directly after <c>@</c>)
    /// so they highlight as decorators instead of plain variables.
    /// </summary>
    private static void CollectAnnotationTokens(AnalysisResult result, Dictionary<(int, int), int> overrides)
    {
        void WalkAnnotations(List<Annotation> annotations)
        {
            foreach (var ann in annotations)
            {
                var line = ann.Name.Span.StartLn - 1;
                var col = ann.Name.Span.StartCol - 1;
                overrides[(line, col)] = TK_DECORATOR;
            }
        }

        void WalkStmts(List<Stmt> stmts)
        {
            foreach (var stmt in stmts)
            {
                var decl = stmt switch
                {
                    ExportStmt ex => ex.Declaration,
                    Decl d => d,
                    _ => null,
                };
                if (decl == null) continue;

                switch (decl)
                {
                    case FunctionDecl fd:
                        WalkAnnotations(fd.Annotations);
                        foreach (var p in fd.Parameters) WalkAnnotations(p.Annotations);
                        WalkStmts(fd.Body);
                        break;
                    case LocalFunctionDecl lfd:
                        WalkAnnotations(lfd.Annotations);
                        foreach (var p in lfd.Parameters) WalkAnnotations(p.Annotations);
                        WalkStmts(lfd.Body);
                        break;
                    case LocalDecl ld:
                        WalkAnnotations(ld.Annotations);
                        break;
                    case ClassDecl cd:
                        WalkAnnotations(cd.Annotations);
                        foreach (var f in cd.Fields) WalkAnnotations(f.Annotations);
                        foreach (var m in cd.Methods)
                        {
                            WalkAnnotations(m.Annotations);
                            foreach (var p in m.Parameters) WalkAnnotations(p.Annotations);
                        }
                        if (cd.Constructor != null)
                        {
                            WalkAnnotations(cd.Constructor.Annotations);
                            foreach (var p in cd.Constructor.Parameters) WalkAnnotations(p.Annotations);
                        }
                        foreach (var a in cd.Accessors)
                        {
                            WalkAnnotations(a.Annotations);
                            foreach (var p in a.Parameters) WalkAnnotations(p.Annotations);
                        }
                        break;
                    case EnumDecl ed:
                        WalkAnnotations(ed.Annotations);
                        foreach (var m in ed.Members) WalkAnnotations(m.Annotations);
                        break;
                    case InterfaceDecl id:
                        WalkAnnotations(id.Annotations);
                        foreach (var f in id.Fields) WalkAnnotations(f.Annotations);
                        foreach (var m in id.Methods)
                        {
                            WalkAnnotations(m.Annotations);
                            foreach (var p in m.Parameters) WalkAnnotations(p.Annotations);
                        }
                        break;
                }
            }
        }

        WalkStmts(result.Hir.Body);
    }
}
