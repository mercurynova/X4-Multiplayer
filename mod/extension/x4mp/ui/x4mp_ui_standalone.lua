-- x4mp_ui_standalone.lua : standalone renderer ("X4MPMenu") for the X4MP screens (M2-05).
--
-- A normal Helper menu registered under its own name and opened with OpenMenu, so it does not depend on injecting rows into the
-- vanilla OptionsMenu (that is the embedded entry of M2-11). It draws the model of x4mp_menu.lua; see the renderer interface there.
--
-- Pattern (all under x4-unpacked/ui/addons/): menu registration and frames follow ego_chatwindow/chatwindow.lua:28-46 and
-- ego_detailmonitorhelper/helper.lua (registerMenu :1331, createFrameHandle :3767, clearDataForRefresh :2414, closeMenu :1744).
-- Edit box property textHidden: helper.lua:3239. Whether the menu opens over the start menu is session-2 question D2.

-- luacheck: globals X4MPBridge X4MPScreens X4MPMenu Menus Helper Color OpenMenu

if X4MPMenu and X4MPMenu.loaded then return end

local S = X4MPScreens
if type(S) ~= "table" then
	pcall(DebugError, "[X4MP] standalone ui: x4mp_menu.lua must load first")
	return
end
local log = X4MPBridge.log

local MENU_NAME = "X4MPMenu"
local LAYER = 4

local menu = { name = MENU_NAME, loaded = true, model = nil, registered = false }
X4MPMenu = menu

local TONE_COLOR = { error = "text_error", warning = "text_warning", positive = "text_positive", inactive = "text_inactive" }

local function toneProps(tone)
	local props = { wordwrap = true }
	local key = TONE_COLOR[tone]
	if key and type(Color) == "table" and Color[key] then props.color = Color[key] end
	return props
end

------------------------------------------------------------------------------
-- drawing
------------------------------------------------------------------------------
-- The frame handle is the only piece of the window the engine still holds after a close, and a close path that did not run
-- menu.cleanup (a bare Helper.closeMenu from the engine or a script) leaves it set while shown is already nil. One place drops
-- it, so every path - our close, the engine's close, a redraw - ends with exactly one handle or none.
local function releaseFrame()
	menu.frame = nil
end
menu.releaseFrame = releaseFrame

local function drawFrame()
	local model = menu.model
	if not model then return end
	-- Drop a handle left behind by a close path that never reached menu.onCloseElement, before building the new one.
	releaseFrame()
	Helper.clearDataForRefresh(menu, LAYER)
	local width = Helper.scaleX(model.wide and 620 or 460)
	menu.frame = Helper.createFrameHandle(menu, {
		layer = LAYER,
		standardButtons = Helper.standardButtons_Close,
		width = width,
		x = math.floor((Helper.viewWidth - width) / 2),
		y = math.floor(Helper.viewHeight / 5),
		autoFrameHeight = true,
		startAnimation = false,
		blurBackground = false,
		playerControls = false,
	})
	menu.frame:setBackground("solid", { color = Color["frame_background_semitransparent"] })

	local ftable = menu.frame:addTable(2, { tabOrder = 1, highlightMode = "off" })
	ftable:setColWidthPercent(1, 35)

	local head = ftable:addRow(false, { fixed = true })
	head[1]:setColSpan(2):createText(model.title, Helper.headerRowCenteredProperties)

	for _, row in ipairs(model.rows) do
		if row.type == "text" then
			local r = ftable:addRow(false, {})
			r[1]:setColSpan(2):createText(row.text, toneProps(row.tone))
		elseif row.type == "edit" then
			local r = ftable:addRow(true, {})
			local cell = r[2]
			if row.fullWidth then
				cell = r[1]:setColSpan(2) -- M2-X3: a read-only link box uses the whole row
			else
				r[1]:createText(row.label or "", {})
			end
			local props = { height = Helper.standardButtonHeight, description = row.description or row.label or "",
				maxChars = row.maxChars or 64, textHidden = row.hidden == true, selectTextOnActivation = row.readonly == true }
			cell:createEditBox(props):setText(row.value or "", {})
			-- only the model's callback keeps the value; nothing is cached here
			cell.handlers.onTextChanged = function(_, value) row.onChange(value) end
			cell.handlers.onEditBoxDeactivated = function(_, value) row.onChange(value) end
		elseif row.type == "button" then
			local r = ftable:addRow(row.active ~= false, {})
			r[1]:setColSpan(2):createButton({ active = row.active ~= false }):setText(row.text, { halign = "center" })
			r[1].handlers.onClick = function() row.onClick() end
		end
	end
	menu.frame:display()
