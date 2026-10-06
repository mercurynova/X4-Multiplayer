-- x4mp_avatars.lua : Lua half of the authority's avatars in game (M3-11). Loaded after x4mp_teams.lua (ui.xml order).
--
-- Native (features/avatars) decides everything; MD (md/x4mp_avatars.xml) does what only MD can: a safe spawn position, the name / min hull /
-- early-game loadout of a spawned ship, and the velocity hint. This file only carries the three requests across and the answers back.
--   native -> Lua  x4mp.avatars_safepos {"v":1,"seq":N,"sector":"<id>","x":..,"y":..,"z":..,"radius":..}
--                  -> AddUITriggeredEvent("X4MP_Avatars", "safepos", { seq, <sector lua id>, x, y, z, radius })
--   native -> Lua  x4mp.avatars_dress   {"v":1,"seq":N,"id":"<id>","name":S,"min_hull":P,"macro":S,"loadout":S,"basic":bool}
--                  -> AddUITriggeredEvent("X4MP_Avatars", "dress", { seq, <object lua id>, name, min_hull, loadout, basic(1|0), skip_radar_known(1|0) })
--   native -> Lua  x4mp.avatars_vel     {"v":1,"h":[["<id>",vx,vy,vz],...]}   (5 Hz)
--                  -> AddUITriggeredEvent("X4MP_Avatars", "velocity", { n, <lua id>, vx, vy, vz, ... })
--   MD -> Lua      x4mp.md_avatars <string>   "P;seq;ok;x;y;z" (safe position) or "D;seq;ok;detail" (dressed)
--   Lua -> native  x4mp.avatars_md {"v":1,"data":"<string>"}   (unchanged; a request Lua cannot carry is answered at once with ok = 0)
-- Ids are decimal strings (UniverseID is 64 bit); ConvertStringToLuaID turns them into the ids MD understands (the same call the sitting-0
-- spike used for its dress and velocity blocks).
--
-- luacheck: globals X4MPBridge X4MPAvatars DebugError RegisterEvent AddUITriggeredEvent ConvertStringToLuaID ConvertStringTo64Bit

local B = rawget(_G, "X4MPBridge")
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] avatars: x4mp_bridge.lua must load first")
	return
end

local A = { MD_SCREEN = "X4MP_Avatars", luaIds = {}, luaIdCount = 0 }
X4MPAvatars = A

local function log(msg) pcall(DebugError, "[X4MP] avatars: " .. tostring(msg)) end

local function send(data)
	local api = B.getApi()
	if not api then return false, "no_api" end
	local text, err = B.json.encode({ v = 1, data = data })
	if not text then return false, err end
	local ok, perr = pcall(api.raise_event, "x4mp.avatars_md", text)
	if not ok then return false, tostring(perr) end
	return true
end

--- decimal id string -> the id MD understands (cached: a ship is hinted every 200 ms)
function A.luaid(idString)
	if type(idString) ~= "string" or idString == "" then return nil end
	local c = A.luaIds[idString]
	if c ~= nil then return c end
	if type(ConvertStringToLuaID) ~= "function" then return nil end
	local ok, r = pcall(ConvertStringToLuaID, idString)
	if not ok or r == nil then return nil end
	if A.luaIdCount > 256 then
		A.luaIds, A.luaIdCount = {}, 0
	end
	A.luaIds[idString] = r
	A.luaIdCount = A.luaIdCount + 1
	return r
end

local function num(v) return type(v) == "number" and v == v and v ~= math.huge and v ~= -math.huge end

local function toMD(control, list)
	if type(AddUITriggeredEvent) ~= "function" then return false, "no_md_event" end
	local ok, err = pcall(AddUITriggeredEvent, A.MD_SCREEN, control, list)
	if not ok then
		log("AddUITriggeredEvent failed: " .. tostring(err))
		return false, "md_event_failed"
	end
	return true
end

