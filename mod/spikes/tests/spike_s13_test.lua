-- Stub-environment test of the session-4 spike Lua (mod/spikes/x4mp_spike/ui/x4mp_spike_core.lua + x4mp_spike_s13.lua).
-- Run (LuaJIT or Lua 5.1, from the repo root):   luajit mod/spikes/tests/spike_s13_test.lua <existing temp folder>
-- (the folder gets Egosoft/X4/123/save and Egosoft/X4/x4mp created by the caller: the galaxy dump writes there.)
-- Without an interpreter: python -c "from lupa import luajit21 as L; r=L.LuaRuntime(); r.globals().arg=r.table(); ..." works too.
-- Prints "OK <n> checks" and exits 0, or the failing check and exits 1.

local TMP = (arg and arg[1]) or __TEST_TMP
local ROOT = (arg and arg[0] and arg[0]:match("^(.*)[/\\]tests[/\\][^/\\]*$")) or __TEST_ROOT or "mod/spikes"
local checks = 0
local function check(cond, msg)
	checks = checks + 1
	if not cond then error("CHECK FAILED: " .. tostring(msg), 2) end
end
local function contains(hay, needle, msg) check(hay:find(needle, 1, true) ~= nil, (msg or "contains") .. ": '" .. needle .. "' not in log") end

-- ---- stub game ------------------------------------------------------------
local logs, md, events, scripts = {}, {}, {}, {}
local setaOn = false
local clock = 0
local files = {}

