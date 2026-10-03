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
function menu.display()
	local model = menu.model
	if not model then return end
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

------------------------------------------------------------------------------
-- Helper menu callbacks
------------------------------------------------------------------------------
function menu.onShowMenu()
	menu.shown = true
	menu.opening = nil
	local ok, err = pcall(menu.display)
	if not ok then log("standalone ui: display failed: " .. tostring(err)) end
end

function menu.onCloseElement(dueToClose)
	Helper.closeMenu(menu, dueToClose or "close")
	menu.cleanup()
end

function menu.cleanup()
	menu.shown = nil
	menu.opening = nil
	menu.frame = nil
	menu.model = nil
	S.onClosed()
	menu.restoreStartMenu()
end

-- Session-2 finding D2: our window opens OVER the start menu, and closing it (Close button or Esc) leaves a blank screen. The
-- vanilla way back is what gameoptions.lua does at start-up (init(), "restore handling"): OpenMenu("OptionsMenu", submenu, nil, true)
-- with no submenu = the main page. Not done when a session save is being loaded (the game replaces the screen itself) or when the
-- window was not opened over the start menu (in game, the vanilla menu handling resumes by itself).
function menu.restoreStartMenu()
	local overStartMenu = menu.overStartMenu
	menu.overStartMenu = nil
	if not overStartMenu then return end
	if X4MPBridge.loadingSave then return end
	local function reopen()
		local ok, err = pcall(OpenMenu, "OptionsMenu", nil, nil, true)
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
	local ok, err = pcall(OpenMenu, MENU_NAME, { 0, 0 }, nil)
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
