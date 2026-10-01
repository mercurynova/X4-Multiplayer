-- x4mp_spike.lua : Lua side of the throwaway X4MP in-game spike extension.
--
-- Everything here is wrapped in pcall. Every result line starts with "[X4MP-SPIKE]" and is written with
-- DebugError (vanilla global, used e.g. in ui/addons/ego_chatwindow/chatwindow.lua:445), so it shows up in
-- the log started with  -debug all -logfile x4mp_spike.log.
--
-- Bridge (same mechanism as vanilla):
--   MD  -> Lua : <raise_lua_event name="'X4MP_Spike_cmd'" param="'cmd;k=v;k=v'"/>  -> RegisterEvent(name, fn(_, param))
--                (vanilla: ego_chatwindow.lua:32 RegisterEvent, md/cover.xml:397 raise_lua_event). A component can be
--                passed with a preceding 'X4MP_Spike_obj' event (param = component), see onObj().
--   Lua -> MD  : AddUITriggeredEvent("X4MP_Spike", control, value) -> <event_ui_triggered screen="'X4MP_Spike'"/>
--                (vanilla: menu_map.lua:3059 passes ConvertStringToLuaID(tostring(id)); menu_ship_configuration.lua:1399
--                passes a table, which MD sees as a list).
--
-- Source of every non-trivial call (all paths below x4-unpacked/ui/addons/):
--   AddPlayerMoney(int64)  ffi.cdef ego_detailmonitor/menu_map.lua:503 ; vanilla passes price*100 (menu_trader_inventory.lua:497)
--   GetPlayerMoney()       Lua global, ego_detailmonitor/menu_map.lua (60 uses); TransferPlayerMoneyTo(amount, comp) menu_map.lua:3498
--   CanTeleportPlayerTo / TeleportPlayerTo : menu_platformundock.lua:15,28,76 ; menu_interactmenu.lua:169,329,6305
--   SpawnObjectAtPos2 / SetObjectSectorPos / FindMacro / GetObjectPositionInSector : menu_mapeditor.lua:69,114,121 (+ UIPosRot struct :60)
--   ActivateObject : menu_interactmenu.lua:153 ; SelfDestructComponent / RemoveComponent / SetComponentOwner / GetOwnerDetails :
--                    menu_map.lua / menu_mapeditor.lua cdefs (see x4-api-notes 2.4/2.5)
--   GetNumAllFactions / GetAllFactions : menu_mapeditor.lua:70,340 ; GetNumAllFactionShips / GetAllFactionShips : menu_playerinfo.lua:185,205,3737
--   GetNumCargo / GetCargo / UIWareInfo : ego_targetmonitor/targetmonitor.lua:82-86
--   GetComponentData(id, "macro", ...) : Lua global, ego_targetmonitor/targetmonitor.lua:203
--   ConvertStringTo64Bit / ConvertStringToLuaID : Lua globals, menu_map.lua (hundreds of uses)
--   SetScript("onUpdate", fn) : ego_viewhelper/viewhelper.lua:28 ; GetCurRealTime() : menu_ship_configuration.lua:9924
--   IsValidComponent(luaid) : Lua global, menu_*.lua (44 uses)
--   debug.getupvalue : NOT used by vanilla; probed on purpose (V20).

local ffi = require("ffi")
local C = ffi.C

local TAG = "[X4MP-SPIKE]"
local SCREEN = "X4MP_Spike"

------------------------------------------------------------------------------
-- logging
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

