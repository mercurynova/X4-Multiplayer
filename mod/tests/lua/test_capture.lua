-- M4-03: ui/x4mp_capture.lua (the Lua half of the authority's sector scan) and md/x4mp_capture.xml.
local t, env = ...
local test, eq, truthy = t.test, t.eq, t.truthy

local function setup()
	env.reset()
	env.installApi()
	local triggers = {}
	_G.AddUITriggeredEvent = function(screen, control, value) triggers[#triggers + 1] = { screen = screen, control = control, value = value } end
	env.loadAll({ "x4mp_bridge.lua" })
	env.load("x4mp_capture.lua")
	return X4MPCapture, triggers
end

test("capture: a scan chunk is forwarded unchanged as x4mp.capture_scan", function()
	local C = setup()
	env.fire("x4mp.capture_scan", "S;sec_a;0x18ead5|ship_arg_m_01_macro|argon;42|ship_x|")
	local sent = env.raisedNamed("x4mp.capture_scan")
	eq(#sent, 1)
	t.contains(sent[1], '"data":"S;sec_a;0x18ead5|ship_arg_m_01_macro|argon;42|ship_x|"')
	eq(C.sends, 1)
	-- An empty chunk is a broken answer, not a scan.
	env.fire("x4mp.capture_scan", "")
	eq(#env.raisedNamed("x4mp.capture_scan"), 1)
	eq(C.bad, 1)
end)

test("capture: a native request becomes the MD control 'scan' with the sector macro", function()
	local C, triggers = setup()
	env.fire("x4mp.capture_request", '{"v":1,"sector":"sec_a"}')
	eq(#triggers, 1)
	eq(triggers[1].screen, "X4MP_Capture")
	eq(triggers[1].control, "scan")
	eq(triggers[1].value[1], "sec_a")
	eq(C.requests, 1)
	-- A request without a sector macro cannot be answered by MD, so it is never raised.
	env.fire("x4mp.capture_request", '{"v":1}')
	env.fire("x4mp.capture_request", "garbage")
	eq(#triggers, 1)
end)

test("capture: a missing AddUITriggeredEvent is survived", function()
	setup()
	_G.AddUITriggeredEvent = nil
	env.fire("x4mp.capture_request", '{"v":1,"sector":"sec_b"}')
	eq(X4MPCapture.requests, 0)
	eq(X4MPCapture.skipped, 1)
end)

test("capture: the Lua file is registered in ui.xml and the MD cue matches it", function()
	truthy(env.readFile(env.uiPath("../ui.xml")):find("ui/x4mp_capture.lua", 1, true), "ui.xml lists x4mp_capture.lua")
	local md = env.readFile(env.uiPath("../md/x4mp_capture.xml"))
	t.contains(md, "screen=\"'X4MP_Capture'\" control=\"'scan'\"")
	t.contains(md, "'x4mp.capture_scan'")
	t.contains(md, "class=\"class.ship\"")
	t.contains(md, "space=\"$Sec\"")
	-- The closing count is what lets native send SectorComplete; without it the pass would never close.
	t.contains(md, "find_object")
end)
