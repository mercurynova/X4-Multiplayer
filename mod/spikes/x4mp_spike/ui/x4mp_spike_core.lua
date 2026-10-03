-- x4mp_spike_core.lua : spike v2 framework (block registry, launchers, log helpers, scheduler).
--
-- Loaded FIRST (see ui.xml). Defines the global table X4MPSpike that every other spike Lua file uses.
-- Blocks never run automatically: they run when the user types  /x4mpspike <block> [k=v ...]  in the chat window,
-- when the probe (x4mp_probe, M2-005) raises the Lua event "x4mp_spike.run" (param "<block>;k=v;k=v"), or when an
-- MD cue raises the same event (raise_lua_event name="'x4mp_spike.run'" param="'<block>;k=v'").
--
-- Public API (documented in mod/spikes/README.md, "Writing a block"):
--   X4MPSpike.register(name, fn, description)  register block `name`; fn(args) is called inside pcall. Re-registering
--                                              the same name replaces it (idempotent on file reload).
--   X4MPSpike.registerMD(name, description)    register a block that only forwards to an MD cue (see toMD).
--   X4MPSpike.run(name, rawArgs)               run a block now (what the launchers call).
--   X4MPSpike.toMD(control, value)             Lua -> MD: AddUITriggeredEvent("X4MP_Spike2", control, value).
--   X4MPSpike.notify(text)                     on-screen notification through the MD core cue (prefixed "X4MP spike: ").
--   X4MPSpike.log(step, level, kv)             "[X4MP-SPIKE] <step> <PASS|FAIL|INFO|MEASURE> <kv>"
--   X4MPSpike.K(name, value, ...)              builds "name=value name=value" (spaces in values become "_")
--   X4MPSpike.now()                            seconds, QueryPerformanceCounter when available
--   X4MPSpike.addUpdate(fn)                    called every frame from the single "onUpdate" script
--   X4MPSpike.startRoutine(name, fn)           coroutine run from the frame loop; inside it use waitSeconds/waitFrames
--   X4MPSpike.waitSeconds(s), X4MPSpike.waitFrames(n)
--   X4MPSpike.parseArgs(text)                  "k=v k=v pos" or "k=v;k=v" -> { k = v, _ = {pos...}, raw = text }
--
-- Sources (all under x4-unpacked/ui/addons/): RegisterEvent ego_chatwindow/chatwindow.lua:32; SetScript("onUpdate")
-- ego_viewhelper/viewhelper.lua:28; ExecuteDebugCommand(command, parameter) is what the vanilla chat window calls for
-- "/command parameter" (chatwindow.lua:465); DebugError chatwindow.lua:445; AddUITriggeredEvent menu_map.lua:3059.

-- luacheck: globals X4MPSpike DebugError RegisterEvent SetScript AddUITriggeredEvent ExecuteDebugCommand GetCurRealTime

local ffi = require("ffi")
local C = ffi.C

local S = X4MPSpike or {}
X4MPSpike = S

S.TAG = "[X4MP-SPIKE]"
S.SCREEN = "X4MP_Spike2" -- Lua -> MD screen id (session 1 used "X4MP_Spike"; both can coexist)
S.RUN_EVENT = "x4mp_spike.run"
S.blocks = S.blocks or {} -- name -> { fn = function, desc = string }
S.runCount = S.runCount or {}
S.version = 2

------------------------------------------------------------------------------
-- logging (same format as session 1)
------------------------------------------------------------------------------
local function emit(line)
	local ok = pcall(DebugError, line)
	if not ok then pcall(print, line) end
end

local function fmtv(v)
	local t = type(v)
	if t == "number" then
		if v == math.floor(v) and math.abs(v) < 1e15 then return string.format("%d", v) end
		return string.format("%.3f", v)
	elseif t == "boolean" then
		return v and "true" or "false"
	elseif v == nil then
		return "nil"
	end
	local s = tostring(v):gsub("%s+", "_")
	return s
end

