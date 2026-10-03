-- x4mp_spike_diplo.lua : spike v2 blocks diplo1..diplo7 (task M2-004, spike S11: team diplomacy, ADR-047).
--
--   diplo1  S11.1  teams in the Diplomacy menu: activate teams 1-3, names, relations, lock team 1, known flags
--                  (part 1: team 3 unknown; part 2: teams 1 and 2 unknown, team 3 known). CHANGES the game.
--   diplo2  S11.2  lock semantics (a)-(f) in one MD action block, originals restored. Changes relations briefly.
--   diplo3  S11.3  diplomacy-active / events-allowed flags on team 2 for 70 s, then off for 70 s. Changes the game.
--   diplo4  S11.4  test diplomatic event "X4MP test treaty" (team 1 vs team 2) + test action "X4MP test action"
--                  (libraries/diplomacy.xml diff). Starts an event, unlocks the Diplomatic Events tab. CHANGES the game.
--   diplo5  S11.5  switch vanilla event capability off for all NPC factions for 70 s, then restore. Changes the game.
--   diplo6  S11.6  inject a "Teams (test)" tab into DiplomacyMenu (Lua only, nothing saved).
--   diplo7  S11.7  argon-teladi and player-argon relation change events (changed by 0.01 and restored).
--   diplo_look     internal helper: Lua view of the four test teams (the blocks call it; harmless to run by hand).
--
-- MD side (state changes, same-frame readbacks, listeners): md/x4mp_spike_diplo.xml. The MD blocks are driven from here
-- with S.toMD(control) so the timing and the notifications live in one place. Log step: S11.
--
-- Sources (x4-unpacked/ui/addons/ego_detailmonitor/): DiplomacyMenu menu_diplomacy.lua: `config.leftBar` (:199, an upvalue
-- of menu.createLeftBar :1037), buttons call menu.buttonTogglePlayerInfo(mode) (:355) which sets menu.mode and calls
-- menu.refreshInfoFrame -> menu.createInfoFrame (:351, :1078, looked up through the menu table, so it can be wrapped);
-- frame/table recipe copied from createInfoFrame; GetLibrary("factions") and GetFactionData(id, "isdiplomacyactive", ...)
-- :1188-1199; ffi structs DiplomacyActionInfo / DiplomacyEventInfo and C.GetDiplomacyActions / C.GetDiplomacyEvents
-- :14-90, :150-172 (declared by the vanilla file, used here at call time).

-- luacheck: globals X4MPSpike DebugError Menus Helper Color GetLibrary GetFactionData

local ffi = require("ffi")
local C = ffi.C

local S = X4MPSpike
if type(S) ~= "table" then return end
local K, log = S.K, S.log

S.diplo = S.diplo or {}
local D = S.diplo

local TAB_MODE = "x4mp_teams"
local TAB_NAME = "Teams (test)"

------------------------------------------------------------------------------
-- Lua view of the test teams (the data the vanilla Factions tab is built from)
------------------------------------------------------------------------------
local function libraryIndex()
	local ok, lib = pcall(GetLibrary, "factions")
	local set, n = {}, 0
	if ok and type(lib) == "table" then
		n = #lib
		for i, f in ipairs(lib) do
			if type(f) == "table" and f.id then set[f.id] = i end
		end
	end
	return set, n, ok
end

local function teamView(tag)
	local set, n, ok = libraryIndex()
	log("S11", "INFO", K("what", "lua_library", "tag", tag, "GetLibrary_factions_ok", ok, "count", n))
	for k = 1, 4 do
		local id = "x4mp_team_" .. k
		local ok2, name, short, dactive, locked, lockshort, rangename = pcall(GetFactionData, id,
			"name", "shortname", "isdiplomacyactive", "isrelationlocked", "relationlockshortreason", "prioritizedrelationrangename")
		log("S11", "INFO", K("what", "lua_view", "tag", tag, "team", id, "listed_at_index", set[id] or 0,
			"ok", ok2, "name", name, "shortname", short, "diplomacy_active", dactive, "relation_locked", locked,
			"lock_short_reason", lockshort, "relation_range", rangename))
	end
end

S.register("diplo_look", function(args)
	teamView(args.tag or "look")
end, "S11 helper: Lua view of the four test teams")

