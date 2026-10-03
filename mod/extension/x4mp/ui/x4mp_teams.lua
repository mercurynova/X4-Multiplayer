-- x4mp_teams.lua : Lua half of the team factions in game (M3-08). Loaded after x4mp_chat.lua (ui.xml order).
--
-- Native decides what the factions and relations must be; MD (md/x4mp_teams.xml) does it. This file only carries the plan across:
--   native -> Lua  x4mp.teams_apply {"v":1,"seq":N,"reason":"universe_ready|welcome|teams","own":S,"slots":[1,2],"rel":[[a,b,value],...]}
--                  a, b = faction codes (0 = player, 1..8 = x4mp_team_<n>), value = the relation to set (both directions)
--                  -> AddUITriggeredEvent("X4MP_Teams", "apply", { seq, nSlots, nRel, slot..., a, b, v, ... })   (one flat Lua array = one MD list)
--   MD -> Lua      x4mp.md_teams <string>      "R;seq;active;mismatches;player_locked;slots;relations" or "E;seq;reason"
--   Lua -> native  x4mp.teams_md {"v":1,"data":"<string>"}   (unchanged; native turns it into team_setup_state)
-- A payload Lua cannot use (bad numbers, no AddUITriggeredEvent) is answered with "E;<seq>;<reason>" so native reports the failure at once.
--
-- luacheck: globals X4MPBridge X4MPTeams DebugError RegisterEvent AddUITriggeredEvent

local B = rawget(_G, "X4MPBridge")
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] teams: x4mp_bridge.lua must load first")
	return
end

local T = { MD_SCREEN = "X4MP_Teams", MAX_SLOT = 8 }
X4MPTeams = T

local function log(msg) pcall(DebugError, "[X4MP] teams: " .. tostring(msg)) end

local function send(data)
	local api = B.getApi()
	if not api then return false, "no_api" end
	local text, err = B.json.encode({ v = 1, data = data })
	if not text then return false, err end
	local ok, perr = pcall(api.raise_event, "x4mp.teams_md", text)
	if not ok then return false, tostring(perr) end
	return true
end

local function isCode(n) return type(n) == "number" and n >= 0 and n <= T.MAX_SLOT and n == math.floor(n) end

--- plan table (decoded x4mp.teams_apply) -> flat array for MD, or nil, reason
function T.flatten(p)
	if type(p) ~= "table" or type(p.seq) ~= "number" or p.seq < 1 or p.seq ~= math.floor(p.seq) then return nil, "bad_payload" end
	local slots, rel = p.slots, p.rel
	if type(slots) ~= "table" or type(rel) ~= "table" then return nil, "bad_payload" end
	local out = { p.seq, #slots, #rel }
	for _, s in ipairs(slots) do
		if not isCode(s) or s < 1 then return nil, "bad_payload" end
		out[#out + 1] = s
	end
	for _, r in ipairs(rel) do
		if type(r) ~= "table" or not isCode(r[1]) or not isCode(r[2]) or type(r[3]) ~= "number" or r[3] < -1 or r[3] > 1 then return nil, "bad_payload" end
		out[#out + 1] = r[1]
		out[#out + 1] = r[2]
		out[#out + 1] = r[3]
	end
	return out
end

function T.apply(p)
	local list, why = T.flatten(p)
	if not list then
		local seq = type(p) == "table" and tonumber(p.seq) or 0
		send("E;" .. tostring(seq or 0) .. ";" .. tostring(why))
		log("unusable plan: " .. tostring(why))
		return false
	end
	if type(AddUITriggeredEvent) ~= "function" then
		send("E;" .. tostring(list[1]) .. ";no_md_event")
		log("AddUITriggeredEvent is missing, MD cannot be asked")
		return false
	end
	local ok, err = pcall(AddUITriggeredEvent, T.MD_SCREEN, "apply", list)
	if not ok then
		send("E;" .. tostring(list[1]) .. ";md_event_failed")
		log("AddUITriggeredEvent failed: " .. tostring(err))
		return false
	end
	T.lastSeq = list[1]
	return true
end

B.on("teams_apply", function(p) T.apply(p) end)

--- MD -> native, unchanged
RegisterEvent("x4mp.md_teams", function(_, param)
	if type(param) ~= "string" or param == "" then return end
	local ok, err = send(param)
	if not ok then log("could not forward a team report: " .. tostring(err)) end
end)

log("loaded")
