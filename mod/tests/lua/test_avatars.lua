-- M3-11: ui/x4mp_avatars.lua carries the avatar requests from native to md/x4mp_avatars.xml and the answers back.
local t, env = ...
local test, eq, truthy = t.test, t.eq, t.truthy

local function setup()
	env.reset()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua" })
	local log = { md = {} }
	_G.AddUITriggeredEvent = function(a, b, c) log.md[#log.md + 1] = { a, b, c } end
	_G.ConvertStringToLuaID = function(s) return "ID:" .. s end
	env.load("x4mp_avatars.lua")
	return log
end

local function lastRaised(name)
	local sent = env.raisedNamed(name)
	return sent[#sent] and X4MPBridge.json.decode(sent[#sent])
end

test("safepos becomes one list for MD with the sector as a Lua id", function()
	local log = setup()
	env.fire("x4mp.avatars_safepos", '{"v":1,"seq":4,"sector":"123456789012","x":10.5,"y":-3,"z":2000,"radius":150}')
	eq(#log.md, 1)
	eq(log.md[1][1], "X4MP_Avatars")
	eq(log.md[1][2], "safepos")
	local l = log.md[1][3]
	eq(l[1], 4)
	eq(l[2], "ID:123456789012")
	eq(l[3], 10.5) eq(l[4], -3) eq(l[5], 2000) eq(l[6], 150)
	eq(#env.raisedNamed("x4mp.avatars_md"), 0)
end)

test("an unusable safepos request is answered with ok = 0 at once and never reaches MD", function()
	local log = setup()
	env.fire("x4mp.avatars_safepos", '{"v":1,"seq":4,"sector":"","x":1,"y":2,"z":3,"radius":150}')
	env.fire("x4mp.avatars_safepos", '{"v":1,"seq":5,"sector":"77","x":"a","y":2,"z":3,"radius":150}')
	eq(#log.md, 0)
	local sent = env.raisedNamed("x4mp.avatars_md")
	eq(#sent, 2)
	eq(X4MPBridge.json.decode(sent[1]).data, "P;4;0")
	eq(X4MPBridge.json.decode(sent[2]).data, "P;5;0")
end)

test("dress: seq, object lua id, name, min hull, loadout, basic flag", function()
	local log = setup()
	env.fire("x4mp.avatars_dress", '{"v":1,"seq":7,"id":"5001","name":"[MP] Alice","min_hull":100,"macro":"m","loadout":"","basic":true}')
	env.fire("x4mp.avatars_dress", '{"v":1,"seq":8,"id":"5002","name":"[MP] Bob","min_hull":100,"macro":"m","loadout":"scenario_basic_fighter","basic":false}')
	eq(#log.md, 2)
	eq(log.md[1][2], "dress")
	local a, b = log.md[1][3], log.md[2][3]
	eq(a[1], 7) eq(a[2], "ID:5001") eq(a[3], "[MP] Alice") eq(a[4], 100) eq(a[5], "") eq(a[6], 1)
	eq(b[1], 8) eq(b[5], "scenario_basic_fighter") eq(b[6], 0)
end)

test("dress without a usable id is answered with ok = 0", function()
	local log = setup()
	_G.ConvertStringToLuaID = function() return nil end
	env.fire("x4mp.avatars_dress", '{"v":1,"seq":9,"id":"5001","name":"x","min_hull":100}')
	eq(#log.md, 0)
	eq(lastRaised("x4mp.avatars_md").data, "D;9;0;bad_request")
end)

test("velocity hints become one flat list: n, then id, vx, vy, vz per ship; bad entries are skipped", function()
	local log = setup()
	env.fire("x4mp.avatars_vel", '{"v":1,"h":[["5001",1,2,3],["5002",-4.5,0,9],["",1,1,1],["5003","x",1,1]]}')
	eq(#log.md, 1)
	eq(log.md[1][2], "velocity")
	local l = log.md[1][3]
	eq(l[1], 2)
	eq(#l, 1 + 2 * 4)
	eq(l[2], "ID:5001") eq(l[3], 1) eq(l[4], 2) eq(l[5], 3)
	eq(l[6], "ID:5002") eq(l[7], -4.5)
	-- nothing usable = no event
	env.fire("x4mp.avatars_vel", '{"v":1,"h":[]}')
	eq(#log.md, 1)
end)

test("a failing AddUITriggeredEvent is reported for the requests that expect an answer", function()
	setup()
	_G.AddUITriggeredEvent = function() error("boom") end
	env.fire("x4mp.avatars_safepos", '{"v":1,"seq":4,"sector":"1","x":1,"y":2,"z":3,"radius":150}')
	eq(lastRaised("x4mp.avatars_md").data, "P;4;0")
	env.fire("x4mp.avatars_dress", '{"v":1,"seq":5,"id":"1","name":"n","min_hull":100}')
	eq(lastRaised("x4mp.avatars_md").data, "D;5;0;md_event_failed")
end)

test("the MD answer is forwarded to native unchanged; empty ones are dropped", function()
	setup()
	env.fire("x4mp.md_avatars", "P;4;1;1.5;2;3")
	eq(lastRaised("x4mp.avatars_md").data, "P;4;1;1.5;2;3")
	local n = #env.raisedNamed("x4mp.avatars_md")
	env.fire("x4mp.md_avatars", "")
	eq(#env.raisedNamed("x4mp.avatars_md"), n)
	truthy(true)
end)
