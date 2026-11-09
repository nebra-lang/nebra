using System.Text;
using Nebra.Configuration;

namespace Nebra.Compiler.Codegen;

public sealed class LuaGenerator(Config config)
{
    private readonly StringBuilder _preamble = new();
    private readonly StringBuilder _body = new();
    private readonly HashSet<string> _emittedHelpers = [];
    private readonly Dictionary<string, string> _helperNames = new();
    private int _indent;
    private bool _lineStart = true;
    private int _tempCounter;

    public bool Minify => config.Minify;
    public LuaVersion Target => config.Target;
    public LuaFeatureSet Features { get; } = LuaFeatureSet.For(config.Target);

    public Config Config => config;

    private readonly List<(int GeneratedLine, int SourceLine)> _sourceLines = [];
    private int _countedBodyLength;
    private int _bodyLine;

    public string Finish()
    {
        var sb = new StringBuilder();
        if (_preamble.Length > 0)
        {
            sb.Append(_preamble);
            if (!Minify)
                sb.AppendLine();
        }
        sb.Append(_body);
        return sb.ToString();
    }

    /// <summary>Records that the code written from here on comes from line <paramref name="sourceLine"/> of the source.</summary>
    public void MarkSourceLine(int sourceLine)
    {
        var line = CurrentBodyLine();
        if (_sourceLines.Count > 0 && _sourceLines[^1].GeneratedLine == line) return;

        _sourceLines.Add((line, sourceLine));
    }

    /// <summary>
    /// The source line of every line <see cref="Finish"/> returns, in order, 0 for a line that
    /// comes from no source. A line carries on the source line before it, as one statement can
    /// expand over several lines.
    /// </summary>
    public int[] SourceLineMap()
    {
        var bodyOffset = _preamble.Length == 0 ? 0 : _preamble.ToString().Count(character => character == '\n') + (Minify ? 0 : 1);
        var map = new int[bodyOffset + CurrentBodyLine() + 1];

        for (var index = 0; index < _sourceLines.Count; index++)
        {
            var end = index + 1 < _sourceLines.Count ? _sourceLines[index + 1].GeneratedLine : CurrentBodyLine() + 1;
            Array.Fill(map, _sourceLines[index].SourceLine, bodyOffset + _sourceLines[index].GeneratedLine, end - _sourceLines[index].GeneratedLine);
        }

        return map;
    }

    private int CurrentBodyLine()
    {
        for (; _countedBodyLength < _body.Length; _countedBodyLength++)
        {
            if (_body[_countedBodyLength] == '\n') _bodyLine++;
        }

        return _bodyLine;
    }

    #region Writing

    public void Write(string text)
    {
        if (_lineStart && !Minify)
        {
            _body.Append(new string('\t', _indent));
            _lineStart = false;
        }
        _body.Append(text);
    }

    public void WriteLine(string text)
    {
        Write(text);
        NewLine();
    }

    public void WriteLine()
    {
        NewLine();
    }

    public void NewLine()
    {
        if (Minify)
        {
            _body.Append(' ');
        }
        else
        {
            _body.AppendLine();
        }
        _lineStart = true;
    }

    public void Space()
    {
        if (!Minify)
            _body.Append(' ');
    }

    public void Sep()
    {
        if (Minify)
            _body.Append(' ');
        else
            _body.Append(' ');
    }

    public void WriteSemicolon()
    {
        if (config.Code.Semicolons == Policy.Required)
            _body.Append(';');
    }

    public void Indent() => _indent++;
    public void Dedent() => _indent = Math.Max(0, _indent - 1);

    #endregion

    #region Preamble / Helpers

    public void WritePreambleLine(string text)
    {
        _preamble.AppendLine(text);
    }

    public string RequireHelper(string key, Func<string, string> helperFactory)
    {
        if (_helperNames.TryGetValue(key, out var existing))
            return existing;

        var name = "__nebra_" + key;
        _helperNames[key] = name;

        if (_emittedHelpers.Add(key))
        {
            var code = helperFactory(name);
            _preamble.AppendLine(code);
        }

        return name;
    }

