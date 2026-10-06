-- M3-08: ui/x4mp_teams.lua carries the team faction plan from native to md/x4mp_teams.xml and the answer back.
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local function setup()
	env.reset()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua" })
	local log = { md = {} }
	_G.AddUITriggeredEvent = function(a, b, c) log.md[#log.md + 1] = { a, b, c } end
	env.load("x4mp_teams.lua")
	return log
end

local function lastRaised(name)
	local sent = env.raisedNamed(name)
	return sent[#sent] and X4MPBridge.json.decode(sent[#sent])
end

local PLAN = '{"v":1,"seq":3,"reason":"teams","own":1,"slots":[1,2],"rel":[[1,2,-1.0],[0,1,1.0],[0,2,-1.0]]}'

test("teams_apply becomes one flat list for MD: seq, counts, slots, then triples", function()
	local log = setup()
	env.fire("x4mp.teams_apply", PLAN)
	eq(#log.md, 1)
	eq(log.md[1][1], "X4MP_Teams")
	eq(log.md[1][2], "apply")
	local l = log.md[1][3]
	eq(#l, 3 + 2 + 3 * 3)
	eq(l[1], 3)
	eq(l[2], 2)
	eq(l[3], 3)
	eq(l[4], 1)
	eq(l[5], 2)
	eq(l[6], 1) eq(l[7], 2) eq(l[8], -1)
	eq(l[9], 0) eq(l[10], 1) eq(l[11], 1)
	eq(l[12], 0) eq(l[13], 2) eq(l[14], -1)
	eq(#env.raisedNamed("x4mp.teams_md"), 0, "no error was reported")
end)

test("skip_known (M3-28 diag.no_set_faction_known) appends ONE trailing 1; 0.99 is a normal relation value", function()
	local log = setup()
	env.fire("x4mp.teams_apply", '{"v":1,"seq":4,"reason":"teams","own":1,"slots":[1,2],"rel":[[0,1,0.99]],"skip_known":true}')
	local l = log.md[1][3]
	eq(#l, 3 + 2 + 3 + 1)
	eq(l[8], 0.99)
	eq(l[9], 1)
	env.fire("x4mp.teams_apply", '{"v":1,"seq":5,"reason":"teams","own":1,"slots":[1,2],"rel":[[0,1,1.0]]}')
	eq(#log.md[2][3], 3 + 2 + 3, "no trailing flag without the switch")
end)

test("a plan with no relations and one slot is valid", function()
	local log = setup()
	env.fire("x4mp.teams_apply", '{"v":1,"seq":1,"slots":[1],"rel":[]}')
	eq(#log.md, 1)
	eq(#log.md[1][3], 4)
end)

test("an unusable plan is answered with an E report and never reaches MD", function()
	local log = setup()
	local bad = {
		'{"v":1,"seq":2,"slots":[9],"rel":[]}',                  -- slot out of range
		'{"v":1,"seq":2,"slots":[0],"rel":[]}',                  -- player is not a slot
		'{"v":1,"seq":2,"slots":[1],"rel":[[1,2,3]]}',           -- value out of range
		'{"v":1,"seq":2,"slots":[1],"rel":[[1,"x",0.5]]}',       -- not a number
		'{"v":1,"seq":2,"slots":[1]}',                           -- no rel
		'{"v":1,"slots":[1],"rel":[]}',                          -- no seq
	}
	for _, p in ipairs(bad) do env.fire("x4mp.teams_apply", p) end
	eq(#log.md, 0)
	eq(#env.raisedNamed("x4mp.teams_md"), #bad)
	eq(lastRaised("x4mp.teams_md").data, "E;0;bad_payload")
end)

test("without AddUITriggeredEvent the failure is reported, not thrown", function()
	setup()
	_G.AddUITriggeredEvent = nil
	env.fire("x4mp.teams_apply", PLAN)
	eq(lastRaised("x4mp.teams_md").data, "E;3;no_md_event")
end)

test("an AddUITriggeredEvent that throws is reported", function()
	setup()
	_G.AddUITriggeredEvent = function() error("boom") end
	env.fire("x4mp.teams_apply", PLAN)
	eq(lastRaised("x4mp.teams_md").data, "E;3;md_event_failed")
end)

test("the MD report is forwarded to native unchanged; empty ones are dropped", function()
	setup()
	env.fire("x4mp.md_teams", "R;3;2;0;0;2;3")
	eq(lastRaised("x4mp.teams_md").data, "R;3;2;0;0;2;3")
	env.fire("x4mp.md_teams", "")
	eq(#env.raisedNamed("x4mp.teams_md"), 1)
end)

test("the Lua file is registered in ui.xml and the MD cue listens on the same screen and control", function()
	env.reset()
	truthy(env.readFile(env.uiPath("../ui.xml")):find('ui/x4mp_teams.lua', 1, true), "ui.xml lists x4mp_teams.lua")
	local md = env.readFile(env.uiPath("../md/x4mp_teams.xml"))
	truthy(md:find("screen=\"'X4MP_Teams'\"", 1, true))
	truthy(md:find("control=\"'apply'\"", 1, true))
	truthy(md:find("'x4mp.md_teams'", 1, true))
	falsy(md:find('faction="faction.player" locked', 1, true), "MD never locks or unlocks player")
end)
