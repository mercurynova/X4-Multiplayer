-- x4mp_authority.lua : Lua half of the in-game authority (M2-09). Loaded after x4mp_saves.lua (ui.xml order).
--
-- Native drives everything; this file only does what needs the Lua/MD side of the game:
--   native -> Lua  x4mp.auth_collect {"v":1}                   ask MD (md/x4mp_galaxy.xml) for the sectors and the player's ship
--                  {"v":1,"ship_only":true}                      only the player's ship (close-out A item 3: MD had none at the first checkpoint):
--                                                               answered with one "P;..." or, when there is none, "N;"
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
-- M3-09 (sector map for the own-ship tracker, SETA block / switch-off): see the block at the end of this file.
--
-- luacheck: globals X4MPBridge X4MPSaves X4MPAuthority DebugError RegisterEvent SaveGame AddUITriggeredEvent ConvertStringTo64Bit

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

B.on("auth_collect", function(p)
	if type(AddUITriggeredEvent) ~= "function" then
		log("AddUITriggeredEvent is missing, MD cannot be asked for the galaxy")
		return
	end
	if type(p) == "table" and p.ship_only == true then
		pcall(AddUITriggeredEvent, A.MD_SCREEN, "ship", "1")
		log("player ship requested from MD")
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

------------------------------------------------------------------------------
-- M3-09: sector map (every node) and SETA (native features/selfship; MD side in md/x4mp_galaxy.xml)
------------------------------------------------------------------------------
--   native -> Lua  x4mp.sector_map_collect {"v":1}            ask MD for every sector as a (macro, component) pair
--   MD -> Lua      x4mp.md_sector_tag <macro id>, then x4mp.md_sector <the sector component>, then x4mp.md_sector_end <count>
--                  (a table param {macro, sector} is accepted too); the component is converted with ConvertStringTo64Bit
--   Lua -> native  x4mp.sector_map {"v":1,"data":"S;macro|id;macro|id;..."} (<= 40 per message) and {"data":"E;<count>"}
--   native -> Lua  x4mp.seta_block {"v":1,"on":bool}           block SETA at the source while connected (MD flag)
--                  x4mp.seta_off {"v":1}                       SETA is on: switch it off now (MD)
--   MD -> Lua      x4mp.md_seta 'blocked'                      MD stopped a SETA start itself
--   Lua -> native  x4mp.seta_blocked {"v":1}                   (native shows "SETA is disabled in multiplayer")
local MAP_CHUNK = 40

local function toId(x)
	local ok, r = pcall(function() return ConvertStringTo64Bit(tostring(x)) end)
	if not ok or r == nil then return nil end
	local n = tonumber(r)
	if not n or n <= 0 then return nil end
	return string.format("%.0f", n)
end

local function mapFlush(m)
	if #m.chunk == 0 then return end
	local ok, err = send("sector_map", { data = "S;" .. table.concat(m.chunk, ";") })
	if not ok then log("could not forward the sector map: " .. tostring(err)) end
	m.chunk = {}
end

local function mapAdd(macro, comp)
	local m = A.map
	if not m then return end
	macro = tostring(macro or "")
	local id = toId(comp)
	if macro == "" or macro:find("[|;]") or not id then
		m.dropped = m.dropped + 1
		return
	end
	m.chunk[#m.chunk + 1] = macro .. "|" .. id
	m.total = m.total + 1
	if #m.chunk >= MAP_CHUNK then mapFlush(m) end
end

RegisterEvent("x4mp.md_sector_tag", function(_, param)
	if A.map then A.map.pendingMacro = tostring(param or "") end
end)
RegisterEvent("x4mp.md_sector", function(_, param)
	local m = A.map
	if not m then return end
	if type(param) == "table" then
		mapAdd(param[1], param[2])
	else
		mapAdd(m.pendingMacro, param)
	end
	m.pendingMacro = nil
end)
RegisterEvent("x4mp.md_sector_end", function(_, param)
	local m = A.map
	if not m then return end
	mapFlush(m)
	local ok, err = send("sector_map", { data = "E;" .. tostring(m.total) })
	if not ok then log("could not forward the sector map end: " .. tostring(err)) end
	log("sector map sent: " .. tostring(m.total) .. " sectors, " .. tostring(m.dropped) .. " dropped (MD announced " .. tostring(param) .. ")")
	A.map = nil
end)

B.on("sector_map_collect", function()
	if type(AddUITriggeredEvent) ~= "function" then
		log("AddUITriggeredEvent is missing, MD cannot be asked for the sector map")
		return
	end
	A.map = { chunk = {}, total = 0, dropped = 0 }
	pcall(AddUITriggeredEvent, A.MD_SCREEN, "map", "1")
	log("sector map requested from MD")
end)

B.on("seta_block", function(p)
	if type(AddUITriggeredEvent) ~= "function" then return end
	local on = type(p) == "table" and p.on == true
	pcall(AddUITriggeredEvent, A.MD_SCREEN, "seta_block", on and "1" or "0")
	log("SETA " .. (on and "blocked" or "released") .. " at the source")
end)

B.on("seta_off", function()
	if type(AddUITriggeredEvent) ~= "function" then return end
	pcall(AddUITriggeredEvent, A.MD_SCREEN, "seta_off", "1")
	log("SETA switch-off requested from MD")
end)

RegisterEvent("x4mp.md_seta", function(_, param)
	local ok, err = send("seta_blocked", { source = tostring(param or "") })
	if not ok then log("could not report the SETA block: " .. tostring(err)) end
end)

-- M3-33: the game's own superhighway signal (md/x4mp_galaxy.xml X4MP_Highway_*), one string per event ('gate' / 'zone' / 'sector' = entry,
-- 'exit'). Native decides what to do (features/selfship): the entry hides the ghost / holds the avatar at once.
--   MD -> Lua      x4mp.md_highway <source>
--   Lua -> native  x4mp.highway_signal {"v":1,"source":"<source>"}
RegisterEvent("x4mp.md_highway", function(_, param)
	local ok, err = send("highway_signal", { source = tostring(param or "") })
	if not ok then log("could not report the superhighway signal: " .. tostring(err)) end
end)

log("loaded")
