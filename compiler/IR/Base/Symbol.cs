using Nebra.Configuration;

namespace Nebra.IR;

/// <summary>
/// Represents the kind of symbol. This enum is used to distinguish between different kinds of symbols and to determine
/// how they should be treated during semantic analysis and code generation.
/// </summary>
public enum SymbolKind
{
    Variable,
    Function,
    Enum,
    Class,
    Interface,
    TypeParam,
}

/// <summary>
/// Represents the flags of a symbol. This enum is used to indicate additional properties of a symbol. The flags can be
/// combined using bitwise operations to represent multiple properties at once.
/// </summary>
[Flags]
public enum SymbolFlags
{
    None = 0,
    /// <summary>
    /// The symbol is constant. This is the &gt;const&lt; qualifier in lua and will generate one in the Lua codegen.
    /// A constant symbol cannot be changed.
    /// </summary>
    Const = 1 << 0,
    /// <summary>
    /// Same as <see cref="Const"/> but used in the Nebra language to indicate immutability by default as a language design choice.
    /// This won't generate any code but be used in the semantic analysis phase to enforce immutability.
    /// </summary>
    Immutable = 1 << 1,
    /// <summary>
    /// Similar to <see cref="Immutable"/> but used to indicate that a symbol is deeply immutable, meaning that not
    /// only the symbol itself cannot be changed, but also any data it references cannot be changed.
    /// </summary>
    DeepFreeze = 1 << 2,
    /// <summary>
    /// The symbol is mutable. This can be used to indicate that a symbol is mutable and can be modified after it is
    /// initialized, which can be useful for optimization and code generation. Mutable symbols can be treated differently
    /// during code generation, such as being stored in memory or being passed by reference, which can improve the
    /// performance of the generated code.
    /// </summary>
    Mutable = 1 << 3,
    /// <summary>
    /// The symbol is unused. This can be used to indicate that a symbol is declared but not used in the program, which
    /// can be useful for diagnostics and code generation. Unused symbols can be reported as warnings to the user, and
    /// can also be optimized away during code generation to reduce the size of the generated code.
    /// </summary>
    Unused = 1 << 4,
    Async = 1 << 5,
    Static = 1 << 6,
    /// <summary>
    /// The symbol names something provided from outside the generated code: anything introduced by
    /// a <c>declare</c> form, including everything the stdlib declaration files bring in. Its name
    /// is fixed by whatever supplies it at runtime, so mangling must leave it alone.
    /// </summary>
    External = 1 << 7,
}

/// <summary>
/// Represents a symbol in the intermediate representation (IR) of the program. A symbol is an entity that can be
/// referred to by name, such as a variable, a function, a type, etc. Each symbol has a unique ID and a kind that
/// indicates what kind of symbol it is. The symbol can also have other properties, such as its type, its scope, etc.,
/// which can be used during semantic analysis and code generation.
/// </summary>
public sealed class Symbol(SymID id, SymbolKind kind, string name, ScopeID owner, TypID type, NodeID declaringNode, SymbolFlags flags = SymbolFlags.None)
{
    /// <summary>
    /// The unique ID of the symbol. This is assigned when the symbol is created and can be used to reference the symbol from other nodes or from external code.
    /// </summary>
    public SymID ID { get; } = id;
    
    /// <summary>
    /// The kind of the symbol.
    /// </summary>
    public SymbolKind Kind { get; } = kind;
    
    /// <summary>
    /// The original name of the symbol.
    /// </summary>
    public string Name { get; } = name;
    
    /// <summary>
    /// The ID of the owning scope of the symbol. This is assigned when the symbol is created and can be used to
    /// reference the owning scope from other nodes or from external code. The owning scope is the scope in which the
    /// symbol is declared, and can be used to determine the visibility and accessibility of the symbol from other scopes.
    /// </summary>
    public ScopeID Owner { get; } = owner;
    
    /// <summary>
    /// The current type of the symbol.
    /// </summary>
    public TypID Type { get; set; } = type;
    
    /// <summary>
    /// The declaring node of the symbol. This is the node in the IR that declares the symbol.
    /// </summary>
    public NodeID DeclaringNode { get; } = declaringNode;
    
    /// <summary>
    /// The flags of the symbol. This is used to indicate additional properties of the symbol.
    /// </summary>
    public SymbolFlags Flags { get; set; } = flags;

    /// <summary>
    /// The execution-side mask of the symbol (Client/Server/Shared). Set by
    /// <see cref="Compiler.Passes.BindDeclarePass"/> from the symbol's
    /// <c>@side(...)</c> annotation, defaulting to <see cref="Side.All"/> if
    /// unannotated.
    /// </summary>
    public Side Side { get; set; } = Side.All;

