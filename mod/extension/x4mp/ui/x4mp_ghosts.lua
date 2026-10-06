-- x4mp_ghosts.lua : Lua half of the client ghosts (M3-10; native features/ghosts, MD half md/x4mp_ghosts.xml).
--
-- Native does the game calls (spawn, move, hide) itself. Three things only MD can do go through here:
--   native -> Lua  x4mp.ghost_dress    {"v":1,"id":"<UniverseID>","name":"[MP] Pia","minhull":100}
--                    -> MD control "dress"      [component, name, min hull %]: name, minimum hull, known, forced radar (spike S13.1)
--                  x4mp.ghost_velocity "<id>,<vx>,<vy>,<vz>;<id>,..."   (m/s, one batch per native frame, 5 Hz per ghost)
--                    -> MD control "velocity"   [component, vx, vy, vz, component, ...]  (spike S13.2, mode c: without it the target
--                       info shows 0 m/s on straight flight)
-- The ids travel as decimal strings ("169452728"); ConvertStringToLuaID makes MD see the component (proven in session 4 sitting 0, S13.1).
-- The wire sector index -> local sector id map is NOT here: it is the selfship feature's (md/x4mp_galaxy.xml, selfship_hub().map()).
--
-- luacheck: globals X4MPBridge X4MPGhosts DebugError RegisterEvent AddUITriggeredEvent ConvertStringToLuaID

local B = rawget(_G, "X4MPBridge")
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] ghosts: x4mp_bridge.lua must load first")
	return
end

local G = { MD_SCREEN = "X4MP_Ghosts", dressCount = 0, velocityCount = 0 }
X4MPGhosts = G

local function log(msg) pcall(DebugError, "[X4MP] ghosts: " .. tostring(msg)) end

local function luaId(idString)
	local ok, r = pcall(function() return ConvertStringToLuaID(tostring(idString)) end)
	if ok and r ~= nil then return r end
	return nil
end

local function trigger(control, value)
	if type(AddUITriggeredEvent) ~= "function" then return false end
	local ok, err = pcall(AddUITriggeredEvent, G.MD_SCREEN, control, value)
	if not ok then log("AddUITriggeredEvent " .. control .. " failed: " .. tostring(err)) end
	return ok
end

-- ---- dress -------------------------------------------------------------------------------------------------------
RegisterEvent("x4mp.ghost_dress", function(_, param)
	local p = B.json.decode(type(param) == "string" and param or "")
	if type(p) ~= "table" or type(p.id) ~= "string" or type(p.name) ~= "string" then
		log("dress: unreadable payload")
		return
	end
	local lid = luaId(p.id)
	if lid == nil then
		log("dress: ConvertStringToLuaID failed for " .. p.id)
		return
	end
	G.dressCount = G.dressCount + 1
	trigger("dress", { lid, p.name, tonumber(p.minhull) or 100, p.skip_radar_known == true and 1 or 0 })
end)

-- ---- velocity hints ----------------------------------------------------------------------------------------------
RegisterEvent("x4mp.ghost_velocity", function(_, param)
	if type(param) ~= "string" or param == "" then return end
	local list = {}
	for item in param:gmatch("[^;]+") do
		local id, vx, vy, vz = item:match("^(%d+),([%-%d%.eE+]+),([%-%d%.eE+]+),([%-%d%.eE+]+)$")
		local lid = id and luaId(id)
		if lid ~= nil and tonumber(vx) and tonumber(vy) and tonumber(vz) then
			list[#list + 1] = lid
			list[#list + 1] = tonumber(vx)
			list[#list + 1] = tonumber(vy)
			list[#list + 1] = tonumber(vz)
		end
	end
	if #list > 0 then
		G.velocityCount = G.velocityCount + 1
		trigger("velocity", list)
	end
end)

log("loaded")