    #endregion

    #region Hoisting

    private readonly List<string> _hoisted = [];

    public void Hoist(string luaCode)
    {
        _hoisted.Add(luaCode);
    }

    public void FlushHoisted()
    {
        foreach (var code in _hoisted)
        {
            WriteLine(code);
        }
        _hoisted.Clear();
    }

    public bool HasHoisted => _hoisted.Count > 0;

    #endregion

    #region Temp Vars

    public string FreshTemp(string prefix = "_t")
    {
        return prefix + _tempCounter++;
    }

    #endregion

    #region Comments

    public void WriteComment(string text)
    {
        if (Minify) return;
        WriteLine("-- " + text);
    }

    public void WriteBlockComment(string text)
    {
        if (Minify) return;
        WriteLine("--[[ " + text + " ]]");
    }

    #endregion

    #region Concat Helpers

    public string GetConcatHelper()
    {
        return RequireHelper("concat", name =>
            $"local function {name}(a, b) return tostring(a) .. tostring(b) end");
    }

    #endregion
    
    #region Freeze Helpers
    
    public string GetFreezeHelper()
    {
        return RequireHelper("freeze", name =>
            $"local function {name}(t) return setmetatable(t, {{__newindex = function() error(\"attempt to modify frozen table\") end}}) end");
    }
    
    #endregion

    #region Index Base Helpers

    public int IndexBase => config.Code.IndexBase;

    public string AdjustIndex(string indexExpr)
    {
        var offset = config.Code.IndexBase;
        if (offset == 1) return indexExpr;

        var diff = 1 - offset;
        if (diff > 0)
            return "(" + indexExpr + " + " + diff + ")";
        return "(" + indexExpr + " - " + (-diff) + ")";
    }

    /// <summary>
    /// A function a loop over a value of unknown type starts through: a table with an
    /// <c>iterator()</c> method is iterated through it, anything else is used as it is.
    /// </summary>
    /// <summary>A helper building the array of every number from its first argument up to its second.</summary>
    public string GetRangeHelper()
    {
        return RequireHelper("range", name =>
            $"local function {name}(first, last) local t = {{}} for i = first, last do t[#t + 1] = i end return t end");
    }

    public string GetIterateHelper()
    {
        return RequireHelper("iterate", name =>
            $"local function {name}(v) if type(v) == \"table\" and type(v.iterator) == \"function\" then return v:iterator() end return v end");
    }

    public string GetIpairsHelper()
    {
        if (config.Code.IndexBase == 1) return "ipairs";

        var adj = 1 - config.Code.IndexBase;
        return RequireHelper("ipairs", name =>
            $"local function {name}(t) local i = {config.Code.IndexBase - 1} return function() i = i + 1 local v = t[i + {adj}] if v == nil then return nil end return i, v end end");
    }

    #endregion

    #region Increment / Decrement Helpers

    public string GetIncDecHelper(bool isPre, bool isIncrement)
    {
        var key = (isPre ? "pre" : "post") + (isIncrement ? "inc" : "dec");
        var op = isIncrement ? "+" : "-";
        if (isPre)
        {
            return RequireHelper(key, name =>
                $"local function {name}(get, set) local v = get() {op} 1 set(v) return v end");
        }
        else
        {
            return RequireHelper(key, name =>
                $"local function {name}(get, set) local v = get() set(v {op} 1) return v end");
        }
    }

    #endregion

    #region Import Helpers

    public string EmitImport(string modulePath)
    {
        if (modulePath.EndsWith(".neb", StringComparison.OrdinalIgnoreCase))
            modulePath = modulePath[..^4];
        modulePath += config.Code.ImportExtension;
        var template = config.Code.ImportStatement;
        return template.Replace("%s", "\"" + modulePath + "\"");
    }

