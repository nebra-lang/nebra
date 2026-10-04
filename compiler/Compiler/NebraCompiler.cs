using Antlr4.Runtime;
using Nebra.Compiler.Passes;
using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;

namespace Nebra.Compiler;

/// <summary>
/// The nebra compiler is the main class that orchestrates the compilation process. It takes care of parsing the source
/// code, performing various compilation passes, and generating the final output. It also manages the overall state of
/// the compilation process, such as the symbol table, the name map, the IR modules, etc. The nebra compiler is
/// designed to be modular and extensible, allowing for easy addition of new features and optimizations in the future.
/// </summary>
public class NebraCompiler
{
    /// <summary>
    /// The diagnostics bag is a collection of diagnostics that are generated during the compilation process.
    /// </summary>
    public DiagnosticsBag Diagnostics { get; } = new();
    
    /// <summary>
    /// A dictionary that maps package paths to their corresponding package contexts.
    /// </summary>
    public Dictionary<string, PackageContext> Packages { get; } = new();
    
    /// <summary>
    /// The NodeID allocator for this compiler run.
    /// </summary>
    public IDAlloc<NodeID> NodeAlloc { get; } = new();
    
    /// <summary>
    /// The SymID allocator for this compiler run.
    /// </summary>
    public IDAlloc<SymID> SymAlloc { get; } = new();
    
    /// <summary>
    /// The ScopeID allocator for this compiler run.
    /// </summary>
    public IDAlloc<ScopeID> ScopeAlloc { get; } = new();
    
    /// <summary>
    /// The global type table that holds all the types that are defined in the whole project.
    /// </summary>
    public TypeTable TypeUniverse { get; } = new(new IDAlloc<TypID>());

    /// <summary>
    /// The name map containing the compilers mangle and original names for all symbols in the whole project.
    /// </summary>
    public NameMap Names { get; } = new();

    /// <summary>
    /// The configuration for the compiler. This controls language-level behavior such as the concat operator,
    /// index base, string interpolation, etc.
    /// </summary>
    public Config Config { get; set; } = new();
    
    /// <summary>
    /// Adds a source file to the compiler. The source file is preparsed and converted into the high-level intermediate
    /// representation (HIR) before being added to the compiler's internal state. If the preparsing process fails, an
    /// error diagnostic is reported and the source file is not added to the compiler.
    /// </summary>
    /// <param name="source">The source code content of the source file.</param>
    public void AddRawSource(string source) => AddSource("", source);
    
    /// <summary>
    /// Adds a source file to the compiler. The source file is preparsed and converted into the high-level intermediate
    /// representation (HIR) before being added to the compiler's internal state. If the preparsing process fails, an
    /// error diagnostic is reported and the source file is not added to the compiler.
    /// </summary>
    /// <param name="file">The filename (full path) of the source file.</param>
    public void AddSource(string file) => AddSource(file, File.ReadAllText(file));

    /// <summary>
    /// Adds a source file to the compiler. The source file is preparsed and converted into the high-level intermediate
    /// representation (HIR) before being added to the compiler's internal state. If the preparsing process fails, an
    /// error diagnostic is reported and the source file is not added to the compiler.
    /// </summary>
    /// <param name="filename">The filename (full path) of the source file.</param>
    /// <param name="source">The source code content of the source file.</param>
    public void AddSource(string filename, string source)
    {
        var file = new PreparsedFile(filename, source);
        if (!Preparse(file))
        {
            return;
        }
        
        if (Packages.TryGetValue(file.Filename ?? string.Empty, out var pkg))
        {
            pkg.Files.Add(file);
        }
        else
        {
            var sc = new ScopeGraph(Diagnostics, ScopeAlloc);
            var newPkg = new PackageContext(filename, new SymbolArena(SymAlloc), sc, TypeUniverse, sc.Root);
            newPkg.Files.Add(file);
            Packages[file.Filename ?? string.Empty] = newPkg;
        }
    }

    /// <summary>
    /// Compiles the added source files into the final output.
    /// </summary>
    /// <returns>true if the compilation was successful, false otherwise.</returns>
    public Dictionary<string, object> Cache { get; } = new();

    /// <summary>
    /// Runs the analysis passes only - binding, name and type resolution and every check - without
    /// generating code. For callers that want the diagnostics and the typed program, not the output.
    /// </summary>
    public bool Check()
    {
        var pm = new PassManager();
        pm.BuildOrder(PassManager.CheckPipeline);

        return pm.Run(Diagnostics, Packages.Values.ToList(), TypeUniverse, SymAlloc, ScopeAlloc, NodeAlloc, Names,
            Cache, Config);
    }

    public bool Compile()
    {
        var pm = new PassManager();
        pm.BuildOrder(PassManager.CompilerPipeline);

        return pm.Run(Diagnostics, Packages.Values.ToList(), TypeUniverse, SymAlloc, ScopeAlloc, NodeAlloc, Names,
            Cache, Config);
    }

    /// <summary>
    /// Prepares the source code and converts it into the high-level intermediate representation (HIR).
    /// </summary>
    /// <param name="file">The preparsed file containing the source code and its associated metadata.</param>
    /// <returns>true if the preparsing was successful, false otherwise.</returns>
    private bool Preparse(PreparsedFile file)
    {
        var inputStream = new AntlrInputStream(file.Content);
        var lexer = new NebraLexer(inputStream);
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(new DiagnosticsSymbolErrorListener(Diagnostics, file.Filename));
        var tokenStream = new CommonTokenStream(lexer);
        var parser = new NebraParser(tokenStream);
        parser.RemoveErrorListeners();
        parser.AddErrorListener(new DiagnosticsTokenErrorListener(Diagnostics, file.Filename));
        var visitor = new IRVisitor(file.Filename, NodeAlloc, Diagnostics, Config);

        Node? ir;
        try
        {
            ir = visitor.Visit(parser.script());
        }
        catch (Exception)
        {
            // ANTLR error-recovery can hand the visitor a partial/malformed tree
            // (missing tokens show up as nulls). If lowering that tree throws, the
            // syntax error has already been recorded by the error listeners, so we
            // surface a clean diagnostic instead of letting the build crash with an
            // unhandled NullReferenceException.
            ReportPreparsingFailure();
            return false;
        }

        if (ir is not IRScript script)
        {
            ReportPreparsingFailure();
            return false;
        }
        
        file.Hir = script;
        Nebra.Doc.DocBinder.Bind(script, file.Content);
        return true;
    }

    /// <summary>
    /// Reports that lowering produced no usable HIR. Stays quiet when an error has already been
    /// recorded: the error listeners will have pointed at the syntax that caused it, and this
    /// carries no span, so adding it on top only buries the actionable message under what reads
    /// like a compiler crash.
    /// </summary>
    private void ReportPreparsingFailure()
    {
        if (Diagnostics.HasErrors) return;

        Diagnostics.Report(TextSpan.Empty, DiagnosticCode.ErrPreparsingFailed);
    }
}