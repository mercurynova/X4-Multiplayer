-- M5-15: ui/x4mp_actions.lua (the Lua half of the economy actions shim) and md/x4mp_actions.xml.
local t, env = ... 
local test, eq, truthy = t.test, t.eq, t.truthy

local function setup(withTrigger)
	env.reset()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua" })
	env.triggers = {}
	if withTrigger ~= false then
		_G.AddUITriggeredEvent = function(screen, control, param)
			env.triggers[#env.triggers + 1] = { screen = screen, control = control, param = param }
			return true
		end
	end
	env.load("x4mp_actions.lua")
	return X4MPActions
end

local function triggerOf(control)
	for _, tr in ipairs(env.triggers or {}) do
		if tr.control == control then return tr end
	end
	return nil
end

test("actions: a cargo batch becomes one trigger with the same rows", function()
	local A = setup()
	env.fire("x4mp.actions", '{"v":1,"cargo":[[4660,"energycells",-500],[4661,"hullparts",12]]}')
	eq(#env.triggers, 1)
	local tr = triggerOf("apply")
	truthy(tr, "no apply trigger")
	eq(tr.screen, "X4MP_Actions")
	eq(#tr.param, 2)
	eq(tr.param[1][1], 4660)
	eq(tr.param[1][2], "energycells")
	eq(tr.param[1][3], -500)
	eq(tr.param[2][3], 12)
	eq(A.cargo, 1)
	eq(A.ops, 2)
	eq(A.bad, 0)
end)

test("actions: a malformed row is dropped before it reaches MD", function()
	local A = setup()
	env.fire("x4mp.actions", '{"v":1,"cargo":[["4660","energycells",5],[4660,"",5],[4660,"energycells","x"],[4660,"energycells",0]]}')
	local tr = triggerOf("apply")
	truthy(tr, "the one good row should still go")
	eq(#tr.param, 1)
	eq(tr.param[1][3], 5)
	eq(A.ops, 1)
end)

test("actions: nothing to apply raises no trigger at all", function()
	local A = setup()
	env.fire("x4mp.actions", '{"v":1}')
	env.fire("x4mp.actions", '{"v":1,"cargo":[],"money":[]}')
	env.fire("x4mp.actions", "not json")
	eq(#env.triggers, 0)
	eq(A.cargo, 0)
	eq(A.money, 0)
	-- A payload the bridge cannot decode never reaches the handler at all (B.dispatch refuses it and logs), so the file's own
	-- counter stays clean: the shim only counts shapes it was actually given.
	eq(A.bad, 0)
	truthy(env.debugText():find("bad payload", 1, true), "the bridge should log the undecodable topic payload")
end)

test("actions: money is its own trigger and refuses a self transfer", function()
	local A = setup()
	env.fire("x4mp.actions", '{"v":1,"money":[["argon","x4mp_team_1",2500],["argon","argon",10],["argon","x4mp_team_1",0]]}')
	eq(#env.triggers, 1)
	local tr = triggerOf("money")
	truthy(tr, "no money trigger")
	eq(#tr.param, 1)
	eq(tr.param[1][1], "argon")
	eq(tr.param[1][2], "x4mp_team_1")
	eq(tr.param[1][3], 2500)
	eq(A.money, 1)
	eq(A.cargo, 0)
end)

test("actions: without AddUITriggeredEvent the batch is skipped, never queued", function()
	local A = setup(false)
	env.fire("x4mp.actions", '{"v":1,"cargo":[[4660,"energycells",-5]]}')
	eq(#env.triggers, 0)
	eq(A.skipped, 1)
	eq(A.cargo, 0)
end)

test("actions: the MD half applies exactly, and mutates nothing else", function()
	setup()
	truthy(env.readFile(env.uiPath("../ui.xml")):find("ui/x4mp_actions.lua", 1, true), "ui.xml lists x4mp_actions.lua")
	local md = env.readFile(env.uiPath("../md/x4mp_actions.xml"))
	t.contains(md, "<add_cargo")
	t.contains(md, "<remove_cargo")
	t.contains(md, 'exact="$Op.{3}"')
	t.contains(md, 'exact="0 - $Op.{3}"')
	t.contains(md, "<transfer_money")
	t.contains(md, "screen=\"'X4MP_Actions'\"")
	-- M5-15/M5-16: the shim changes stock and money and nothing else. It never creates, moves, damages or destroys an object, and it
	-- never touches the player's ship (docs/mod-design.md 6.1).
	for _, bad in ipairs({
		"<destroy_object", "<create_object", "<set_object_hull", "<set_object_velocity",
		"<set_object_owner", "<set_faction_relation", "<teleport_object",
	}) do
		eq(md:find(bad, 1, true), nil)
	end
end)
