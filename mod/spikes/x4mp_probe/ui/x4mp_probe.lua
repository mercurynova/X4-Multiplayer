-- x4mp_probe Lua shim (M2-005, throwaway). The X4Native 9.0.0 API gives native extensions no lua_State*, so the DLL
-- talks to Lua through events:
--   native -> Lua : x4n::raise_lua("x4mp_probe.cmd", "<verb>;<arg>")   -> RegisterEvent below
--                   x4n::raise_lua("x4mp_probe.notify", "<text>")       -> on-screen notification (via MD, md/x4mp_probe.xml)
--   Lua -> native : __X4NATIVE_API.raise_event("x4mp_probe.reply", "<verb>;k=v;...")  (native: x4n::on("x4mp_probe.reply"))
--                   __X4NATIVE_API.raise_event("x4mp_probe.save_begin", "<name>")     (native QPC mark just before a SaveGame)
--                   __X4NATIVE_API.raise_event("x4mp_probe.mark", "<label>")          (native QPC mark, any label)
-- Verbs: loadgame;<name>  pause  unpause  money_read;<tag>  savegame;<name>  reloadui
-- Every line is also written with DebugError as "[X4MP-PROBE-LUA] ...".
-- Sources: RegisterEvent/DebugError/ExecuteDebugCommand: ui/addons/ego_chatwindow/chatwindow.lua; Pause/Unpause: ego_gameoptions;
-- SaveGame(filename, displayname): gameoptions.lua:9296; LoadGame: gameoptions.lua:2963; GetPlayerMoney: menu_diplomacy.lua.

local function log(msg)
	pcall(DebugError, "[X4MP-PROBE-LUA] " .. tostring(msg))
end

local function api()
	return _G.__X4NATIVE_API
end

local function reply(text)
	local a = api()
	if a and a.raise_event then
		local ok, err = pcall(a.raise_event, "x4mp_probe.reply", text)
		if not ok then log("reply failed: " .. tostring(err)) end
	else
		log("no __X4NATIVE_API yet; reply dropped: " .. text)
	end
end

-- Public helpers for other test Lua (e.g. the spike's saves4 block): X4MP_Probe.markSaveBegin(name) before SaveGame.
X4MP_Probe = X4MP_Probe or {}
function X4MP_Probe.markSaveBegin(name)
	local a = api()
	if a and a.raise_event then pcall(a.raise_event, "x4mp_probe.save_begin", tostring(name)) end
end
function X4MP_Probe.mark(label)
	local a = api()
	if a and a.raise_event then pcall(a.raise_event, "x4mp_probe.mark", tostring(label)) end
end

local verbs = {}

function verbs.loadgame(arg)
	-- Same call the vanilla loadSave handler makes (after its 0.1 s delay).
	local ok, err = pcall(function()
		if Helper and Helper.addDelayedOneTimeCallbackOnUpdate and getElapsedTime then
			Helper.addDelayedOneTimeCallbackOnUpdate(function() LoadGame(arg) end, true, getElapsedTime() + 0.1)
		else
			LoadGame(arg)
		end
	end)
	reply("loadgame;ok=" .. tostring(ok) .. ";err=" .. tostring(err))
end

function verbs.pause()
	local ok, err = pcall(Pause)
	reply("pause;ok=" .. tostring(ok) .. ";err=" .. tostring(err))
end

function verbs.unpause()
	local ok, err = pcall(Unpause)
	reply("unpause;ok=" .. tostring(ok) .. ";err=" .. tostring(err))
end

function verbs.money_read(arg)
	local ok, v = pcall(GetPlayerMoney)
	if ok and type(v) == "number" then
		reply(string.format("money;tag=%s;value=%.0f", tostring(arg), v))
	else
		reply("money;tag=" .. tostring(arg) .. ";error=" .. tostring(v))
	end
end

function verbs.savegame(arg)
	local t0 = (GetCurRealTime and GetCurRealTime()) or 0
	local ok, err = pcall(SaveGame, arg, arg)
	local t1 = (GetCurRealTime and GetCurRealTime()) or 0
	reply(string.format("savegame;name=%s;ok=%s;lua_call_s=%.3f;err=%s", tostring(arg), tostring(ok), t1 - t0, tostring(err)))
end

function verbs.reloadui()
	local ok, err = pcall(ExecuteDebugCommand, "reloadui", nil)
	if not ok and ScheduleReloadUI then
		ok, err = pcall(ScheduleReloadUI)
		reply("reloadui;via=ScheduleReloadUI;ok=" .. tostring(ok) .. ";err=" .. tostring(err))
		return
	end
	reply("reloadui;via=ExecuteDebugCommand;ok=" .. tostring(ok) .. ";err=" .. tostring(err))
end

local function onCmd(_, param)
	param = tostring(param or "")
	local verb, arg = string.match(param, "^([^;]*);?(.*)$")
	local fn = verbs[verb]
	if fn then
		log("cmd " .. verb .. " arg=" .. arg)
		local ok, err = pcall(fn, arg)
		if not ok then log("verb " .. verb .. " raised: " .. tostring(err)) end
	else
		log("unknown verb '" .. tostring(verb) .. "'")
	end
end

local function onNotify(_, param)
	param = tostring(param or "")
	log("notify " .. param)
	pcall(AddUITriggeredEvent, "X4MP_Probe", "notify", param)
end

RegisterEvent("x4mp_probe.cmd", onCmd)
RegisterEvent("x4mp_probe.notify", onNotify)
log("shim loaded (api present: " .. tostring(api() ~= nil) .. ")")
