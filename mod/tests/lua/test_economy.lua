-- M5-29/30: ui/x4mp_economy.lua and the /mp branch of the chat adapter. What a test can pin without a game is the contract the
-- native half relies on: the verb and the topic are registered, every /mp line stays in this module, the request body carries the
-- fields the native parse_request reads, the rows the snapshot holds are rendered with the id a command takes, and a trade accept
-- quotes the version the player saw (or the server answers StaleVersion).
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local lines

local function fresh()
	env.reset()
	env.installApi()
	lines = {}
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua", "x4mp_players.lua", "x4mp_chat.lua", "x4mp_economy.lua" })
	X4MPChat.systemLine = function(text) lines[#lines + 1] = text end
end

local function all() return table.concat(lines, "\n") end

local function players(list, self)
	local body = {}
	for _, p in ipairs(list) do
		body[#body + 1] = string.format('{"id":%d,"name":%q,"team":%d,"online":true,"in_game":true,"ping":20,"sector":0}', p.id, p.name, p.team or 1)
	end
	env.fire("x4mp.players", string.format('{"v":1,"self":%d,"players":[%s],"events":[]}', self or 1, table.concat(body, ",")))
end

local SNAPSHOT = '{"v":1,"self":1,"loans":[{"id":"000000000000000a:000000000000000b","lender":1,"borrower":2,' ..
	'"state":1,"principal":5000,"repay_total":5500,"repaid":0,"forgiven":0,"created_us":1,"due_us":2,"memo":"x",' ..
	'"mine":true,"i_lend":true}],"trades":[{"id":"0000000000000014:0000000000000015","version":3,"state":0,' ..
	'"initiator":2,"counterparty":1,"initiator_team":1,"counterparty_team":2,"give":[{"kind":0,"amount":1200}],' ..
	'"want":[{"kind":1,"amount":500,"ware":12}],"expires_us":1,"memo":"","incoming":true,"mine":true}],' ..
	'"last":{"ok":true,"status":0,"reason":0,"detail":"","ref":"0:0","key":"2:1"}}'

local function loaded()
	fresh()
	env.fire("x4mp.economy", SNAPSHOT)
end

--- the last body the bridge raised for the economy verb, decoded with the bridge's own json reader
local function sent()
	local raised = env.raisedNamed("x4mp.economy")
	truthy(#raised > 0, "no economy verb was raised")
	return X4MPBridge.json.decode(raised[#raised])
end

test("economy: the module registers the economy verb and topic", function()
	fresh()
	truthy(X4MPBridge.VERBS.economy, "verb economy is not allowed")
	local found = false
	for _, topic in ipairs(X4MPBridge.TOPICS) do if topic == "economy" then found = true end end
	truthy(found, "topic economy is not subscribed")
	eq(X4MPEconomy.snapshot, nil)
end)

test("economy: the topic fills the snapshot and /mp shows the header", function()
	loaded()
	truthy(type(X4MPEconomy.snapshot) == "table", "the snapshot was not taken")
	eq(X4MPEconomy.snapshot.self, 1)

	X4MPChat.handleCommand("mp", "")
	truthy(all():find("Open loans: 1, incoming trades: 1", 1, true), all())
	truthy(all():find("Last request: ok", 1, true), all())
end)

test("economy: the header shows the wallet the HUD already keeps", function()
	loaded()
	_G.X4MPHud = { wallet = { mode = "shared", ["local"] = 1234567, debt = 500 } }
	X4MPChat.handleCommand("mp", "")
	truthy(all():find("Economy: shared, wallet 1 234 567, pool debt 500", 1, true), all())
	_G.X4MPHud = nil
end)

test("economy: /mp without a session says so instead of showing empty tables", function()
	fresh()
	X4MPChat.handleCommand("mp", "")
	eq(#lines, 1)
	truthy(lines[1]:find("No economy data yet", 1, true), lines[1])
end)

test("economy: /mp loans shows the short id the repay command takes", function()
	loaded()
	X4MPChat.handleCommand("mp", "loans")
	truthy(all():find("Open loans:", 1, true), all())
	truthy(all():find("Loan 00000a: active, to player 2, repaid 0 of 5 500", 1, true), all())
end)

test("economy: /mp trades shows the incoming side, /mp outgoing the other", function()
	loaded()
	X4MPChat.handleCommand("mp", "trades")
	truthy(all():find("Incoming trades:", 1, true), all())
	truthy(all():find("Trade 000014: proposed, from player 2, gives 1 200 Cr, wants 500 x 12", 1, true), all())

	lines = {}
	X4MPChat.handleCommand("mp", "outgoing")
	truthy(all():find("Outgoing trades:", 1, true), all())
	truthy(all():find("nothing here", 1, true), all())
end)

test("economy: /mp accept quotes the version the player saw", function()
	loaded()
	X4MPChat.handleCommand("mp", "accept 000014")
	local body = sent()
	eq(body.op, "accept")
	eq(body.id, "0000000000000014:0000000000000015")
	eq(body.version, 3)
	eq(body.v, 1)
end)

test("economy: /mp decline asks for a cancel of the same trade", function()
	loaded()
	X4MPChat.handleCommand("mp", "decline 000014")
	local body = sent()
	eq(body.op, "cancel")
	eq(body.id, "0000000000000014:0000000000000015")
end)

test("economy: /mp send resolves the name and carries the memo", function()
	fresh()
	players({ { id = 1, name = "Me" }, { id = 2, name = "Bob" } }, 1)
	env.fire("x4mp.economy", SNAPSHOT)
	X4MPChat.handleCommand("mp", "send Bob 500 for the fuel")
	local body = sent()
	eq(body.op, "send")
	eq(body.to, 2)
	eq(body.amount, 500)
	eq(body.memo, "for the fuel")
end)

test("economy: a name nobody has and an amount that is not whole are refused locally", function()
	fresh()
	players({ { id = 1, name = "Me" } }, 1)
	env.fire("x4mp.economy", SNAPSHOT)
	X4MPChat.handleCommand("mp", "send Nobody 500")
	eq(#env.raisedNamed("x4mp.economy"), 0)
	truthy(all():find("No player named Nobody", 1, true), all())

	lines = {}
	X4MPChat.handleCommand("mp", "deposit 12.5")
	eq(#env.raisedNamed("x4mp.economy"), 0)
	truthy(all():find("whole number of credits", 1, true), all())
end)

test("economy: deposit, withdraw, repay and forgive carry the fields the native half reads", function()
	loaded()
	X4MPChat.handleCommand("mp", "deposit 1000")
	local body = sent()
	eq(body.op, "deposit")
	eq(body.amount, 1000)

	lines = {}
	X4MPChat.handleCommand("mp", "withdraw 250")
	body = sent()
	eq(body.op, "withdraw")
	eq(body.amount, 250)

	lines = {}
	X4MPChat.handleCommand("mp", "repay 00000a 500")
	body = sent()
	eq(body.op, "repay")
	eq(body.id, "000000000000000a:000000000000000b")
	eq(body.amount, 500)

	lines = {}
	X4MPChat.handleCommand("mp", "forgive 00000a")
	body = sent()
	eq(body.op, "forgive")
	eq(body.id, "000000000000000a:000000000000000b")
	eq(body.amount, 0)
end)

test("economy: an id nobody holds is refused and an unknown subcommand answers with the name", function()
	loaded()
	X4MPChat.handleCommand("mp", "repay deadbeef 500")
	eq(#env.raisedNamed("x4mp.economy"), 0)
	truthy(all():find("No such loan or trade id: deadbeef", 1, true), all())

	lines = {}
	X4MPChat.handleCommand("mp", "bogus")
	truthy(all():find("Unknown /mp command: bogus", 1, true), all())
end)

test("economy: every /mp line stays in this module, other commands do not", function()
	loaded()
	truthy(X4MPChat.handleCommand("mp", "help"), "/mp help must be ours")
	truthy(all():find("/mp shows the economy", 1, true), all())
	falsy(X4MPChat.handleCommand("notacommand", ""), "a foreign command must reach the previous function")
end)
