local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local function sampleList()
	return {
		{ id = "ego_dlc_boron", name = "Kingdom End", version = "900", enabled = true, egosoftextension = true, personal = false,
			ignored = "not copied", nested = { a = 1 } },
		{ id = "some_mod", name = "Some Mod", version = "1.2.3", enabled = false, error = "missing dependency",
			date = string.rep("d", 400) },
	}
end

local function loadWithList(list, modified)
	_G.GetExtensionList = function() return list end
	env.cfuncs.GetModifiedBasegameUIFilesExtensions = function() return modified or "" end
	env.loadAll({ "x4mp_bridge.lua", "x4mp_extensions.lua" })
	return X4MPBridge, X4MPExtensions
end

test("start menu / reloadui load: list is gathered, logged and sent as x4mp.extensions", function()
	env.installApi()
	env.startmenu = true
	local B = loadWithList(sampleList(), "some_mod;other_mod")
	local sent = env.raisedNamed("x4mp.extensions")
	eq(#sent, 1)
	local p = B.json.decode(sent[1])
	eq(p.v, 1)
	eq(p.source, "load")
	eq(p.startmenu, true)
	eq(p.count, 2)
	eq(p.modified_ui_files, "some_mod;other_mod")
	eq(p.list[1].id, "ego_dlc_boron")
	eq(p.list[1].version, "900")
	eq(p.list[1].enabled, true)
	eq(p.list[1].egosoftextension, true)
	eq(p.list[1].ignored, nil, "only known scalar fields are copied")
	eq(p.list[1].nested, nil)
	eq(p.list[2].enabled, false)
	eq(p.list[2].error, "missing dependency")
	eq(#p.list[2].date, 256, "long strings are capped")
	local log = env.debugText()
	t.contains(log, "extension id=ego_dlc_boron version=900 enabled=true")
	t.contains(log, "extension id=some_mod version=1.2.3 enabled=false")
	t.contains(log, "2 found")
end)

test("an empty list is sent as []", function()
	env.installApi()
	loadWithList({}, "")
	local sent = env.raisedNamed("x4mp.extensions")
	eq(#sent, 1)
	t.contains(sent[1], '"list":[]')
	t.contains(sent[1], '"count":0')
end)

test("sent before the native api exists: queued, flushed on gfx_ok", function()
	loadWithList(sampleList())
	eq(#env.raisedNamed("x4mp.extensions"), 0)
	env.installApi()
	env.fire("gfx_ok")
	eq(#env.raisedNamed("x4mp.extensions"), 1)
	env.fire("show")
	eq(#env.raisedNamed("x4mp.extensions"), 1, "not sent again")
end)

test("no list at load time: gathered once on the first gfx_ok / show", function()
	env.installApi()
	_G.GetExtensionList = nil
	env.loadAll({ "x4mp_bridge.lua", "x4mp_extensions.lua" })
	eq(#env.raisedNamed("x4mp.extensions"), 0)
	t.contains(env.debugText(), "extensions:")
	_G.GetExtensionList = sampleList
	env.fire("gfx_ok")
	local sent = env.raisedNamed("x4mp.extensions")
	eq(#sent, 1)
	eq(X4MPBridge.json.decode(sent[1]).source, "gfx_ok")
	env.fire("show")
	eq(#env.raisedNamed("x4mp.extensions"), 1)
end)

test("a throwing GetExtensionList is survived", function()
	env.installApi()
	_G.GetExtensionList = function() error("engine says no") end
	env.loadAll({ "x4mp_bridge.lua", "x4mp_extensions.lua" })
	eq(#env.raisedNamed("x4mp.extensions"), 0)
	t.contains(env.debugText(), "engine says no")
	falsy(X4MPExtensions.gathered)
end)

test("the list is capped", function()
	env.installApi()
	local list = {}
	for i = 1, 600 do list[i] = { id = "m" .. i, name = "n", version = "1", enabled = true } end
	local B = loadWithList(list)
	local p = B.json.decode(env.raisedNamed("x4mp.extensions")[1])
	eq(p.count, 512)
	eq(#p.list, 512)
	truthy(p.list[512])
end)

test("without the bridge the extension file refuses to run", function()
	env.load("x4mp_extensions.lua")
	eq(X4MPExtensions, nil)
	t.contains(env.debugText(), "x4mp_bridge.lua must load first")
end)
