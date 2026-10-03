-- x4mp_players.lua : the player table of the Multiplayer window and join/leave notifications (M3-06; docs/mod-design.md 7.7, m3-plan 4.11).
--
-- Native topic handled here (docs/mod-design.md 7.1; native side features/chat/chat_json.h):
--   x4mp.players {"v":1,"self":2,"players":[{"id","name","team","team_role","roles","phase","in_game","online","ping","sector","ship"}],
--                 "events":[{"kind":"join"|"leave","id","name"}]}
--   The whole list on every change (at most 2 Hz); "events" only on the push that announces a join or leave.
--
-- What it does:
--   * keeps the list (X4MPPlayers.list, sorted by team then name) and the own id;
--   * X4MPPlayers.rows(rows) adds the table to the Multiplayer screen model (x4mp_menu.lua calls it while connected): one text row per player,
--     name in the team colour, team, online / offline / joining, ping, sector. The screen is redrawn when the list changes and the
--     main screen is open (never the join form: a redraw there would steal the focus of an edit box);
--   * a join / leave shows a notification through the HUD notify cue (AddUITriggeredEvent "X4MP" "notify") and a line in the chat window;
--   * X4MPPlayers.resolveWhisper(text) finds the player a "/w name message" line is meant for (names may contain blanks).
--
-- Team colours: the product colour faction_x4mp_team_<n> (libraries/colors.xml, M3-08) when the game's Color table has it, else a fixed
-- palette, so the table is readable before the libraries exist. Sector: X4MPPlayers.sectorResolver(index) -> name may be set by the
-- galaxy code (M3-09 / M3-10); until then the sector index is shown.
--
-- luacheck: globals X4MPBridge X4MPScreens X4MPChat X4MPPlayers ReadText AddUITriggeredEvent Color DebugError

if X4MPPlayers and X4MPPlayers.loaded then return end

local B = X4MPBridge
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] players: x4mp_bridge.lua must load first")
	return
end

local P = { loaded = true, list = {}, byId = {}, self = 0, MAX_ROWS = 16, notifications = true, sectorResolver = nil }
X4MPPlayers = P
local log = B.log

local function T(id, ...)
	local S = rawget(_G, "X4MPScreens")
	if type(S) == "table" and type(S.T) == "function" then return S.T(id, ...) end
	local text = ""
	if type(ReadText) == "function" then
		local ok, res = pcall(ReadText, 92000, id)
		if ok and type(res) == "string" then text = res end
	end
	if text == "" then text = "[92000," .. id .. "]" end
	if select("#", ...) > 0 then
		local ok, res = pcall(string.format, text, ...)
		if ok then text = res end
	end
	return text
end

------------------------------------------------------------------------------
-- colours
------------------------------------------------------------------------------
P.PALETTE = { "4a9eff", "ff5a4a", "5adf6a", "ffd23f", "c07aff", "ff9a3c", "3fe0d0", "ff7ac8" }
P.NO_TEAM_COLOR = "c8c8c8"

local function hex2(n) return string.format("%02x", math.max(0, math.min(255, math.floor((tonumber(n) or 0) + 0.5)))) end

