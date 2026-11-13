-- x4mp_actions.lua : Lua half of the economy actions shim (M5-15; native features/economy, MD half md/x4mp_actions.xml).
--
-- Native cannot change cargo or money (x4_game_func_list.inc has no cargo or credit setter), MD can. Native queues the changes a trade
-- needs and raises one request per frame; this file turns it into AddUITriggeredEvent calls and MD applies the list.
--
--   native -> Lua  x4mp.actions {"v":1,"cargo":[[<component>,<ware macro>,<signed amount>],...],
--                                "money":[[<from faction>,<to faction>,<amount>],...],
--                                "damage":[[<component>,<hull percent>,<shield percent>],...]}
--   Lua -> MD      AddUITriggeredEvent("X4MP_Actions", "apply", <the cargo list>)
--                  AddUITriggeredEvent("X4MP_Actions", "money", <the money list>)
--                  AddUITriggeredEvent("X4MP_Actions", "damage", <the damage list>)
--   MD             add_cargo / remove_cargo with exact= on the component, transfer_money from/to,
--                  set_object_hull / set_object_shield with exact= (a relative percent, M15)
--
-- One trigger per frame in the common case: the trade path only ever queues cargo, and money is rare. Money gets its own trigger because
-- a nested {cargo, money} table is exactly the shape that is unverified in MD's event.param3; the extra trigger costs one frame, which is
-- what the Lua->MD round trip costs anyway (docs/spikes/session-1-results.md: 8.5 ms).
local B = X4MPBridge

local O = { cargo = 0, money = 0, damage = 0, skipped = 0, bad = 0, ops = 0 }
X4MPActions = O

local function log(msg) pcall(DebugError, "[X4MP] actions: " .. tostring(msg)) end

-- Only well-formed rows reach MD: a bad row is dropped here rather than becoming an MD error line.
local function cargo_rows(list)
	if type(list) ~= "table" then return nil end
	local out = {}
	for _, op in ipairs(list) do
		if type(op) == "table" and tonumber(op[1]) and type(op[2]) == "string" and op[2] ~= "" and tonumber(op[3])
			and tonumber(op[3]) ~= 0 then
			out[#out + 1] = { tonumber(op[1]), op[2], tonumber(op[3]) }
		end
	end
	if #out == 0 then return nil end
	return out
end

local function money_rows(list)
	if type(list) ~= "table" then return nil end
	local out = {}
	for _, op in ipairs(list) do
		if type(op) == "table" and type(op[1]) == "string" and type(op[2]) == "string" and op[1] ~= "" and op[2] ~= ""
			and op[1] ~= op[2] and tonumber(op[3]) and tonumber(op[3]) > 0 then
			out[#out + 1] = { op[1], op[2], tonumber(op[3]) }
		end
	end
	if #out == 0 then return nil end
	return out
end

-- Damage (M15): one row per hit ship, in percent of hull/shield. MD subtracts them from what it reads, so the value is a delta, never an
-- absolute; a row with no damage or a non-finite amount never reaches MD.
local function damage_rows(list)
	if type(list) ~= "table" then return nil end
	local function pct(v)
		v = tonumber(v)
		if not v or v ~= v or v == math.huge or v == -math.huge or v <= 0 then return 0 end
		if v > 100 then return 100 end
		return v
	end
	local out = {}
	for _, op in ipairs(list) do
		if type(op) == "table" and tonumber(op[1]) and tonumber(op[1]) > 0 then
			local hull, shield = pct(op[2]), pct(op[3])
			if hull > 0 or shield > 0 then
				out[#out + 1] = { tonumber(op[1]), hull, shield }
			end
		end
	end
	if #out == 0 then return nil end
	return out
end

B.on("actions", function(p)
	if type(p) ~= "table" then O.bad = O.bad + 1; return end
	if type(AddUITriggeredEvent) ~= "function" then O.skipped = O.skipped + 1; return end
	local cargo = cargo_rows(p.cargo)
	if cargo then
		O.ops = O.ops + #cargo
		if pcall(AddUITriggeredEvent, "X4MP_Actions", "apply", cargo) then O.cargo = O.cargo + 1
		else log("cargo trigger failed") end
	end
	local money = money_rows(p.money)
	if money then
		if pcall(AddUITriggeredEvent, "X4MP_Actions", "money", money) then O.money = O.money + 1
		else log("money trigger failed") end
	end
	local damage = damage_rows(p.damage)
	if damage then
		O.ops = O.ops + #damage
		if pcall(AddUITriggeredEvent, "X4MP_Actions", "damage", damage) then O.damage = O.damage + 1
		else log("damage trigger failed") end
	end
end)
