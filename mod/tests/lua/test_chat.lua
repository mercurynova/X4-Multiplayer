-- M3-06: ui/x4mp_chat.lua against vanilla-shaped and SirNukes-shaped globals: chaining, re-wrapping, unwrapping only if ours, "/" passthrough,
-- /t and /w, author colours, history replay after /reloadui.
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local V -- what the vanilla stubs saw

local function now() return 1760000000000 end

--- A fresh Lua state with vanilla globals; the X4MP files are loaded in ui.xml order.
local function fresh(opts)
	opts = opts or {}
	env.reset()
	env.installApi()
	V = { sent = {}, getCalls = 0, md = {}, refreshes = 0 }
	_G.OnlineGetChatMessages = function()
		V.getCalls = V.getCalls + 1
		return V.vanillaMessages or {}
	end
	_G.OnlineSendChatMessage = function(text, userid) V.sent[#V.sent + 1] = { text, userid } end
	_G.OnlineGetUserName = function() return "Me", 77 end
	_G.AddUITriggeredEvent = function(screen, control, value) V.md[#V.md + 1] = { screen, control, value } end
	V.window = { name = "ChatWindow", shown = true, onChatMessageReceived = function() V.refreshes = V.refreshes + 1 end }
	_G.Menus = { V.window }
	env.cfuncs.GetCurrentUTCDataTime = function() return now() / 1000 end
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua", "x4mp_players.lua", "x4mp_chat.lua" })
	if opts.connect ~= false then env.fire("x4mp.status", '{"v":1,"state":"ingame"}') end
end

local function players(list, self)
	local body = {}
	for _, p in ipairs(list) do
		body[#body + 1] = string.format('{"id":%d,"name":%q,"team":%d,"online":true,"in_game":true,"ping":20,"sector":0}', p.id, p.name, p.team or 1)
	end
	env.fire("x4mp.players", string.format('{"v":1,"self":%d,"players":[%s],"events":[]}', self or 1, table.concat(body, ",")))
end

local function incoming(m, replay)
	local json = string.format('{"v":1,"replay":%s,"messages":[%s]}', replay and "true" or "false", table.concat(type(m) == "table" and m or { m }, ","))
	env.fire("x4mp.chat", json)
end

local function msg(from, name, channel, text, team, self)
	return string.format('{"from":%d,"name":%q,"team":%d,"channel":%q,"text":%q,"t":1,"self":%s}', from, name, team or 1, channel, text,
		self and "true" or "false")
end

local function sentVerbs()
	local out = {}
	for _, raw in ipairs(env.raisedNamed("x4mp.chat_send")) do out[#out + 1] = X4MPBridge.json.decode(raw) end
	return out
end

local function stripEscapes(s) return (s:gsub("\027#%x%x%x%x%x%x%x%x#", ""):gsub("\027X", "")) end

test("chat: nothing is wrapped outside a session; the wrappers go on with the first active status and off at the end", function()
	fresh({ connect = false })
	local get, send, exec = OnlineGetChatMessages, OnlineSendChatMessage, ExecuteDebugCommand
	env.fire("x4mp.status", '{"v":1,"state":"connecting"}')
	eq(OnlineGetChatMessages, get)
	eq(OnlineSendChatMessage, send)
	eq(ExecuteDebugCommand, exec)
	env.fire("x4mp.status", '{"v":1,"state":"ingame"}')
	truthy(OnlineGetChatMessages ~= get)
	truthy(OnlineSendChatMessage ~= send)
	truthy(ExecuteDebugCommand ~= exec)
	env.fire("x4mp.status", '{"v":1,"state":"disconnected"}')
	eq(OnlineGetChatMessages, get, "unwrapped: ours was on top")
	eq(OnlineSendChatMessage, send)
	eq(ExecuteDebugCommand, exec)
end)

test("chat: the wrappers are idempotent (no stacking on repeated ensure / status / gfx_ok)", function()
	fresh()
	local get, send, exec = OnlineGetChatMessages, OnlineSendChatMessage, ExecuteDebugCommand
	for _ = 1, 5 do
		env.fire("x4mp.status", '{"v":1,"state":"ingame"}')
		env.fire("gfx_ok")
		env.fire("show")
		X4MPChat.install()
	end
	eq(OnlineGetChatMessages, get)
	eq(OnlineSendChatMessage, send)
	eq(ExecuteDebugCommand, exec)
	eq(#X4MPChat.wrappers.OnlineGetChatMessages, 1)
end)

test("chat: an incoming line shows with the author in the team colour, refreshes the window and no toast while it is open", function()
	fresh()
	players({ { id = 1, name = "Me", team = 1 }, { id = 2, name = "Alice", team = 2 } }, 1)
	incoming(msg(2, "Alice", "all", "hello pilots", 2))
	local list = OnlineGetChatMessages()
	eq(#list, 1)
	local m = list[1]
	eq(m.author, "\027#ffff5a4a#Alice", "team 2 palette colour")
	eq(m.text, "hello pilots")
	eq(m.authorid, -102)
	eq(m.reported, false)
	eq(m.isprivate, false)
	truthy(type(m.time) == "number")
	eq(V.refreshes, 1)
	eq(#V.md, 0, "the chat window is shown: no toast")
end)

test("chat: own lines carry the user id so the window treats them as mine", function()
	fresh()
	incoming(msg(1, "Me", "all", "my own line", 1, true))
	eq(OnlineGetChatMessages()[1].authorid, 77)
end)

test("chat: team and whisper lines carry a channel prefix, system lines come from X4MP in the notice colour", function()
	fresh()
	incoming({ msg(2, "Alice", "team", "go"), msg(2, "Alice", "whisper", "psst"),
		'{"from":0,"name":"server","team":0,"channel":"system","text":"You are muted.","t":0,"self":false}' })
	local list = OnlineGetChatMessages()
	eq(list[1].text, "[Team] go")
	eq(list[2].text, "[Whisper] psst")
	eq(stripEscapes(list[3].author), "server")
	t.contains(list[3].author, "ffcc33")
	eq(list[3].text, "You are muted.")
end)

test("chat: a local notice code is shown as the localised text", function()
	fresh()
	incoming('{"from":0,"name":"","team":0,"channel":"system","text":"english fallback","t":0,"self":false,"code":"not_connected"}')
	local m = OnlineGetChatMessages()[1]
	eq(stripEscapes(m.author), "X4MP")
	t.contains(m.text, "Not connected to a multiplayer session")
	incoming('{"from":0,"name":"","team":0,"channel":"system","text":"x","t":0,"self":false,"code":"no_recipient"}')
	t.contains(OnlineGetChatMessages()[2].text, "/w name message")
end)

test("chat: with the window closed an incoming line from another player raises one toast; replayed and own lines never do", function()
	fresh()
	V.window.shown = nil
	incoming(msg(2, "Alice", "all", "are you there"))
	eq(#V.md, 1)
	eq(V.md[1][1], "X4MP")
	eq(V.md[1][2], "notify")
	eq(V.md[1][3], "Alice: are you there")
	incoming(msg(1, "Me", "all", "mine", 1, true))
	incoming(msg(2, "Alice", "all", "old"), true)
	eq(#V.md, 1)
	eq(V.refreshes, 0, "a closed window is not refreshed")
	local long = string.rep("x", 100)
	incoming(msg(2, "Alice", "all", long))
	eq(#V.md[2][3] < 100, true, "long texts are cut in the toast")
end)

test("chat: lines are merged with the previous function's result, by time", function()
	fresh()
	V.vanillaMessages = { { author = "Venture", authorid = 5, time = 1, text = "first", reported = false },
		{ author = "Venture", authorid = 5, time = 99999999999999, text = "last", reported = false } }
	incoming(msg(2, "Alice", "all", "middle"))
	local list = OnlineGetChatMessages()
	eq(#list, 3)
	eq(list[1].text, "first")
	eq(list[2].text, "middle")
	eq(list[3].text, "last")
end)

test("chat: a previous function that fails or returns rubbish does not break the window", function()
	fresh({ connect = false })
	_G.OnlineGetChatMessages = function() error("ventures offline") end
	env.fire("x4mp.status", '{"v":1,"state":"ingame"}')
	incoming(msg(2, "Alice", "all", "still works"))
	eq(#OnlineGetChatMessages(), 1)
	eq(X4MPChat.busy.get, false, "the re-entrancy guard is released after an error")
end)

test("chat: a typed line goes to the session on channel all and never to the previous function", function()
	fresh()
	OnlineSendChatMessage("hello all", 0)
	eq(#V.sent, 0)
	local sent = sentVerbs()
	eq(#sent, 1)
	eq(sent[1].channel, "all")
	eq(sent[1].text, "hello all")
	eq(sent[1].to, nil)
	eq(sent[1].v, 1)
	OnlineSendChatMessage("   ", 0)
	eq(#sentVerbs(), 1, "an empty line sends nothing")
end)

test("chat: a private Ventures tab (userid > 0) and any typed line outside a session pass through", function()
	fresh()
	OnlineSendChatMessage("to a friend", 4242)
	eq(#V.sent, 1)
	eq(V.sent[1][1], "to a friend")
	eq(V.sent[1][2], 4242)
	eq(#sentVerbs(), 0)
	env.fire("x4mp.status", '{"v":1,"state":"disconnected"}')
	OnlineSendChatMessage("offline line", 0)
	eq(#V.sent, 2)
	eq(#sentVerbs(), 0)
end)

test("chat: /t sends team chat, /t without text explains", function()
	fresh()
	ExecuteDebugCommand("t", "form up on me")
	local sent = sentVerbs()
	eq(sent[1].channel, "team")
	eq(sent[1].text, "form up on me")
	eq(#env.commands, 0, "the command was ours: nothing reaches the previous function")
	ExecuteDebugCommand("t", "  ")
	eq(#sentVerbs(), 1)
	t.contains(OnlineGetChatMessages()[1].text, "/t message")
end)

test("chat: /x4mp knowledge asks the knowledge probe (M3-23); other /x4mp commands pass through", function()
	fresh()
	env.load("x4mp_diag.lua")
	ExecuteDebugCommand("x4mp", "knowledge")
	eq(#env.raisedNamed("x4mp.knowledge_cmd"), 1)
	eq(#env.commands, 0, "the command was ours")
	ExecuteDebugCommand("x4mp", "status") -- not ours: the menu's own /x4mp wrapper below ours handles it, the probe is not asked again
	eq(#env.raisedNamed("x4mp.knowledge_cmd"), 1)
end)

test("chat (M3-28): /x4mp knowledge watch toggles, 'watch off' stops, the plain probe is unchanged", function()
	fresh()
	env.load("x4mp_diag.lua")
	ExecuteDebugCommand("x4mp", "knowledge watch")
	ExecuteDebugCommand("x4mp", "Knowledge Watch Off")
	ExecuteDebugCommand("x4mp", "knowledge watch on")
	local sent = env.raisedNamed("x4mp.knowledge_cmd")
	eq(#sent, 3)
	t.contains(sent[1], '"watch":"toggle"')
	t.contains(sent[2], '"watch":"off"')
	t.contains(sent[3], '"watch":"on"')
	eq(#env.commands, 0, "the commands were ours")
	ExecuteDebugCommand("x4mp", "knowledge watch maybe") -- not a watch command: not ours
	eq(#env.raisedNamed("x4mp.knowledge_cmd"), 3)
end)

test("chat: /w resolves the player (blanks in names, unique prefix) and refuses unknown names", function()
	fresh()
	players({ { id = 1, name = "Me" }, { id = 2, name = "Bob Smith" }, { id = 3, name = "Carol" } }, 1)
	ExecuteDebugCommand("w", "Bob Smith meet at the gate")
	ExecuteDebugCommand("w", "car hello")
	local sent = sentVerbs()
	eq(sent[1].channel, "whisper")
	eq(sent[1].to, 2)
	eq(sent[1].text, "meet at the gate")
	eq(sent[2].to, 3)
	eq(sent[2].text, "hello")
	ExecuteDebugCommand("w", "Nobody hi")
	ExecuteDebugCommand("w", "Carol")
	ExecuteDebugCommand("w", "")
	eq(#sentVerbs(), 2)
	local list = OnlineGetChatMessages()
	t.contains(list[1].text, 'No online player matches "Nobody"')
	t.contains(list[2].text, "/w name message")
	eq(#env.commands, 0)
end)

test("chat: every other /command passes through untouched, arguments included; so does /x4mp", function()
	fresh()
	ExecuteDebugCommand("x4mpspike", "ping now")
	ExecuteDebugCommand("rui", "")
	eq(#env.commands, 2)
	eq(env.commands[1][1], "x4mpspike")
	eq(env.commands[1][2], "ping now")
	eq(env.commands[2][1], "rui")
	X4MPScreens.setRenderer({ name = "test", isOpen = function() return false end, close = function() end, show = function() end })
	ExecuteDebugCommand("x4mp", "status") -- the menu's own wrapper below ours still gets it
	eq(#env.commands, 2, "handled by the menu wrapper, not forwarded")
	eq(#sentVerbs(), 0)
end)

test("chat: a failed send puts a system line in the window", function()
	fresh()
	_G.__X4NATIVE_API = nil -- the bridge has no native side
	OnlineSendChatMessage("lost line", 0)
	t.contains(OnlineGetChatMessages()[1].text, "Not connected to a multiplayer session")
end)

-- SirNukes' Chat Window API replaces OnlineGetChatMessages / OnlineSendChatMessage and wraps ExecuteDebugCommand (sn/ui/chat_window/interface.lua),
-- late, after its own loader signal. Shapes only: no code of it is used here.
local function installSirNukes(ownRing)
	local sn = { sent = {}, cmds = {} }
	local prevExec = ExecuteDebugCommand
	_G.OnlineGetChatMessages = function() return ownRing end -- replaces wholesale: it returns only its own ring buffer
	_G.OnlineSendChatMessage = function(text, userid) sn.sent[#sn.sent + 1] = { text, userid } end
	_G.OnlineGetUserName = function() return "SnUser", 1 end
	_G.ExecuteDebugCommand = function(cmd, param, ...)
		if cmd == "rui" then
			sn.cmds[#sn.cmds + 1] = cmd
			return
		end
		return prevExec(cmd, param, ...)
	end
	sn.exec = _G.ExecuteDebugCommand
	return sn
end

test("chat: SirNukes installs after us and replaces the globals: the next status re-wraps on top and its lines stay visible", function()
	fresh()
	incoming(msg(2, "Alice", "all", "ours"))
	local ring = { { author = "SN", authorid = 1, time = 5, text = "sn line", reported = false } }
	local sn = installSirNukes(ring)
	local replaced = OnlineGetChatMessages
	env.fire("x4mp.status", '{"v":1,"state":"ingame"}') -- the heartbeat
	truthy(OnlineGetChatMessages ~= replaced, "wrapped again on top of SirNukes' function")
	local list = OnlineGetChatMessages()
	eq(#list, 2)
	eq(list[1].text, "sn line")
	eq(list[2].text, "ours")
	-- typing: ours handles the plain line, SirNukes' /command still works through the chain
	OnlineSendChatMessage("via us", 0)
	eq(#sn.sent, 0)
	eq(sentVerbs()[1].text, "via us")
	ExecuteDebugCommand("rui", "")
	eq(#sn.cmds, 1)
	ExecuteDebugCommand("t", "team line")
	eq(sentVerbs()[2].channel, "team")
	ExecuteDebugCommand("other", "x")
	eq(env.commands[#env.commands][1], "other", "an unknown command reaches the bottom of the chain")
end)

test("chat: SirNukes wraps on top of ours: no duplicated lines, no double send", function()
	fresh()
	incoming(msg(2, "Alice", "all", "ours"))
	local ourGet, ourSend, ourExec = OnlineGetChatMessages, OnlineSendChatMessage, ExecuteDebugCommand
	local snLines = { { author = "SN", authorid = 1, time = 5, text = "sn line", reported = false } }
	_G.OnlineGetChatMessages = function(...)
		local out = {}
		for _, m in ipairs(ourGet(...)) do out[#out + 1] = m end
		for _, m in ipairs(snLines) do out[#out + 1] = m end
		return out
	end
	_G.OnlineSendChatMessage = function(...) return ourSend(...) end
	_G.ExecuteDebugCommand = function(...) return ourExec(...) end
	env.fire("x4mp.status", '{"v":1,"state":"ingame"}') -- re-wrap on top of their wrapper
	local list = OnlineGetChatMessages()
	eq(#list, 2, "our line exactly once")
	eq(list[1].text, "sn line")
	eq(list[2].text, "ours")
	OnlineSendChatMessage("once", 0)
	eq(#sentVerbs(), 1, "sent exactly once")
	ExecuteDebugCommand("t", "once too")
	eq(#sentVerbs(), 2)
end)

test("chat: unwrap only if ours: a wrapper under somebody else's stays in place, inert and transparent", function()
	fresh({ connect = false })
	local vanillaSend, vanillaExec = OnlineSendChatMessage, ExecuteDebugCommand
	env.fire("x4mp.status", '{"v":1,"state":"ingame"}')
	incoming(msg(2, "Alice", "all", "ours"))
	local ourGet = OnlineGetChatMessages
	_G.OnlineGetChatMessages = function(...) return ourGet(...) end -- somebody wrapped on top
	local theirs = OnlineGetChatMessages
	env.fire("x4mp.status", '{"v":1,"state":"disconnected"}')
	eq(OnlineGetChatMessages, theirs, "their global is not touched")
	eq(OnlineSendChatMessage, vanillaSend, "the send wrapper was on top and is restored")
	eq(ExecuteDebugCommand, vanillaExec)
	local list = OnlineGetChatMessages()
	eq(#list, 0, "the left-in-place wrapper no longer adds lines")
	eq(V.getCalls >= 1, true, "it delegates to the vanilla function")
	-- a new session wraps again on top
	env.fire("x4mp.status", '{"v":1,"state":"ingame"}')
	incoming(msg(2, "Alice", "all", "again"))
	eq(#OnlineGetChatMessages(), 1)
end)

test("chat: the lines of an ended session are gone, a reconnect blip keeps them", function()
	fresh()
	incoming(msg(2, "Alice", "all", "kept"))
	env.fire("x4mp.status", '{"v":1,"state":"connecting"}')
	eq(#X4MPChat.ring, 1, "connecting again keeps the ring")
	env.fire("x4mp.status", '{"v":1,"state":"disconnected"}')
	eq(#X4MPChat.ring, 0)
end)

test("chat: /reloadui: a fresh Lua state replays the history silently and wraps again with the first status", function()
	fresh({ connect = false })
	incoming({ msg(2, "Alice", "all", "one"), msg(3, "Cy", "all", "two") }, true) -- native replay on ui_ready, before any status
	eq(#V.md, 0)
	truthy(OnlineGetChatMessages ~= nil)
	env.fire("x4mp.status", '{"v":1,"state":"ingame"}')
	local list = OnlineGetChatMessages()
	eq(#list, 2)
	eq(list[1].text, "one")
	eq(list[2].text, "two")
	incoming({ msg(2, "Alice", "all", "one"), msg(3, "Cy", "all", "two") }, true) -- a second replay does not duplicate
	eq(#OnlineGetChatMessages(), 2)
end)

test("chat: a replacing global that is missing leaves the other two wrapped and reports it", function()
	fresh({ connect = false })
	_G.OnlineGetChatMessages = nil
	local res = X4MPChat.install()
	eq(res.OnlineGetChatMessages, "missing_global")
	eq(res.OnlineSendChatMessage, "installed")
end)

test("chat: the ring is bounded", function()
	fresh()
	for i = 1, X4MPChat.RING_MAX + 25 do incoming(msg(2, "Alice", "all", "n" .. i)) end
	local list = OnlineGetChatMessages()
	eq(#list, X4MPChat.RING_MAX)
	eq(list[#list].text, "n" .. (X4MPChat.RING_MAX + 25))
end)

test("chat: join / leave lines from the player table appear in the window as X4MP lines", function()
	fresh()
	env.fire("x4mp.players", '{"v":1,"self":1,"players":[],"events":[{"kind":"join","id":2,"name":"Bob"}]}')
	local m = OnlineGetChatMessages()[1]
	eq(stripEscapes(m.author), "X4MP")
	eq(m.text, "Bob joined the session")
	eq(#V.md, 1, "and one HUD notification")
end)