--- teamColorHex(team) -> "rrggbb"
function P.teamColorHex(team)
	team = tonumber(team) or 0
	if team < 1 then return P.NO_TEAM_COLOR end
	local colors = rawget(_G, "Color")
	if type(colors) == "table" then
		local c = colors["faction_x4mp_team_" .. team]
		if type(c) == "table" and c.r and c.g and c.b then return hex2(c.r) .. hex2(c.g) .. hex2(c.b) end
	end
	return P.PALETTE[(team - 1) % #P.PALETTE + 1]
end

--- colorEscape("rrggbb") -> the X4 text colour escape (opaque)
function P.colorEscape(hex)
	if type(hex) == "string" and hex:match("^%x%x%x%x%x%x$") then return "\027#ff" .. hex:lower() .. "#" end
	return ""
end

------------------------------------------------------------------------------
-- list
------------------------------------------------------------------------------
local function lower(s) return tostring(s or ""):lower() end

local function sortList(list)
	table.sort(list, function(a, b)
		if (a.team or 0) ~= (b.team or 0) then return (a.team or 0) < (b.team or 0) end
		local an, bn = lower(a.name), lower(b.name)
		if an ~= bn then return an < bn end
		return (a.id or 0) < (b.id or 0)
	end)
end

--- find(id) -> row or nil
function P.find(id) return P.byId[id] end

--- findByName(name) -> row; exact (case-insensitive) first, else a unique prefix match; nil otherwise
function P.findByName(name)
	local want = lower(name)
	if want == "" then return nil end
	local prefix, count
	for _, row in ipairs(P.list) do
		local n = lower(row.name)
		if n == want then return row end
		if n:sub(1, #want) == want then
			prefix, count = row, (count or 0) + 1
		end
	end
	if count == 1 then return prefix end
	return nil
end

--- resolveWhisper("name message") -> row, message   (nil, firstWord when no player matches). Names may contain blanks: the longest
--- player name that starts the text and is followed by a blank wins.
function P.resolveWhisper(text)
	text = tostring(text or ""):gsub("^%s+", "")
	local best, bestLen
	for _, row in ipairs(P.list) do
		local n = lower(row.name)
		if n ~= "" then
			local head = lower(text:sub(1, #n))
			local after = text:sub(#n + 1, #n + 1)
			if head == n and (after == " " or after == "") and (not bestLen or #n > bestLen) then best, bestLen = row, #n end
		end
	end
	if best then return best, (text:sub(bestLen + 1):gsub("^%s+", "")) end
	local word, rest = text:match("^(%S+)%s*(.*)$")
	if word then
		local row = P.findByName(word)
		if row then return row, rest end
	end
	return nil, word or ""
end

------------------------------------------------------------------------------
-- rows for the Multiplayer screen
------------------------------------------------------------------------------
local function sectorText(index)
	index = tonumber(index) or 0
	if index <= 0 then return "-" end
	if type(P.sectorResolver) == "function" then
		local ok, name = pcall(P.sectorResolver, index)
		if ok and type(name) == "string" and name ~= "" then return name end
	end
	return T(527, index)
end

local function statusText(row)
	if not row.online then return T(522) end
	if row.in_game == false then return T(523) end
	return T(521)
end

--- rowText(player) -> the table line of one player
function P.rowText(row)
	local name = P.colorEscape(P.teamColorHex(row.team)) .. tostring(row.name) .. "\027X"
	local you = (row.id == P.self) and T(526) or ""
	local team = (row.team or 0) > 0 and T(529, row.team) or T(525)
	return T(524, name, you, team, statusText(row), tostring(row.ping or 0), sectorText(row.sector))
end

--- rows(rows): appends the header and one text row per player (nothing while the list is empty)
function P.rows(rows)
	if #P.list == 0 then return end
	rows[#rows + 1] = { type = "header", text = T(520) }
	for i, row in ipairs(P.list) do
		if i > P.MAX_ROWS then
			rows[#rows + 1] = { type = "text", text = T(528, #P.list - P.MAX_ROWS), tone = "inactive" }
			break
		end
		rows[#rows + 1] = { type = "text", text = P.rowText(row), tone = row.online and "normal" or "inactive" }
	end
end

------------------------------------------------------------------------------
-- native topic
------------------------------------------------------------------------------
local function announce(event)
	local text = event.kind == "join" and T(508, tostring(event.name)) or T(509, tostring(event.name))
	local chat = rawget(_G, "X4MPChat")
	if type(chat) == "table" and type(chat.systemLine) == "function" then pcall(chat.systemLine, text) end
	if P.notifications and type(AddUITriggeredEvent) == "function" then pcall(AddUITriggeredEvent, "X4MP", "notify", text) end
end

--- onPlayers(payload): the x4mp.players handler (public for tests)
function P.onPlayers(p)
	local list, byId = {}, {}
	if type(p.players) == "table" then
		for _, r in ipairs(p.players) do
			if type(r) == "table" and type(r.name) == "string" and tonumber(r.id) then
				local row = {
					id = tonumber(r.id), name = r.name, team = tonumber(r.team) or 0, online = r.online ~= false,
					in_game = r.in_game ~= false, ping = tonumber(r.ping) or 0, sector = tonumber(r.sector) or 0,
					phase = tonumber(r.phase) or 0, ship = tonumber(r.ship) or 0, team_role = tonumber(r.team_role) or 0,
				}
				list[#list + 1] = row
				byId[row.id] = row
			end
		end
	end
	sortList(list)
	P.list, P.byId, P.self = list, byId, tonumber(p.self) or 0
	if type(p.events) == "table" then
		for _, e in ipairs(p.events) do
			if type(e) == "table" and (e.kind == "join" or e.kind == "leave") then announce(e) end
		end
	end
	local S = rawget(_G, "X4MPScreens")
	if type(S) == "table" and S.state and S.state.screen == "main" and type(S.render) == "function" then S.render() end
end

B.on("players", function(p) P.onPlayers(p) end)
log("players loaded")