    /// <summary>
    /// For a name brought in by an import, the symbol it originally comes from, followed through
    /// re-exports; <see cref="SymID.Invalid"/> for every other symbol. Lets tooling relate an
    /// imported name to its declaration across files.
    /// </summary>
    public SymID ImportedFrom { get; set; } = SymID.Invalid;
}

/// <summary>
/// The symbol arena is a data structure that stores all the symbols in the IR. It is used to manage the symbols and to
/// provide efficient lookup and retrieval of symbols during semantic analysis and code generation. 
/// </summary>
public sealed class SymbolArena(IDAlloc<SymID>? idAlloc = null)
{
    /// <summary>
    /// The underlying dictionary that maps symbol IDs to symbols. 
    /// </summary>
    public Dictionary<SymID, Symbol> ByID { get; } = new();
    
    private readonly IDAlloc<SymID> _idAlloc = idAlloc ?? new IDAlloc<SymID>();
    
    /// <summary>
    /// Creates a new symbol and adds it to the arena. The symbol is created with the specified kind, name, owner, type,
    /// declaring node, and flags. The symbol is assigned a unique ID using the ID allocator, and is added to the
    /// underlying dictionary for efficient lookup and retrieval.
    /// </summary>
    /// <param name="kind">The kind of the symbol.</param>
    /// <param name="name">The original name of the symbol.</param>
    /// <param name="owner">The ID of the owning scope of the symbol.</param>
    /// <param name="type">The current type of the symbol.</param>
    /// <param name="decl">The declaring node of the symbol.</param>
    /// <param name="flags">The flags of the symbol.</param>
    /// <returns>The unique ID of the newly created symbol.</returns>
    public SymID NewSymbol(SymbolKind kind, string name, ScopeID owner, TypID type, NodeID decl, params SymbolFlags[] flags)
    {
        var id = _idAlloc.Next();
        var symbol = new Symbol(id, kind, name, owner, type, decl, flags.Aggregate(SymbolFlags.None, (a, b) => a | b));
        ByID[id] = symbol;
        return id;
    }
    
    /// <summary>
    /// Tries to get the symbol with the specified ID. If a symbol with the specified ID exists in the arena, it is
    /// returned in the out parameter and the method returns true. Otherwise, the out parameter is set to null and the
    /// method returns false.
    /// </summary>
    /// <param name="id">The unique ID of the symbol to retrieve.</param>
    /// <param name="symbol">When this method returns, contains the symbol with the specified ID if it exists in the arena; otherwise, null.</param>
    /// <returns>true if a symbol with the specified ID exists in the arena; otherwise, false.</returns>
    public bool GetByID(SymID id, out Symbol symbol)
    {
        if (ByID.TryGetValue(id, out var sym))
        {
            symbol = sym;
            return true;
        }
        
        symbol = null!;
        return false;
    }
    
    /// <summary>
    /// Tries to get the symbol with the specified name and owning scope. If a symbol with the specified name and owning
    /// scope exists in the arena, it is returned in the out parameter and the method returns true. Otherwise, the out
    /// parameter is set to null and the method returns false. This method is used to look up symbols by their original
    /// name and owning scope, which can be useful during name resolution and semantic analysis.
    /// </summary>
    /// <param name="name">The original name of the symbol to retrieve.</param>
    /// <param name="scope">The ID of the owning scope of the symbol to retrieve.</param>
    /// <param name="symbol">When this method returns, contains the symbol with the specified name and owning scope if it exists in the arena; otherwise, null.</param>
    /// <returns>true if a symbol with the specified name and owning scope exists in the arena; otherwise, false.</returns>
    public bool GetByName(string name, ScopeID scope, out Symbol symbol)
    {
        var sym = ByID.Values.FirstOrDefault(s => s.Name == name && s.Owner == scope);
        if (sym != null)
        {
            symbol = sym;
            return true;
        }
        
        symbol = null!;
        return false;
    }

    /// <summary>
    /// Sets the type of the symbol with the specified ID. If a symbol with the specified ID exists in the arena, its
    /// type is set to the specified type and the method returns true. Otherwise, the method returns false. This method
    /// is used to update the type of a symbol during semantic analysis, such as when the type of variable is inferred
    /// or when the type of a function is determined based on its parameters and return type.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="type"></param>
    /// <returns></returns>
    public bool SetType(SymID id, TypID type)
    {
        if (ByID.TryGetValue(id, out var sym))
        {
            sym.Type = type;
            return true;
        }

        return false;
    }
}