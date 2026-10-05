-- M3-23: ui/x4mp_diag.lua (the Lua half of the knowledge probe): native ask -> MD control, MD answer -> native verb, the chat command.
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local function setup()
	env.reset()
	env.installApi()
	local triggers = {}
	_G.AddUITriggeredEvent = function(screen, control, value) triggers[#triggers + 1] = { screen = screen, control = control, value = value } end
	env.loadAll({ "x4mp_bridge.lua" })
	env.load("x4mp_diag.lua")
	return X4MPDiag, triggers
end

test("diag: native ask becomes the MD probe control with the sequence number", function()
	local _, triggers = setup()
	env.fire("x4mp.knowledge_ask", '{"v":1,"seq":7}')
	eq(#triggers, 1)
	eq(triggers[1].screen, "X4MP_Diag")
	eq(triggers[1].control, "probe")
	eq(triggers[1].value, 7)
	env.fire("x4mp.knowledge_ask", "not json")
	env.fire("x4mp.knowledge_ask", '{"v":1}')
	eq(#triggers, 1)
end)

test("diag: the MD answer is forwarded unchanged as x4mp.knowledge_md", function()
	setup()
	env.fire("x4mp.md_knowledge", "K;7;100.5;3;7;2;4;5;9;1;6;01_001=1,07_001=0,14_001=-")
	local sent = env.raisedNamed("x4mp.knowledge_md")
	eq(#sent, 1)
	t.contains(sent[1], '"data":"K;7;100.5;3;7;2;4;5;9;1;6;01_001=1,07_001=0,14_001=-"')
	env.fire("x4mp.md_knowledge", "")
	eq(#env.raisedNamed("x4mp.knowledge_md"), 1)
end)

test("diag: requestKnowledge sends the chat command verb; a missing AddUITriggeredEvent is survived", function()
	local D = setup()
	truthy(D.requestKnowledge())
	eq(#env.raisedNamed("x4mp.knowledge_cmd"), 1)
	_G.AddUITriggeredEvent = nil
	env.fire("x4mp.knowledge_ask", '{"v":1,"seq":1}')
end)

test("diag: the Lua file is registered in ui.xml and the MD cue listens on the same screen and control", function()
	truthy(env.readFile(env.uiPath("../ui.xml")):find("ui/x4mp_diag.lua", 1, true), "ui.xml lists x4mp_diag.lua")
	local md = env.readFile(env.uiPath("../md/x4mp_diag.xml"))
	t.contains(md, "screen=\"'X4MP_Diag'\" control=\"'probe'\"")
	t.contains(md, "'x4mp.md_knowledge'")
end)
