-- M3-10: ui/x4mp_ghosts.lua (the Lua half of the client ghosts): native events -> MD controls (dress, velocity hint).
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local function setup()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua" })
	-- the engine function: a component travels as ConvertStringToLuaID(id)
	_G.ConvertStringToLuaID = function(s) return { component = s } end
	local triggers = {}
	_G.AddUITriggeredEvent = function(screen, control, value) triggers[#triggers + 1] = { screen = screen, control = control, value = value } end
	env.load("x4mp_ghosts.lua")
	return X4MPGhosts, triggers
end

test("dress: native event -> MD control with the component, the name and the minimum hull", function()
	local _, triggers = setup()
	env.fire("x4mp.ghost_dress", '{"v":1,"id":"169452728","name":"[MP] Pia","minhull":100}')
	eq(#triggers, 1)
	eq(triggers[1].screen, "X4MP_Ghosts")
	eq(triggers[1].control, "dress")
	eq(triggers[1].value[1].component, "169452728")
	eq(triggers[1].value[2], "[MP] Pia")
	eq(triggers[1].value[3], 100)
end)

test("dress: a damaged payload or a bad id is dropped without an error", function()
	local _, triggers = setup()
	env.fire("x4mp.ghost_dress", "not json")
	env.fire("x4mp.ghost_dress", '{"v":1,"id":5,"name":"x"}')   -- id must be a string
	env.fire("x4mp.ghost_dress", '{"v":1,"id":"7"}')            -- no name
	eq(#triggers, 0)
	_G.ConvertStringToLuaID = function() error("engine says no") end
	env.fire("x4mp.ghost_dress", '{"v":1,"id":"1","name":"[MP] A","minhull":100}')
	eq(#triggers, 0)
end)

test("velocity: one batch becomes one flat MD list", function()
	local _, triggers = setup()
	env.fire("x4mp.ghost_velocity", "11,100.00,0.00,-250.50;12,0.00,0.00,0.00")
	eq(#triggers, 1)
	eq(triggers[1].control, "velocity")
	local v = triggers[1].value
	eq(#v, 8)
	eq(v[1].component, "11")
	eq(v[2], 100)
	eq(v[4], -250.5)
	eq(v[5].component, "12")
	env.fire("x4mp.ghost_velocity", "junk;also,junk")
	env.fire("x4mp.ghost_velocity", "")
	eq(#triggers, 1)
end)

test("a missing AddUITriggeredEvent is survived", function()
	setup()
	_G.AddUITriggeredEvent = nil
	env.fire("x4mp.ghost_dress", '{"v":1,"id":"1","name":"[MP] A","minhull":100}')
	env.fire("x4mp.ghost_velocity", "1,0,0,0")
end)