    #endregion

    #region Operator Mapping

    public string BinaryOpToLua(IR.BinaryOp op)
    {
        return op switch
        {
            IR.BinaryOp.Add => "+",
            IR.BinaryOp.Sub => "-",
            IR.BinaryOp.Mul => "*",
            IR.BinaryOp.Div => "/",
            IR.BinaryOp.FloorDiv => Features.HasFloorDiv ? "//" : "math.floor(a/b)",
            IR.BinaryOp.Mod => "%",
            IR.BinaryOp.Pow => "^",
            IR.BinaryOp.Concat => "..",
            IR.BinaryOp.Eq => "==",
            IR.BinaryOp.Neq => "~=",
            IR.BinaryOp.Lt => "<",
            IR.BinaryOp.Gt => ">",
            IR.BinaryOp.Lte => "<=",
            IR.BinaryOp.Gte => ">=",
            IR.BinaryOp.And => "and",
            IR.BinaryOp.Or => "or",
            IR.BinaryOp.BitwiseAnd => Features.BitwiseStyle switch
            {
                BitwiseStyle.Operator => "&",
                BitwiseStyle.BitLib => "bit.band",
                _ => ""
            },
            IR.BinaryOp.BitwiseOr => Features.BitwiseStyle switch
            {
                BitwiseStyle.Operator => "|",
                BitwiseStyle.BitLib => "bit.bor",
                _ => ""
            },
            IR.BinaryOp.BitwiseXor => Features.BitwiseStyle switch
            {
                BitwiseStyle.Operator => "~",
                BitwiseStyle.BitLib => "bit.bxor",
                _ => ""
            },
            IR.BinaryOp.LShift => Features.BitwiseStyle switch
            {
                BitwiseStyle.Operator => "<<",
                BitwiseStyle.BitLib => "bit.lshift",
                _ => ""
            },
            IR.BinaryOp.RShift => Features.BitwiseStyle switch
            {
                BitwiseStyle.Operator => ">>",
                BitwiseStyle.BitLib => "bit.rshift",
                _ => ""
            },
            _ => "?"
        };
    }

    public string UnaryOpToLua(IR.UnaryOp op)
    {
        return op switch
        {
            IR.UnaryOp.Negate => "-",
            IR.UnaryOp.LogicalNot => "not ",
            IR.UnaryOp.Length => "#",
            IR.UnaryOp.BitwiseNot => Features.BitwiseStyle switch
            {
                BitwiseStyle.Operator => "~",
                BitwiseStyle.BitLib => "bit.bnot",
                _ => ""
            },
            _ => "?"
        };
    }

    public bool IsBinaryInfix(IR.BinaryOp op)
    {
        if (!Features.HasBitwise && IsBitwiseOp(op)) return false;
        if (op == IR.BinaryOp.FloorDiv && !Features.HasFloorDiv) return false;
        return Features.BitwiseStyle != BitwiseStyle.BitLib || !IsBitwiseOp(op);
    }

    public bool IsBitwiseOp(IR.BinaryOp op) =>
        op is IR.BinaryOp.BitwiseAnd or IR.BinaryOp.BitwiseOr or IR.BinaryOp.BitwiseXor
            or IR.BinaryOp.LShift or IR.BinaryOp.RShift;

    public bool IsConfiguredConcatOp(IR.BinaryOp op)
    {
        var configured = config.Code.ConcatOperator;
        if (string.IsNullOrEmpty(configured)) return false;
        var mapped = configured switch
        {
            "+" => IR.BinaryOp.Add,
            "-" => IR.BinaryOp.Sub,
            "*" => IR.BinaryOp.Mul,
            "/" => IR.BinaryOp.Div,
            "//" => IR.BinaryOp.FloorDiv,
            "%" => IR.BinaryOp.Mod,
            "^" => IR.BinaryOp.Pow,
            ".." => IR.BinaryOp.Concat,
            _ => (IR.BinaryOp?)null
        };
        return mapped.HasValue && mapped.Value == op;
    }