end

-- One draw at a time: a handler that fires while the frame is being built must not build it again (the same re-entrancy class as
-- the close callback below). The flag is cleared on every path, including an error, so one failed draw does not freeze the
-- window.
function menu.display()
	if menu.inDisplay then return end
	menu.inDisplay = true
	local ok, err = pcall(drawFrame)
	menu.inDisplay = nil
	if not ok then log("standalone ui: display failed: " .. tostring(err)) end
end

------------------------------------------------------------------------------
-- Helper menu callbacks
------------------------------------------------------------------------------
function menu.onShowMenu()
	menu.shown = true
	menu.opening = nil
	-- Helper.showMenuCallback stored the parameters OpenMenu got in menu.param2; Helper.closeMenu / clearMenu wipes them again, so keep a
	-- copy. For a window opened by Helper.closeMenuAndOpenNewMenu(OptionsMenu, ...) it is { "OptionsMenu", param, {"restore", ...} }.
	local p2 = menu.param2
	menu.returnTo = (type(p2) == "table" and #p2 > 0) and p2 or nil
	local ok, err = pcall(menu.display)
	if not ok then log("standalone ui: display failed: " .. tostring(err)) end
end

-- One close at a time. With this module enabled the save load died with 0xc00000fd at ~8000 frames deep on one four-address
-- cycle, Lua -> X4 C function -> the same Lua callback (session 8 8.5: three dumps, 09:16/09:23/09:32). Measured in game on
-- 2026-10-30: Helper.closeMenu(menu, "close") does NOT call this callback (shown cleared, menu.frame left set), so the cycle is
-- not our call to Helper.closeMenu coming straight back. It is this callback being entered again from the engine's own close
-- path while the frame is being torn down, which is exactly what the flag stops: the re-entry returns at once and the outer
-- call finishes the job.
function menu.onCloseElement(dueToClose)
	if menu.closing then return end
	menu.closing = true
	local ret = menu.returnTo
	-- Vanilla Helper.closeMenu(menu, "back") reopens param2[1] itself (helper.lua, Helper.closeMenu). Every other reason (the Close
	-- button and Esc deliver "close") only closes, so the way back is ours.
	menu.vanillaReturn = (dueToClose == "back") and ret ~= nil
	-- When the engine's own close path brought us here, Helper is already closing this menu; a second closeMenu would reopen the
	-- start menu twice for "back".
	if not menu.inHelperClose then
		menu.inHelperClose = true
		local ok, err = pcall(Helper.closeMenu, menu, dueToClose or "close")
		menu.inHelperClose = nil
		if not ok then log("standalone ui: closeMenu failed: " .. tostring(err)) end
	end
	menu.closing = nil
	menu.cleanup()
end

function menu.cleanup()
	menu.shown = nil
	menu.opening = nil
	releaseFrame()
	menu.model = nil
	S.onClosed()
	menu.restoreStartMenu()
end

local function findOptionsMenu()
	if type(Menus) ~= "table" then return nil end
	for _, m in ipairs(Menus) do
		if type(m) == "table" and m.name == "OptionsMenu" then return m end
	end
	return nil
end

-- Session-2/3 finding: our window opens OVER the start menu, and closing it (Close button or Esc) left a blank screen.
-- Root cause (helper.lua showMenuCallback :1337-1343): OpenMenu("OptionsMenu") is ignored while OptionsMenu.shown is still true, and
-- it was, because the row click opened our window with a plain OpenMenu: nothing ever ran Helper.closeMenu/clearMenu on the options
-- menu, View.clearMenus only blanked its frames. Vanilla (gameoptions.lua:13381) leaves with Helper.closeMenuAndOpenNewMenu, which
-- clears the old menu (shown=nil) and records { "OptionsMenu", param, ... } as param2 so that "back" returns to it.
-- So: renderer.show leaves the options menu that way, and the close path reopens it from that record (or, for Esc/"close", from
-- OpenMenu("OptionsMenu", ...) like gameoptions.lua init() does). Not done when a session save is being loaded (the game replaces
-- the screen itself) or when the window was not opened over the start menu: in game there is no start screen to restore, and the
-- Esc menu we replaced was already closed by closeMenuAndOpenNewMenu, so the game resumes by itself.
function menu.restoreStartMenu()
	local overStartMenu = menu.overStartMenu
	local ret = menu.returnTo
	local vanillaReturn = menu.vanillaReturn
	menu.overStartMenu = nil
	menu.returnTo = nil
	menu.vanillaReturn = nil
	if not overStartMenu then return end
	if X4MPBridge.loadingSave then
		log("standalone: closed, a session save is loading, not reopening the start menu")
		return
	end
	if vanillaReturn then
		log("standalone: closed, reopening start menu (via Helper.closeMenu back -> " .. tostring(ret[1]) .. ")")
		return
	end
	local name = (ret and type(ret[1]) == "string") and ret[1] or "OptionsMenu"
	local param, param2 = nil, nil
	if ret then param, param2 = ret[2], ret[3] end
	log("standalone: closed, reopening start menu (OpenMenu " .. name .. ", saved return record "
		.. (ret and "yes" or "no") .. ")")
	local function reopen()
		-- A delayed callback can outlive the reason it was queued for: a session save started loading in the meantime, or the
		-- window was opened again. Reopening the start menu then would fight the game's own screen switch - the exact
		-- situation the save-load crash happened in.
		if X4MPBridge.loadingSave then
			log("standalone: the delayed reopen is dropped, a session save is loading")
			return
		end
		if menu.shown then
			log("standalone: the delayed reopen is dropped, the window is open again")
			return
		end
		-- a stale "shown" would make the game ignore the request (see above): clear it the way closeMenu does
		local om = findOptionsMenu()
		if om and om.shown and type(Helper) == "table" and type(Helper.clearMenu) == "function" then
			log("standalone: OptionsMenu still marked shown, clearing it first")
			pcall(Helper.clearMenu, om)
		end
		local ok, err = pcall(OpenMenu, name, param, param2, true)
		if not ok then log("standalone ui: restoring the start menu failed: " .. tostring(err)) end
	end
	if type(getElapsedTime) == "function" and type(Helper) == "table" and Helper.addDelayedOneTimeCallbackOnUpdate then
		Helper.addDelayedOneTimeCallbackOnUpdate(reopen, true, getElapsedTime() + 0.1) -- after this menu finished closing
	else
		reopen()
	end
end

------------------------------------------------------------------------------
-- registration + renderer interface
------------------------------------------------------------------------------
local function ensureRegistered()
	if menu.registered then return true end
	if type(Helper) ~= "table" or type(Helper.registerMenu) ~= "function" then
		log("standalone ui: Helper missing")
		return false
	end
	Menus = Menus or {}
	for i = #Menus, 1, -1 do -- drop a stale entry of ours
		if type(Menus[i]) == "table" and Menus[i].name == MENU_NAME then table.remove(Menus, i) end
	end
	table.insert(Menus, menu)
	local ok, err = pcall(Helper.registerMenu, menu)
	menu.registered = ok
	if not ok then log("standalone ui: registerMenu failed: " .. tostring(err)) end
	return ok
end

local renderer = { name = "standalone" }

function renderer.isOpen()
	return menu.shown == true
end

function renderer.show(model)
	menu.model = model
	if menu.opening then return end -- OpenMenu already requested; onShowMenu draws the latest model
	if menu.shown then
		local ok, err = pcall(menu.display)
		if not ok then log("standalone ui: redraw failed: " .. tostring(err)) end
		return
	end
	if not ensureRegistered() then return end
	menu.opening = true
	menu.overStartMenu = X4MPBridge.isStartMenu() == true
	local ok, err
	-- Both the start menu and the in-game Esc menu show OptionsMenu before our row is clicked, so the lookup cannot depend on
	-- isStartMenu(): with a plain OpenMenu the Esc menu stayed marked "shown" while its frames were gone, and closing our window
	-- left the game with a menu it believed was open and no HUD or input (the in-game close hang, 2026-11-12). overStartMenu only
	-- decides whether the close path reopens the start menu.
	local om = findOptionsMenu()
	if om and om.shown and type(Helper.closeMenuAndOpenNewMenu) == "function" then
		-- leave the options menu the vanilla way (closes it, records the return target in param2), see restoreStartMenu
		ok, err = pcall(Helper.closeMenuAndOpenNewMenu, om, MENU_NAME, { 0, 0 })
	else
		if not om then
			log("standalone ui: no OptionsMenu in Menus; opening with a plain OpenMenu")
		end
		ok, err = pcall(OpenMenu, MENU_NAME, { 0, 0 }, nil)
	end
	if not ok then
		menu.opening = nil
		menu.overStartMenu = nil
		log("standalone ui: OpenMenu failed: " .. tostring(err))
	end
end

function renderer.close()
	if menu.shown then menu.onCloseElement("close") end
end

menu.renderer = renderer
S.setRenderer(renderer)
log("standalone ui loaded")
