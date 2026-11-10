-- T1.6: ui/x4mp_map.lua, the strategic map screen. What a test can pin without a game is the contract the native map hub relies
-- on: the verb and the topic are registered, the screen asks once and can refresh, a payload fills the rows, the owner filter
-- and the pagination are stable, and the screen is only offered inside a session.
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local function fresh()
	env.reset()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua", "x4mp_map.lua" })
end

local function rowById(model, id)
	for _, row in ipairs(model.rows) do if row.id == id then return row end end
	return nil
end

local function textsOf(model)
	local texts = {}
	for _, row in ipairs(model.rows) do if row.type == "text" then texts[#texts + 1] = row.text end end
	return table.concat(texts, "\n")
end

local function sectorLines(model)
	local n = 0
	for _, row in ipairs(model.rows) do
		if row.type == "text" and row.text:find("^Sector ") then n = n + 1 end
	end
	return n
end

local function screen()
	X4MPScreens.go("map")
	return X4MPScreens.buildModel()
end

local LIVE = '{"v":1,"version":7,"source":"live","sectors":[' ..
	'{"index":1,"macro":"sector_01","cluster":"c1","name":"Argon Prime","owner":"argon","x":1,"y":2,"z":3},' ..
	'{"index":2,"macro":"sector_02","cluster":"c1","name":"Xenon Hub","owner":"xenon","x":4,"y":5,"z":6},' ..
	'{"index":3,"macro":"sector_03","cluster":"c2","name":"Unowned Space","owner":"","x":7,"y":8,"z":9}],' ..
	'"links":[{"from":1,"to":2,"kind":"gate"},{"from":2,"to":3,"kind":"highway"}]}'
local CHECKPOINT = '{"v":1,"version":0,"source":"checkpoint","sectors":[' ..
	'{"index":1,"macro":"sector_01","cluster":"c1","name":"Argon Prime","owner":"","x":0,"y":0,"z":0}],"links":[]}'

test("map: the module registers the map request verb and the map topic", function()
	fresh()
	truthy(X4MPBridge.VERBS.map_request, "verb map_request is not allowed")
	local found = false
	for _, topic in ipairs(X4MPBridge.TOPICS) do if topic == "map" then found = true end end
	truthy(found, "topic map is not subscribed")
	eq(X4MPMap.snapshot, nil)
end)

test("map: the first build asks once, and Refresh asks again", function()
	fresh()
	local model = screen()
	eq(model.screen, "map")
	eq(model.title, "Strategic map")
	truthy(textsOf(model):find("No map yet", 1, true), textsOf(model))
	eq(#env.raisedNamed("x4mp.map_request"), 1, "the screen asks for the map once")
	local body = X4MPBridge.json.decode(env.raisedNamed("x4mp.map_request")[1])
	eq(body.v, 1)

	-- a second build with no answer does not spam the native half
	X4MPScreens.buildModel()
	eq(#env.raisedNamed("x4mp.map_request"), 1)
	rowById(model, "map_refresh").onClick()
	eq(#env.raisedNamed("x4mp.map_request"), 2, "Refresh always asks")
end)

test("map: a live payload fills the summary, the rows and the link count", function()
	fresh()
	env.fire("x4mp.map", LIVE)
	eq(X4MPMap.snapshot.version, 7)
	eq(X4MPMap.snapshot.source, "live")

	local model = screen()
	local text = textsOf(model)
	truthy(text:find("Sectors: 3, links: 2", 1, true), text)
	truthy(text:find("Map source: live, version 7", 1, true), text)
	truthy(text:find("Sector 1: Argon Prime, owner argon", 1, true), text)
	truthy(text:find("Sector 2: Xenon Hub, owner xenon", 1, true), text)
	truthy(text:find("Sector 3: Unowned Space, owner unowned", 1, true), text)
	truthy(text:find("Owner: all owners", 1, true), text)
	eq(sectorLines(model), 3)
	falsy(rowById(model, "map_prev").active)
	falsy(rowById(model, "map_next").active)
	truthy(rowById(model, "map_refresh").active)
	truthy(rowById(model, "back"))
end)

test("map: the owner filter cycles through every owner and back to all", function()
	fresh()
	env.fire("x4mp.map", LIVE)
	local model = screen()

	-- sorted owners: "" (unowned), argon, xenon
	rowById(model, "map_filter").onClick()
	eq(X4MPScreens.state.map.filter, 1)
	model = X4MPScreens.buildModel()
	truthy(textsOf(model):find("Owner: unowned", 1, true), textsOf(model))
	truthy(textsOf(model):find("Unowned Space", 1, true), textsOf(model))
	falsy(textsOf(model):find("Argon Prime", 1, true))
	eq(sectorLines(model), 1)

	rowById(model, "map_filter").onClick()
	model = X4MPScreens.buildModel()
	truthy(textsOf(model):find("Owner: argon", 1, true), textsOf(model))
	truthy(textsOf(model):find("Argon Prime", 1, true), textsOf(model))
	falsy(textsOf(model):find("Xenon Hub", 1, true))

	rowById(model, "map_filter").onClick()
	model = X4MPScreens.buildModel()
	truthy(textsOf(model):find("Owner: xenon", 1, true), textsOf(model))
	truthy(textsOf(model):find("Xenon Hub", 1, true), textsOf(model))

	rowById(model, "map_filter").onClick()
	model = X4MPScreens.buildModel()
	eq(X4MPScreens.state.map.filter, 0)
	truthy(textsOf(model):find("Owner: all owners", 1, true), textsOf(model))
	eq(sectorLines(model), 3)
end)

test("map: twenty sectors are two pages, and the page buttons stop at the ends", function()
	fresh()
	local sectors = X4MPBridge.json.array({})
	for i = 1, 20 do
		sectors[i] = { index = i, macro = "sector_" .. i, cluster = "c", name = "Sector " .. i, owner = "argon",
			x = 0, y = 0, z = 0 }
	end
	local payload = X4MPBridge.json.encode({ v = 1, version = 1, source = "live", sectors = sectors,
		links = X4MPBridge.json.array({}) })
	env.fire("x4mp.map", payload)

	local model = screen()
	eq(sectorLines(model), 16)
	truthy(textsOf(model):find("Page 1 of 2", 1, true), textsOf(model))
	falsy(rowById(model, "map_prev").active)
	truthy(rowById(model, "map_next").active)

	rowById(model, "map_next").onClick()
	model = X4MPScreens.buildModel()
	eq(sectorLines(model), 4)
	truthy(textsOf(model):find("Page 2 of 2", 1, true), textsOf(model))
	truthy(textsOf(model):find("Sector 20", 1, true), textsOf(model))
	truthy(rowById(model, "map_prev").active)
	falsy(rowById(model, "map_next").active)

	rowById(model, "map_prev").onClick()
	eq(X4MPScreens.state.map.page, 1)
end)

test("map: the checkpoint fallback shows the sectors as unowned", function()
	fresh()
	env.fire("x4mp.map", CHECKPOINT)
	local model = screen()
	local text = textsOf(model)
	truthy(text:find("Map source: checkpoint, version 0", 1, true), text)
	truthy(text:find("Sector 1: Argon Prime, owner unowned", 1, true), text)
end)

test("map: a map topic redraws only while the map screen is in front", function()
	fresh()
	env.fire("x4mp.map", LIVE)
	X4MPScreens.go("map")
	local renders = 0
	X4MPScreens.render = function() renders = renders + 1 end
	env.fire("x4mp.map", LIVE)
	eq(renders, 1)

	X4MPScreens.state.screen = "main"
	env.fire("x4mp.map", LIVE)
	eq(renders, 1, "a background map update must not steal the screen")
end)

test("map: the main screen offers the map only inside a session", function()
	fresh()
	X4MPBridge.status = nil
	local model = X4MPScreens.buildModel()
	falsy(rowById(model, "map"), "no map button without a session")

	X4MPBridge.status = { v = 1, state = "ingame" }
	model = X4MPScreens.buildModel()
	local row = rowById(model, "map")
	truthy(row, "the main screen has a map button in a session")
	truthy(row.active)
	row.onClick()
	eq(X4MPScreens.state.screen, "map")
	eq(X4MPScreens.buildModel().title, "Strategic map")
end)

test("map: every new text id exists in English and Russian", function()
	fresh()
	for id = 640, 658 do
		truthy(env.texts[id], "text " .. id .. " is missing in 0001-l044.xml")
	end
	local ru = env.readFile(env.uiPath("../t/0001-l007.xml"))
	for id = 640, 658 do
		truthy(ru:find('<t id="' .. id .. '">', 1, true), "text " .. id .. " is missing in 0001-l007.xml")
	end
	for body in ru:gmatch('<t id="%d+">(.-)</t>') do
		falsy(body:find("[%(%){}]"), "Russian text contains brackets: " .. body)
	end
end)
