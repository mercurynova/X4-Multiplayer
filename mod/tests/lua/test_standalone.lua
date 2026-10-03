local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local SECRET = "hunter2-hunter2"

local function setup()
	env.installApi()
	env.installUi()
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua", "x4mp_ui_standalone.lua" })
	return X4MPScreens, X4MPMenu
end

local function editByLabel(label)
	-- edit cells sit in column 2; the label is the text cell of the same row
	for _, tbl in ipairs(env.lastFrame.tables) do
		for _, row in ipairs(tbl.rows) do
			if row[1].kind == "text" and row[1].text == label and row[2].kind == "edit" then return row[2] end
		end
	end
	return nil
end

local function buttonByText(text)
	return env.findCell("button", function(c) return c.text == text end)
end

test("loading registers the standalone renderer with the screens", function()
	local S, menu = setup()
	eq(S.renderer, menu.renderer)
	eq(menu.renderer.name, "standalone")
	falsy(menu.renderer.isOpen())
end)

test("opening a screen registers the menu once and draws through Helper", function()
	local S, menu = setup()
	eq(S.open("join"), true)
	eq(env.opened[1], "X4MPMenu")
	eq(#_G.Menus, 1)
	eq(env.registeredMenu, menu)
	truthy(menu.renderer.isOpen())
	truthy(env.lastFrame, "frame displayed")
	eq(env.lastFrame.props.layer, 4)
	truthy(env.lastFrame.props.standardButtons.close)
	S.open("status")
	eq(#_G.Menus, 1, "no duplicate registration")
	eq(#env.opened, 1, "an open window is redrawn, not reopened")
end)

test("join screen widgets: password box is textHidden, others are not", function()
	local S = setup()
	S.open("join")
	local address = editByLabel("Server address")
	local name = editByLabel("Player name")
	local pw = editByLabel("Password")
	truthy(address and name and pw)
	eq(pw.props.textHidden, true)
	eq(address.props.textHidden, false)
	eq(name.props.textHidden, false)
	eq(pw.props.selectTextOnActivation, false)
end)

test("typing then clicking Connect sends x4mp.join and the window never keeps the password", function()
	local S, menu = setup()
	S.open("join")
	editByLabel("Server address").handlers.onTextChanged(nil, "play.example.com:5000")
	editByLabel("Player name").handlers.onTextChanged(nil, "Alice")
	editByLabel("Password").handlers.onEditBoxDeactivated(nil, SECRET)
	buttonByText("Connect").handlers.onClick()

	local raised = env.raisedNamed("x4mp.join")
	eq(#raised, 1)
	local p = X4MPBridge.json.decode(raised[1])
	eq(p.address, "play.example.com:5000")
	eq(p.name, "Alice")
	eq(p.password, SECRET)
	-- the window redrew as the status screen; nothing of it contains the password
	eq(menu.model.screen, "status")
	eq(t.findString(menu.model, SECRET), nil)
	for _, c in ipairs(env.cells()) do
		eq(t.findString(c.text or "", SECRET), nil)
		eq(t.findString(c.props or {}, SECRET), nil)
	end
	eq(S.state.password, "")
	eq(t.findString(__X4MP_USER, SECRET), nil)
end)

test("inactive buttons are drawn inactive", function()
	local S = setup()
	env.fire("x4mp.status", '{"state":"connecting"}')
	S.open("join")
	local connect = buttonByText("Connect")
	eq(connect.props.active, false)
end)

test("closing the window clears the password and the model", function()
	local S, menu = setup()
	S.open("join")
	editByLabel("Password").handlers.onTextChanged(nil, SECRET)
	eq(S.state.password, SECRET)
	menu.onCloseElement("close")
	eq(S.state.password, "")
	eq(menu.model, nil)
	falsy(menu.renderer.isOpen())
end)

test("status updates redraw the open window", function()
	local S = setup()
	S.open("status")
	local before = #env.frames
	env.fire("x4mp.status", '{"state":"ingame","server":"h:1"}')
	truthy(#env.frames > before)
	truthy(env.findCell("text", function(c) return c.text == "Server: h:1" end))
end)

test("a missing Helper is logged, not fatal", function()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua", "x4mp_ui_standalone.lua" })
	_G.Helper = nil
	eq(X4MPScreens.open("main"), true)
	t.contains(env.debugText(), "Helper missing")
end)
