-- M5-01: ui/x4mp_events.lua (the Lua half of the event capture ring) and md/x4mp_events.xml.
local t, env = ...
local test, eq, truthy = t.test, t.eq, t.truthy

local function setup(withApi)
	env.reset()
	if withApi ~= false then env.installApi() end
	env.loadAll({ "x4mp_bridge.lua" })
	env.load("x4mp_events.lua")
	return X4MPEvents
end

test("events: a line is forwarded unchanged as x4mp.event", function()
	local E = setup()
	env.fire("x4mp.event", "K;0x1b1d;ship_arg_s_01_macro;argon;0x1b20")
	local sent = env.raisedNamed("x4mp.event")
	eq(#sent, 1)
	t.contains(sent[1], '"data":"K;0x1b1d;ship_arg_s_01_macro;argon;0x1b20"')
	eq(E.received, 1)
	eq(E.sends, 1)
	eq(E.bad, 0)
end)

test("events: an empty or non-string line is refused, not forwarded", function()
	local E = setup()
	env.fire("x4mp.event", "")
	env.fire("x4mp.event", nil)
	env.fire("x4mp.event", 42)
	eq(#env.raisedNamed("x4mp.event"), 0)
	eq(E.received, 3)
	eq(E.bad, 3)
	eq(E.sends, 0)
end)

test("events: without the native API the line is skipped, never queued", function()
	local E = setup(false)
	env.fire("x4mp.event", "M;1234")
	eq(#env.raisedNamed("x4mp.event"), 0)
	eq(E.skipped, 1)
	eq(E.sends, 0)
end)

test("events: the bridge whitelists the event verb", function()
	setup()
	eq(X4MPBridge.VERBS.event, true)
	local ok, err = X4MPBridge.send("event", { data = "P;" })
	truthy(ok, "send returned " .. tostring(err))
	eq(#env.raisedNamed("x4mp.event"), 1)
end)

test("events: the MD half arms the seven hooks and mutates nothing", function()
	setup()
	truthy(env.readFile(env.uiPath("../ui.xml")):find("ui/x4mp_events.lua", 1, true), "ui.xml lists x4mp_events.lua")
	local md = env.readFile(env.uiPath("../md/x4mp_events.xml"))
	t.contains(md, "'x4mp.event'")
	t.contains(md, "'H;7'")
	for _, ev in ipairs({
		"event_player_owned_killed_object",
		"event_player_owned_destroyed",
		"event_player_trade_completed",
		"event_player_build_finished",
		"event_player_money_updated",
		"event_boarding_operation_started",
		"event_boarding_operation_removed",
	}) do
		t.contains(md, "<" .. ev)
	end
	-- The file observes and reports. It must never create, move, damage or destroy anything, and it never touches the player's
	-- ship: that is the M5 rule that the player's ship is the game's business (docs/mod-design.md 6.1).
	for _, bad in ipairs({
		"<destroy_object", "<create_object", "<set_object_hull", "<set_object_velocity",
		"<add_cargo", "<remove_cargo", "<set_object_owner",
	}) do
		eq(md:find(bad, 1, true), nil)
	end
end)
