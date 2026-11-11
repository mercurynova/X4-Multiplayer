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
-- luacheck: globals X4MPBridge X4MPTeams X4MPPlayers X4MPChat DebugError ReadText RegisterEvent AddUITriggeredEvent

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
	-- M3-28 diag.no_set_faction_known: ONE trailing 1 (the list is then 3 + nSlots + 3 * nRel + 1 long; MD skips set_faction_known)
	if p.skip_known == true then out[#out + 1] = 1 end
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

-- ---- M5b: a player invites another player to their team ---------------------------------------------------------------
-- The invitation is the only team action a player starts from the Talk menu or the chat line, and it is the only one whose
-- target must agree, so the two half-words are:
--   Lua -> native  x4mp.teams_invite {"op":"invite","to":<pid>} / {"op":"respond","id":<invite id>,"accept":<bool>}
--   native -> Lua  x4mp.teams_invite {"v":1,"kind":"notice","id":N,"from":P,"team":T,"name":"<team name>","expires":S}
--                  x4mp.teams_invite {"v":1,"kind":"result","op":"invite|respond","ok":<bool>,"reason":"<token>","team":T}
--                  x4mp.teams_invite {"v":1,"kind":"error","op":"invite","error":"<what the native half refused>"}
-- The invite id is the only thing /mp teamaccept and /mp teamdecline take; the server answers the same id twice with Rejected.

local invites = {}   -- invite id -> { from = pid, team = "<team name>" }
local answer = nil    -- the last respond: { id = N, accept = <bool> }, so the answer line can say which one it was
B.VERBS.teams_invite = true   -- Lua -> native (M5b); native subscribes x4mp.teams_invite in teams_feature.cpp

local function trim(text) return tostring(text or ""):match("^%s*(.-)%s*$") or "" end

local function text(id, ...)
	local S = rawget(_G, "X4MPScreens")
	if type(S) == "table" and type(S.T) == "function" then return S.T(id, ...) end
	local body = ""
	if type(ReadText) == "function" then
		local ok, res = pcall(ReadText, 92000, id)
		if ok and type(res) == "string" then body = res end
	end
	if body == "" then body = "[92000," .. id .. "]" end
	if select("#", ...) > 0 then
		local ok, res = pcall(string.format, body, ...)
		if ok then body = res end
	end
	return body
end

local function chatLine(line)
	local chat = rawget(_G, "X4MPChat")
	if type(chat) == "table" and type(chat.systemLine) == "function" then pcall(chat.systemLine, line) end
end

local function nameOf(pid)
	local P = rawget(_G, "X4MPPlayers")
	if type(P) == "table" and type(P.list) == "table" then
		for _, row in ipairs(P.list) do
			if tonumber(row.id) == tonumber(pid) then return tostring(row.name) end
		end
	end
	return "player " .. tostring(pid)
end

local function ask(body)
	if type(B) ~= "table" or type(B.send) ~= "function" then return false end
	local ok, err = B.send("teams_invite", body)
	if not ok then chatLine(text(950, tostring(err or "no bridge"))) end
	return ok
end

--- T.invite(pid): ask the leader's team to take that player in. The server decides whether the local player may ask.
function T.invite(pid)
	local p = tonumber(pid)
	if not p then return false end
	return ask({ op = "invite", to = p })
end

--- T.respond(id, accept): answer a pending invitation. Only the invited player can, the server checks that again.
function T.respond(id, accept)
	local n = tonumber(id)
	if not n then chatLine(text(953)) return false end
	answer = { id = n, accept = accept and true or false }
	invites[n] = nil
	return ask({ op = "respond", id = n, accept = answer.accept })
end

--- T.pending(): the invitations this player has not answered yet, newest first (the HUD may show the count).
function T.pending()
	local list = {}
	for id, row in pairs(invites) do list[#list + 1] = { id = id, from = row.from, team = row.team } end
	table.sort(list, function(a, b) return a.id > b.id end)
	return list
end

--- T.command(cmd, rest) -> true when the "/mp" word is a team invitation one ("teamaccept"/"teamdecline").
function T.command(cmd, rest)
	local sub = tostring(cmd or ""):lower()
	if sub ~= "teamaccept" and sub ~= "teamdecline" then return false end
	local id = tonumber(trim(rest))
	if not id then chatLine(text(953)) return true end
	T.respond(id, sub == "teamaccept")
	return true
end

B.on("teams_invite", function(p)
	if type(p) ~= "table" then return end
	local kind = tostring(p.kind or "")
	if kind == "notice" then
		local id = tonumber(p.id)
		if not id then return end
		local from = tonumber(p.from) or 0
		local team = tostring(p.name or "")
		invites[id] = { from = from, team = team }
		chatLine(text(948, nameOf(from), team, id))
	elseif kind == "result" then
		local ok = p.ok == true
		local op = tostring(p.op or "")
		if op == "invite" then
			if ok then chatLine(text(949)) else chatLine(text(950, tostring(p.reason or "?"))) end
		elseif op == "respond" then
			if not ok then
				chatLine(text(950, tostring(p.reason or "?")))
			elseif type(answer) == "table" and answer.accept then
				chatLine(text(951))
			else
				chatLine(text(952))
			end
		end
	elseif kind == "error" then
		chatLine(text(950, tostring(p.error or "?")))
	end
end)

log("loaded")