------------------------------------------------------------------------------
-- diplo1 .. diplo5, diplo7: Lua drives the MD controls and the notifications
------------------------------------------------------------------------------
S.register("diplo1", function()
	S.startRoutine("diplo1", function()
		S.toMD("diplo1_setup", "go")
		S.waitSeconds(1.5)
		teamView("lua_part1")
		S.notify("diplo1: set up (CHANGES the game: activates test Teams 1-3, sets their relations to you, locks Team 1). Open Diplomacy > Factions and Relations and take a screenshot. Team 3 is unknown now. Part 2 in 45 s.")
		S.waitSeconds(45)
		S.toMD("diplo1_part2", "go")
		S.waitSeconds(1.5)
		teamView("lua_part2")
		S.notify("diplo1: part 2. Teams 1 and 2 are now UNKNOWN, Team 3 is known. Look again and take a screenshot. Done in 45 s.")
		S.waitSeconds(45)
		S.toMD("diplo1_end", "go")
		S.waitSeconds(1.5)
		teamView("lua_end")
		S.notify("diplo1: done (all three teams known again, Team 1 still locked).")
	end)
end, "S11.1 teams in the Diplomacy menu (changes the game)")

S.register("diplo2", function()
	S.startRoutine("diplo2", function()
		S.notify("diplo2: running (briefly changes relations of Team 1, Argon and you, locks the player faction for one frame, then restores everything).")
		teamView("lua_before")
		S.toMD("diplo2", "go")
		S.waitSeconds(8)
		teamView("lua_after")
		S.notify("diplo2: done. Relations restored; details are in the log.")
	end)
end, "S11.2 relation lock semantics (changes relations briefly, restored)")

S.register("diplo3", function()
	S.startRoutine("diplo3", function()
		S.toMD("diplo3_on", "go")
		S.waitSeconds(1.5)
		teamView("lua_on_immediately")
		S.notify("diplo3: Team 2 diplomacy is now ACTIVE and events allowed (CHANGES the game, reverted in part 2). Do not pause. Open Diplomacy and look: is Team 2 still Unreceptive? Is it in the faction-pair dropdown of interference actions? Wait 70 s for part 2.")
		S.waitSeconds(70)
		S.toMD("diplo3_log", "after_70s_on")
		S.waitSeconds(1)
		teamView("lua_after_70s_on")
		S.toMD("diplo3_off", "go")
		S.waitSeconds(1.5)
		S.notify("diplo3: part 2. Both flags are now false again. Look at the menu again. Final check in 70 s.")
		S.waitSeconds(70)
		S.toMD("diplo3_log", "after_70s_off")
		S.waitSeconds(1)
		teamView("lua_after_70s_off")
		S.notify("diplo3: done.")
	end)
end, "S11.3 diplomacy flags on team 2 (changes the game, reverted)")

