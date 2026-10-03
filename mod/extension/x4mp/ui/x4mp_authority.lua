-- x4mp_authority.lua : Lua half of the in-game authority (M2-09). Loaded after x4mp_saves.lua (ui.xml order).
--
-- Native drives everything; this file only does what needs the Lua/MD side of the game:
--   native -> Lua  x4mp.auth_collect {"v":1}                   ask MD (md/x4mp_galaxy.xml) for the sectors and the player's ship
--   MD -> Lua      x4mp.md_galaxy <string>                      raw messages "G;..." "E;n" "P;..." (format: native
--                                                               features/authority/authority_data.h); forwarded unchanged to native as
--   Lua -> native  x4mp.auth_md {"v":1,"data":"<string>"}
--   native -> Lua  x4mp.auth_save {"v":1,"name":"x4mp_ckpt_<16 hex>","request_id":N,"display":"..."}
--                  calls SaveGame(name, display) inside X4MPSaves.allowSaves (the authority is never save-blocked, but a client-time
--                  block flag must not be able to swallow the checkpoint) and answers
--   Lua -> native  x4mp.auth_saved {"v":1,"ok":true,"name":...,"request_id":N,"game_time":123.4}   or {"ok":false,"error":"..."}
--                  game_time is Lua GetCurrentGameTime() sampled right before SaveGame (== MD player.age, session 2 C5).
-- The save file is NOT complete when SaveGame returns (session 2 C4): native waits for it.
--
-- luacheck: globals X4MPBridge X4MPSaves X4MPAuthority DebugError RegisterEvent SaveGame AddUITriggeredEvent

local B = rawget(_G, "X4MPBridge")
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] authority: x4mp_bridge.lua must load first")
	return
end

local A = { MD_SCREEN = "X4MP_Authority" }
X4MPAuthority = A

local function log(msg) pcall(DebugError, "[X4MP] authority: " .. tostring(msg)) end

local function send(verb, payload)
	local api = B.getApi()
	if not api then return false, "no_api" end
	payload.v = 1
	local text, err = B.json.encode(payload)
	if not text then return false, err end
	local ok, perr = pcall(api.raise_event, "x4mp." .. verb, text)
	if not ok then return false, tostring(perr) end
	return true
end
A.send = send

--- Lua game clock in seconds (nil when unavailable). Stops while the game is paused.
function A.gameTime()
	local okFfi, ffi = pcall(require, "ffi")
	if not okFfi then return nil end
	pcall(ffi.cdef, "double GetCurrentGameTime(void);")
	local ok, t = pcall(function() return ffi.C.GetCurrentGameTime() end)
	if ok and type(t) == "number" then return t end
	return nil
end

--- MD -> native, unchanged
RegisterEvent("x4mp.md_galaxy", function(_, param)
	if type(param) ~= "string" or param == "" then return end
	local ok, err = send("auth_md", { data = param })
	if not ok then log("could not forward a galaxy message: " .. tostring(err)) end
end)

B.on("auth_collect", function()
	if type(AddUITriggeredEvent) ~= "function" then
		log("AddUITriggeredEvent is missing, MD cannot be asked for the galaxy")
		return
	end
	pcall(AddUITriggeredEvent, A.MD_SCREEN, "collect", "1")
	log("galaxy collection requested from MD")
end)

B.on("auth_save", function(p)
	local name = B.validSaveName(p.name)
	if not name or not name:find("^x4mp_ckpt_") then
		send("auth_saved", { ok = false, error = "bad_save_name", request_id = p.request_id })
		log("refused save name " .. tostring(p.name))
		return
	end
	local display = type(p.display) == "string" and p.display or name
	local gt = A.gameTime()
	local function doSave() SaveGame(name, display) end
	local saves = rawget(_G, "X4MPSaves")
	local ok, err
	if type(saves) == "table" and type(saves.allowSaves) == "function" then
		ok, err = pcall(saves.allowSaves, doSave)
	else
		ok, err = pcall(doSave)
	end
	if ok then
		log("SaveGame called for " .. name)
		send("auth_saved", { ok = true, name = name, request_id = p.request_id, game_time = gt })
	else
		log("SaveGame failed: " .. tostring(err))
		send("auth_saved", { ok = false, error = tostring(err), name = name, request_id = p.request_id })
	end
end)

log("loaded")
