using System.Reflection;
using KeraLua;
using Nebra.Runtime.Bindings;
using Nebra.Runtime.Library;

namespace Nebra.Runtime;

/// <summary>
/// Thin wrapper around an embedded Lua 5.4 interpreter (KeraLua) used to execute
/// Nebra-transpiled Lua code directly from the <c>nebra run</c> command. Instances of
/// this class own a native Lua state — dispose them to free it.
/// </summary>
public sealed class NebraRuntime : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Underlying KeraLua state. Exposed to the binding helpers in
    /// <c>Nebra.Runtime.Bindings</c> so they can push/read values directly.
    /// </summary>
    internal Lua State { get; }

    /// <summary>
    /// Holds strong references to every <see cref="LuaFunction"/> delegate that has been
    /// pushed into Lua. Without this list the GC would collect them and the next call
    /// from Lua would crash the runtime.
    /// </summary>
    internal readonly List<LuaFunction> PinnedDelegates = [];

    /// <summary>
    /// Maps a CLR <see cref="Type"/> to the <see cref="Bindings.NebraClass"/> that was
    /// registered for it. Used by the marshaller to push a CLR instance back into Lua
    /// as the correct userdata with the right metatable.
    /// </summary>
    internal readonly Dictionary<Type, Bindings.NebraClass> ClassRegistry = new();

    /// <summary>Rewrites an error before it is printed, such as to point at the original source.</summary>
    public Func<string, string>? ErrorTranslator { get; set; }

    public NebraRuntime() : this(sandboxed: false) { }

    private NebraRuntime(bool sandboxed)
    {
        State = new Lua();
        State.OpenLibs();
        if (sandboxed) ApplySandbox();
        else LoadNebraLibrary();
    }

    /// <summary>
    /// Creates a restricted runtime suitable for executing annotation plugins at compile time.
    /// Disables <c>io</c>, <c>os</c>, <c>package</c>, <c>require</c> and other globals that
    /// could escape the sandbox, while leaving pure Lua (string/math/table/coroutine) intact.
    /// The <c>ir</c> helper module is pre-loaded as a global so annotation scripts can build
    /// new IR nodes ergonomically.
    /// </summary>
    public static NebraRuntime CreateSandboxed()
    {
        var rt = new NebraRuntime(sandboxed: true);
        rt.LoadEmbeddedHelpers();
        return rt;
    }

    private void ApplySandbox()
    {
        foreach (var global in new[] { "io", "os", "package", "require", "dofile", "loadfile", "load", "loadstring", "debug" })
        {
            State.PushNil();
            State.SetGlobal(global);
        }
    }

    private void LoadEmbeddedHelpers()
    {
        var asm = typeof(NebraRuntime).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("ir_helpers.lua", StringComparison.OrdinalIgnoreCase));
        if (resourceName == null) return;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return;
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        LoadAndRun(source, "ir_helpers");
    }

    private void LoadNebraLibrary()
    {
        this.RegisterClass(NebraClass.FromReflection<HTTP>());
        this.RegisterClass(NebraClass.FromReflection<JSON>());
        this.RegisterClass(NebraClass.FromReflection<FS>());
        this.RegisterClass(NebraClass.FromReflection<ConsoleLib>());
        this.RegisterClass(NebraClass.FromReflection<Project>());
        this.RegisterClassHidden(NebraClass.FromReflection<Spinner>("Spinner"));
        this.RegisterClassHidden(NebraClass.FromReflection<ProgressBar>("ProgressBar"));
    }
    
    /// <summary>
    /// Extends Lua's <c>package.path</c> so that <c>require("foo/bar")</c> and
    /// <c>require("foo.bar")</c> find <c>&lt;dir&gt;/foo/bar.lua</c> and
    /// <c>&lt;dir&gt;/foo/bar/init.lua</c>. Accepts any number of root directories
    /// and prepends them to the existing path in order.
    /// </summary>
    public void AddPackagePath(params string[] roots)
    {
        foreach (var root in roots.Reverse())
        {
            if (string.IsNullOrEmpty(root)) continue;
            var normalized = root.Replace('\\', '/').TrimEnd('/');
            var entry = $"{normalized}/?.lua;{normalized}/?/init.lua";

            State.GetGlobal("package");
            State.GetField(-1, "path");
            var existing = State.ToString(-1) ?? "";
            State.Pop(1);

            State.PushString(entry + ";" + existing);
            State.SetField(-2, "path");
            State.Pop(1);
        }
    }

    /// <summary>
    /// Registers a Lua module under <c>package.preload[name]</c> from a source string.
    /// After this call, generated <c>require(name)</c> resolves to the supplied source
    /// without touching <c>package.path</c>. Used by <c>nebra test</c> for the built-in
    /// <c>nebra:test</c> module and by standalone binaries to surface every bundled
    /// project module via <see cref="RegisterEmbeddedModule"/>.
    /// </summary>
    public bool RegisterModule(string name, string luaSource)
    {
        var loadStatus = State.LoadString(luaSource, name);
        if (loadStatus != LuaStatus.OK)
        {
            var err = State.ToString(-1) ?? "unknown load error";
            Console.Error.WriteLine($"nebra: failed to load module '{name}': {err}");
            State.Pop(1);
            return false;
        }

        State.GetGlobal("package");
        State.GetField(-1, "preload");
        State.PushCopy(-3);
        State.SetField(-2, name);
        State.Pop(3);
        return true;
    }

    /// <summary>
    /// Convenience wrapper that reads an embedded resource from the given assembly
    /// (matched by suffix-equality on <paramref name="resourceName"/>) and feeds it
    /// to <see cref="RegisterModule"/>. Returns <c>false</c> if the resource is
    /// missing or the source fails to load.
    /// </summary>
    public bool RegisterEmbeddedModule(string moduleName, Assembly asm, string resourceName)
    {
        var found = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));
        if (found == null) return false;

        using var stream = asm.GetManifestResourceStream(found);
        if (stream == null) return false;
        using var reader = new StreamReader(stream);
        return RegisterModule(moduleName, reader.ReadToEnd());
    }

    /// <summary>
    /// Invokes <c>require(name)</c> on this runtime. Returns <c>true</c> on success;
    /// prints any error to stderr and returns <c>false</c> otherwise. Used by the
    /// standalone-binary launcher to kick off the bundled entry module after
    /// <see cref="RegisterEmbeddedModule"/> has primed <c>package.preload</c>.
    /// </summary>
    public bool RequireAndRun(string moduleName)
    {
        var script = $"require({EscapeLuaString(moduleName)})";
        return RunChunk(script, $"<require:{moduleName}>");
    }

    private static string EscapeLuaString(string s)
    {
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
    }

    /// <summary>
    /// Runs a Lua source string as the entry script. The <paramref name="chunkName"/>
    /// is used in error messages / stack traces. Returns <c>true</c> on success and
    /// prints any runtime error to stderr on failure.
    /// </summary>
    public bool RunChunk(string luaSource, string chunkName)
    {
        var loadStatus = State.LoadString(luaSource, chunkName);
        if (loadStatus != LuaStatus.OK)
        {
            var err = State.ToString(-1) ?? "unknown load error";
            Console.Error.WriteLine($"nebra run: load error: {err}");
            State.Pop(1);
            return false;
        }

        var callStatus = State.PCall(0, 0, 0);
        if (callStatus != LuaStatus.OK)
        {
            var err = State.ToString(-1) ?? "unknown runtime error";
            Console.Error.WriteLine($"nebra run: runtime error: {ErrorTranslator?.Invoke(err) ?? err}");
            State.Pop(1);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Loads and runs a Lua file from disk via the native <c>luaL_loadfile</c> +
    /// <c>lua_pcall</c> path. Equivalent to <see cref="RunChunk"/> but lets Lua
    /// itself handle file reading so line numbers map directly to the file.
    /// </summary>
    public bool RunFile(string path)
    {
        var loadStatus = State.LoadFile(path);
        if (loadStatus != LuaStatus.OK)
        {
            var err = State.ToString(-1) ?? "unknown load error";
            Console.Error.WriteLine($"nebra run: load error: {err}");
            State.Pop(1);
            return false;
        }

        var callStatus = State.PCall(0, 0, 0);
        if (callStatus != LuaStatus.OK)
        {
            var err = State.ToString(-1) ?? "unknown runtime error";
            Console.Error.WriteLine($"nebra run: runtime error: {ErrorTranslator?.Invoke(err) ?? err}");
            State.Pop(1);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gives a sandboxed runtime a <c>require</c> that serves only the modules handed to it, such
    /// as the helpers an annotation imports. Each module receives a <c>require</c> of its own that
    /// resolves the names it imports through <paramref name="imports"/>, so a relative name means
    /// the file next to the importing one. <paramref name="entry"/> is the file whose imports the
    /// global <c>require</c> resolves. Returns <c>null</c> on success, else the failure.
    /// </summary>
    public string? InstallModules(string entry, IReadOnlyDictionary<string, string> sources,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> imports)
    {
        State.NewTable();
        foreach (var (file, source) in sources)
        {
            var loadStatus = State.LoadString("return function(require) " + source + "\nend", Path.GetFileNameWithoutExtension(file));
            if (loadStatus != LuaStatus.OK || State.PCall(0, 1, 0) != LuaStatus.OK)
            {
                var err = State.ToString(-1) ?? "unknown load error";
                State.Pop(2);
                return err;
            }

            State.SetField(-2, file);
        }

        State.SetGlobal("__nebra_modules");

        var table = string.Join(", ", imports.Select(importer =>
            $"[{EscapeLuaString(importer.Key)}] = {{ {string.Join(", ", importer.Value.Select(import => $"[{EscapeLuaString(import.Key)}] = {EscapeLuaString(import.Value)}"))} }}"));
        var installer = $$"""
            local loaders, imports, cache = __nebra_modules, { {{table}} }, {}
            __nebra_modules = nil
            local function requireFrom(file)
                return function(name)
                    local target = imports[file] and imports[file][name]
                    if target == nil then
                        error("module '" .. tostring(name) .. "' is not available here", 2)
                    end
                    if cache[target] == nil then
                        cache[target] = loaders[target](requireFrom(target))
                        if cache[target] == nil then cache[target] = true end
                    end
                    return cache[target]
                end
            end
            require = requireFrom({{EscapeLuaString(entry)}})
            """;
        return LoadAndRun(installer, "modules");
    }

    /// <summary>
    /// Loads a Lua source chunk and executes it as the module body (so any globals or
    /// functions defined at the top level become visible). Returns <c>null</c> on success
    /// or a string describing the failure.
    /// </summary>
    public string? LoadAndRun(string luaSource, string chunkName)
    {
        var loadStatus = State.LoadString(luaSource, chunkName);
        if (loadStatus != LuaStatus.OK)
        {
            var err = State.ToString(-1) ?? "unknown load error";
            State.Pop(1);
            return err;
        }

        var callStatus = State.PCall(0, 0, 0);
        if (callStatus != LuaStatus.OK)
        {
            var err = State.ToString(-1) ?? "unknown runtime error";
            State.Pop(1);
            return err;
        }

        return null;
    }

    /// <summary>
    /// Calls a global function by name with the given arguments (serialized C# object trees —
    /// <see cref="Dictionary{TKey,TValue}"/> / <see cref="List{T}"/> / primitives). Returns the
    /// first return value converted back to the same shape, or sets <paramref name="error"/>
    /// if the call failed.
    /// </summary>
    public object? CallGlobalFunction(string funcName, object?[] args, out string? error)
    {
        error = null;
        var top = State.GetTop();
        try
        {
            var t = State.GetGlobal(funcName);
            if (t != LuaType.Function)
            {
                State.SetTop(top);
                error = $"global '{funcName}' is not a function (got {t}).";
                return null;
            }

            foreach (var arg in args) PushValue(arg);

            var status = State.PCall(args.Length, 1, 0);
            if (status != LuaStatus.OK)
            {
                var err = State.ToString(-1) ?? "unknown error";
                State.SetTop(top);
                error = err;
                return null;
            }

            var result = ReadValue(-1);
            State.SetTop(top);
            return result;
        }
        catch (Exception ex)
        {
            State.SetTop(top);
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Pushes a C# object tree (Dictionary / List / primitives) onto the Lua stack as a nested
    /// table. Dictionaries use string keys; Lists become 1-indexed arrays. Unsupported types
    /// are pushed as <c>nil</c>.
    /// </summary>
    private void PushValue(object? value)
    {
        ReserveStack(2);
        switch (value)
        {
            case null:
                State.PushNil();
                break;
            case string s:
                State.PushString(s);
                break;
            case bool b:
                State.PushBoolean(b);
                break;
            case int i:
                State.PushInteger(i);
                break;
            case long l:
                State.PushInteger(l);
                break;
            case ulong ul:
                State.PushInteger((long)ul);
                break;
            case double d:
                State.PushNumber(d);
                break;
            case float f:
                State.PushNumber(f);
                break;
            case IDictionary<string, object?> dict:
                State.NewTable();
                foreach (var kv in dict)
                {
                    PushValue(kv.Value);
                    State.SetField(-2, kv.Key);
                }
                break;
            case System.Collections.IList list:
                State.NewTable();
                for (var idx = 0; idx < list.Count; idx++)
                {
                    PushValue(list[idx]);
                    State.RawSetInteger(-2, idx + 1);
                }
                break;
            default:
                State.PushNil();
                break;
        }
    }

    /// <summary>
    /// Reads a Lua value at the given stack index and converts it to a C# object tree
    /// (<see cref="Dictionary{TKey,TValue}"/> for tables with string keys, <see cref="List{T}"/>
    /// for sequences, plus primitives). Does not mutate the stack.
    /// </summary>
    private object? ReadValue(int index)
    {
        var abs = State.AbsIndex(index);
        var t = State.Type(abs);
        switch (t)
        {
            case LuaType.Nil: return null;
            case LuaType.Boolean: return State.ToBoolean(abs);
            case LuaType.Number:
                if (State.IsInteger(abs)) return State.ToInteger(abs);
                return State.ToNumber(abs);
            case LuaType.String: return State.ToString(abs);
            case LuaType.Table: return ReadTable(abs);
            case LuaType.None:
            case LuaType.LightUserData:
            case LuaType.Function:
            case LuaType.UserData:
            case LuaType.Thread:
            default: return null;
        }
    }

    private object? ReadTable(int absIndex)
    {
        ReserveStack(3);
        var hasStringKey = false;
        var maxIntKey = 0;
        var intKeyCount = 0;

        State.PushNil();
        while (State.Next(absIndex))
        {
            var keyType = State.Type(-2);
            if (keyType == LuaType.String)
            {
                hasStringKey = true;
            }
            else if (keyType == LuaType.Number && State.IsInteger(-2))
            {
                var k = (int)State.ToInteger(-2);
                if (k > maxIntKey) maxIntKey = k;
                intKeyCount++;
            }
            State.Pop(1);
        }

        if (!hasStringKey && intKeyCount > 0 && maxIntKey == intKeyCount)
        {
            var list = new List<object?>(intKeyCount);
            for (var i = 1; i <= intKeyCount; i++)
            {
                State.RawGetInteger(absIndex, i);
                list.Add(ReadValue(-1));
                State.Pop(1);
            }
            return list;
        }

        var dict = new Dictionary<string, object?>();
        State.PushNil();
        while (State.Next(absIndex))
        {
            // key at -2, value at -1
            if (State.Type(-2) == LuaType.String)
            {
                var key = State.ToString(-2) ?? "";
                dict[key] = ReadValue(-1);
            }
            else if (State.Type(-2) == LuaType.Number && State.IsInteger(-2))
            {
                var key = State.ToInteger(-2).ToString();
                dict[key] = ReadValue(-1);
            }
            State.Pop(1);
        }
        return dict;
    }

    /// <summary>
    /// Makes room on the Lua stack before a nested table is pushed or read. Lua guarantees only a
    /// few free slots and does not check them, so a deep tree would otherwise be written past the
    /// end of the stack, into memory that is not Lua's.
    /// </summary>
    private void ReserveStack(int slots)
    {
        if (!State.CheckStack(slots))
            throw new InvalidOperationException("the value is nested too deeply for the Lua stack");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        State.Dispose();
    }
}
