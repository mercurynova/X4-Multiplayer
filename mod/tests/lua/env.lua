-- Stubbed X4 environment for the Lua unit tests: DebugError, RegisterEvent, ReadText (real texts from t/0001-l044.xml),
-- the ffi module (C.IsStartmenu etc.), the X4Native API handle, and an optional recording Helper/OpenMenu/Color UI stub.
local E = {}

local ROOT = "."
function E.setRoot(root) ROOT = root end
function E.uiPath(file) return ROOT .. "/mod/extension/x4mp/ui/" .. file end

local GLOBALS = { "X4MPBridge", "X4MPScreens", "X4MPMenu", "X4MPExtensions", "__X4MP_USER", "__X4NATIVE_API", "Menus",
	"OpenMenu", "Helper", "Color", "LoadGame", "getElapsedTime", "GetExtensionList", "ExecuteDebugCommand", "AddUITriggeredEvent" }

local function readFile(path)
	local f = assert(io.open(path, "rb"))
	local s = f:read("*a")
	f:close()
	return s
end
E.readFile = readFile

local function loadTexts()
	local xml = readFile(ROOT .. "/mod/extension/x4mp/t/0001-l044.xml")
	local texts = {}
	for id, body in xml:gmatch('<t id="(%d+)">(.-)</t>') do
		body = body:gsub("&lt;", "<"):gsub("&gt;", ">"):gsub("&quot;", '"'):gsub("&apos;", "'"):gsub("&amp;", "&")
		texts[tonumber(id)] = body
	end
	return texts
end

function E.reset()
	for _, g in ipairs(GLOBALS) do _G[g] = nil end
	E.events, E.debug, E.raised, E.commands, E.loaded, E.opened = {}, {}, {}, {}, {}, {}
	E.startmenu = false
	E.texts = loadTexts()
	E.cfuncs = {}
	E.cfuncs.IsStartmenu = function() return E.startmenu end

	_G.DebugError = function(m) E.debug[#E.debug + 1] = tostring(m) end
	_G.RegisterEvent = function(name, fn)
		E.events[name] = E.events[name] or {}
		table.insert(E.events[name], fn)
	end
	_G.ReadText = function(page, id)
		if page ~= 92000 then return "" end
		return E.texts[id] or ""
	end
	_G.LoadGame = function(name) E.loaded[#E.loaded + 1] = name end
	_G.ExecuteDebugCommand = function(cmd, param) E.commands[#E.commands + 1] = { cmd, param } end

	local fakeFfi = {
		cdef = function() end,
		string = function(s) return s end,
		C = setmetatable({}, { __index = function(_, k) return E.cfuncs[k] end }),
	}
	package.loaded.ffi = fakeFfi
	package.preload.ffi = function() return fakeFfi end
end

--- Makes the X4Native API handle exist. Every raise_event is recorded in E.raised as { name, data }.
function E.installApi()
	_G.__X4NATIVE_API = {
		raise_event = function(name, data) E.raised[#E.raised + 1] = { name = name, data = data } end,
	}
end

function E.load(file)
	dofile(E.uiPath(file))
end

--- loads the bridge (and optionally more files, in ui.xml order)
function E.loadAll(files)
	for _, f in ipairs(files or { "x4mp_bridge.lua", "x4mp_menu.lua" }) do E.load(f) end
end

--- native -> Lua: runs every RegisterEvent handler of `name` with the raw parameter string
function E.fire(name, param)
	for _, fn in ipairs(E.events[name] or {}) do fn(nil, param) end
end

--- all raised events with this name (oldest first)
function E.raisedNamed(name)
	local out = {}
	for _, r in ipairs(E.raised) do
		if r.name == name then out[#out + 1] = r.data end
	end
	return out
end

function E.debugText()
	return table.concat(E.debug, "\n")
end

------------------------------------------------------------------------------
-- recording Helper stub (frames, tables, rows, cells) for the standalone renderer
------------------------------------------------------------------------------
local function newCell()
	local c = { handlers = {} }
	function c:setColSpan(n) self.colspan = n return self end
	function c:createText(text, props) self.kind, self.text, self.props = "text", text, props return self end
	function c:createEditBox(props) self.kind, self.props, self.text = "edit", props, "" return self end
	function c:createButton(props) self.kind, self.props = "button", props return self end
	function c:setText(text) self.text = text return self end
	return c
end

function E.installUi()
	E.frames = {}
	_G.Menus = {}
	_G.Color = setmetatable({}, { __index = function(_, k) return "color:" .. k end })
	_G.Helper = {
		viewWidth = 1920, viewHeight = 1080, standardButtonHeight = 25,
		standardButtons_Close = { close = true }, headerRowCenteredProperties = {},
		scaleX = function(v) return v end,
		clearDataForRefresh = function() end,
		closeMenu = function() end,
		registerMenu = function(menu) E.registeredMenu = menu end,
		createFrameHandle = function(menu, props)
			local frame = { menu = menu, props = props, tables = {} }
			function frame:setBackground() end
			function frame:display() E.frames[#E.frames + 1] = self E.lastFrame = self end
			function frame:addTable()
				local tbl = { rows = {} }
				function tbl:setColWidthPercent() end
				function tbl:addRow(selectable, props2)
					local row = { newCell(), newCell(), selectable = selectable, props = props2 }
					self.rows[#self.rows + 1] = row
					return row
				end
				self.tables[#self.tables + 1] = tbl
				return tbl
			end
			return frame
		end,
	}
	-- the game calls menu.onShowMenu after OpenMenu
	_G.OpenMenu = function(name)
		E.opened[#E.opened + 1] = name
		for _, m in ipairs(_G.Menus) do
			if m.name == name then m.onShowMenu() end
		end
	end
end

--- all cells of the last displayed frame, in order
function E.cells()
	local out = {}
	local frame = E.lastFrame
	if not frame then return out end
	for _, tbl in ipairs(frame.tables) do
		for _, row in ipairs(tbl.rows) do
			for i = 1, 2 do
				if row[i].kind then out[#out + 1] = row[i] end
			end
		end
	end
	return out
end

function E.findCell(kind, predicate)
	for _, c in ipairs(E.cells()) do
		if c.kind == kind and predicate(c) then return c end
	end
	return nil
end

return E
