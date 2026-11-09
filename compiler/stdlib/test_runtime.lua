-- Runtime implementation of the `nebra:test` module. Loaded via
-- package.preload by NebraRuntime.RegisterTestModule so that
-- `require("nebra:test")` resolves without touching package.path.

local M = {}

local state = {
    passed = 0,
    failed = 0,
    skipped = 0,
    failures = {},
    suiteStack = {},
    currentFile = nil,
    filter = nil,
    quiet = false,
    beforeEach = {},
    afterEach = {},
}

M.__state = state

local ANSI_GREEN = "\27[32m"
local ANSI_RED = "\27[31m"
local ANSI_YELLOW = "\27[33m"
local ANSI_DIM = "\27[2m"
local ANSI_RESET = "\27[0m"

local function indent()
    return string.rep("  ", #state.suiteStack)
end

local function fullName(name)
    if #state.suiteStack == 0 then return name end
    return table.concat(state.suiteStack, " > ") .. " > " .. name
end

local function matchesFilter(name)
    if not state.filter or state.filter == "" then return true end
    return string.find(name, state.filter, 1, true) ~= nil
end

local function printLine(s)
    if not state.quiet then print(s) end
end

local function located(err)
    local text = tostring(err)
    return state.locate and state.locate(text) or text
end

local function runHooks(hooks)
    for i = 1, #hooks do
        local ok, err = xpcall(hooks[i], debug.traceback)
        if not ok then error("hook failed: " .. located(err), 0) end
    end
end

local function tickOk(name)
    printLine(indent() .. ANSI_GREEN .. "✓" .. ANSI_RESET .. " " .. name)
end

local function tickFail(name, err)
    printLine(indent() .. ANSI_RED .. "✗" .. ANSI_RESET .. " " .. name)
    if err then
        for line in tostring(err):gmatch("[^\r\n]+") do
            printLine(indent() .. "    " .. ANSI_DIM .. line .. ANSI_RESET)
        end
    end
end

local function tickSkip(name)
    printLine(indent() .. ANSI_YELLOW .. "○" .. ANSI_RESET .. " " .. name .. ANSI_DIM .. " (skipped)" .. ANSI_RESET)
end

function M.test(name, fn)
    local full = fullName(name)
    if not matchesFilter(full) then return end
    local ok1 = xpcall(function() runHooks(state.beforeEach) end, debug.traceback)
    local ok, err = xpcall(fn, debug.traceback)
    if not ok then err = located(err) end
    local ok2 = xpcall(function() runHooks(state.afterEach) end, debug.traceback)
    if ok and ok1 and ok2 then
        state.passed = state.passed + 1
        tickOk(name)
    else
        state.failed = state.failed + 1
        state.failures[#state.failures+1] = { name = full, error = err, file = state.currentFile }
        tickFail(name, err)
    end
end

M.it = M.test

function M.skip(name, _fn)
    state.skipped = state.skipped + 1
    tickSkip(name)
end

function M.describe(name, fn)
    printLine(indent() .. name)
    state.suiteStack[#state.suiteStack+1] = name
    local savedBefore = state.beforeEach
    local savedAfter = state.afterEach
    state.beforeEach = {}
    for i = 1, #savedBefore do state.beforeEach[i] = savedBefore[i] end
    state.afterEach = {}
    for i = 1, #savedAfter do state.afterEach[i] = savedAfter[i] end
    local ok, err = xpcall(fn, debug.traceback)
    if not ok then err = located(err) end
    state.beforeEach = savedBefore
    state.afterEach = savedAfter
    state.suiteStack[#state.suiteStack] = nil
    if not ok then
        state.failed = state.failed + 1
        state.failures[#state.failures+1] = {
            name = fullName(name), error = err, file = state.currentFile,
        }
        printLine(indent() .. ANSI_RED .. "✗ describe block raised: " .. tostring(err) .. ANSI_RESET)
    end
end

function M.beforeEach(fn) state.beforeEach[#state.beforeEach+1] = fn end
function M.afterEach(fn) state.afterEach[#state.afterEach+1] = fn end

local Assertion = {}
Assertion.__index = Assertion

local function fmt(v)
    local t = type(v)
    if t == "string" then return string.format("%q", v) end
    if t == "nil" then return "nil" end
    if t == "table" then
        local parts = {}
        for k, val in pairs(v) do
            parts[#parts+1] = tostring(k) .. "=" .. tostring(val)
            if #parts >= 6 then parts[#parts+1] = "..."; break end
        end
        return "{ " .. table.concat(parts, ", ") .. " }"
    end
    return tostring(v)
end

local function deepEqual(a, b)
    if a == b then return true end
    if type(a) ~= "table" or type(b) ~= "table" then return false end
    for k, v in pairs(a) do if not deepEqual(v, b[k]) then return false end end
    for k, _ in pairs(b) do if a[k] == nil then return false end end
    return true
end

function Assertion:toBe(other)
    if self.value ~= other then
        error("expected " .. fmt(self.value) .. " to be " .. fmt(other), 2)
    end
end

function Assertion:toNotBe(other)
    if self.value == other then
        error("expected " .. fmt(self.value) .. " not to be " .. fmt(other), 2)
    end
end

function Assertion:toEqual(other)
    if not deepEqual(self.value, other) then
        error("expected " .. fmt(self.value) .. " to equal " .. fmt(other), 2)
    end
end

function Assertion:toNotEqual(other)
    if deepEqual(self.value, other) then
        error("expected " .. fmt(self.value) .. " not to equal " .. fmt(other), 2)
    end
end

function Assertion:toBeNear(other, eps)
    eps = eps or 1e-9
    if type(self.value) ~= "number" or type(other) ~= "number" then
        error("toBeNear requires numbers", 2)
    end
    local diff = self.value - other
    if diff < 0 then diff = -diff end
    if diff > eps then
        error(string.format("expected %s to be within %s of %s",
            tostring(self.value), tostring(eps), tostring(other)), 2)
    end
end

function Assertion:toBeTruthy()
    if not self.value then
        error("expected " .. fmt(self.value) .. " to be truthy", 2)
    end
end

function Assertion:toBeFalsy()
    if self.value then
        error("expected " .. fmt(self.value) .. " to be falsy", 2)
    end
end

function Assertion:toBeNil()
    if self.value ~= nil then
        error("expected nil, got " .. fmt(self.value), 2)
    end
end

function Assertion:toBeDefined()
    if self.value == nil then
        error("expected value to be defined", 2)
    end
end

function Assertion:toContain(needle)
    if type(self.value) == "string" then
        if not string.find(self.value, tostring(needle), 1, true) then
            error("expected " .. fmt(self.value) .. " to contain " .. fmt(needle), 2)
        end
    elseif type(self.value) == "table" then
        for _, v in pairs(self.value) do if v == needle then return end end
        error("expected table to contain " .. fmt(needle), 2)
    else
        error("toContain requires string or table", 2)
    end
end

function Assertion:toNotContain(needle)
    if type(self.value) == "string" then
        if string.find(self.value, tostring(needle), 1, true) then
            error("expected " .. fmt(self.value) .. " not to contain " .. fmt(needle), 2)
        end
    elseif type(self.value) == "table" then
        for _, v in pairs(self.value) do
            if v == needle then
                error("expected table not to contain " .. fmt(needle), 2)
            end
        end
    else
        error("toNotContain requires string or table", 2)
    end
end

function Assertion:toMatch(pattern)
    if type(self.value) ~= "string" then error("toMatch requires a string", 2) end
    if not string.find(self.value, pattern) then
        error("expected " .. fmt(self.value) .. " to match " .. fmt(pattern), 2)
    end
end

function Assertion:toHaveLength(n)
    local len = #(self.value)
    if len ~= n then
        error("expected length " .. tostring(n) .. ", got " .. tostring(len), 2)
    end
end

function Assertion:toThrow(expected)
    if type(self.value) ~= "function" then
        error("toThrow requires a function", 2)
    end
    local ok, err = pcall(self.value)
    if ok then error("expected function to throw", 2) end
    if expected ~= nil and type(expected) == "string" then
        if not string.find(tostring(err), expected, 1, true) then
            error("expected error to contain " .. fmt(expected) .. ", got " .. tostring(err), 2)
        end
    end
end

function M.expect(value)
    return setmetatable({ value = value }, Assertion)
end

function M.__set_filter(f) state.filter = f end
function M.__set_quiet(b) state.quiet = b end
function M.__set_locate(fn) state.locate = fn end
function M.__set_current_file(p) state.currentFile = p end

function M.__begin_file(path)
    state.currentFile = path
    if not state.quiet then
        print("")
        print(ANSI_DIM .. "--- " .. (path or "<anonymous>") .. " ---" .. ANSI_RESET)
    end
end

function M.__results() return state end

-- The summary prints in quiet mode too: quiet suppresses the per-test ticks, not the result. A run
-- whose outcome is invisible is worse than a noisy one, and a failing run has to name its failures.
function M.__summary()
    if not state.quiet then
        print("")
        print("-----------------------------------------")
    end
    local total = state.passed + state.failed
    if state.failed > 0 then
        print(ANSI_RED .. "✗ " .. tostring(state.failed) .. " failed" .. ANSI_RESET
            .. ", " .. ANSI_GREEN .. tostring(state.passed) .. " passed" .. ANSI_RESET
            .. " (" .. tostring(total) .. " total)"
            .. (state.skipped > 0
                and (", " .. ANSI_YELLOW .. tostring(state.skipped) .. " skipped" .. ANSI_RESET)
                or ""))
        print("")
        for i = 1, #state.failures do
            local f = state.failures[i]
            print(ANSI_RED .. "  • " .. f.name .. ANSI_RESET)
            if f.file then print("    " .. ANSI_DIM .. f.file .. ANSI_RESET) end
        end
    else
        print(ANSI_GREEN .. "✓ all " .. tostring(state.passed) .. " test(s) passed" .. ANSI_RESET
            .. (state.skipped > 0 and (" (" .. tostring(state.skipped) .. " skipped)") or ""))
    end
end

return M