    #endregion

    #region Defer Helpers

    /// <summary>
    /// Records a call on a function's defer list with the function and arguments it has now.
    /// </summary>
    public string GetDeferHelper()
    {
        var unpackFn = Features.HasTableUnpack ? "table.unpack" : "unpack";
        return RequireHelper("defer", name =>
            $"local function {name}(list, fn, ...) local n = select(\"#\", ...) local args = {{ ... }} " +
            $"list[#list + 1] = function() return fn({unpackFn}(args, 1, n)) end end");
    }

    /// <summary>
    /// Runs a function's defer list in reverse once its body ended, then returns what the body
    /// returned or raises the error it raised.
    /// </summary>
    public string GetDeferFinishHelper()
    {
        return RequireHelper("finish_defers", name =>
            $"local function {name}(list, ok, ...) for i = #list, 1, -1 do list[i]() end " +
            $"if not ok then error((...), 0) end return ... end");
    }

    /// <summary>
    /// A helper running a try statement: the body under <c>pcall</c>, then the catch clause under
    /// <c>pcall</c> if the body raised, then the finally block, after which a remaining error is
    /// raised again. A body or catch clause returning from the enclosing function returns the
    /// marker <c>&lt;name&gt;_return</c> first, and the helper hands the returned values on in a table.
    /// </summary>
    public string GetTryHelper()
    {
        var unpackFn = Features.HasTableUnpack ? "table.unpack" : "unpack";
        return RequireHelper("try", name =>
            $"local {name}_return = {{}} " +
            $"local function {name}_pack(...) return {{ n = select(\"#\", ...), ... }} end " +
            $"local function {name}(body, handler, finalizer, swallow, ...) " +
            $"local result = {name}_pack(pcall(body, ...)) " +
            $"if not result[1] and handler then result = {name}_pack(pcall(handler, result[2], ...)) " +
            $"elseif not result[1] and swallow then result = {{ n = 1, true }} end " +
            $"if finalizer then finalizer(...) end " +
            $"if not result[1] then error(result[2], 0) end " +
            $"if result[2] == {name}_return then return {{ n = result.n - 2, {unpackFn}(result, 3, result.n) }} end " +
            $"end");
    }

    #endregion

    #region Async Driver Helper

    public string GetAsyncDriverHelper()
    {
        var unpackFn = Features.HasTableUnpack ? "table.unpack" : "unpack";
        return RequireHelper("async_drive", name =>
            $"local function {name}(co, done) " +
            $"local function step(...) " +
            $"local ok, payload = coroutine.resume(co, ...) " +
            $"if not ok then error(payload, 0) end " +
            $"if coroutine.status(co) == \"dead\" then if done then done(payload) end return end " +
            $"local fn = payload[1] local n = payload.n " +
            $"local args = {{{unpackFn}(payload, 2, n)}} " +
            $"args[n] = step " +
            $"fn({unpackFn}(args, 1, n)) " +
            $"end step() end");
    }

    #endregion

    #region Class Proxy Helper

