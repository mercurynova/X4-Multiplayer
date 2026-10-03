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

test("B.on registers one Lua event per topic and supports new topics", function()
	local B = bridge()
	for _, topic in ipairs(B.TOPICS) do
		truthy(env.events["x4mp." .. topic], "x4mp." .. topic .. " registered")
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

test("loading the bridge twice does not register events twice", function()
	local B = bridge()
	local before = #env.events["x4mp.status"]
	env.load("x4mp_bridge.lua")
	eq(X4MPBridge, B)
	eq(#env.events["x4mp.status"], before)
end)
