-- M5-32: ui/x4mp_server.lua, the server screen and the /mp server branch of the chat adapter. What a test can pin without a game is
-- the contract the native launcher relies on: the verb and the topic are registered, every /mp server line is ours, the request body
-- carries the op the native half reads, a payload fills the state the screen and the chat line show, and the buttons cannot send an
-- action the launcher would refuse.
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local lines

local function fresh()
	env.reset()
	env.installApi()
	lines = {}
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua", "x4mp_chat.lua", "x4mp_economy.lua", "x4mp_server.lua" })
	X4MPChat.systemLine = function(text) lines[#lines + 1] = text end
end

local function all() return table.concat(lines, "\n") end

local RUNNING = '{"v":1,"state":"running","pid":4242,"exit_code":0,"listening":true,"port":47780,"http_port":47790,' ..
	'"auto_stop":true,"exe":"C:\\\\X4MP\\\\x4mp-server.exe","data_dir":"C:\\\\X4MP\\\\server-data",' ..
	'"log":"C:\\\\X4MP\\\\logs\\\\server-console.log","error":""}'
local STOPPED = '{"v":1,"state":"stopped","pid":0,"exit_code":0,"listening":false,"port":47780,"http_port":47790,' ..
	'"auto_stop":true,"exe":"C:\\\\X4MP\\\\x4mp-server.exe","data_dir":"C:\\\\X4MP\\\\server-data",' ..
	'"log":"C:\\\\X4MP\\\\logs\\\\server-console.log","error":""}'
local EXITED = '{"v":1,"state":"exited","pid":4242,"exit_code":1,"listening":false,"port":47780,"http_port":47790,' ..
	'"auto_stop":true,"exe":"C:\\\\X4MP\\\\x4mp-server.exe","data_dir":"C:\\\\X4MP\\\\server-data",' ..
	'"log":"C:\\\\X4MP\\\\logs\\\\server-console.log",' ..
	'"error":"the server exited with code 1, see C:\\\\X4MP\\\\logs\\\\server-console.log"}'

--- the last body the bridge raised for the server verb, decoded with the bridge's own json reader
local function sent()
	local raised = env.raisedNamed("x4mp.server")
	truthy(#raised > 0, "no server verb was raised")
	return X4MPBridge.json.decode(raised[#raised])
end

local function rowById(model, id)
	for _, row in ipairs(model.rows) do if row.id == id then return row end end
	return nil
end

local function screen()
	X4MPScreens.go("server")
	return X4MPScreens.buildModel()
end

local function textsOf(model)
	local texts = {}
	for _, row in ipairs(model.rows) do if row.type == "text" then texts[#texts + 1] = row.text end end
	return table.concat(texts, "\n")
end

test("server: the module registers the server verb and topic", function()
	fresh()
	truthy(X4MPBridge.VERBS.server, "verb server is not allowed")
	local found = false
	for _, topic in ipairs(X4MPBridge.TOPICS) do if topic == "server" then found = true end end
	truthy(found, "topic server is not subscribed")
	eq(X4MPServer.state, nil)
end)

test("server: /mp server asks for the status", function()
	fresh()
	truthy(X4MPChat.handleCommand("mp", "server"), "/mp server must be ours")
	local body = sent()
	eq(body.op, "status")
end)

test("server: start and stop send the op the native half reads", function()
	fresh()
	X4MPChat.handleCommand("mp", "server start")
	eq(sent().op, "start")
	X4MPChat.handleCommand("mp", "server stop")
	eq(sent().op, "stop")
	X4MPChat.handleCommand("mp", "server status")
	eq(sent().op, "status")
end)

test("server: a payload fills the state and /mp server prints it as one line", function()
	fresh()
	env.fire("x4mp.server", RUNNING)
	eq(X4MPServer.state.state, "running")

	lines = {}
	X4MPChat.handleCommand("mp", "server status")
	eq(#lines, 1, "one chat line")
	local text = lines[1]
	truthy(text:find("Server running", 1, true), text)
	truthy(text:find("pid 4242", 1, true), text)
	truthy(text:find("port 47780", 1, true), text)
	truthy(text:find("listening", 1, true), text)
	truthy(text:find("C:\\X4MP\\server-data", 1, true), text)
	truthy(text:find("server-console.log", 1, true), text)
end)

test("server: the first /mp server prints the answer when it lands", function()
	fresh()
	X4MPChat.handleCommand("mp", "server")
	eq(#lines, 0, "nothing to print before the answer")
	env.fire("x4mp.server", RUNNING)
	eq(#lines, 1)
	truthy(lines[1]:find("Server running", 1, true), lines[1])
end)

test("server: the screen shows the state and the log, and greys out the action that cannot run", function()
	fresh()
	env.fire("x4mp.server", RUNNING)
	local model = screen()
	eq(model.screen, "server")
	eq(model.title, "Dedicated server")
	local text = textsOf(model)
	truthy(text:find("Server state: running", 1, true), text)
	truthy(text:find("PID 4242, port 47780, listening", 1, true), text)
	truthy(text:find("Admin GUI port 47790", 1, true), text)
	truthy(text:find("server-console.log", 1, true), text)
	falsy(rowById(model, "server_start").active, "the start button is inactive while running")
	truthy(rowById(model, "server_stop").active)

	env.fire("x4mp.server", STOPPED)
	model = X4MPScreens.buildModel()
	truthy(rowById(model, "server_start").active)
	falsy(rowById(model, "server_stop").active, "the stop button is inactive while stopped")
	truthy(rowById(model, "back"), "the screen always has a way back")
end)

test("server: the screen buttons send the same ops as the chat command", function()
	fresh()
	env.fire("x4mp.server", STOPPED)
	local model = screen()
	rowById(model, "server_start").onClick()
	eq(sent().op, "start")

	env.fire("x4mp.server", RUNNING)
	model = X4MPScreens.buildModel()
	rowById(model, "server_stop").onClick()
	eq(sent().op, "stop")

	rowById(model, "server_refresh").onClick()
	eq(sent().op, "status")
end)

test("server: an exited server shows the error text in the screen and the chat line", function()
	fresh()
	env.fire("x4mp.server", EXITED)
	eq(X4MPServer.state.state, "exited")
	local model = screen()
	local found
	for _, row in ipairs(model.rows) do
		if row.type == "text" and row.text:find("exited with code 1", 1, true) then found = row end
	end
	truthy(found, "the screen says why the server is gone")
	eq(found.tone, "error")

	lines = {}
	X4MPChat.handleCommand("mp", "server")
	truthy(all():find("exited with code 1", 1, true), all())
end)

test("server: the main screen opens the server screen without a session", function()
	fresh()
	eq(X4MPBridge.status, nil, "no session is active")
	local model = X4MPScreens.buildModel()
	local row = rowById(model, "server")
	truthy(row, "the main screen has a server button")
	truthy(row.active)
	row.onClick()
	eq(X4MPScreens.state.screen, "server")
	eq(X4MPScreens.buildModel().title, "Dedicated server")
end)

test("server: an unknown subcommand answers with the command list", function()
	fresh()
	X4MPChat.handleCommand("mp", "server bogus")
	eq(#env.raisedNamed("x4mp.server"), 0, "nothing was sent")
	truthy(all():find("/mp server start", 1, true), all())
end)

test("server: every new text id exists in English and Russian", function()
	fresh()
	for id = 614, 636 do
		truthy(env.texts[id], "text " .. id .. " is missing in 0001-l044.xml")
	end
	local ru = env.readFile(env.uiPath("../t/0001-l007.xml"))
	for id = 614, 636 do
		truthy(ru:find('<t id="' .. id .. '">', 1, true), "text " .. id .. " is missing in 0001-l007.xml")
	end
	for body in ru:gmatch('<t id="%d+">(.-)</t>') do
		falsy(body:find("[%(%){}]"), "Russian text contains brackets: " .. body)
	end
end)