DebugError = function(m) logs[#logs + 1] = tostring(m) end
RegisterEvent = function(name, fn) events[name] = events[name] or {}; table.insert(events[name], fn) end
SetScript = function(name, fn) scripts[name] = fn end
AddUITriggeredEvent = function(screen, control, value) md[#md + 1] = { screen = screen, control = control, value = value } end
ExecuteDebugCommand = function() end
GetCurRealTime = function() return clock end
ConvertStringToLuaID = function(s) return "LID" .. s end
ConvertStringTo64Bit = function(s) return tonumber(s) end
GetFactionData = function(f, ...) if select(1, ...) == "color" then return { r = 255, g = 128, b = 0, a = 100 } end return "Team " .. f, 0.75 end
GetComponentData = function(id, ...) return "ship" .. tostring(id), 100, 3, "x4mp_team_1" end
Menus = { { name = "Other" }, { name = "ChatWindow", shown = true, refreshed = 0, onChatMessageReceived = function(self) end } }
Menus[2].onChatMessageReceived = function() Menus[2].refreshed = Menus[2].refreshed + 1 end
local vanillaMessages = { { author = "Venture", authorid = 5, time = 1, text = "from ventures", reported = false, isprivate = false } }
OnlineGetChatMessages = function() return vanillaMessages end
local sentToVentures = {}
OnlineSendChatMessage = function(text, userid) sentToVentures[#sentToVentures + 1] = text end
OnlineGetUserName = function() return "Tester", 77 end

local fakeC = {
	QueryPerformanceFrequency = function(f) f[0] = 1000; return 1 end,
	QueryPerformanceCounter = function(b) b[0] = clock * 1000 end,
	IsSetaActive = function() return setaOn end,
	GetCurrentUTCDataTime = function() return 1780000000 end,
	GetChatAuthorColor2 = function(a) return "chatuser_" .. (#a % 18) end,
	GetSaveFolderPath = function() return TMP .. "/Egosoft/X4/123/save/" end,
}
package.loaded.ffi = {
	cdef = function() end,
	string = function(s) return s end,
	new = function() return { [0] = 0 } end,
	cast = function() return 0 end,
	C = setmetatable({}, { __index = function(_, k) return fakeC[k] end }),
}
-- the galaxy writer tries Lua io first; point its Windows-style backslash path at the temp folder on every OS
local realOpen = io.open
io.open = function(path, mode)
	files.attempted = path
	return realOpen((path:gsub("\\", "/")), mode)
end

dofile(ROOT .. "/x4mp_spike/ui/x4mp_spike_core.lua")
dofile(ROOT .. "/x4mp_spike/ui/x4mp_spike_s13.lua")
local S = X4MPSpike

local function fire(name, param) for _, fn in ipairs(events[name] or {}) do fn(nil, param) end end
local function runFrames(n) for _ = 1, n do clock = clock + 0.1; scripts.onUpdate() end end
local function logText() return table.concat(logs, "\n") end
local function lastMD(control)
	for i = #md, 1, -1 do if md[i].control == control then return md[i] end end
end

-- ---- registration -----------------------------------------------------------
for _, e in ipairs({ "x4mp.spike_dress", "x4mp.spike_velocity", "x4mp.spike_seta_off", "x4mp_spike.run", "x4mp_spike.s13_galaxy" }) do
	check(events[e] and #events[e] == 1, "event registered once: " .. e)
end
for _, b in ipairs({ "dress", "velocity", "seta_off", "seta_watch", "teams_product", "teams_report", "teams_end", "md_table", "galaxy_dump", "chat" }) do
	check(S.blocks[b] ~= nil, "block registered: " .. b)
end

-- ---- S13.1 dress ------------------------------------------------------------
fire("x4mp.spike_dress", "12345|[MP] Alice")
local m = lastMD("s13_dress")
check(m and m.screen == "X4MP_Spike2", "dress goes to MD screen X4MP_Spike2")
check(m.value[1] == "LID12345" and m.value[2] == "[MP] Alice" and m.value[3] == 50, "dress list [id, name, minhull 50]")
fire("x4mp.spike_dress", "777|name|with|bars")
check(lastMD("s13_dress").value[2] == "name|with|bars", "name keeps '|' characters")
fire("x4mp.spike_dress", "")
contains(logText(), "result=bad_param", "empty dress param is a FAIL line")
S.run("dress", "minhull=30")
fire("x4mp.spike_dress", "9|x")
check(lastMD("s13_dress").value[3] == 30, "block dress minhull=30 changes the default")

-- ---- S13.2c velocity ----------------------------------------------------------
fire("x4mp.spike_velocity", "42|1.5|0|-300")
m = lastMD("s13_velocity")
check(m.value[1] == "LID42" and m.value[2] == 1.5 and m.value[4] == -300 and m.value[5] == 0, "velocity list")
local before = #md
fire("x4mp.spike_velocity", "42|a|b|c")
check(#md == before, "bad velocity args send nothing")
contains(logText(), "result=bad_args")
S.run("velocity", "id=5 vx=1 vy=2 vz=3 const=1")
check(lastMD("s13_velocity").value[5] == 1, "velocity block const=1")

-- ---- S13.9 seta -----------------------------------------------------------------
setaOn = false
fire("x4mp.spike_seta_off", "")
check(lastMD("s13_seta_off") == nil, "SETA not active: MD is not asked to toggle")
setaOn = true
fire("x4mp.spike_seta_off", "")
check(lastMD("s13_seta_off").value == "toggle", "SETA active: MD asked to toggle")
runFrames(5) -- first readback after 0.3 s
contains(logText(), "what=seta_lua_readback IsSetaActive=true", "readback while still on")
setaOn = false
runFrames(20) -- second readback 1.2 s later
contains(logText(), "S13.9 PASS what=seta_off_result", "SETA off verified")
setaOn = true
S.run("seta_watch", "secs=2")
runFrames(2)
setaOn = false
runFrames(2)
contains(logText(), "what=seta_changed IsSetaActive=false", "seta_watch sees the change")

-- ---- S13.7 teams ----------------------------------------------------------------
S.run("teams_product", "minhull=40")
check(lastMD("s13_teams").value == 40, "teams minhull passed to MD")
fire("x4mp_spike.s13_teams_ship_tag", "1")
fire("x4mp_spike.s13_teams_ship", "1001")
fire("x4mp_spike.s13_teams_ship_tag", "2")
fire("x4mp_spike.s13_teams_ship", "1002")
runFrames(40)
contains(logText(), "what=faction_readback tag=after_3s faction=x4mp_team_1", "faction readback")
contains(logText(), "colour_rgb=255/128/0", "colour readback")
contains(logText(), "what=ship_readback tag=after_3s team=2 id=1002", "ship readback")
S.run("teams_end", "")
check(lastMD("s13_teams_end") ~= nil, "teams_end sent")

-- ---- S13.8 md_table -----------------------------------------------------------------
S.run("md_table", "")
check(lastMD("s13_persist") ~= nil, "md_table sent")

-- ---- S13.11 galaxy dump ---------------------------------------------------------------
S.run("galaxy_dump", "")
check(lastMD("s13_galaxy") ~= nil, "galaxy request sent to MD")
local chunk, n = {}, 0
for i = 150, 1, -1 do -- reverse order on purpose: the dump must be sorted
	n = n + 1
	chunk[#chunk + 1] = string.format("cluster_%03d_sector001_macro|cluster_%03d_macro|cluster_%03d_sector001_macro,cluster_%03d_sector001_macro", i, i, (i % 150) + 1, ((i + 1) % 150) + 1)
	if n % 30 == 0 then fire("x4mp_spike.s13_galaxy", "R;" .. table.concat(chunk, ";")); chunk = {} end
end
fire("x4mp_spike.s13_galaxy", "E;150")
contains(logText(), "S13.11 PASS what=galaxy_collected sectors=150")
contains(logText(), "what=galaxy_file_write ok=true", "file written (Lua io or ffi)")
local f = io.open(TMP .. "/Egosoft/X4/x4mp/galaxy-dump.json", "rb")
check(f ~= nil, "galaxy-dump.json exists in <Documents>/Egosoft/X4/x4mp")
local json = f:read("*a"); f:close()
check(json:find('"sector_count":150', 1, true) ~= nil, "json sector_count")
check(json:find('"macro":"cluster_001_sector001_macro"', 1, true) < json:find('"macro":"cluster_150_sector001_macro"', 1, true), "sorted by macro")
-- the chunks in the log rebuild the same JSON
local parts = {}
for part, total, data in logText():gmatch("S13%.11 DATA part=(%d+)/(%d+) json=([^\n]*)") do parts[tonumber(part)] = data end
check(#parts >= 2, "json logged in several chunks")
check((table.concat(parts):gsub("~", "\n")) == json, "log chunks rebuild the file exactly")
for _, line in ipairs(logs) do check(#line < 1100, "log line short enough: " .. #line) end

-- ---- S13.12 chat ------------------------------------------------------------------------
local origGet, origSend = OnlineGetChatMessages, OnlineSendChatMessage
S.run("chat", "")
check(OnlineGetChatMessages ~= origGet and OnlineSendChatMessage ~= origSend, "chat installed both wrappers")
local list = OnlineGetChatMessages()
check(#list == 3, "vanilla message + Alice + Bob (got " .. #list .. ")")
check(list[1].author == "Venture", "vanilla messages kept")
check(list[2].author:find("Alice", 1, true) and list[2].author:sub(1, 1) == "\027", "Alice has an embedded colour code")
check(list[3].author == "Bob", "Bob uses the palette")
check(Menus[2].refreshed >= 2, "chat window refreshed on injection")
check(type(list[2].time) == "number" and list[2].authorid < 0 and list[2].reported == false and list[2].isprivate == false, "message shape")
contains(logText(), "GetChatAuthorColor2=chatuser_")
S.run("chat", "")
check(#OnlineGetChatMessages() == 5, "second run appends, installs nothing twice")
check(OnlineGetChatMessages == S.s13.chat.getWrapper, "idempotent: wrapper not wrapped twice")
OnlineSendChatMessage("hello", 0)
check(#sentToVentures == 0, "typed line is NOT passed to Ventures while capture is on")
contains(logText(), "S13.12 PASS what=typed_line_captured n=1 text=hello", "typed line logged")
local after = OnlineGetChatMessages()
check(after[#after].text == "hello" and after[#after].author == "Tester", "typed line echoed under the player's name")
S.run("chat", "say Carol 00ff00 hi there friend")
after = OnlineGetChatMessages()
check(after[#after].text == "hi there friend" and after[#after].author:find("Carol", 1, true), "chat say")
-- another mod wraps on top of us: 'chat off' must leave its wrapper alone
local ours = OnlineGetChatMessages
local other = function(...) return ours(...) end
OnlineGetChatMessages = other
S.run("chat", "off")
check(OnlineGetChatMessages == other, "unwrap only if ours: foreign outer wrapper left in place")
check(OnlineSendChatMessage == origSend, "send wrapper restored (still outermost)")

print("OK " .. checks .. " checks")
