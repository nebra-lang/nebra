namespace Nebra.IR;

/// <summary>
/// Represents a pre-parsed source file after ANTLR converted the source code into the high-level intermediate representation (HIR).
/// </summary>
public sealed class PreparsedFile(string? filename, string content)
{
    /// <summary>
    /// Thze filename (full path) of the source file. May be null, if the source file is not from a file, such as from a string or from a network stream.
    /// </summary>
    public string? Filename { get; } = filename;

    /// <summary>
    /// The source code content of the source file. 
    /// </summary>
    public string Content { get; } = content;

    /// <summary>
    /// The HIR of the source file (script). Can be null until the preparsing process is completed.
    /// </summary>
    public IRScript Hir { get; set; } = null!;

    public string? GeneratedLua { get; set; }

    /// <summary>The source line of every line of <see cref="GeneratedLua"/>, 0 for a line that comes from no source.</summary>
    public int[]? GeneratedLineSources { get; set; }

    public bool IsDeclarationFile => Filename != null && Filename.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Optional binding-scope override used by <see cref="Nebra.Compiler.Passes.BindDeclarePass"/>.
    /// When set, the file's top-level statements bind into this scope instead of the package
    /// root; lets imported library files keep their exports in a private scope so their
    /// names don't clash with the importer's <c>import { … }</c> declarations.
    /// </summary>
    public ScopeID? BindingScopeOverride { get; set; }
}