local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local function bridge()
	env.loadAll({ "x4mp_bridge.lua" })
	return X4MPBridge
end

test("api present at load: ui_ready is raised once with the start-menu flag", function()
	env.installApi()
	env.startmenu = true
	local B = bridge()
	local sent = env.raisedNamed("x4mp.ui_ready")
	eq(#sent, 1)
	local p = B.json.decode(sent[1])
	eq(p.v, 1)
	eq(p.startmenu, true)
	env.fire("gfx_ok")
	env.fire("show")
	eq(#env.raisedNamed("x4mp.ui_ready"), 1, "no duplicate ui_ready")
end)

test("isStartMenu: the ffi symbol is bound once, the answer stays live", function()
	local B = bridge()
	local ffi = require("ffi")
	local cdefs = 0
	local realCdef = ffi.cdef
	ffi.cdef = function(...) cdefs = cdefs + 1 return realCdef(...) end
	env.startmenu = false
	eq(B.isStartMenu(), false)
	env.startmenu = true
	eq(B.isStartMenu(), true, "a cached binding must still read the live answer")
	eq(B.isStartMenu(), true)
	ffi.cdef = realCdef
	-- Session 8: the money poll asked this 100 times a second and the game died with 0xc00000fd; require("ffi") plus
	-- cdef on every call was pure overhead on top of that. One cdef for the whole session is the rule.
	eq(cdefs, 1, "ffi.cdef must run once, not once per call")
end)

test("api missing at load: queued verbs flush on gfx_ok once the api exists", function()
	local B = bridge()
	eq(#env.raised, 0)
	local ok, how = B.send("request_status", {})
	eq(ok, true)
	eq(how, "queued")
	B.send("extensions", { count = 1 })
	B.send("extensions", { count = 2 }) -- latest wins
	env.fire("gfx_ok") -- still no api: nothing happens
	eq(#env.raised, 0)
	env.installApi()
	env.fire("show")
	eq(#env.raisedNamed("x4mp.ui_ready"), 1)
	eq(#env.raisedNamed("x4mp.request_status"), 1)
	local ext = env.raisedNamed("x4mp.extensions")
	eq(#ext, 1)
	eq(B.json.decode(ext[1]).count, 2)
end)

test("join and disconnect are never queued without the api", function()
	local B = bridge()
	local ok, err = B.send("join", { address = "h:1", name = "n", password = "pw" })
	eq(ok, false)
	eq(err, "no_api")
	ok, err = B.send("disconnect", {})
	eq(ok, false)
	eq(err, "no_api")
	eq(next(B.pending), nil, "nothing queued")
	falsy(t.findString(B.pending, "pw"))
	falsy(env.debugText():find("pw", 1, true), "payload never logged")
end)

test("send: unknown verb refused, payload gets v=1, event name is x4mp.<verb>", function()
	env.installApi()
	local B = bridge()
	eq((B.send("bogus", {})), false)
	eq((B.send("disconnect")), true)
	local raised = env.raisedNamed("x4mp.disconnect")
	eq(#raised, 1)
	eq(raised[1], '{"v":1}')
end)

test("send: a throwing raise_event is reported, not propagated", function()
	_G.__X4NATIVE_API = { raise_event = function() error("boom") end }
	local B = bridge()
	local ok, err = B.send("disconnect", {})
	eq(ok, false)
	t.contains(err, "boom")
end)

test("topics: status, notify and error update the bridge state", function()
	local B = bridge()
	env.fire("x4mp.status", '{"v":1,"state":"connecting","server":"h:47780"}')
	eq(B.status.state, "connecting")
	env.fire("x4mp.notify", '{"v":1,"text":"hello","level":"info"}')
	eq(B.lastNotice.text, "hello")
	env.fire("x4mp.error", '{"v":1,"code":"E1","text":"bad"}')
	eq(B.lastError.code, "E1")
end)

test("topics: bad JSON is logged and survived; handler errors are isolated", function()
	local B = bridge()
	local seen = 0
	B.on("status", function() error("handler failure") end)
	B.on("status", function(p) seen = p.n end)
	env.fire("x4mp.status", "{not json")
	t.contains(env.debugText(), "bad payload")
	env.fire("x4mp.status", '{"state":"x","n":7}')
	eq(seen, 7)
	t.contains(env.debugText(), "handler raised")
	env.fire("x4mp.status", "null")
	env.fire("x4mp.status", nil)
end)

-- Topics the bridge only documents: the addon file that owns the topic registers the Lua event,
-- so the bridge alone cannot have it.  Keep the list honest: a topic added to B.TOPICS must be
-- either handled here or named with its owner below.
local ADDON_TOPICS = {
	appearance_state = "x4mp_menu.lua",
	onfoot_talk_in = "x4mp_onfoot.lua",
	wallet = "x4mp_hud.lua",
	presence = "x4mp_onfoot.lua",
	damage_probe = "x4mp_damage.lua",
}

local function uiSource(file)
	local f = assert(io.open(env.uiPath(file), "rb"))
	local text = f:read("*a")
	f:close()
	return text
end

test("B.on registers one Lua event per topic and supports new topics", function()
	local B = bridge()
	for _, topic in ipairs(B.TOPICS) do
		local owner = ADDON_TOPICS[topic]
		if owner then
			truthy(uiSource(owner):find('B.on("' .. topic .. '"', 1, true),
				"x4mp." .. topic .. " registered by " .. owner)
		else
			truthy(env.events["x4mp." .. topic], "x4mp." .. topic .. " registered")
		end
	end
	for topic in pairs(ADDON_TOPICS) do
		local listed = false
		for _, known in ipairs(B.TOPICS) do
			if known == topic then listed = true end
		end
		truthy(listed, topic .. " is still a documented topic")
	end
	local got
	B.on("custom", function(p) got = p.k end)
	env.fire("x4mp.custom", '{"k":"v"}')
	eq(got, "v")
end)

test("validSaveName strips extensions and refuses paths", function()
	local B = bridge()
	eq(B.validSaveName("x4mp_abc123"), "x4mp_abc123")
	eq(B.validSaveName("x4mp_abc123.xml.gz"), "x4mp_abc123")
	eq(B.validSaveName("x4mp_abc123.xml"), "x4mp_abc123")
	for _, bad in ipairs({ "", "../x", "a/b", "a\\b", "c:x", "a..b", "x\ny" }) do
		eq(B.validSaveName(bad), nil, bad)
	end
	eq(B.validSaveName(nil), nil)
	eq(B.validSaveName(5), nil)
end)

test("load_save: the normal topic only records the load (native raised the vanilla loadSave event); no LoadGame", function()
	local B = bridge()
	env.fire("x4mp.load_save", '{"v":1,"name":"x4mp_abc123.xml.gz"}')
	eq(#env.loaded, 0, "Lua must not load by itself: native already raised loadSave")
	eq(B.loadingSave, "x4mp_abc123")
end)

test("load_save: fallback calls LoadGame without the extension", function()
	bridge()
	env.fire("x4mp.load_save", '{"v":1,"name":"x4mp_abc123.xml.gz","fallback":true}')
	eq(#env.loaded, 1)
	eq(env.loaded[1], "x4mp_abc123")
end)

test("load_save: the fallback uses the delayed callback when Helper offers it", function()
	local delayed
	_G.Helper = { addDelayedOneTimeCallbackOnUpdate = function(fn, _, when) delayed = { fn = fn, when = when } end }
	_G.getElapsedTime = function() return 10 end
	bridge()
	env.fire("x4mp.load_save", '{"name":"x4mp_ok","fallback":true}')
	eq(#env.loaded, 0, "not loaded yet")
	truthy(delayed)
	truthy(math.abs(delayed.when - 10.1) < 1e-9)
	delayed.fn()
	eq(env.loaded[1], "x4mp_ok")
end)

test("load_save: bad names are refused", function()
	local B = bridge()
	env.fire("x4mp.load_save", '{"name":"../../evil","fallback":true}')
	env.fire("x4mp.load_save", '{"fallback":true}')
	eq(#env.loaded, 0)
	eq(B.lastError.code, "bad_save_name")
end)

-- M3-12: the takeover's HUD hint topic
test("hint: show keeps the hint and raises the HUD notification, hide clears it", function()
	local B = bridge()
	local notes = {}
	_G.AddUITriggeredEvent = function(a, b, c) notes[#notes + 1] = { a, b, c } end
	eq(B.hint, nil)
	env.fire("x4mp.hint", '{"v":1,"id":"takeover","show":true,"text":"Sit in the pilot seat to take over your ship"}')
	eq(B.hint.id, "takeover")
	eq(#notes, 1)
	eq(notes[1][1], "X4MP") eq(notes[1][2], "notify")
	t.contains(notes[1][3], "pilot seat")
	env.fire("x4mp.hint", '{"v":1,"id":"takeover","show":true,"text":"Sit in the pilot seat to take over your ship"}')
	eq(#notes, 2) -- native repeats the hint on purpose
	env.fire("x4mp.hint", '{"v":1,"id":"takeover","show":false}')
	eq(B.hint, nil)
	eq(#notes, 2)
	env.fire("x4mp.hint", "null")
	env.fire("x4mp.hint", '{"v":1,"show":true}') -- no text: kept, no notification
	eq(#notes, 2)
	eq(B.hint.show, true)
end)

test("takeover: the last stage is kept", function()
	local B = bridge()
	env.fire("x4mp.takeover", '{"v":1,"stage":"teleporting","net_id":9,"avatar":"400001","hint":true,"refusals":3}')
	eq(B.takeover.stage, "teleporting")
	eq(B.takeover.refusals, 3)
	env.fire("x4mp.takeover", '{"v":1,"stage":"done","net_id":9}')
	eq(B.takeover.stage, "done")
end)

test("session_pause: a save pauses once and unpauses only its own pause", function()
	local B = bridge()
	local paused, unpaused, notes = 0, 0, 0
	_G.Pause = function() paused = paused + 1 end
	_G.Unpause = function() unpaused = unpaused + 1 end
	_G.AddUITriggeredEvent = function() notes = notes + 1 end
	env.fire("x4mp.session_pause", '{"v":1,"paused":true,"reason":"Saving","phase":5,"game_paused":false}')
	eq(paused, 1)
	eq(unpaused, 0)
	eq(B.saving, true)
	truthy(B.pauseLease)
	env.fire("x4mp.session_pause", '{"v":1,"paused":true,"reason":"Saving","phase":5,"game_paused":false}')
	eq(paused, 1, "a repeated broadcast must not pause again")
	env.fire("x4mp.session_pause", '{"v":1,"paused":false,"reason":"","phase":5,"game_paused":false}')
	eq(unpaused, 1)
	eq(B.saving, false)
	eq(B.pauseLease, nil)
	eq(notes, 2, "saving and complete are announced once each")
end)

test("session_pause: a player already paused keeps the game paused", function()
	local B = bridge()
	local paused, unpaused = 0, 0
	_G.Pause = function() paused = paused + 1 end
	_G.Unpause = function() unpaused = unpaused + 1 end
	env.fire("x4mp.session_pause", '{"v":1,"paused":true,"reason":"Saving","phase":5,"game_paused":true}')
	eq(paused, 0, "the player's Esc pause is left alone")
	eq(B.pauseLease.wasPaused, true)
	env.fire("x4mp.session_pause", '{"v":1,"paused":false,"reason":"","phase":5,"game_paused":true}')
	eq(unpaused, 0, "the bridge did not pause, so it must not unpause")
	eq(B.pauseLease, nil)
end)

test("session_pause: other pause reasons are not a save lease", function()
	local B = bridge()
	local paused, unpaused = 0, 0
	_G.Pause = function() paused = paused + 1 end
	_G.Unpause = function() unpaused = unpaused + 1 end
	env.fire("x4mp.session_pause", '{"v":1,"paused":true,"reason":"Admin","phase":5,"game_paused":false}')
	eq(paused, 0)
	eq(B.pauseLease, nil)
	eq(B.saving, false)
	env.fire("x4mp.session_pause", '{"v":1,"paused":false,"reason":"","phase":5,"game_paused":false}')
	eq(unpaused, 0)
end)

test("session_pause: a disconnect releases only the pause the bridge created", function()
	local B = bridge()
	local paused, unpaused = 0, 0
	_G.Pause = function() paused = paused + 1 end
	_G.Unpause = function() unpaused = unpaused + 1 end
	env.fire("x4mp.session_pause", '{"v":1,"paused":true,"reason":"Saving","phase":5,"game_paused":false}')
	eq(paused, 1)
	env.fire("x4mp.status", '{"v":1,"state":"disconnected"}')
	eq(unpaused, 1)
	eq(B.pauseLease, nil)
	eq(B.saving, false)

	-- The next save sees a player who is (still) paused by choice, so the bridge must not touch the game.
	env.fire("x4mp.session_pause", '{"v":1,"paused":true,"reason":"Saving","phase":5,"game_paused":true}')
	eq(paused, 1)
	env.fire("x4mp.status", '{"v":1,"state":"disconnected"}')
	eq(unpaused, 1)
end)

test("loading the bridge twice does not register events twice", function()
	local B = bridge()
	local before = #env.events["x4mp.status"]
	env.load("x4mp_bridge.lua")
	eq(X4MPBridge, B)
	eq(#env.events["x4mp.status"], before)
end)