    public string GetClassProxyHelper()
    {
        return RequireHelper("class_proxy", name =>
            $"local function {name}(cls, parent) " +
            $"local mt = {{ " +
            $"__class = cls, " +
            $"__index = function(t, k) " +
            $"local g = cls[\"__get_\" .. k] " +
            $"if g then return g(t) end " +
            $"local v = cls[k] " +
            $"if v ~= nil then return v end " +
            $"if parent then " +
            $"g = parent[\"__get_\" .. k] " +
            $"if g then return g(t) end " +
            $"v = parent[k] " +
            $"if v ~= nil then return v end " +
            $"end " +
            $"local op = cls.__op_index " +
            $"if op ~= nil and not (cls.__nebra_fields and cls.__nebra_fields[k]) then return op(t, k) end " +
            $"end, " +
            $"__newindex = function(t, k, v) " +
            $"local s = cls[\"__set_\" .. k] " +
            $"if s then s(t, v) return end " +
            $"if cls[\"__get_\" .. k] then error(\"Cannot set readonly property '\" .. k .. \"'\") end " +
            $"if parent then " +
            $"s = parent[\"__set_\" .. k] " +
            $"if s then s(t, v) return end " +
            $"if parent[\"__get_\" .. k] then error(\"Cannot set readonly property '\" .. k .. \"'\") end " +
            $"end " +
            $"local op = cls.__op_newindex " +
            $"if op ~= nil and not (cls.__nebra_fields and cls.__nebra_fields[k]) then op(t, k, v) return end " +
            $"rawset(t, k, v) " +
            $"end " +
            $"}} " +
            $"local __ops = {{\"__add\",\"__sub\",\"__mul\",\"__div\",\"__mod\",\"__pow\",\"__unm\",\"__concat\",\"__len\",\"__eq\",\"__lt\",\"__le\",\"__idiv\",\"__band\",\"__bor\",\"__bxor\",\"__bnot\",\"__shl\",\"__shr\",\"__call\",\"__tostring\",\"__close\",\"__pairs\"}} " +
            $"for i = 1, #__ops do " +
            $"local k = __ops[i] " +
            $"if rawget(cls, k) then mt[k] = cls[k] " +
            $"elseif parent and rawget(parent, k) then mt[k] = parent[k] end " +
            $"end " +
            $"return mt end");
    }

    #endregion

    #region TypeOf / InstanceOf Helpers

    public string GetTypeOfHelper()
    {
        return RequireHelper("typeof", name =>
            $"local function {name}(v) " +
            $"local t = type(v) " +
            $"if t == \"table\" then " +
            $"local mt = getmetatable(v) " +
            $"while mt ~= nil do " +
            $"if type(mt) == \"table\" and type(mt.__index) == \"table\" and mt.__index.__name then return mt.__index.__name end " +
            $"if type(mt) == \"table\" and mt.__name then return mt.__name end " +
            $"mt = getmetatable(mt) " +
            $"end " +
            $"end " +
            $"return t " +
            $"end");
    }

    /// <summary>
    /// Emits the helper backing an `is` test against an interface. Interfaces have no runtime
    /// value, so the check looks up the name in the `__interfaces` set the class carries.
    /// </summary>
    /// <summary>
    /// A helper recording the type arguments of a generic object in a table shared by every module
    /// and keyed weakly by the object, so the object itself carries nothing extra.
    /// </summary>
    public string GetGenericRecordHelper()
    {
        return RequireHelper("typed", name =>
            $"local {name}_registry = _G.__nebra_generic_types or setmetatable({{}}, {{ __mode = \"k\" }}) " +
            $"_G.__nebra_generic_types = {name}_registry " +
            $"local function {name}(v, record) {name}_registry[v] = record return v end");
    }

    /// <summary>
    /// A helper telling whether the type arguments recorded for a value under a generic class or
    /// interface's name match the expected ones. A value nothing was recorded for, such as one
    /// created in Lua, matches, as does a recorded <c>any</c>.
    /// </summary>
    public string GetGenericArgumentsHelper()
    {
        return RequireHelper("type_args", name =>
            $"local function {name}(v, key, args) " +
            $"local types = _G.__nebra_generic_types " +
            $"local record = types and types[v] " +
            $"local stored = record and record[key] " +
            $"if stored == nil then return true end " +
            $"for i = 1, #args do if stored[i] ~= args[i] and stored[i] ~= \"any\" then return false end end " +
            $"return true " +
            $"end");
    }