function S.K(...)
	local n = select("#", ...)
	local a = { ... }
	local out = {}
	for i = 1, n, 2 do
		out[#out + 1] = tostring(a[i]) .. "=" .. fmtv(a[i + 1])
	end
	return table.concat(out, " ")
end

function S.log(step, level, kv)
	emit(S.TAG .. " " .. step .. " " .. level .. " " .. (kv or ""))
end

local K, log = S.K, S.log

------------------------------------------------------------------------------
-- timer
------------------------------------------------------------------------------
local qpcFreq, qpcBuf = nil, nil
do
	-- a failed cdef because of "attempt to redefine" is fine: the functions are declared either way
	pcall(ffi.cdef, "int QueryPerformanceCounter(int64_t *lpPerformanceCount); int QueryPerformanceFrequency(int64_t *lpFrequency);")
	pcall(function()
		local f = ffi.new("int64_t[1]")
		if C.QueryPerformanceFrequency(f) ~= 0 and tonumber(f[0]) > 0 then
			qpcFreq = tonumber(f[0])
			qpcBuf = ffi.new("int64_t[1]")
		end
	end)
end
S.timerKind = qpcFreq and "QueryPerformanceCounter" or "GetCurRealTime"

function S.now()
	if qpcFreq then
		C.QueryPerformanceCounter(qpcBuf)
		return tonumber(qpcBuf[0]) / qpcFreq
	end
	return GetCurRealTime()
end

------------------------------------------------------------------------------
-- argument parsing
------------------------------------------------------------------------------
function S.parseArgs(text)
	text = tostring(text or "")
	local args = { _ = {}, raw = text }
	-- accept both "k=v k=v" (chat) and "k=v;k=v" (events); the first token of an event is the block name and is
	-- stripped by the caller
	for token in text:gmatch("[^%s;]+") do
		local k, v = token:match("^([^=]+)=(.*)$")
		if k then args[k] = v else args._[#args._ + 1] = token end
	end
	return args
end

------------------------------------------------------------------------------
-- MD bridge
------------------------------------------------------------------------------
function S.toMD(control, value)
	local ok, err = pcall(AddUITriggeredEvent, S.SCREEN, control, value)
	if not ok then log("LUA", "FAIL", K("what", "AddUITriggeredEvent", "control", control, "err", err)) end
	return ok
end

function S.notify(text)
	log("NOTIFY", "INFO", K("text", text))
	return S.toMD("notify", tostring(text))
end

------------------------------------------------------------------------------
-- scheduler: coroutines resumed from the per-frame update
------------------------------------------------------------------------------
local routines = {}
local updateHooks = {}
local frameCounter = 0

function S.addUpdate(fn)
	updateHooks[#updateHooks + 1] = fn
end

function S.frame() return frameCounter end

function S.startRoutine(name, fn)
	local ok, co = pcall(coroutine.create, fn)
	if not ok then
		log(name, "FAIL", K("what", "coroutine.create", "err", co))
		return
	end
	routines[#routines + 1] = { name = name, co = co, wakeAt = 0, wakeFrame = 0 }
end

function S.waitSeconds(s) coroutine.yield({ sec = s }) end
function S.waitFrames(n) coroutine.yield({ frames = n }) end

local function onUpdate()
	frameCounter = frameCounter + 1
	for i = 1, #updateHooks do
		local ok, err = pcall(updateHooks[i])
		if not ok then log("LUA", "FAIL", K("what", "update_hook_error", "hook", i, "err", err)) end
	end
	if #routines == 0 then return end
	local t = S.now()
	local i = 1
	while i <= #routines do
		local r = routines[i]
		local remove = false
		if t >= r.wakeAt and frameCounter >= r.wakeFrame then
			local ok, req = coroutine.resume(r.co)
			if not ok then
				log(r.name, "FAIL", K("what", "routine_error", "err", req))
				remove = true
			elseif coroutine.status(r.co) == "dead" then
				remove = true
			elseif type(req) == "table" then
				if req.sec then r.wakeAt = S.now() + req.sec end
				if req.frames then r.wakeFrame = frameCounter + req.frames end
			end
		end
		if remove then table.remove(routines, i) else i = i + 1 end
	end
end

------------------------------------------------------------------------------
-- block registry
------------------------------------------------------------------------------
function S.register(name, fn, desc)
	if type(name) ~= "string" or type(fn) ~= "function" then
		log("LUA", "FAIL", K("what", "register_bad_args", "name", tostring(name)))
		return false
	end
	S.blocks[name] = { fn = fn, desc = desc or "" }
	return true
end

function S.registerMD(name, desc)
	return S.register(name, function(args)
		log("RUN", "INFO", K("block", name, "what", "forwarded_to_md"))
		S.toMD(name, args.raw)
	end, desc or "MD block (forwarded)")
end

local function blockNames()
	local names = {}
	for n in pairs(S.blocks) do names[#names + 1] = n end
	table.sort(names)
	return names
end
S.blockNames = blockNames

function S.run(name, rawArgs)
	local entry = S.blocks[name]
	if not entry then
		log("RUN", "FAIL", K("what", "unknown_block", "block", name, "known", table.concat(blockNames(), ",")))
		S.notify("unknown block '" .. tostring(name) .. "'. Known: " .. table.concat(blockNames(), ", "))
		return false
	end
	S.runCount[name] = (S.runCount[name] or 0) + 1
	log("RUN", "INFO", K("block", name, "state", "start", "run", S.runCount[name], "args", rawArgs or ""))
	local ok, err = pcall(entry.fn, S.parseArgs(rawArgs))
	if not ok then
		log("RUN", "FAIL", K("block", name, "what", "block_error", "err", err))
		S.notify(name .. ": error, see log")
		return false
	end
	log("RUN", "INFO", K("block", name, "state", "returned", "run", S.runCount[name]))
	return true
end

-- built-in blocks
S.register("list", function()
	log("RUN", "INFO", K("what", "blocks", "known", table.concat(blockNames(), ",")))
	S.notify("blocks: " .. table.concat(blockNames(), ", "))
end, "log and show the registered blocks")

S.register("ping", function(args)
	log("RUN", "INFO", K("what", "ping", "src", args.src, "frame", frameCounter, "timer", S.timerKind))
	S.toMD("ping2", frameCounter)
	S.notify("ping ok, framework v" .. S.version)
end, "framework smoke test (Lua log + MD notification)")

------------------------------------------------------------------------------
-- launchers
------------------------------------------------------------------------------
-- (1) Lua event "x4mp_spike.run", raised by the probe or by an MD cue. param = "<block>;k=v;k=v"
local function onRunEvent(_, param)
	param = tostring(param or "")
	local name, rest = param:match("^%s*([^;%s]+)[;%s]*(.*)$")
	if not name then
		log("RUN", "FAIL", K("what", "empty_run_event", "param", param))
		return
	end
	S.run(name, rest)
end

-- (2) chat "/x4mpspike <block> [k=v ...]": the vanilla chat window calls ExecuteDebugCommand("x4mpspike", "<block> ...").
-- Wrapper is chain safe: it captures whatever the global is now (vanilla or another mod's wrapper) and delegates every
-- other command. Idempotent: an existing wrapper of ours is never wrapped twice.
local function installChatWrapper()
	if S.edcWrapper and ExecuteDebugCommand == S.edcWrapper then
		return "already_installed"
	end
	local prev = ExecuteDebugCommand
	local wrapper = function(cmd, param, ...)
		if cmd == "x4mpspike" then
			local name, rest = tostring(param or ""):match("^%s*(%S+)%s*(.*)$")
			if not name then
				S.run("list", "")
			else
				S.run(name, rest)
			end
			return
		end
		if prev then return prev(cmd, param, ...) end
	end
	S.edcWrapper = wrapper
	ExecuteDebugCommand = wrapper
	return prev and "wrapped" or "installed_no_previous"
end

if not S.launchersInstalled then
	S.launchersInstalled = true
	local ok, err = pcall(function()
		RegisterEvent(S.RUN_EVENT, onRunEvent)
		SetScript("onUpdate", onUpdate)
	end)
	log("LUA", ok and "INFO" or "FAIL", K("what", "x4mp_spike_core_loaded", "version", S.version, "timer", S.timerKind,
		"run_event", S.RUN_EVENT, "err", ok and "" or err))
end

do
	local ok, res = pcall(installChatWrapper)
	log("LUA", ok and "INFO" or "FAIL", K("what", "chat_command_wrapper", "command", "x4mpspike", "result", res))
end