-- what the engine thinks it has loaded of diplomacy.xml: is our test content in the lists the menu reads?
local function listDiplomacyContent()
	local ok, err = pcall(function()
		local n = tonumber(C.GetNumDiplomacyActions())
		local buf = ffi.new("DiplomacyActionInfo[?]", math.max(n, 1))
		n = tonumber(C.GetDiplomacyActions(buf, n))
		local ids, found, hidden = {}, false, "n/a"
		for i = 0, n - 1 do
			local id = ffi.string(buf[i].id)
			if id:find("x4mp_", 1, true) then
				found = true
				hidden = buf[i].hidden
				ids[#ids + 1] = id .. "(cat=" .. ffi.string(buf[i].category) .. ",paramtype=" .. ffi.string(buf[i].paramtype) .. ")"
			end
		end
		log("S11", found and "PASS" or "INFO", K("what", "lua_actions_listed", "count", n, "x4mp_test_action_present", found,
			"hidden", hidden, "ids", table.concat(ids, ",")))
	end)
	if not ok then log("S11", "INFO", K("what", "lua_actions_listed", "ok", false, "err", err)) end
	ok, err = pcall(function()
		local n = tonumber(C.GetNumDiplomacyEvents())
		local buf = ffi.new("DiplomacyEventInfo[?]", math.max(n, 1))
		n = tonumber(C.GetDiplomacyEvents(buf, n))
		local ids, found = {}, false
		for i = 0, n - 1 do
			local id = ffi.string(buf[i].id)
			if id:find("x4mp_", 1, true) then
				found = true
				ids[#ids + 1] = id .. "(options=" .. tostring(buf[i].numoptions) .. ")"
			end
		end
		local ops = tonumber(C.GetNumDiplomacyEventOperations(true))
		log("S11", found and "PASS" or "INFO", K("what", "lua_events_listed", "count", n, "x4mp_test_treaty_present", found,
			"ids", table.concat(ids, ","), "active_event_operations", ops))
	end)
	if not ok then log("S11", "INFO", K("what", "lua_events_listed", "ok", false, "err", err)) end
end

S.register("diplo4", function()
	S.startRoutine("diplo4", function()
		listDiplomacyContent()
		S.toMD("diplo4", "go")
		S.waitSeconds(2)
		listDiplomacyContent()
		S.notify("diplo4: test event 'X4MP test treaty' (Team 1 vs Team 2) started (CHANGES the game: also unlocks the Diplomatic Events tab; vanilla concludes it after 3 min and may change the Team 1/Team 2 relation). If you have agents, open Agent Actions and start 'X4MP test action'. Open Diplomatic Events and try to pick an option. Take a screenshot.")
		S.waitSeconds(200)
		S.toMD("diplo4_end", "go")
		S.waitSeconds(1.5)
		S.notify("diplo4: done.")
	end)
end, "S11.4 test diplomatic event / action from the libraries diff (changes the game)")

S.register("diplo5", function()
	S.startRoutine("diplo5", function()
		S.toMD("diplo5_on", "go")
		S.waitSeconds(1.5)
		S.notify("diplo5: vanilla diplomatic-event capability of all NPC factions is OFF for 70 s (CHANGES the game, restored automatically; do not pause and do not save meanwhile).")
		S.waitSeconds(70)
		S.toMD("diplo5_log", "after_70s")
		S.waitSeconds(1)
		S.toMD("diplo5_restore", "go")
		S.waitSeconds(1.5)
		S.notify("diplo5: done (restored).")
	end)
end, "S11.5 switch NPC diplomacy events off for 70 s (changes the game, restored)")

S.register("diplo7", function()
	S.startRoutine("diplo7", function()
		S.notify("diplo7: running (changes Argon-Teladi and you-Argon relations by 0.01 and restores them).")
		S.toMD("diplo7", "go")
		S.waitSeconds(8)
		S.notify("diplo7: done. See the log lines event_faction_relation_changed / event_player_relation_changed.")
	end)
end, "S11.7 NPC-NPC relation change events (changes relations by 0.01, restored)")

------------------------------------------------------------------------------
-- diplo6: inject a tab into the Diplomacy menu (Lua only)
------------------------------------------------------------------------------
local function findMenu(name)
	for _, m in ipairs(Menus or {}) do
		if type(m) == "table" and m.name == name then return m end
	end
	return nil
end

-- the file-local `config` of menu_diplomacy.lua is an upvalue of menu.createLeftBar (it reads config.leftBar)
local function captureLeftBarConfig(menu)
	local okReq, dbg = pcall(require, "debug")
	local getup = okReq and type(dbg) == "table" and dbg.getupvalue or nil
	log("S11", getup and "PASS" or "INFO", K("what", "diplo6_require_debug", "require_ok", okReq,
		"getupvalue", type(getup), "global_debug", type(_G.debug)))
	if type(getup) ~= "function" or type(menu.createLeftBar) ~= "function" then return nil end
	local scanned = 0
	for i = 1, 150 do
		local ok, name, val = pcall(getup, menu.createLeftBar, i)
		if not ok or name == nil then break end
		scanned = scanned + 1
		if name == "config" and type(val) == "table" and type(val.leftBar) == "table" then
			log("S11", "PASS", K("what", "diplo6_config_upvalue", "index", i, "left_bar_entries", #val.leftBar))
			return val
		end
	end
	log("S11", "INFO", K("what", "diplo6_config_upvalue", "found", false, "upvalues_scanned", scanned))
	return nil
end

local function drawTeamsTab(menu)
	Helper.clearDataForRefresh(menu, 5) -- config.infoLayer of menu_diplomacy.lua
	local frame = Helper.createFrameHandle(menu, { standardButtons = {}, width = Helper.viewWidth, height = Helper.viewHeight,
		x = 0, y = 0, layer = 5 })
	menu.infoFrame = frame
	menu.createTopLevel(frame)
	menu.tableProperties = {
		width = Helper.playerInfoConfig.width - menu.sideBarWidth - Helper.borderSize,
		x = Helper.playerInfoConfig.offsetX + menu.sideBarWidth + Helper.standardContainerOffset + Helper.minorPanelSpacing,
		y = math.max(menu.playerInfoHeight, menu.topLevelHeight),
	}
	menu.tableProperties.height = Helper.viewHeight - menu.tableProperties.y - Helper.frameBorder
	Helper.clearTableConnectionColumn(menu, 2)
	Helper.clearTableConnectionColumn(menu, 3)
	local t = frame:addTable(3, { tabOrder = 1, width = menu.tableProperties.width, x = menu.tableProperties.x, y = menu.tableProperties.y })
	t:setColWidthPercent(2, 22)
	t:setColWidthPercent(3, 28)
	local head = t:addRow(false, { fixed = true })
	head[1]:setColSpan(3):createText("X4MP teams (spike test S11.6)", Helper.headerRowCenteredProperties)
	local rows = 0
	for k = 1, 8 do
		local id = "x4mp_team_" .. k
		local ok, name, short, locked = pcall(GetFactionData, id, "name", "shortname", "isrelationlocked")
		if ok then
			local r = t:addRow(true, {})
			r[1]:createText(tostring(name))
			r[2]:createText(tostring(short))
			r[3]:createText(locked and "relations locked" or "relations open")
			rows = rows + 1
		end
	end
	if rows == 0 then
		local r = t:addRow(false, {})
		r[1]:setColSpan(3):createText("(no x4mp teams found)", { halign = "center" })
	end
	frame:display()
	log("S11", "PASS", K("what", "diplo6_teams_tab_drawn", "rows", rows, "mode", menu.mode))
end

local function installTab()
	local menu = findMenu("DiplomacyMenu")
	if not menu then
		log("S11", "FAIL", K("what", "diplo6_menu_missing", "menus", type(Menus) == "table" and #Menus or -1))
		return nil, "DiplomacyMenu_not_found"
	end
	log("S11", "INFO", K("what", "diplo6_menu_found", "createLeftBar", type(menu.createLeftBar),
		"createInfoFrame", type(menu.createInfoFrame), "buttonTogglePlayerInfo", type(menu.buttonTogglePlayerInfo),
		"refreshInfoFrame", type(menu.refreshInfoFrame), "createTopLevel", type(menu.createTopLevel), "mode_now", menu.mode))

	-- 1. the info frame: draw our own table for our mode, delegate everything else
	if not menu.x4mpInfoWrapped and type(menu.createInfoFrame) == "function" then
		local orig = menu.createInfoFrame
		menu.createInfoFrame = function(...)
			if menu.mode == TAB_MODE then
				local ok, err = pcall(drawTeamsTab, menu)
				if ok then return end
				log("S11", "FAIL", K("what", "diplo6_draw_error", "err", err))
			end
			return orig(...)
		end
		menu.x4mpInfoWrapped = true
	end

	-- 2. the button: method A, add an entry to config.leftBar (found through the upvalue of createLeftBar)
	if menu.x4mpTabMethod then
		return menu.x4mpTabMethod, "already_installed"
	end
	local cfg = captureLeftBarConfig(menu)
	if cfg then
		local present = false
		for _, e in ipairs(cfg.leftBar) do
			if e.mode == TAB_MODE then present = true end
		end
		if not present then
			cfg.leftBar[#cfg.leftBar + 1] = { name = TAB_NAME, icon = "mapst_factionrelation", mode = TAB_MODE, active = true,
				iconcolor = function() return Color["icon_mission"] end }
		end
		menu.x4mpTabMethod = "config_upvalue"
		return menu.x4mpTabMethod, "entry_added"
	end

	-- method B: wrap createLeftBar and add one more button table below the vanilla ones
	if type(menu.createLeftBar) == "function" then
		local origBar = menu.createLeftBar
		menu.createLeftBar = function(frame, offsetx, offsety, ...)
			local r = origBar(frame, offsetx, offsety, ...)
			local okAdd, errAdd = pcall(function()
				local n = 1
				if C.IsStoryFeatureUnlocked("x4ep1_diplomacy_agent") then n = n + 2 end
				if C.IsStoryFeatureUnlocked("x4ep1_diplomacy_interference") then n = n + 1 end
				local y = offsety + n * (menu.sideBarWidth + Helper.borderSize)
				local ft = frame:addTable(1, { tabOrder = 4, scaling = false, borderEnabled = false, x = offsetx, y = y, reserveScrollBar = false })
				ft:setColWidth(1, menu.sideBarWidth, false)
				local row = ft:addRow(true, { fixed = true, bgColor = Color["row_background_blue"] })
				row[1]:createButton({ active = true, height = menu.sideBarWidth, mouseOverText = TAB_NAME,
					bgColor = (menu.mode == TAB_MODE) and Color["row_background_selected"] or Color["row_title_background"] })
					:setIcon("mapst_factionrelation", { color = function() return Color["icon_mission"] end })
				row[1].handlers.onClick = function()
					if type(menu.closeContextMenu) == "function" then menu.closeContextMenu() end
					menu.mode = (menu.mode == TAB_MODE) and "factions" or TAB_MODE
					menu.refreshInfoFrame(1, 1)
				end
			end)
			if not okAdd then log("S11", "FAIL", K("what", "diplo6_button_error", "err", errAdd)) end
			return r
		end
		menu.x4mpTabMethod = "wrap_createLeftBar"
		return menu.x4mpTabMethod, "wrapper_installed"
	end
	return nil, "no_method"
end

S.register("diplo6", function()
	local method, how = installTab()
	log("S11", method and "PASS" or "FAIL", K("what", "diplo6_installed", "method", method, "detail", how))
	if method then
		S.notify("diplo6: tab installed (" .. method .. ", Lua only, nothing saved). Open the Diplomacy menu: below the vanilla tabs there is a new orange button, tooltip '" .. TAB_NAME .. "'. Click it (does it draw?), then click a vanilla tab (does it work?). If the menu is already open, close and reopen it.")
	else
		S.notify("diplo6: could not install the tab (" .. tostring(how) .. "), see the log.")
	end
end, "S11.6 inject a 'Teams (test)' tab into DiplomacyMenu (Lua only)")
