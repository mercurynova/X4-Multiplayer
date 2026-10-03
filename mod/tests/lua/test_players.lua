-- M3-06: ui/x4mp_players.lua (player table in the Multiplayer screen, join/leave notifications, whisper name resolution, team colours).
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local md -- AddUITriggeredEvent calls of the test

local function fresh()
	env.reset()
	env.installApi()
	md = {}
	_G.AddUITriggeredEvent = function(screen, control, value) md[#md + 1] = { screen, control, value } end
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua", "x4mp_players.lua" })
end

local function players(list, self, events)
	local body = {}
	for _, p in ipairs(list) do
		body[#body + 1] = string.format('{"id":%d,"name":%q,"team":%d,"online":%s,"in_game":%s,"ping":%d,"sector":%d}', p.id, p.name, p.team or 0,
			p.online == false and "false" or "true", p.in_game == false and "false" or "true", p.ping or 20, p.sector or 0)
	end
	local ev = {}
	for _, e in ipairs(events or {}) do ev[#ev + 1] = string.format('{"kind":"%s","id":%d,"name":"%s"}', e[1], e[2], e[3]) end
	env.fire("x4mp.players", string.format('{"v":1,"self":%d,"players":[%s],"events":[%s]}', self or 1, table.concat(body, ","), table.concat(ev, ",")))
end

local function connect()
	env.fire("x4mp.status", '{"v":1,"state":"ingame"}')
end

test("players: the list is sorted by team then name and findable by id", function()
	fresh()
	players({ { id = 3, name = "Zed", team = 1 }, { id = 1, name = "alice", team = 2 }, { id = 2, name = "Bob", team = 1 } }, 1)
	eq(#X4MPPlayers.list, 3)
	eq(X4MPPlayers.list[1].name, "Bob")
	eq(X4MPPlayers.list[2].name, "Zed")
	eq(X4MPPlayers.list[3].name, "alice")
	eq(X4MPPlayers.find(2).name, "Bob")
	eq(X4MPPlayers.self, 1)
end)

test("players: rows for the Multiplayer screen show name, team, status, ping and sector; offline is greyed", function()
	fresh()
	players({ { id = 1, name = "Alice", team = 1, ping = 31, sector = 4 }, { id = 2, name = "Bob", team = 0, online = false },
		{ id = 3, name = "Cy", team = 2, in_game = false } }, 1)
	local rows = {}
	X4MPPlayers.rows(rows)
	eq(rows[1].type, "header")
	eq(#rows, 4)
	-- sorted by team: Bob (no team), Alice (1), Cy (2)
	t.contains(rows[3].text, "Alice")
	t.contains(rows[3].text, " - you")
	t.contains(rows[3].text, "Team 1")
	t.contains(rows[3].text, "online")
	t.contains(rows[3].text, "31 ms")
	t.contains(rows[3].text, "sector 4")
	eq(rows[3].tone, "normal")
	t.contains(rows[2].text, "offline")
	t.contains(rows[2].text, "no team")
	eq(rows[2].tone, "inactive")
	t.contains(rows[4].text, "joining")
	local none = {}
	fresh()
	X4MPPlayers.rows(none)
	eq(#none, 0, "an empty list adds nothing")
end)

test("players: the name is drawn in the team colour (palette, or the product colour when the game has it)", function()
	fresh()
	players({ { id = 1, name = "Alice", team = 1 } }, 1)
	local rows = {}
	X4MPPlayers.rows(rows)
	t.contains(rows[2].text, "\027#ff4a9eff#Alice\027X")
	_G.Color = { faction_x4mp_team_1 = { r = 255, g = 0, b = 16, a = 100 } }
	eq(X4MPPlayers.teamColorHex(1), "ff0010")
	eq(X4MPPlayers.teamColorHex(2), "ff5a4a") -- palette fallback when the colour is missing
	eq(X4MPPlayers.teamColorHex(0), "c8c8c8")
	eq(X4MPPlayers.teamColorHex(9), "4a9eff") -- wraps around the palette
end)

test("players: a sector resolver names the sector, a failing one falls back to the index", function()
	fresh()
	players({ { id = 1, name = "Alice", team = 1, sector = 7 } }, 1)
	X4MPPlayers.sectorResolver = function(i) return "Argon Prime " .. i end
	local rows = {}
	X4MPPlayers.rows(rows)
	t.contains(rows[2].text, "Argon Prime 7")
	X4MPPlayers.sectorResolver = function() error("boom") end
	rows = {}
	X4MPPlayers.rows(rows)
	t.contains(rows[2].text, "sector 7")
end)

test("players: more rows than the cap are summarised", function()
	fresh()
	local list = {}
	for i = 1, 20 do list[#list + 1] = { id = i, name = "P" .. string.format("%02d", i), team = 1 } end
	players(list, 1)
	local rows = {}
	X4MPPlayers.rows(rows)
	eq(#rows, 1 + 16 + 1)
	t.contains(rows[#rows].text, "and 4 more")
end)

test("players: a join / leave event notifies once (HUD cue) and the chat gets a system line when it is loaded", function()
	fresh()
	local lines = {}
	_G.X4MPChat = { systemLine = function(text) lines[#lines + 1] = text end }
	players({ { id = 1, name = "Alice", team = 1 }, { id = 2, name = "Bob", team = 1 } }, 1, { { "join", 2, "Bob" } })
	eq(#md, 1)
	eq(md[1][1], "X4MP")
	eq(md[1][2], "notify")
	eq(md[1][3], "Bob joined the session")
	eq(lines[1], "Bob joined the session")
	players({ { id = 1, name = "Alice", team = 1 } }, 1, { { "leave", 2, "Bob" } })
	eq(md[2][3], "Bob left the session")
	players({ { id = 1, name = "Alice", team = 1 } }, 1) -- an unchanged list pushes no notification
	eq(#md, 2)
end)

test("players: whisper resolution handles blanks in names, case, unique prefixes and unknown names", function()
	fresh()
	players({ { id = 1, name = "Alice" }, { id = 2, name = "Bob Smith" }, { id = 3, name = "Bob" }, { id = 4, name = "Carol" } }, 1)
	local row, rest = X4MPPlayers.resolveWhisper("bob smith hello there")
	eq(row.id, 2)
	eq(rest, "hello there")
	row, rest = X4MPPlayers.resolveWhisper("Bob  you around")
	eq(row.id, 3, "the exact short name wins when the long one does not match")
	eq(rest, "you around")
	row, rest = X4MPPlayers.resolveWhisper("car hi")
	eq(row.id, 4, "a unique prefix is enough")
	eq(rest, "hi")
	row, rest = X4MPPlayers.resolveWhisper("nobody hi")
	eq(row, nil)
	eq(rest, "nobody")
	row = X4MPPlayers.resolveWhisper("b hi")
	eq(row, nil, "an ambiguous prefix matches nobody")
end)

test("players: the Multiplayer screen shows the table while connected and redraws on a change", function()
	fresh()
	local renders = 0
	X4MPScreens.setRenderer({ name = "test", isOpen = function() return true end, close = function() end,
		show = function(model) renders = renders + 1 X4MPPlayersLastModel = model end })
	env.fire("x4mp.status", '{"v":1,"state":"ingame"}')
	X4MPScreens.open("main")
	local before = renders
	players({ { id = 1, name = "Alice", team = 1 } }, 1)
	eq(renders, before + 1, "a changed list redraws the main screen")
	local found = false
	for _, row in ipairs(X4MPPlayersLastModel.rows) do
		if row.type == "text" and row.text:find("Alice", 1, true) then found = true end
	end
	truthy(found, "the player row is in the model")
	X4MPScreens.go("join")
	before = renders
	players({ { id = 1, name = "Alice", team = 1, ping = 99 } }, 1)
	eq(renders, before, "the join form is never redrawn by a roster change")
	X4MPPlayersLastModel = nil
end)

test("players: the Multiplayer screen shows no table while not connected", function()
	fresh()
	players({ { id = 1, name = "Alice", team = 1 } }, 1)
	local model = X4MPScreens.buildModel()
	for _, row in ipairs(model.rows) do
		falsy(row.text and row.text:find("Alice", 1, true), "no player rows while disconnected")
	end
	connect()
	model = X4MPScreens.buildModel()
	local found = false
	for _, row in ipairs(model.rows) do
		if row.text and row.text:find("Alice", 1, true) then found = true end
	end
	truthy(found)
end)

test("players: a bad payload does not break the list", function()
	fresh()
	players({ { id = 1, name = "Alice", team = 1 } }, 1)
	env.fire("x4mp.players", '{"v":1,"self":1,"players":[{"id":"x"},5,{"id":2}],"events":[7]}')
	eq(#X4MPPlayers.list, 0)
	env.fire("x4mp.players", "not json")
	eq(#X4MPPlayers.list, 0)
end)
