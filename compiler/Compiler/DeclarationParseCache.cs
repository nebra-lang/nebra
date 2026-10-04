using System.Collections.Concurrent;
using Antlr4.Runtime;

namespace Nebra.Compiler;

/// <summary>
/// Parse trees of declaration sources, keyed by their text. A library is loaded into every
/// package of a build and again on each build of a long-running process such as the language
/// server; with this it is parsed once. The tree is only read afterwards - every load lowers it
/// into fresh IR - so no two packages share nodes the later passes mutate. Parse errors are not
/// kept: declaration sources are loaded without error listeners.
/// </summary>
internal static class DeclarationParseCache
{
    private const int Capacity = 256;

    private static readonly ConcurrentDictionary<string, NebraParser.ScriptContext> Trees = new(StringComparer.Ordinal);

    public static NebraParser.ScriptContext Parse(string source)
    {
        if (Trees.TryGetValue(source, out var cached))
        {
            return cached;
        }

        var lexer = new NebraLexer(new AntlrInputStream(source));
        lexer.RemoveErrorListeners();
        var parser = new NebraParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        var tree = parser.script();

        if (Trees.Count >= Capacity)
        {
            Trees.Clear();
        }

        Trees.TryAdd(source, tree);
        return tree;
    }
}
