-- M2-10: ui/x4mp_saves.lua (wrapper chaining, unwrap only if ours, idempotence, flag set from native on every load).
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local log -- per-test record of what the stubs saw

--- A fresh Lua state: vanilla globals, the bridge, the stubs; the x4mp_saves.lua file is NOT loaded yet.
local function fresh(opts)
	opts = opts or {}
	env.reset()
	if opts.api ~= false then env.installApi() end
	env.loadAll({ "x4mp_bridge.lua" })
	log = { saved = {}, md = {}, possibleCalls = 0 }
	_G.X4MPSaves = nil
	_G.SaveGame = function(...) log.saved[#log.saved + 1] = { ... } return "saved" end
	_G.IsSavingPossible = function(...) log.possibleCalls = log.possibleCalls + 1 return true end
	_G.AddUITriggeredEvent = function(screen, control, value) log.md[#log.md + 1] = { screen, control, value } end
	_G.Menus = nil
end

local function load() env.load("x4mp_saves.lua") end

local function lastMd() return log.md[#log.md] end

local function pushBlock(on) env.fire("x4mp.saves", on and '{"v":1,"block":true}' or '{"v":1,"block":false}') end

local function statusPayload()
	local sent = env.raisedNamed("x4mp.saves_status")
	if #sent == 0 then return nil end
	return X4MPBridge.json.decode(sent[#sent])
end

test("unblocked: the wrappers are transparent (arguments and results pass through)", function()
	fresh()
	local origSave, origPossible = SaveGame, IsSavingPossible
	load()
	truthy(SaveGame ~= origSave, "SaveGame is wrapped")
	truthy(IsSavingPossible ~= origPossible, "IsSavingPossible is wrapped")
	eq(SaveGame("slot", "name"), "saved")
	eq(log.saved[1][1], "slot")
	eq(log.saved[1][2], "name")
	eq(IsSavingPossible(true), true)
end)

test("blocked by native: SaveGame is swallowed, IsSavingPossible is false, the MD flag is set", function()
	fresh()
	load()
	pushBlock(true)
	eq(SaveGame("slot", "n"), nil)
	eq(#log.saved, 0, "the real SaveGame was not called")
	eq(IsSavingPossible(), false)
	eq(lastMd()[1], "X4MP_Saves")
	eq(lastMd()[2], "noSave")
	eq(lastMd()[3], "1")
	pushBlock(false)
	eq(SaveGame("slot", "n"), "saved")
	eq(IsSavingPossible(), true)
	eq(lastMd()[3], "0")
end)

test("chaining: a wrapper installed BEFORE us still runs; one installed AFTER us still sees our block", function()
	fresh()
	local before = {}
	local vanilla = SaveGame
	_G.SaveGame = function(...) before[#before + 1] = "A" return vanilla(...) end -- somebody else's wrapper
	load()
	local after = {}
	local ours = SaveGame
	_G.SaveGame = function(...) after[#after + 1] = "C" return ours(...) end -- wrapped on top of us
	SaveGame("s", "n")
	eq(#after, 1)
	eq(#before, 1)
	eq(#log.saved, 1, "unblocked: whole chain reaches the real function")
	pushBlock(true)
	SaveGame("s", "n")
	eq(#after, 2, "the outer wrapper still ran")
	eq(#before, 1, "blocked before the inner chain")
	eq(#log.saved, 1)
end)

test("unwrap only if ours: uninstall restores the previous function when we are on top", function()
	fresh()
	local origSave, origPossible, origDebug = SaveGame, IsSavingPossible, ExecuteDebugCommand
	load()
	local res = X4MPSaves.uninstall()
	eq(res.SaveGame, "removed")
	eq(SaveGame, origSave)
	eq(IsSavingPossible, origPossible)
	eq(ExecuteDebugCommand, origDebug)
	pushBlock(true) -- flag set but nothing is wrapped any more: saves work
	eq(SaveGame("s", "n"), "saved")
end)

test("unwrap only if ours: when somebody wrapped on top, uninstall leaves the chain alone and turns into a pass-through", function()
	fresh()
	load()
	local ours = SaveGame
	local mine = 0
	_G.SaveGame = function(...) mine = mine + 1 return ours(...) end
	local theirs = SaveGame
	pushBlock(true)
	local res = X4MPSaves.uninstall()
	eq(res.SaveGame, "left_in_place")
	eq(SaveGame, theirs, "the other mod's wrapper was not touched")
	eq(SaveGame("s", "n"), "saved", "our wrapper no longer blocks")
	eq(mine, 1, "their wrapper still runs")
	eq(#log.saved, 1)
end)

test("idempotent: loading the file twice in one Lua state does not stack wrappers", function()
	fresh()
	load()
	local w1, w2, w3 = SaveGame, IsSavingPossible, ExecuteDebugCommand
	load()
	eq(SaveGame, w1)
	eq(IsSavingPossible, w2)
	eq(ExecuteDebugCommand, w3)
	SaveGame("s", "n")
	eq(#log.saved, 1)
	eq(log.possibleCalls, 0)
	IsSavingPossible()
	eq(log.possibleCalls, 1, "the real function runs once per call")
	pushBlock(true)
	load() -- a reload with the block on keeps it (same state)
	eq(IsSavingPossible(), false)
	eq(#env.events["x4mp.saves"], 1, "the bridge handler is registered once")
end)

test("flag comes from native on every load: a fresh Lua state starts unblocked, native pushes it again", function()
	fresh()
	load()
	pushBlock(true)
	eq(IsSavingPossible(), false)
	-- save load / /reloadui: everything in Lua is rebuilt, the in-memory flag is gone
	fresh()
	load()
	eq(IsSavingPossible(), true, "new state: not blocked until native says so")
	eq(lastMd()[3], "0", "stale MD flag (it lives in the save game) is cleared at load")
	pushBlock(true) -- the push native sends after ui_ready
	eq(IsSavingPossible(), false)
	eq(lastMd()[3], "1")
	local st = statusPayload()
	eq(st.blocking, true, "the status report carries the flag for the self-test")
end)

test("status report: what is wrapped, sent at load and after every push", function()
	fresh()
	load()
	local st = statusPayload()
	eq(st.v, 1)
	eq(st.save_game, true)
	eq(st.is_saving_possible, true)
	eq(st.blocking, false)
	eq(st.menu_row, false)
	local n = #env.raisedNamed("x4mp.saves_status")
	pushBlock(true)
	truthy(#env.raisedNamed("x4mp.saves_status") > n)
	eq(statusPayload().blocking, true)
end)

test("no native api yet: nothing is sent, nothing throws", function()
	fresh({ api = false })
	load()
	eq(#env.raised, 0)
	eq(IsSavingPossible(), true)
	pushBlock(true)
	eq(IsSavingPossible(), false)
end)

local function menu(withUix)
	local cfg = { optionDefinitions = { main = {
		{ id = "load", selectable = function() return true end },
		{ id = "save", selectable = function() return log.vanillaPossible end },
	} } }
	log.vanillaPossible = true
	local om = { name = "OptionsMenu", saveMouseOverText = function() return "vanilla tooltip" end }
	if withUix then om.uix_getConfig = function() return cfg end end
	_G.Menus = { om }
	return om, cfg
end

test("Esc menu: the Save row is greyed while blocked and the tooltip says why", function()
	fresh()
	local om, cfg = menu(true)
	load()
	local row = cfg.optionDefinitions.main[2]
	eq(om.saveMouseOverText(), "vanilla tooltip")
	eq(row.selectable(), true)
	pushBlock(true)
	eq(row.selectable(), false, "row greyed")
	eq(om.saveMouseOverText(), "Saving is disabled while connected as a client", "tooltip from page 92000 id 60")
	eq(cfg.optionDefinitions.main[1].selectable(), true, "other rows untouched")
	pushBlock(false)
	eq(row.selectable(), true)
	eq(om.saveMouseOverText(), "vanilla tooltip")
	local st = statusPayload()
	eq(st.menu_row, true)
	eq(st.tooltip, true)
	eq(st.detail, "config:uix")
end)

test("Esc menu: patched once even if the file loads again; options menu that appears later is picked up on show", function()
	fresh()
	load() -- no OptionsMenu yet
	eq(statusPayload().menu_row, false)
	local om, cfg = menu(true)
	env.fire("show")
	eq(statusPayload().menu_row, true)
	local sel = cfg.optionDefinitions.main[2].selectable
	local tip = om.saveMouseOverText
	load()
	env.fire("show")
	eq(cfg.optionDefinitions.main[2].selectable, sel, "row hook not replaced")
	eq(om.saveMouseOverText, tip, "tooltip hook not stacked")
end)

test("Esc menu: config handed over by another X4MP file is used when UIX is absent", function()
	fresh()
	local om, cfg = menu(false)
	load()
	eq(statusPayload().menu_row, false, "no way to the config yet")
	X4MPSaves.setMenuConfig(cfg)
	env.fire("gfx_ok")
	pushBlock(true)
	eq(cfg.optionDefinitions.main[2].selectable(), false)
	eq(statusPayload().detail, "config:handoff")
	eq(om.saveMouseOverText(), "Saving is disabled while connected as a client")
end)

test("chat /x4mp_selftest raises the bridge verb and is not passed on; other commands are", function()
	fresh()
	local seen = {}
	_G.ExecuteDebugCommand = function(cmd, param) seen[#seen + 1] = cmd .. ":" .. tostring(param) end
	load()
	ExecuteDebugCommand("x4mp_selftest", "")
	eq(#env.raisedNamed("x4mp.selftest"), 1)
	eq(X4MPBridge.json.decode(env.raisedNamed("x4mp.selftest")[1]).v, 1)
	eq(#seen, 0)
	ExecuteDebugCommand("x4mp", "join")
	eq(seen[1], "x4mp:join")
end)

test("MD game_saved is forwarded to native with success and age", function()
	fresh()
	load()
	env.fire("x4mp.md_game_saved", "success=1;age=12.5")
	local sent = env.raisedNamed("x4mp.game_saved")
	eq(#sent, 1)
	local p = X4MPBridge.json.decode(sent[1])
	eq(p.success, 1)
	eq(p.age, 12.5)
	env.fire("x4mp.md_game_saved", "garbage")
	eq(#env.raisedNamed("x4mp.game_saved"), 2, "garbage still reports a save (defaults)")
end)

test("allowSaves lifts the block for the authority's own save and restores it", function()
	fresh()
	load()
	pushBlock(true)
	local r = X4MPSaves.allowSaves(function()
		SaveGame("x4mp_session", "n")
		return IsSavingPossible()
	end)
	eq(r, true)
	eq(#log.saved, 1)
	eq(IsSavingPossible(), false)
	local ok = pcall(X4MPSaves.allowSaves, function() error("boom") end)
	falsy(ok)
	eq(IsSavingPossible(), false, "bypass depth restored after an error")
end)

test("a missing SaveGame global is reported, not fatal", function()
	fresh()
	_G.SaveGame = nil
	load()
	eq(statusPayload().save_game, false)
	eq(IsSavingPossible(), true)
end)

test("the tooltip text exists on page 92000 and has no X4 comment brackets", function()
	fresh()
	local text = env.texts[60]
	truthy(text and text ~= "")
	falsy(text:find("[%(%){}]"))
end)