B.on("avatars_safepos", function(p)
	local seq = type(p) == "table" and p.seq
	if not num(seq) then return end
	local sector = A.luaid(p.sector)
	if not sector or not (num(p.x) and num(p.y) and num(p.z) and num(p.radius)) then
		send("P;" .. seq .. ";0")
		return
	end
	local ok = toMD("safepos", { seq, sector, p.x, p.y, p.z, p.radius })
	if not ok then send("P;" .. seq .. ";0") end
end)

B.on("avatars_dress", function(p)
	local seq = type(p) == "table" and p.seq
	if not num(seq) then return end
	local obj = A.luaid(p.id)
	if not obj or type(p.name) ~= "string" or not num(p.min_hull) then
		send("D;" .. seq .. ";0;bad_request")
		return
	end
	local ok, why = toMD("dress", { seq, obj, p.name, p.min_hull, tostring(p.loadout or ""), p.basic and 1 or 0, p.skip_radar_known == true and 1 or 0 })
	if not ok then send("D;" .. seq .. ";0;" .. tostring(why)) end
end)

-- M3-30 (client): native asks MD to create the player's own ship (md/x4mp_avatars.xml X4MP_Avatars_Create)
--   native -> Lua  x4mp.avatars_create {"v":1,"seq":N,"sector":"<id>","x","y","z","macro":S,"name":S,"loadout":S,"basic":bool}
--                  -> AddUITriggeredEvent("X4MP_Avatars", "create", { seq, <sector lua id>, x, y, z, macro, name, loadout, basic(1|0) })
--   MD -> Lua      x4mp.md_avatars_created { seq, <ship component> }  -> x4mp.avatars_md "C;seq;1;<decimal id>" (ConvertStringTo64Bit)
B.on("avatars_create", function(p)
	local seq = type(p) == "table" and p.seq
	if not num(seq) then return end
	local sector = A.luaid(p.sector)
	if not sector or type(p.macro) ~= "string" or not (num(p.x) and num(p.y) and num(p.z)) then
		send("C;" .. seq .. ";0")
		return
	end
	local ok = toMD("create", { seq, sector, p.x, p.y, p.z, p.macro, tostring(p.name or ""), tostring(p.loadout or ""), p.basic and 1 or 0 })
	if not ok then send("C;" .. seq .. ";0") end
end)

local function shipId(x)
	if type(ConvertStringTo64Bit) ~= "function" then return nil end
	local ok, r = pcall(function() return ConvertStringTo64Bit(tostring(x)) end)
	if not ok or r == nil then return nil end
	local n = tonumber(r)
	if not n or n <= 0 then return nil end
	return string.format("%.0f", n)
end

RegisterEvent("x4mp.md_avatars_created", function(_, param)
	if type(param) ~= "table" then return end
	local seq = tonumber(param[1])
	if not seq then return end
	local id = shipId(param[2])
	local ok, err = send(id and ("C;" .. seq .. ";1;" .. id) or ("C;" .. seq .. ";0"))
	if not ok then log("could not forward the created ship: " .. tostring(err)) end
end)

B.on("avatars_vel", function(p)
	if type(p) ~= "table" or type(p.h) ~= "table" then return end
	local list, n = { 0 }, 0
	for _, h in ipairs(p.h) do
		local id = type(h) == "table" and A.luaid(h[1])
		if id and num(h[2]) and num(h[3]) and num(h[4]) then
			list[#list + 1] = id
			list[#list + 1] = h[2]
			list[#list + 1] = h[3]
			list[#list + 1] = h[4]
			n = n + 1
		end
	end
	if n == 0 then return end
	list[1] = n
	toMD("velocity", list)
end)

--- MD -> native, unchanged
RegisterEvent("x4mp.md_avatars", function(_, param)
	if type(param) ~= "string" or param == "" then return end
	local ok, err = send(param)
	if not ok then log("could not forward an MD answer: " .. tostring(err)) end
end)

log("loaded")