-- K("a", 1, "b", "x") -> "a=1 b=x"   (varargs name,value pairs; nil values allowed)
local function K(...)
	local n = select("#", ...)
	local a = { ... }
	local out = {}
	for i = 1, n, 2 do
		out[#out + 1] = tostring(a[i]) .. "=" .. fmtv(a[i + 1])
	end
	return table.concat(out, " ")
end

local function log(step, level, kv)
	emit(TAG .. " " .. step .. " " .. level .. " " .. (kv or ""))
end

------------------------------------------------------------------------------
-- cdefs: each in its own pcall, signatures copied verbatim from vanilla ffi.cdef blocks
------------------------------------------------------------------------------
local cdefs = {
	"typedef uint64_t UniverseID;",
	"typedef struct { float x; float y; float z; float yaw; float pitch; float roll; } UIPosRot;",
	"typedef struct { const char* factionID; const char* factionName; const char* factionIcon; } FactionDetails;",
	"typedef struct { const char* ware; const char* macro; int amount; } UIWareInfo;",
	"void AddPlayerMoney(int64_t money);",
	"const char* CanTeleportPlayerTo(UniverseID controllableid, bool allowcontrolling, bool force);",
	"bool TeleportPlayerTo(UniverseID controllableid, bool allowcontrolling, bool instant, bool force);",
	"void SetObjectSectorPos(UniverseID objectid, UniverseID sectorid, UIPosRot offset);",
	"UIPosRot GetObjectPositionInSector(UniverseID objectid);",
	"UniverseID SpawnObjectAtPos2(const char* macroname, UniverseID sectorid, UIPosRot offset, const char* ownerid);",
	"bool FindMacro(const char* macroname);",
	"void ActivateObject(UniverseID objectid, bool active);",
	"void SelfDestructComponent(UniverseID componentid);",
	"void RemoveComponent(UniverseID componentid);",
	"void SetComponentOwner(UniverseID componentid, const char* factionid);",
	"FactionDetails GetOwnerDetails(UniverseID componentid);",
	"UniverseID GetPlayerOccupiedShipID(void);",
	"UniverseID GetPlayerControlledShipID(void);",
	"UniverseID GetContextByClass(UniverseID componentid, const char* classname, bool includeself);",
	"bool IsComponentClass(UniverseID componentid, const char* classname);",
	"bool IsComponentWrecked(const UniverseID componentid);",
	"const char* GetComponentName(UniverseID componentid);",
	"uint32_t GetNumAllFactions(bool includehidden);",
	"uint32_t GetAllFactions(const char** result, uint32_t resultlen, bool includehidden);",
	"uint32_t GetNumAllFactionShips(const char* factionid);",
	"uint32_t GetAllFactionShips(UniverseID* result, uint32_t resultlen, const char* factionid);",
	"bool IsFactionAllyToFaction(const char* factionid, const char* otherfactionid);",
	"bool IsFactionEnemyToFaction(const char* factionid, const char* otherfactionid);",
	"bool IsFactionHostileToFaction(const char* factionid, const char* otherfactionid);",
	"uint32_t GetNumCargo(UniverseID containerid, const char* tags);",
	"uint32_t GetCargo(UIWareInfo* result, uint32_t resultlen, UniverseID containerid, const char* tags);",
}
local cdefFail = 0
for _, def in ipairs(cdefs) do
	local ok, err = pcall(ffi.cdef, def)
	if not ok then
		-- "attempt to redefine" is expected when another addon declared the same thing first; anything else is logged.
		if not tostring(err):find("redefine", 1, true) then
			cdefFail = cdefFail + 1
			emit(TAG .. " LUA INFO cdef_issue=" .. fmtv(def:sub(1, 60)) .. " err=" .. fmtv(err))
		end
	end
end

-- Optional high resolution timer (Windows). Falls back to os.clock, then GetCurRealTime.
local timerKind = "GetCurRealTime"
local qpcFreq = nil
local qpcBuf = nil
do
	local ok = pcall(ffi.cdef, "int QueryPerformanceCounter(int64_t *lpPerformanceCount); int QueryPerformanceFrequency(int64_t *lpFrequency);")
	if ok then
		local ok2 = pcall(function()
			local f = ffi.new("int64_t[1]")
			if C.QueryPerformanceFrequency(f) ~= 0 and tonumber(f[0]) > 0 then
				qpcFreq = tonumber(f[0])
				qpcBuf = ffi.new("int64_t[1]")
				timerKind = "QueryPerformanceCounter"
			end
		end)
		if not ok2 then qpcFreq = nil end
	end
	if not qpcFreq and type(os) == "table" and type(os.clock) == "function" then
		timerKind = "os.clock"
	end
end

local function now()
	if qpcFreq then
		C.QueryPerformanceCounter(qpcBuf)
		return tonumber(qpcBuf[0]) / qpcFreq
	elseif timerKind == "os.clock" then
		return os.clock()
	end
	return GetCurRealTime()
end

------------------------------------------------------------------------------
-- helpers
------------------------------------------------------------------------------
local function toid(x)
	if x == nil then return 0 end
	local ok, r = pcall(function() return ConvertStringTo64Bit(tostring(x)) end)
	if ok and r then return r end
	return 0
end

local function luaid(id)
	local ok, r = pcall(function() return ConvertStringToLuaID(tostring(id)) end)
	if ok then return r end
	return nil
end

local function sid(id) return tostring(toid(id)) end

local function sendMD(control, value)
	local ok, err = pcall(AddUITriggeredEvent, SCREEN, control, value)
	if not ok then log("LUA", "FAIL", K("what", "AddUITriggeredEvent", "control", control, "err", err)) end
	return ok
end

local function parse(param)
	param = tostring(param or "")
	local cmd, rest = param:match("^([^;]*);?(.*)$")
	local kv = {}
	for k, v in rest:gmatch("([^;=]+)=([^;]*)") do kv[k] = v end
	return cmd, kv
end

local function percentile(list, p)
	local n = #list
	if n == 0 then return 0 end
	local c = {}
	for i = 1, n do c[i] = list[i] end
	table.sort(c)
	local idx = math.ceil(p * n)
	if idx < 1 then idx = 1 end
	if idx > n then idx = n end
	return c[idx]
end

local function avg(list)
	local n = #list
	if n == 0 then return 0 end
	local s = 0
	for i = 1, n do s = s + list[i] end
	return s / n
end

local function playerShip()
	local ok, r = pcall(function() return toid(C.GetPlayerOccupiedShipID()) end)
	if ok then return r end
	return 0
end

local function ownerOf(id)
	local ok, r = pcall(function() return ffi.string(C.GetOwnerDetails(id).factionID) end)
	if ok then return r end
	return "err:" .. tostring(r)
end

local function validComp(id)
	local ok, r = pcall(function() return IsValidComponent(luaid(id)) end)
	return ok and r and true or false
end

------------------------------------------------------------------------------
-- scheduler: coroutines resumed from the per-frame onUpdate script
------------------------------------------------------------------------------
local routines = {}
local frameCounter = 0
local lastFrameT = nil
local frameStats = nil -- when set: list of frame dt in ms

local function startRoutine(name, fn)
	local ok, co = pcall(coroutine.create, fn)
	if not ok then
		log(name, "FAIL", K("what", "coroutine.create", "err", co))
		return
	end
	routines[#routines + 1] = { name = name, co = co, wakeAt = 0, wakeFrame = 0 }
end

local function waitSeconds(s) coroutine.yield({ sec = s }) end
local function waitFrames(n) coroutine.yield({ frames = n }) end

local function onUpdate()
	local t = now()
	frameCounter = frameCounter + 1
	if lastFrameT then
		local dt = (t - lastFrameT) * 1000
		if frameStats then frameStats[#frameStats + 1] = dt end
	end
	lastFrameT = t
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
				if req.sec then r.wakeAt = now() + req.sec end
				if req.frames then r.wakeFrame = frameCounter + req.frames end
			end
		end
		if remove then table.remove(routines, i) else i = i + 1 end
	end
end

------------------------------------------------------------------------------
-- state shared between commands
------------------------------------------------------------------------------
local pendingObj = nil
local pong = { n = 0, t = 0, frame = 0 }
local ghostsAll = {} -- every ghost we spawned (for cleanup)

------------------------------------------------------------------------------
-- cmd: hello  (environment probe)
------------------------------------------------------------------------------
local function cmdHello(kv)
	local names = { "GetPlayerMoney", "TransferPlayerMoneyTo", "GetComponentData", "ConvertStringTo64Bit",
		"ConvertStringToLuaID", "SetComponentName", "getElapsedTime", "GetCurRealTime", "GetCurTime", "SetScript",
		"RegisterEvent", "AddUITriggeredEvent", "IsValidComponent", "GetUISafeModeOption", "DebugError", "Menus",
		"GetFactionData", "GetVersionString" }
	local parts = {}
	for _, n in ipairs(names) do
		local ok, v = pcall(function() return _G[n] end)
		parts[#parts + 1] = n .. "=" .. (ok and type(v) or "err")
	end
	log("LUA", "INFO", K("what", "globals") .. " " .. table.concat(parts, " "))
	log("LUA", "INFO", K("timer", timerKind, "cdef_issues", cdefFail, "ffi_os", ffi.os, "ffi_arch", ffi.arch,
		"jit", (type(jit) == "table" and jit.version) or "none", "coroutine", type(coroutine), "os_clock",
		(type(os) == "table" and type(os.clock)) or "none", "debug", type(debug),
		"debug_getupvalue", (type(debug) == "table" and type(debug.getupvalue)) or "none"))
	local ok, safe = pcall(GetUISafeModeOption)
	log("LUA", "INFO", K("protected_ui_mode", ok and safe or "err", "md_trigger", kv.src))
	local ok2, ver = pcall(function() return GetVersionString() end)
	log("LUA", "INFO", K("game_version", ok2 and ver or "n/a"))
	sendMD("hello_ack", "ok")
end

------------------------------------------------------------------------------
-- S7: sector probe + full ship enumeration cost
------------------------------------------------------------------------------
local function cmdS7Probe(kv)
	local id = toid(pendingObj)
	local lid = luaid(id)
	local res = {}
	for _, key in ipairs({ "macro", "name", "owner", "isplayerowned" }) do
		local ok, v = pcall(function() return GetComponentData(lid, key) end)
		res[#res + 1] = key
		res[#res + 1] = ok and v or ("ERR:" .. tostring(v))
	end
	log("S7", "INFO", K("lua_probe_sector", sid(id), "md_macro", kv.md_macro, unpack(res)))
	local ok, cls = pcall(function() return ffi.string(C.GetComponentName(id)) end)
	log("S7", "INFO", K("lua_probe_sector_cname", ok and cls or "err"))
end

local function enumerate(includeHidden)
	local out = { factions = 0, calls_ms = 0, total = 0, uniq = 0, dedupe_ms = 0, top = {} }
	local t0 = now()
	local nf = tonumber(C.GetNumAllFactions(includeHidden))
	local fbuf = ffi.new("const char*[?]", nf)
	nf = tonumber(C.GetAllFactions(fbuf, nf, includeHidden))
	out.factions = nf
	local arrays = {}
	for i = 0, nf - 1 do
		local f = ffi.string(fbuf[i])
		local num = tonumber(C.GetNumAllFactionShips(f))
		if num > 0 then
			local arr = ffi.new("UniverseID[?]", num)
			num = tonumber(C.GetAllFactionShips(arr, num, f))
			arrays[#arrays + 1] = { f = f, arr = arr, n = num }
			out.total = out.total + num
			out.top[#out.top + 1] = { f = f, n = num }
		end
	end
	out.calls_ms = (now() - t0) * 1000
	local t1 = now()
	local seen = {}
	local uniq = 0
	for _, a in ipairs(arrays) do
		for i = 0, a.n - 1 do
			local k = tostring(a.arr[i])
			if not seen[k] then seen[k] = true; uniq = uniq + 1 end
		end
	end
	out.uniq = uniq
	out.dedupe_ms = (now() - t1) * 1000
	table.sort(out.top, function(a, b) return a.n > b.n end)
	return out
end

local function cmdS7Enum(kv)
	for _, hidden in ipairs({ false, true }) do
		local ok, r = pcall(enumerate, hidden)
		if ok then
			local top = {}
			for i = 1, math.min(5, #r.top) do top[#top + 1] = r.top[i].f .. ":" .. r.top[i].n end
			log("S7", "MEASURE", K("enum", "GetAllFactionShips", "include_hidden", hidden, "factions", r.factions,
				"ships_total", r.total, "ships_unique", r.uniq, "api_ms", r.calls_ms, "dedupe_ms", r.dedupe_ms,
				"top5", table.concat(top, ",")))
		else
			log("S7", "FAIL", K("what", "GetAllFactionShips_enum", "include_hidden", hidden, "err", r))
		end
	end
	sendMD("s7_enum_done", "ok")
end

------------------------------------------------------------------------------
-- S4: money
------------------------------------------------------------------------------
local function cmdS4(kv)
	startRoutine("S4", function()
		local md_raw = kv.md_money_raw -- MD player.money as text (type "money", cents)
		local md_ct = tonumber((tostring(md_raw):gsub("[^%d%-%.]", "")))
		local ok0, m0 = pcall(GetPlayerMoney)
		if not ok0 or type(m0) ~= "number" then
			log("S4", "FAIL", K("what", "GetPlayerMoney", "result", m0))
			sendMD("s4_lua_done", "fail")
			return
		end
		log("S4", "INFO", K("lua_money_start", m0, "lua_type", type(m0), "md_money_raw", md_raw, "md_money_ct", md_ct,
			"ratio_md_ct_over_lua", (md_ct and m0 ~= 0) and (md_ct / m0) or "n/a"))

		-- unit probe: Add 100 units, see what GetPlayerMoney() does
		local okA = pcall(C.AddPlayerMoney, 100)
		waitSeconds(0.6)
		local m1 = GetPlayerMoney()
		local delta1 = m1 - m0
		pcall(C.AddPlayerMoney, -100)
		waitSeconds(0.6)
		local m2 = GetPlayerMoney()
		local unit
		if delta1 == 1 then unit = "AddPlayerMoney=cents(1/100cr),GetPlayerMoney=credits"
		elseif delta1 == 100 then unit = "same_unit_as_GetPlayerMoney"
		elseif delta1 == 0 then unit = "no_change_visible_yet"
		else unit = "other" end
		log("S4", "INFO", K("probe_add_units", 100, "call_ok", okA, "delta_lua", delta1, "after_undo_delta", m2 - m0, "unit", unit))
		local perCredit = 100
		if delta1 == 100 then perCredit = 1 end

		-- +1000 credits, then -1000
		pcall(C.AddPlayerMoney, 1000 * perCredit)
		waitSeconds(0.6)
		local mp = GetPlayerMoney()
		log("S4", (mp - m0 == 1000) and "PASS" or "INFO", K("what", "add_plus_1000cr", "delta_lua", mp - m0))
		pcall(C.AddPlayerMoney, -1000 * perCredit)
		waitSeconds(0.6)
		local mm = GetPlayerMoney()
		log("S4", (mm - m0 == 0) and "PASS" or "INFO", K("what", "add_minus_1000cr_back", "delta_lua_vs_start", mm - m0))

		-- negative deltas below zero: drive the balance to -5000 credits
		local over = math.floor(mm) + 5000
		pcall(C.AddPlayerMoney, -over * perCredit)
		waitSeconds(0.8)
		local mo = GetPlayerMoney()
		log("S4", "INFO", K("what", "overdraw", "requested_delta_cr", -over, "balance_after", mo, "went_negative", mo < 0,
			"clamped_at_zero", mo == 0))
		sendMD("s4_overdrawn", tostring(mo)) -- MD logs player.money (cents) at this moment
		waitSeconds(1.5)
		-- restore
		local now0 = GetPlayerMoney()
		pcall(C.AddPlayerMoney, (m0 - now0) * perCredit)
		waitSeconds(0.8)
		local mr = GetPlayerMoney()
		log("S4", (mr == m0) and "PASS" or "FAIL", K("what", "restore_original", "start", m0, "now", mr))
		sendMD("s4_lua_done", "ok")
	end)
end

------------------------------------------------------------------------------
-- S1: Lua view of the team factions
------------------------------------------------------------------------------
local function cmdS1Lua(kv)
	local ok, err = pcall(function()
		local nf = tonumber(C.GetNumAllFactions(true))
		local fbuf = ffi.new("const char*[?]", nf)
		nf = tonumber(C.GetAllFactions(fbuf, nf, true))
		local set = {}
		for i = 0, nf - 1 do set[ffi.string(fbuf[i])] = true end
		local nfv = tonumber(C.GetNumAllFactions(false))
		log("S1", "INFO", K("lua_factions_hidden_incl", nf, "lua_factions_visible", nfv))
		for k = 1, 8 do
			local id = "x4mp_team_" .. k
			local present = set[id] == true
			local ally, enemy, hostile = "n/a", "n/a", "n/a"
			if present then
				ally = C.IsFactionAllyToFaction(id, "player")
				enemy = C.IsFactionEnemyToFaction(id, "player")
				hostile = C.IsFactionHostileToFaction(id, "player")
			end
			log("S1", present and "PASS" or "FAIL", K("lua_faction", id, "listed_in_GetAllFactions", present,
				"ally_to_player", ally, "enemy_to_player", enemy, "hostile_to_player", hostile))
		end
	end)
	if not ok then log("S1", "FAIL", K("what", "lua_faction_scan", "err", err)) end
end

------------------------------------------------------------------------------
-- S8: cargo read-back, owner change, MD round trip latency
------------------------------------------------------------------------------
local function cmdS8Cargo(kv)
	local id = toid(pendingObj)
	local ok, err = pcall(function()
		local n = tonumber(C.GetNumCargo(id, ""))
		local buf = ffi.new("UIWareInfo[?]", math.max(n, 1))
		n = tonumber(C.GetCargo(buf, n, id, ""))
		local parts = {}
		for i = 0, n - 1 do parts[#parts + 1] = ffi.string(buf[i].ware) .. ":" .. tostring(buf[i].amount) end
		log("S8", "INFO", K("lua_cargo_of", sid(id), "stage", kv.stage, "entries", n, "list", (#parts > 0) and table.concat(parts, ",") or "empty"))
	end)
	if not ok then log("S8", "FAIL", K("what", "GetCargo", "err", err)) end
end

local function cmdS8Owner(kv)
	local id = toid(pendingObj)
	local before = ownerOf(id)
	local ok, err = pcall(C.SetComponentOwner, id, kv.faction)
	startRoutine("S8", function()
		waitFrames(3)
		local after = ownerOf(id)
		log("S8", (ok and after == kv.faction) and "PASS" or "FAIL", K("what", "SetComponentOwner_lua", "ship", sid(id),
			"before", before, "requested", kv.faction, "after", after, "call_ok", ok, "err", ok and "" or err))
		sendMD("s8_owner_done", after)
	end)
end

local function cmdS8Ping(kv)
	startRoutine("S8", function()
		local rtts, frames = {}, {}
		local total = tonumber(kv.n) or 10
		for i = 1, total do
			pong.n = 0
			local t0, f0 = now(), frameCounter
			sendMD("ping", i)
			local deadline = t0 + 3
			while pong.n ~= i and now() < deadline do waitFrames(1) end
			if pong.n == i then
				rtts[#rtts + 1] = (pong.t - t0) * 1000
				frames[#frames + 1] = pong.frame - f0
			else
				log("S8", "FAIL", K("what", "ping_timeout", "n", i))
			end
			waitFrames(5)
		end
		if #rtts > 0 then
			local mn, mx = rtts[1], rtts[1]
			for _, v in ipairs(rtts) do if v < mn then mn = v end if v > mx then mx = v end end
			log("S8", "MEASURE", K("lua_md_lua_roundtrip", "AddUITriggeredEvent+raise_lua_event", "pings", #rtts,
				"rt_ms_avg", avg(rtts), "rt_ms_min", mn, "rt_ms_max", mx, "frames_avg", avg(frames)))
		end
		sendMD("s8_ping_done", "ok")
	end)
end

------------------------------------------------------------------------------
-- S2: avatar takeover
------------------------------------------------------------------------------
local function cmdS2(kv)
	startRoutine("S2", function()
		local newId = toid(pendingObj)
		local old = playerShip()
		local oldCtl = toid(C.GetPlayerControlledShipID())
		log("S2", "INFO", K("new_ship", sid(newId), "old_occupied", sid(old), "old_controlled", sid(oldCtl),
			"new_valid", validComp(newId), "new_owner", ownerOf(newId)))
		local c1, c2, c3 = "err", "err", "err"
		pcall(function() c1 = ffi.string(C.CanTeleportPlayerTo(newId, false, false)) end)
		pcall(function() c2 = ffi.string(C.CanTeleportPlayerTo(newId, true, false)) end)
		pcall(function() c3 = ffi.string(C.CanTeleportPlayerTo(newId, true, true)) end)
		log("S2", "INFO", K("can_teleport_no_control", c1, "can_teleport_control", c2, "can_teleport_control_force", c3))
		if c3 ~= "granted" and c2 ~= "granted" then
			log("S2", "FAIL", K("what", "CanTeleportPlayerTo_not_granted"))
			sendMD("s2_done", "fail")
			return
		end
		local okT, resT = pcall(C.TeleportPlayerTo, newId, true, true, true)
		log("S2", "INFO", K("what", "TeleportPlayerTo(ship,true,true,true)", "call_ok", okT, "returned", resT))
		waitSeconds(2.5)
		local occ, ctl = playerShip(), toid(C.GetPlayerControlledShipID())
		local ok = (tostring(occ) == tostring(newId))
		log("S2", ok and "PASS" or "FAIL", K("what", "teleport_result", "occupied_now", sid(occ), "controlled_now", sid(ctl),
			"is_new_ship", ok, "controlling_new", tostring(ctl) == tostring(newId)))
		-- removal feasibility of the old ship. NEVER removed here.
		log("S2", "INFO", K("what", "old_ship_removable_check", "old_valid", validComp(old),
			"old_still_occupied", tostring(occ) == tostring(old), "old_still_controlled", tostring(ctl) == tostring(old),
			"removal_would_be_possible", validComp(old) and tostring(occ) ~= tostring(old) and tostring(ctl) ~= tostring(old),
			"note", "not_removed_by_spike"))
		sendMD("s2_teleported", ok and "ok" or "fail")
		if kv.returnafter and tonumber(kv.returnafter) and tonumber(kv.returnafter) > 0 and ok then
			waitSeconds(tonumber(kv.returnafter))
			local okB, resB = pcall(C.TeleportPlayerTo, old, true, true, true)
			waitSeconds(2.5)
			local occ2 = playerShip()
			log("S2", (tostring(occ2) == tostring(old)) and "PASS" or "INFO", K("what", "teleport_back_to_old_ship",
				"call_ok", okB, "returned", resB, "occupied_now", sid(occ2), "is_old_ship", tostring(occ2) == tostring(old)))
		end
		sendMD("s2_done", "ok")
	end)
end

------------------------------------------------------------------------------
-- S3: ghost cost
------------------------------------------------------------------------------
local GHOST_MACRO = "ship_arg_s_scout_01_a_macro" -- exists in index/macros.xml:1650

local function newPosRot()
	return ffi.new("UIPosRot")
end

local function runGhostTier(N, perFrame, seconds, dmin, dmax, owner, macroOk, results)
	local ship = playerShip()
	if ship == 0 then
		log("S3", "FAIL", K("tier", N, "what", "player_not_in_ship"))
		return false
	end
	local sector = toid(C.GetContextByClass(ship, "sector", true))
	if sector == 0 then
		log("S3", "FAIL", K("tier", N, "what", "no_sector"))
		return false
	end
	local center = C.GetObjectPositionInSector(ship)
	local cx, cy, cz = tonumber(center.x), tonumber(center.y), tonumber(center.z)

	-- ---- spawn ----
	local ghosts = {}
	local spawnMs, spawnFramesN, failed = {}, 0, 0
	local usedOwner = owner
	local pos = newPosRot()
	local tSpawn0 = now()
	local made = 0
	while made < N do
		local f0 = now()
		local batch = math.min(perFrame, N - made)
		for _ = 1, batch do
			local d = dmin + math.random() * (dmax - dmin)
			local th = math.random() * 2 * math.pi
			local ph = (math.random() - 0.5) * math.pi * 0.5
			local ox, oy, oz = d * math.cos(ph) * math.cos(th), d * math.sin(ph), d * math.cos(ph) * math.sin(th)
			pos.x, pos.y, pos.z, pos.yaw, pos.pitch, pos.roll = cx + ox, cy + oy, cz + oz, 0, 0, 0
			local id = C.SpawnObjectAtPos2(GHOST_MACRO, sector, pos, usedOwner)
			if id == 0 and usedOwner ~= "ownerless" then
				-- fallback: neutral faction (task: "or a neutral faction if spawning for mod factions fails")
				if made == 0 and #ghosts == 0 then
					log("S3", "FAIL", K("tier", N, "what", "SpawnObjectAtPos2_returned_0_for_owner", "owner", usedOwner, "fallback", "ownerless"))
				end
				usedOwner = "ownerless"
				id = C.SpawnObjectAtPos2(GHOST_MACRO, sector, pos, usedOwner)
			end
			if id ~= 0 then
				ghosts[#ghosts + 1] = { id = id, cx = cx + ox, cy = cy + oy, cz = cz + oz, r = 30 + math.random() * 120,
					ph = math.random() * 2 * math.pi, w = (2 * math.pi) / (8 + math.random() * 8) }
				ghostsAll[#ghostsAll + 1] = id
			else
				failed = failed + 1
			end
			made = made + 1
		end
		spawnMs[#spawnMs + 1] = (now() - f0) * 1000
		spawnFramesN = spawnFramesN + 1
		waitFrames(1)
	end
	log("S3", (#ghosts > 0) and "INFO" or "FAIL", K("tier", N, "what", "spawned", "ok", #ghosts, "failed", failed, "owner", usedOwner,
		"frames", spawnFramesN, "spawn_ms_per_frame_avg", avg(spawnMs), "spawn_ms_per_frame_max", percentile(spawnMs, 1.0),
		"spawn_total_s", now() - tSpawn0))
	if #ghosts == 0 then return false end
	sendMD("s3_spawned", #ghosts)

	-- ---- deactivate AI/physics (ActivateObject(false)) ----
	local actOk, actFail = 0, 0
	for _, g in ipairs(ghosts) do
		local ok = pcall(C.ActivateObject, g.id, false)
		if ok then actOk = actOk + 1 else actFail = actFail + 1 end
	end
	log("S3", "INFO", K("tier", N, "what", "ActivateObject_false", "ok", actOk, "failed", actFail))
	waitSeconds(1.0)

	-- ---- idle baseline frame time ----
	frameStats = {}
	waitSeconds(2.0)
	local base = frameStats
	frameStats = nil
	local baseAvg = avg(base)

	-- ---- move phase ----
	local moveMs = {}
	frameStats = {}
	local tStart = now()
	local tEnd = tStart + seconds
	local driftSample = {}
	local velLogged = false
	local frames = 0
	while now() < tEnd do
		local t = now() - tStart
		local m0 = now()
		for i = 1, #ghosts do
			local g = ghosts[i]
			local a = g.ph + g.w * t
			pos.x = g.cx + g.r * math.cos(a)
			pos.y = g.cy
			pos.z = g.cz + g.r * math.sin(a)
			pos.yaw = math.deg(a) + 90
			pos.pitch = 0
			pos.roll = 0
			C.SetObjectSectorPos(g.id, sector, pos)
		end
		moveMs[#moveMs + 1] = (now() - m0) * 1000
		frames = frames + 1
		-- velocity probes once, mid-run (V17)
		if (not velLogged) and t > seconds * 0.5 then
			velLogged = true
			for pi = 1, math.min(3, #ghosts) do
				local lid = luaid(ghosts[pi].id)
				local res = {}
				for _, key in ipairs({ "velocity", "speed" }) do
					local ok, v = pcall(function() return GetComponentData(lid, key) end)
					local shown
					if ok then
						if type(v) == "table" then
							local tt = {}
							for kk, vv in pairs(v) do tt[#tt + 1] = tostring(kk) .. ":" .. tostring(vv) end
							shown = "table{" .. table.concat(tt, ",") .. "}"
						else
							shown = tostring(v)
						end
					else
						shown = "ERR:" .. tostring(v)
					end
					res[#res + 1] = key
					res[#res + 1] = shown
				end
				local g = ghosts[pi]
				log("S3", "INFO", K("tier", N, "what", "velocity_probe", "ghost", sid(g.id),
					"expected_speed_ms", g.r * g.w, unpack(res)))
			end
		end
		waitFrames(1)
	end
	local frameDts = frameStats
	frameStats = nil

	-- drift check: compare real position of a few ghosts with the last commanded one
	local ok, errDrift = pcall(function()
		local sum, cnt = 0, 0
		for i = 1, math.min(10, #ghosts) do
			local g = ghosts[i]
			local p = C.GetObjectPositionInSector(g.id)
			local a = g.ph + g.w * (tEnd - tStart)
			local ex, ez = g.cx + g.r * math.cos(a), g.cz + g.r * math.sin(a)
			local dx, dz = tonumber(p.x) - ex, tonumber(p.z) - ez
			sum = sum + math.sqrt(dx * dx + dz * dz)
			cnt = cnt + 1
		end
		driftSample = { avg = (cnt > 0) and sum / cnt or -1 }
	end)
	local mavg = avg(moveMs)
	log("S3", "MEASURE", K("ghosts", N, "move_ms_avg", mavg, "move_ms_p95", percentile(moveMs, 0.95), "move_ms_max", percentile(moveMs, 1.0),
		"frame_ms_avg", avg(frameDts), "frame_ms_p95", percentile(frameDts, 0.95), "frame_ms_max", percentile(frameDts, 1.0),
		"idle_frame_ms_avg", baseAvg, "frames", frames, "move_cost_per_ghost_us", (#ghosts > 0) and (mavg * 1000 / #ghosts) or 0,
		"seconds", seconds, "timer", timerKind))
	log("S3", ok and "INFO" or "FAIL", K("tier", N, "what", "drift_check_vs_commanded_m", "avg_drift_m", ok and driftSample.avg or errDrift))
	results[#results + 1] = { n = N, move_ms = mavg }

	-- ---- destroy half via MD destroy_object, half via Lua SelfDestructComponent ----
	local half = math.floor(#ghosts / 2)
	local mdList, luaList = {}, {}
	for i = 1, #ghosts do
		if i <= half then mdList[#mdList + 1] = ghosts[i].id else luaList[#luaList + 1] = ghosts[i].id end
	end
	local luaIds = {}
	for _, id in ipairs(mdList) do luaIds[#luaIds + 1] = luaid(id) end
	sendMD("s3_destroy_list", luaIds) -- table -> MD list (vanilla: menu_ship_configuration.lua:1399)
	local sd = 0
	for _, id in ipairs(luaList) do
		if tostring(id) ~= tostring(playerShip()) then
			if pcall(C.SelfDestructComponent, id) then sd = sd + 1 end
		end
	end
	log("S3", "INFO", K("tier", N, "what", "destroy_issued", "md_destroy_object", #mdList, "lua_SelfDestructComponent", sd))
	local tD = now()
	waitSeconds(6.0)
	local function survivors(list)
		local valid, wrecked = 0, 0
		for _, id in ipairs(list) do
			if validComp(id) then
				valid = valid + 1
				local okW, w = pcall(C.IsComponentWrecked, id)
				if okW and w then wrecked = wrecked + 1 end
			end
		end
		return valid, wrecked
	end
	local mv, mw = survivors(mdList)
	local lv, lw = survivors(luaList)
	log("S3", "INFO", K("tier", N, "what", "destroy_result_after_6s", "md_half_still_valid", mv, "md_half_wrecked", mw,
		"lua_half_still_valid", lv, "lua_half_wrecked", lw))
	-- cleanup survivors (never the player's ship)
	local removed = 0
	local ps = playerShip()
	for _, g in ipairs(ghosts) do
		if validComp(g.id) and tostring(g.id) ~= tostring(ps) then
			if pcall(C.RemoveComponent, g.id) then removed = removed + 1 end
		end
	end
	log("S3", "INFO", K("tier", N, "what", "cleanup_RemoveComponent_survivors", "removed", removed))
	waitSeconds(1.0)
	return true
end

local function cmdS3(kv)
	startRoutine("S3", function()
		local tiers = {}
		for n in tostring(kv.tiers or "100,250,500"):gmatch("%d+") do tiers[#tiers + 1] = tonumber(n) end
		local perFrame = tonumber(kv.perframe) or 40
		local seconds = tonumber(kv.secs) or 20
		local dmin = tonumber(kv.dmin) or 3000
		local dmax = tonumber(kv.dmax) or 8000
		local owner = kv.owner or "x4mp_team_2"
		local okM, macroOk = pcall(C.FindMacro, GHOST_MACRO)
		log("S3", "INFO", K("what", "start", "tiers", kv.tiers, "perframe", perFrame, "secs", seconds, "dmin", dmin, "dmax", dmax,
			"owner", owner, "macro", GHOST_MACRO, "macro_found", okM and macroOk or "err", "timer", timerKind))
		math.randomseed(12345)
		local results = {}
		for _, N in ipairs(tiers) do
			local ok, r = pcall(runGhostTier, N, perFrame, seconds, dmin, dmax, owner, macroOk, results)
			if not ok then
				log("S3", "FAIL", K("tier", N, "what", "tier_error", "err", r))
				-- emergency cleanup of anything we spawned and still hold
				frameStats = nil
				local ps = playerShip()
				for _, id in ipairs(ghostsAll) do
					if validComp(id) and tostring(id) ~= tostring(ps) then pcall(C.RemoveComponent, id) end
				end
				break
			end
			waitSeconds(2.0)
		end
		-- final sweep
		local ps = playerShip()
		local left = 0
		for _, id in ipairs(ghostsAll) do
			if validComp(id) and tostring(id) ~= tostring(ps) then
				left = left + 1
				pcall(C.RemoveComponent, id)
			end
		end
		log("S3", "INFO", K("what", "final_sweep", "ghosts_ever_spawned", #ghostsAll, "removed_in_final_sweep", left))
		ghostsAll = {}
		sendMD("s3_done", "ok")
	end)
end

------------------------------------------------------------------------------
-- misc: V20 debug.getupvalue probe
------------------------------------------------------------------------------
local function cmdDebugProbe(kv)
	local hasDebug = type(debug) == "table"
	local hasGetUp = hasDebug and type(debug.getupvalue) == "function"
	log("MISC", hasGetUp and "PASS" or "FAIL", K("what", "debug.getupvalue_available", "debug_table", hasDebug,
		"getupvalue", hasGetUp and "function" or type(debug and debug.getupvalue), "getinfo", hasDebug and type(debug.getinfo) or "n/a",
		"setupvalue", hasDebug and type(debug.setupvalue) or "n/a"))
	if not hasGetUp then return end
	local ok, err = pcall(function()
		local mt = _G["Menus"]
		log("MISC", "INFO", K("what", "Menus_global", "type", type(mt), "count", type(mt) == "table" and #mt or "n/a"))
		if type(mt) ~= "table" then return end
		local target
		for _, m in ipairs(mt) do
			if type(m) == "table" and m.name == "OptionsMenu" then target = m break end
		end
		if not target then
			log("MISC", "INFO", K("what", "OptionsMenu_not_found_in_Menus"))
			return
		end
		local found = {}
		local fnames = {}
		for k, v in pairs(target) do
			if type(v) == "function" then fnames[#fnames + 1] = k end
		end
		table.sort(fnames)
		local sawConfig = false
		local checked = 0
		for _, fname in ipairs(fnames) do
			local fn = target[fname]
			for i = 1, 80 do
				local okU, name, val = pcall(debug.getupvalue, fn, i)
				if not okU or name == nil then break end
				if name == "config" and type(val) == "table" then
					sawConfig = true
					found[#found + 1] = fname .. "#" .. i
					if type(val.optionDefinitions) == "table" then found[#found + 1] = "optionDefinitions(main=" .. type(val.optionDefinitions["main"]) .. ")" end
					break
				end
			end
			checked = checked + 1
			if sawConfig then break end
		end
		log("MISC", sawConfig and "PASS" or "INFO", K("what", "OptionsMenu_config_upvalue", "functions_scanned", checked,
			"found_config", sawConfig, "where", table.concat(found, ",")))
	end)
	if not ok then log("MISC", "FAIL", K("what", "getupvalue_probe_error", "err", err)) end
end

------------------------------------------------------------------------------
-- event entry points
------------------------------------------------------------------------------
local handlers = {
	hello = cmdHello,
	s7_probe = cmdS7Probe,
	s7_enum = cmdS7Enum,
	s4_run = cmdS4,
	s1_lua = cmdS1Lua,
	s8_cargo = cmdS8Cargo,
	s8_owner = cmdS8Owner,
	s8_ping = cmdS8Ping,
	s2_teleport = cmdS2,
	s3_run = cmdS3,
	debug_probe = cmdDebugProbe,
}

local function onObj(_, param)
	pendingObj = param
end

local function onCmd(_, param)
	local cmd, kv = parse(param)
	local fn = handlers[cmd]
	if not fn then
		log("LUA", "FAIL", K("what", "unknown_cmd", "cmd", cmd))
		return
	end
	local ok, err = pcall(fn, kv)
	if not ok then log("LUA", "FAIL", K("what", "cmd_error", "cmd", cmd, "err", err)) end
end

local function onPong(_, param)
	pong.n = tonumber(param) or -1
	pong.t = now()
	pong.frame = frameCounter
end

local function init()
	local ok, err = pcall(function()
		RegisterEvent("X4MP_Spike_obj", onObj)
		RegisterEvent("X4MP_Spike_cmd", onCmd)
		RegisterEvent("X4MP_Spike_pong", onPong)
		SetScript("onUpdate", onUpdate)
	end)
	log("LUA", ok and "INFO" or "FAIL", K("what", "x4mp_spike_lua_loaded", "timer", timerKind, "cdef_issues", cdefFail, "err", ok and "" or err))
end

init()
