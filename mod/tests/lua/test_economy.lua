-- M5-29/30: ui/x4mp_economy.lua and the /mp branch of the chat adapter. What a test can pin without a game is the contract the
-- native half relies on: the verb and the topic are registered, every /mp line stays in this module, the request body carries the
-- fields the native parse_request reads, the rows the snapshot holds are rendered with the id a command takes, and a trade accept
-- quotes the version the player saw (or the server answers StaleVersion).
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local lines
local toasts

local function fresh()
	env.reset()
	env.installApi()
	lines = {}
	toasts = {}
	-- env.reset nils the global, so the toast recorder goes in after it: the notice path calls it through pcall and a nil global
	-- would silently look like success.
	_G.AddUITriggeredEvent = function(screen, control, value) toasts[#toasts + 1] = { screen, control, value } end
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

-- M5-30: an offer that arrives while the player is doing something else must be visible without opening anything: one system
-- line in the chat window, one toast, and the count on the HUD badge.
test("economy: an incoming loan offer says so in the chat and in a toast", function()
	loaded()
	lines, toasts = {}, {}
	env.fire("x4mp.economy_notice", '{"v":1,"kind":"loan","id":"000000000000000a:000000000000000b","from":2,"amount":5000,"version":0}')
	eq(lines[1], "New loan offer 00000a from player 2, principal 5 000 Cr")
	eq(lines[2], "Answer with /mp accept 00000a or /mp decline 00000a")
	eq(#toasts, 1, "one toast")
	eq(toasts[1][1], "X4MP")
	eq(toasts[1][2], "notify")
	eq(toasts[1][3], lines[1], "the toast carries the same text as the chat line")
end)

test("economy: a trade offer quotes the credits on the table, or leaves them out when there are none", function()
	loaded()
	lines, toasts = {}, {}
	env.fire("x4mp.economy_notice", '{"v":1,"kind":"trade","id":"0000000000000014:0000000000000015","from":2,"amount":1200,"version":3}')
	eq(lines[1], "New trade offer 000014 from player 2, 1 200 Cr on the table")
	eq(toasts[1][3], lines[1])

	lines, toasts = {}, {}
	env.fire("x4mp.economy_notice", '{"v":1,"kind":"trade","id":"0000000000000016:0000000000000017","from":3,"amount":0,"version":1}')
	eq(lines[1], "New trade offer 000016 from player 3")
	eq(#toasts, 1)
end)

test("economy: the badge counts the offers that wait for an answer", function()
	loaded()
	eq(X4MPEconomy.pending(), 1, "the shipped snapshot holds one open incoming trade and no offered loan")

	env.fire("x4mp.economy", '{"v":1,"self":1,"loans":[' ..
		'{"id":"000000000000000a:000000000000000b","lender":2,"borrower":1,"state":0,"principal":5000,"repay_total":5500,' ..
		'"repaid":0,"forgiven":0,"created_us":1,"due_us":2,"memo":"","mine":true,"i_lend":false},' ..
		'{"id":"000000000000000c:000000000000000d","lender":1,"borrower":2,"state":1,"principal":100,"repay_total":100,' ..
		'"repaid":0,"forgiven":0,"created_us":1,"due_us":2,"memo":"","mine":true,"i_lend":true}],' ..
		'"trades":[' ..
		'{"id":"0000000000000014:0000000000000015","version":3,"state":0,"initiator":2,"counterparty":1,"initiator_team":1,' ..
		'"counterparty_team":2,"give":[],"want":[],"expires_us":1,"memo":"","incoming":true,"mine":true},' ..
		'{"id":"0000000000000018:0000000000000019","version":1,"state":5,"initiator":2,"counterparty":1,"initiator_team":1,' ..
		'"counterparty_team":2,"give":[],"want":[],"expires_us":1,"memo":"","incoming":true,"mine":true}]}')
	eq(X4MPEconomy.pending(), 2, "one offered loan and one open incoming trade; the active loan and the completed trade do not count")
end)

test("economy: the notice topic is registered once, next to the snapshot topic", function()
	loaded()
	local economy, notice = 0, 0
	for _, name in ipairs(X4MPBridge.TOPICS) do
		if name == "economy" then economy = economy + 1 end
		if name == "economy_notice" then notice = notice + 1 end
	end
	eq(economy, 1, "the snapshot topic is registered exactly once")
	eq(notice, 1, "the notice topic is registered exactly once")
	truthy(X4MPBridge.VERBS.economy, "the verb the screen sends on")
end)

------------------------------------------------------------------------------
-- M5-29: the economy screen in the standalone window (model rows, no renderer)
------------------------------------------------------------------------------
local function rowById(model, id)
	for _, row in ipairs(model.rows) do if row.id == id then return row end end
	return nil
end

local function screen()
	X4MPScreens.go("economy")
	return X4MPScreens.buildModel(), X4MPScreens.initState().economy
end

test("economy: the screen shows the wallet, the actions, the loan and the incoming trade", function()
	loaded()
	players({ { id = 1, name = "Ann" }, { id = 2, name = "Bob" } }, 1)
	local model = screen()
	eq(model.screen, "economy")
	eq(model.title, "Economy")

	local texts = {}
	for _, row in ipairs(model.rows) do
		if row.type == "text" then texts[#texts + 1] = row.text end
	end
	eq(texts[1], "Wallet: no balance yet.", "the header line comes from the wallet topic")
	truthy(table.concat(texts, "\n"):find("Open loans: 1, incoming trades: 1", 1, true), table.concat(texts, "\n"))
	truthy(table.concat(texts, "\n"):find("Loan 00000a: active, to player 2, repaid 0 of 5 500", 1, true), table.concat(texts, "\n"))
	truthy(table.concat(texts, "\n"):find("Trade 000014: proposed, from player 2, gives 1 200 Cr, wants 500 x 12", 1, true),
		table.concat(texts, "\n"))

	eq(rowById(model, "economy_amount").type, "edit")
	truthy(rowById(model, "economy_deposit").active)
	truthy(rowById(model, "economy_withdraw").active)
	eq(rowById(model, "economy_target").text, "Send to Bob", "the picker names the other player")
	truthy(rowById(model, "economy_send").active)
	-- self lent this loan, so it can forgive it but not repay it
	falsy(rowById(model, "economy_repay_000000000000000a:000000000000000b").active)
	truthy(rowById(model, "economy_forgive_000000000000000a:000000000000000b").active)
	truthy(rowById(model, "economy_accept_0000000000000014:0000000000000015"), "an incoming trade can be accepted")
	truthy(rowById(model, "economy_decline_0000000000000014:0000000000000015"))
	truthy(rowById(model, "back"), "the screen always has a way back")
end)

test("economy: the screen buttons send the same verbs as the /mp commands", function()
	loaded()
	players({ { id = 1, name = "Ann" }, { id = 2, name = "Bob" } }, 1)
	local model, ui = screen()
	ui.amount = "250"

	rowById(model, "economy_deposit").onClick()
	local body = sent()
	eq(body.op, "deposit")
	eq(body.amount, 250)

	rowById(model, "economy_withdraw").onClick()
	body = sent()
	eq(body.op, "withdraw")
	eq(body.amount, 250)

	rowById(model, "economy_send").onClick()
	body = sent()
	eq(body.op, "send")
	eq(body.to, 2, "the picked player, not this node")
	eq(body.amount, 250)

	rowById(model, "economy_forgive_000000000000000a:000000000000000b").onClick()
	body = sent()
	eq(body.op, "forgive")
	eq(body.id, "000000000000000a:000000000000000b")
	eq(body.amount, 0)

	rowById(model, "economy_accept_0000000000000014:0000000000000015").onClick()
	body = sent()
	eq(body.op, "accept")
	eq(body.version, 3, "the version the player saw")

	rowById(model, "economy_decline_0000000000000014:0000000000000015").onClick()
	body = sent()
	eq(body.op, "cancel")
	eq(body.id, "0000000000000014:0000000000000015")
end)

test("economy: an amount that is not a whole number is refused on the screen, before the server", function()
	loaded()
	players({ { id = 1, name = "Ann" }, { id = 2, name = "Bob" } }, 1)
	local model, ui = screen()
	ui.amount = "12.5"
	local before = #env.raisedNamed("x4mp.economy")
	rowById(model, "economy_deposit").onClick()
	eq(#env.raisedNamed("x4mp.economy"), before, "nothing was sent")
	model = X4MPScreens.buildModel()
	local warning
	for _, row in ipairs(model.rows) do
		if row.type == "text" and row.text == "Amount must be a whole number of credits." then warning = row end
	end
	truthy(warning, "the screen says why")
	eq(warning.tone, "warning")
end)

test("economy: the borrower repays what is left, and the tab switches the trade list", function()
	loaded()
	players({ { id = 1, name = "Ann" }, { id = 2, name = "Bob" } }, 1)
	env.fire("x4mp.economy", '{"v":1,"self":1,"loans":[{"id":"000000000000000a:000000000000000b","lender":2,' ..
		'"borrower":1,"state":1,"principal":5000,"repay_total":5500,"repaid":1500,"forgiven":0,"created_us":1,"due_us":2,' ..
		'"memo":"","mine":true,"i_lend":false}],"trades":[]}')
	local model = screen()
	local repay = rowById(model, "economy_repay_000000000000000a:000000000000000b")
	truthy(repay.active, "the borrower can repay")
	repay.onClick()
	local body = sent()
	eq(body.op, "repay")
	eq(body.amount, 4000, "repay_total - repaid")

	ui = X4MPScreens.initState().economy
	eq(ui.tab, "incoming", "the screen opens on the incoming side")
	rowById(model, "economy_tab_out").onClick()
	eq(ui.tab, "outgoing")
	model = X4MPScreens.buildModel()
	truthy(rowById(model, "economy_tab_out").active == false, "the active tab is drawn as the current one")
end)

------------------------------------------------------------------------------
-- M5-31: the ledger list (protocol 0x0816/0x0817; the hub keeps the pages, the snapshot carries them)
------------------------------------------------------------------------------
local LEDGER = '{"v":1,"self":1,"loans":[],"trades":[],"ledger":[' ..
	'{"id":"01J000000000000000000A1","at_us":1700000000000000,"kind":19,"wallet_kind":0,"wallet_owner":1,' ..
	'"delta":1200,"actor":2,"ref_type":"transfer","ref_id":42,"note":"for the fuel","reversed":false},' ..
	'{"id":"01J000000000000000000A0","at_us":1699990000000000,"kind":2,"wallet_kind":1,"wallet_owner":1,' ..
	'"delta":-500,"actor":0,"ref_type":"","ref_id":0,"note":"","reversed":true}],' ..
	'"ledger_more":true,"ledger_pages":1}'

test("economy: /mp ledger asks for the newest page and prints it when it lands", function()
	loaded()
	lines = {}
	X4MPChat.handleCommand("mp", "ledger")
	local body = sent()
	eq(body.op, "ledger")
	eq(body.limit, 20)
	eq(body.before, nil, "the newest page needs no cursor")
	eq(lines[1], "Ledger requested")

	-- the page arrives as a new snapshot: the counter rises, so the armed command prints what it asked for
	lines = {}
	env.fire("x4mp.economy", LEDGER)
	local text = all()
	truthy(text:find("Ledger, newest first", 1, true), text)
	truthy(text:find("Transfer", 1, true), text)
	truthy(text:find("+1 200", 1, true), text)
	truthy(text:find("from player 2", 1, true), text)
	truthy(text:find("for the fuel", 1, true), text)
	truthy(text:find("Pool withdrawal", 1, true), text)
	truthy(text:find("-500", 1, true), text)
	truthy(text:find("reversed", 1, true), text)
	truthy(text:find("Older entries exist: /mp ledger more", 1, true), text)
end)

test("economy: /mp ledger more asks for the page after the oldest row held", function()
	loaded()
	env.fire("x4mp.economy", LEDGER)
	lines = {}
	X4MPChat.handleCommand("mp", "ledger more")
	local body = sent()
	eq(body.op, "ledger")
	eq(body.limit, 20)
	eq(body.before, "01J000000000000000000A0", "the oldest row id is the cursor")
end)

test("economy: the screen shows the ledger and offers the next page only when one exists", function()
	loaded()
	local model = screen()
	truthy(rowById(model, "economy_ledger"), "the screen can ask for the ledger")
	falsy(rowById(model, "economy_ledger_more").active, "no older page is known before one arrives")

	env.fire("x4mp.economy", LEDGER)
	model = X4MPScreens.buildModel()
	local texts = {}
	for _, row in ipairs(model.rows) do
		if row.type == "text" then texts[#texts + 1] = row.text end
	end
	local text = table.concat(texts, "\n")
	truthy(text:find("Transfer", 1, true), text)
	truthy(text:find("Pool withdrawal", 1, true), text)
	truthy(rowById(model, "economy_ledger_more").active, "the server said an older row exists")
	rowById(model, "economy_ledger_more").onClick()
	local body = sent()
	eq(body.op, "ledger")
	eq(body.before, "01J000000000000000000A0")
end)