    /// <summary>A helper checking that a value is an instance of a class instantiated with the given type arguments.</summary>
    public string GetGenericClassCheckHelper()
    {
        var instanceOf = GetInstanceOfHelper();
        var arguments = GetGenericArgumentsHelper();
        return RequireHelper("is_instance", name =>
            $"local function {name}(v, cls, key, args) return {instanceOf}(v, cls) and {arguments}(v, key, args) end");
    }

    /// <summary>A helper checking that a value implements an interface instantiated with the given type arguments.</summary>
    public string GetGenericInterfaceCheckHelper()
    {
        var implements = GetImplementsHelper();
        var arguments = GetGenericArgumentsHelper();
        return RequireHelper("implements_args", name =>
            $"local function {name}(v, key, args) return {implements}(v, key) and {arguments}(v, key, args) end");
    }

    public string GetImplementsHelper()
    {
        return RequireHelper("implements", name =>
            $"local function {name}(v, iface) " +
            $"if type(v) ~= \"table\" then return false end " +
            $"local mt = getmetatable(v) " +
            $"while type(mt) == \"table\" do " +
            $"if type(mt.__interfaces) == \"table\" and mt.__interfaces[iface] then return true end " +
            $"if type(mt.__index) == \"table\" then " +
            $"if type(mt.__index.__interfaces) == \"table\" and mt.__index.__interfaces[iface] then return true end " +
            $"mt = getmetatable(mt.__index) " +
            $"else " +
            $"mt = getmetatable(mt) " +
            $"end " +
            $"end " +
            $"return false " +
            $"end");
    }

    /// <summary>
    /// Emits the alias both spellings of unpack are rewritten to. Lua 5.1 and LuaJIT provide only
    /// the global <c>unpack</c>, Lua 5.2 and later only <c>table.unpack</c>. The alias is bound at
    /// the top of the chunk, so it cannot be captured by a local the source declares later.
    /// </summary>
    public string GetUnpackHelper()
    {
        return RequireHelper("unpack", name => $"local {name} = table.unpack or unpack");
    }

    /// <summary>
    /// A helper telling whether a value is an instance of the class named by its second argument
    /// or of a class extending it, comparing the <c>__name</c> each class table carries so the
    /// class itself need not be in scope.
    /// </summary>
    public string GetClassCheckHelper()
    {
        return RequireHelper("is_class", name =>
            $"local function {name}(v, className) " +
            $"if type(v) ~= \"table\" then return false end " +
            $"local c = getmetatable(v) " +
            $"if type(c) == \"table\" and rawget(c, \"__class\") ~= nil then c = rawget(c, \"__class\") end " +
            $"while type(c) == \"table\" do " +
            $"if rawget(c, \"__name\") == className then return true end " +
            $"local mt = getmetatable(c) " +
            $"c = mt and mt.__index " +
            $"end " +
            $"return false " +
            $"end");
    }

    public string GetInstanceOfHelper()
    {
        return RequireHelper("instanceof", name =>
            $"local function {name}(v, cls) " +
            $"if type(v) ~= \"table\" or cls == nil then return false end " +
            $"local mt = getmetatable(v) " +
            $"while mt ~= nil do " +
            $"if mt == cls then return true end " +
            $"if type(mt) == \"table\" and mt.__index == cls then return true end " +
            $"if type(mt) == \"table\" and type(mt.__index) == \"table\" then " +
            $"if mt.__index == cls then return true end " +
            $"mt = getmetatable(mt.__index) " +
            $"else " +
            $"mt = getmetatable(mt) " +
            $"end " +
            $"end " +
            $"return false " +
            $"end");
    }

    #endregion

    #region Floor Div Helper

    public string GetFloorDivHelper()
    {
        return RequireHelper("floordiv", name =>
            $"local function {name}(a, b) return math.floor(a / b) end");
    }

    #endregion

    #region Scope Blocks

    public void BeginBlock(string header)
    {
        WriteLine(header);
        Indent();
    }

    public void EndBlock(string footer = "end")
    {
        Dedent();
        WriteLine(footer);
    }

    #endregion
}
