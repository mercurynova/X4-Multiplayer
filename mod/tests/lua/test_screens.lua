local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local SECRET = "s3cr3t-PW!xyz"

--- loads bridge + screens and plugs in a recording renderer; returns S, B, renderer
local function setup(withApi)
	if withApi ~= false then env.installApi() end
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua" })
	local r = { open = false, shown = {} }
	r.name = "test"
	function r.isOpen() return r.open end
	function r.show(model) r.open = true r.shown[#r.shown + 1] = model r.last = model end
	function r.close() r.open = false end
	X4MPScreens.setRenderer(r)
	return X4MPScreens, X4MPBridge, r
end

local function rowById(model, id)
	for _, row in ipairs(model.rows) do
		if row.id == id then return row end
	end
	return nil
end

local function fillJoin(S, address, name, password)
	S.initState()
	S.state.address, S.state.name, S.state.password = address, name, password
end

------------------------------------------------------------------------------
-- validation
------------------------------------------------------------------------------
test("parseAddress: accepted forms", function()
	local S = setup()
	local function ok(text, host, port)
		local h, p = S.parseAddress(text)
		eq(h, host, text)
		eq(p, port, text)
	end
	ok("localhost", "localhost", 47780)
	ok("  example.com  ", "example.com", 47780)
	ok("host.example.com:5000", "host.example.com", 5000)
	ok("10.0.0.5", "10.0.0.5", 47780)
	ok("10.0.0.5:65535", "10.0.0.5", 65535)
	ok("my-host_1:1", "my-host_1", 1)
	ok("[::1]", "::1", 47780)
	ok("[fe80::1]:6000", "fe80::1", 6000)
end)

test("parseAddress: rejected forms", function()
	local S = setup()
	for _, bad in ipairs({ "", "   ", ":47780", "host:0", "host:65536", "host:abc", "ho st", "a..b", ".host", "host.", "-host",
		"host:1:2", "[::1", "[::1]:x", "[localhost]", "[::1]x", "ho/st", string.rep("a", 300) }) do
		local h, err = S.parseAddress(bad)
		eq(h, nil, "'" .. bad:sub(1, 20) .. "' must be rejected")
		eq(err, "bad_address")
	end
	eq(S.parseAddress(nil), nil)
end)

test("normalizeAddress always carries a port", function()
	local S = setup()
	eq(S.normalizeAddress("example.com"), "example.com:47780")
	eq(S.normalizeAddress("example.com:5"), "example.com:5")
	eq(S.normalizeAddress("[::1]"), "[::1]:47780")
	eq(S.normalizeAddress("nope nope"), nil)
end)

test("validateJoin: name rules count characters, not bytes", function()
	local S = setup()
	local function v(name)
		local ok, errs = S.validateJoin({ address = "h", name = name })
		return ok, errs.name
	end
	eq((v("A")), true)
	eq(select(2, v("")), "name_empty")
	eq(select(2, v("   ")), "name_empty")
	eq(select(2, v("\1\2")), "name_empty")
	eq((v(string.rep("a", 24))), true)
	eq(select(2, v(string.rep("a", 25))), "name_long")
	eq((v(string.rep("\195\169", 24))), true, "24 two-byte characters fit")
	eq(select(2, v(string.rep("\195\169", 25))), "name_long")
	eq(S.sanitizeName("  Al\tice\n "), "Alice")
end)

test("validateJoin: reports every failing field", function()
	local S = setup()
	local ok, errs = S.validateJoin({ address = "bad addr", name = "" })
	eq(ok, false)
	eq(errs.address, "bad_address")
	eq(errs.name, "name_empty")
	ok, errs = S.validateJoin({ address = "host:1", name = "Bob" })
	eq(ok, true)
	eq(next(errs), nil)
end)

------------------------------------------------------------------------------
-- join flow and password handling
------------------------------------------------------------------------------
test("submitJoin sends x4mp.join with the right JSON and clears the password", function()
	local S, B = setup()
	fillJoin(S, "  play.example.com  ", "  Alice  ", SECRET)
	local sent = S.submitJoin()
	eq(sent, true)
	local raised = env.raisedNamed("x4mp.join")
	eq(#raised, 1)
	local p = B.json.decode(raised[1])
	eq(p.v, 1)
	eq(p.address, "play.example.com:47780")
	eq(p.name, "Alice")
	eq(p.password, SECRET)
	eq(p.team, "auto")
	eq(S.state.password, "", "password cleared right after the send")
	eq(S.state.screen, "status")
	eq(B.status.state, "connecting")
end)

test("the password never reaches __X4MP_USER, the log, or the next model", function()
	local S, _, r = setup()
	fillJoin(S, "play.example.com:5000", "Alice", SECRET)
	S.open("join")
	S.submitJoin()
	truthy(__X4MP_USER, "saved variable created")
	eq(__X4MP_USER.lastAddress, "play.example.com:5000")
	eq(__X4MP_USER.lastName, "Alice")
	eq(__X4MP_USER.version, 1)
	eq(t.findString(__X4MP_USER, SECRET), nil, "password not in __X4MP_USER")
	eq(t.findString(S.state, SECRET), nil, "password not in the screen state")
	eq(t.findString(r.last, SECRET), nil, "password not in the model drawn after the send")
	falsy(env.debugText():find(SECRET, 1, true), "password not in the log")
	for k in pairs(__X4MP_USER) do
		falsy(tostring(k):lower():find("pass", 1, true), "no password-like field: " .. tostring(k))
	end
	for _, name in ipairs({ "lastAddress", "lastName", "version" }) do truthy(__X4MP_USER[name] ~= nil, name) end
	local n = 0
	for _ in pairs(__X4MP_USER) do n = n + 1 end
	eq(n, 3, "exactly version, lastAddress, lastName")
end)

test("a model built before the send holds the password only in the hidden edit row", function()
	local S = setup()
	fillJoin(S, "h", "n", SECRET)
	S.state.screen = "join"
	local model = S.buildModel()
	local pw = rowById(model, "password")
	eq(pw.hidden, true, "password edit box is textHidden")
	eq(pw.value, SECRET)
	for _, row in ipairs(model.rows) do
		if row.id ~= "password" then eq(t.findString(row, SECRET), nil, "row " .. tostring(row.id)) end
	end
	falsy(rowById(model, "address").hidden)
	falsy(rowById(model, "name").hidden)
end)

test("invalid form is not sent and keeps the password for correction", function()
	local S, _, r = setup()
	fillJoin(S, "bad address", "", SECRET)
	S.open("join")
	local sent, why = S.submitJoin()
	eq(sent, false)
	eq(why, "invalid")
	eq(#env.raised, 1, "only ui_ready was raised")
	eq(S.state.errors.address, "bad_address")
	eq(S.state.errors.name, "name_empty")
	local texts = {}
	for _, row in ipairs(r.last.rows) do
		if row.type == "text" and row.tone == "error" then texts[#texts + 1] = row.text end
	end
	eq(#texts, 2, "two inline errors shown")
	eq(S.state.password, SECRET)
	eq(__X4MP_USER == nil or __X4MP_USER.lastAddress == nil, true, "nothing saved")
end)

test("failed send (no native api): password still cleared, nothing saved, notice shown", function()
	local S, _, r = setup(false)
	fillJoin(S, "play.example.com", "Alice", SECRET)
	S.open("join")
	local sent, err = S.submitJoin()
	eq(sent, false)
	eq(err, "no_api")
	eq(S.state.password, "")
	eq(__X4MP_USER == nil or __X4MP_USER.lastAddress == nil, true)
	truthy(S.state.notice)
	eq(S.state.screen, "join")
	eq(t.findString(r.last, SECRET), nil)
	falsy(env.debugText():find(SECRET, 1, true))
end)

test("closing the window clears the password", function()
	local S = setup()
	fillJoin(S, "h", "n", SECRET)
	S.onClosed()
	eq(S.state.password, "")
end)

test("edit callbacks only store values", function()
	local S = setup()
	S.initState()
	S.state.screen = "join"
	local model = S.buildModel()
	rowById(model, "address").onChange("typed.example.com")
	rowById(model, "name").onChange("Zed")
	rowById(model, "password").onChange(SECRET)
	eq(S.state.address, "typed.example.com")
	eq(S.state.name, "Zed")
	eq(S.state.password, SECRET)
	eq(#env.raisedNamed("x4mp.join"), 0)
end)

test("__X4MP_USER: last address and name prefill; secret-looking fields are scrubbed", function()
	_G.__X4MP_USER = { version = 1, lastAddress = "old.example.com:1", lastName = "Old", password = "leak", Token = "t", mySecret = "s" }
	local S = setup()
	local st = S.initState()
	eq(st.address, "old.example.com:1")
	eq(st.name, "Old")
	eq(st.password, "")
	eq(__X4MP_USER.password, nil)
	eq(__X4MP_USER.Token, nil)
	eq(__X4MP_USER.mySecret, nil)
	eq(__X4MP_USER.lastName, "Old")
end)

test("a non-table __X4MP_USER is replaced", function()
	_G.__X4MP_USER = "garbage"
	local S = setup()
	eq(type(S.user()), "table")
end)

------------------------------------------------------------------------------
-- screens
------------------------------------------------------------------------------
test("main screen: join offered when disconnected, status and disconnect when connected", function()
	local S, B = setup()
	local m = S.buildModel()
	eq(m.screen, "main")
	truthy(rowById(m, "join"))
	falsy(rowById(m, "disconnect"))
	env.fire("x4mp.status", '{"state":"ingame","server":"h:47780","role":"client","team":2,"ping_ms":20,"players":3}')
	eq(B.status.state, "ingame")
	m = S.buildModel()
	falsy(rowById(m, "join"))
	truthy(rowById(m, "status"))
	rowById(m, "disconnect").onClick()
	eq(#env.raisedNamed("x4mp.disconnect"), 1)
end)

test("join screen: Connect is inactive while a connection is active; team is a placeholder", function()
	local S = setup()
	S.initState()
	S.state.screen = "join"
	truthy(rowById(S.buildModel(), "connect").active)
	env.fire("x4mp.status", '{"state":"connecting"}')
	falsy(rowById(S.buildModel(), "connect").active)
	for _, id in ipairs({ "address", "name", "password" }) do truthy(rowById(S.buildModel(), id), id) end
end)

test("status screen shows state, server details and errors", function()
	local S, _, r = setup()
	S.open("status")
	r.last = nil
	env.fire("x4mp.status", '{"state":"downloading","progress":0.5,"server":"h:47780","role":"client","team_name":"Team 2","ping_ms":12,"players":4}')
	local all = {}
	for _, row in ipairs(r.last.rows) do all[#all + 1] = tostring(row.text) end
	local joined = table.concat(all, "|")
	t.contains(joined, "50%")
	t.contains(joined, "h:47780")
	t.contains(joined, "Team 2")
	t.contains(joined, "12")
	env.fire("x4mp.status", '{"state":"rejected","reject":"build","detail":"Build mismatch"}')
	local first = r.last.rows[1]
	eq(first.tone, "error")
	t.contains(first.text, "Build mismatch")
	env.fire("x4mp.error", '{"code":"E9","text":"Something broke"}')
	local found = false
	for _, row in ipairs(r.last.rows) do
		if row.text == "Something broke" then found = true end
	end
	truthy(found, "x4mp.error text on the status screen")
end)

test("rejected: each reject token shows its own localized text with the server detail", function()
	local S, _, r = setup()
	S.open("status")
	local expect = { build = "X4 build", mod = "mods do not match", auth = "password is wrong", full = "is full",
		banned = "banned", name = "already in use" }
	for token, needle in pairs(expect) do
		env.fire("x4mp.status", '{"state":"rejected","reject":"' .. token .. '","detail":"DETAIL-' .. token .. '"}')
		local first = r.last.rows[1]
		eq(first.tone, "error")
		t.contains(first.text, needle, token)
		t.contains(first.text, "DETAIL-" .. token, token)
	end
	env.fire("x4mp.status", '{"state":"rejected","reject":"other","detail":"Kicked by admin"}')
	t.contains(r.last.rows[1].text, "Rejected by the server: Kicked by admin")
	env.fire("x4mp.status", '{"state":"rejected","reject":"build"}')
	falsy(r.last.rows[1].text:find("%s$"), "no trailing space without detail")
end)

test("notify topic becomes a notice on the open screen", function()
	local S, _, r = setup()
	S.open("join")
	env.fire("x4mp.notify", '{"text":"Server restarting"}')
	local found = false
	for _, row in ipairs(r.last.rows) do
		if row.text == "Server restarting" then found = true end
	end
	truthy(found)
end)

test("open: unknown screen names fall back to main; no renderer is reported", function()
	local S, _, r = setup()
	eq(S.open("nonsense"), true)
	eq(r.last.screen, "main")
	eq(S.open("  JOIN "), true)
	eq(r.last.screen, "join")
	S.setRenderer(nil)
	eq(S.open("main"), false)
end)

test("open asks the bridge to retry the api (ui_ready after a late api)", function()
	local S = setup(false)
	env.installApi()
	S.open("main")
	eq(#env.raisedNamed("x4mp.ui_ready"), 1)
end)

test("entry points: x4mp.open event and the /x4mp chat command", function()
	local S, _, r = setup()
	env.fire("x4mp.open", '{"screen":"join"}')
	eq(r.last.screen, "join")
	ExecuteDebugCommand("x4mp", "status")
	eq(r.last.screen, "status")
	eq(#env.commands, 0, "our command is not passed on")
	ExecuteDebugCommand("other", "x")
	eq(#env.commands, 1, "other commands still reach the previous handler")
	eq(env.commands[1][1], "other")
	ExecuteDebugCommand("x4mp", nil)
	eq(r.last.screen, "main")
	S.close()
end)

test("the chat command wrapper is installed once", function()
	setup()
	local wrapper = ExecuteDebugCommand
	env.load("x4mp_menu.lua") -- already loaded: no-op
	eq(ExecuteDebugCommand, wrapper)
end)

test("repeated identical status updates do not redraw the join form", function()
	local S, _, r = setup()
	S.open("join")
	env.fire("x4mp.status", '{"state":"disconnected"}')
	local n = #r.shown
	env.fire("x4mp.status", '{"state":"disconnected"}')
	env.fire("x4mp.status", '{"state":"disconnected","ping_ms":5}')
	eq(#r.shown, n, "no redraw while the state is unchanged")
	env.fire("x4mp.status", '{"state":"connecting"}')
	eq(#r.shown, n + 1, "redraw on a state change")
end)

test("T falls back to a visible placeholder for missing texts", function()
	local S = setup()
	eq(S.T(99999), "[92000,99999]")
	eq(S.T(15, "abc"), "Last server: abc")
end)
