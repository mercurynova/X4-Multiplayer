-- x4mp_economy.lua : the economy screen and the /mp commands (M5-29, M5-30; docs/mod-design.md 12.7, docs/m5-plan.md W7).
--
-- The server decides every economy action; this module only asks and shows. The native half (features/economy_client) publishes
-- topic x4mp.economy {"v":1,"self":<id>,"loans":[...],"trades":[...],"last":{...},"last_trade":{...}} on every change, and takes
-- verb x4mp.economy {"op":"send|deposit|withdraw|repay|forgive|accept|cancel|loan_offer|loan_respond","to":<id>,
-- "amount":<credits>,"id":"<lo:hi>",...}. "loan_offer" takes amount, repay_total, due (seconds) and memo; "loan_respond"
-- takes id and accept.
--
-- The screen is the HUD's economy block plus these chat commands, because the standalone menu renderer is disabled while its
-- close path is being fixed (docs/in-game-session-8.md 8.5): a team can see the wallet, the pool debt, the open loans and the
-- trades that wait for an answer, and act on all of them, without a window that can crash the game.
--
-- Every command answers with a system line in the vanilla chat window, and a refusal from the server arrives as x4mp.notify.

local B = X4MPBridge
local C = X4MPChat

local E = { snapshot = nil }
X4MPEconomy = E

-- a chat /mp ledger is waiting for its page, and how many pages the native half has taken so far: a rising count means
-- the snapshot in hand carries a page the player has not seen yet
local await_ledger = false
local ledger_pages = 0

local function T(id, ...)
	if type(ReadText) ~= "function" then return tostring(id) end
	local text = ReadText(92000, id)
	if text == nil or text == "" then return tostring(id) end
	if select("#", ...) > 0 then
		local ok, out = pcall(string.format, text, ...)
		if ok then return out end
	end
	return text
end

local function trim(s) return (tostring(s or ""):gsub("^%s+", ""):gsub("%s+$", "")) end

local function say(text)
	if C and type(C.systemLine) == "function" then C.systemLine(text) end
end

--- credits as a grouped number: 1234567 -> 1 234 567
local function money(n)
	local s = tostring(math.floor(tonumber(n) or 0))
	local sign = ""
	if s:sub(1, 1) == "-" then sign, s = "-", s:sub(2) end
	local out = s:reverse():gsub("(%d%d%d)", "%1 "):reverse():gsub("^%s+", ""):gsub("%s+$", "")
	return sign .. out
end

--- a request or trade id as the short form the commands take: the last six hex digits of lo:hi
local function shortId(id)
	local s = tostring(id or "")
	local lo = s:match("^([0-9a-fA-F]+):") or s
	if #lo > 6 then lo = lo:sub(-6) end
	return lo:lower()
end

local LOAN_STATE = { [0] = 535, [1] = 536, [2] = 537, [3] = 538, [4] = 539, [5] = 540, [6] = 541, [7] = 542, [8] = 543 }
local TRADE_STATE = { [0] = 544, [1] = 545, [2] = 546, [3] = 547, [4] = 548, [5] = 549, [6] = 550, [7] = 542,
	[8] = 541, [9] = 552 }

local function loanState(state) return T(LOAN_STATE[tonumber(state) or -1] or 552) end
local function tradeState(state) return T(TRADE_STATE[tonumber(state) or -1] or 552) end

--- a trade is open while the two sides can still act on it (5..9 are completed, rolled back, in doubt, expired, cancelled)
local function openTrade(state)
	state = tonumber(state) or -1
	return state ~= 5 and state ~= 6 and state ~= 7 and state ~= 8 and state ~= 9
end

--- the ledger kinds in wire order (protocol/schema/economy.fbs LedgerKind), one text per kind, 606 for a kind we do not know
local LEDGER_KIND = { [0] = 586, [1] = 587, [2] = 588, [3] = 589, [4] = 590, [5] = 591, [6] = 592,
	[7] = 593, [8] = 594, [9] = 595, [10] = 596, [11] = 597, [12] = 598, [13] = 599, [14] = 600,
	[15] = 601, [16] = 602, [17] = 603, [18] = 604, [19] = 605, [20] = 606 }

local function ledgerKind(kind) return T(LEDGER_KIND[tonumber(kind) or -1] or 606) end

--- the time a row shows: the hub formats it on the player's own clock, because this Lua has no os.date at all (os is nil)
local function clock(row)
	local at = type(row.at) == "string" and row.at or ""
	if at ~= "" then return at end
	-- a snapshot from an older DLL carries only the raw stamp: show seconds rather than a wrong hour
	local secs = math.floor((tonumber(row.at_us) or 0) / 1000000)
	return secs > 0 and tostring(secs) or "?"
end

--- one ledger row as one line: "12:04  Transfer  +1 200, from player 3"
local function ledgerLine(row)
	local delta = tonumber(row.delta) or 0
	local text = T(607, clock(row), ledgerKind(row.kind), (delta < 0 and "-" or "+") .. money(math.abs(delta)))
	if (tonumber(row.actor) or 0) > 0 then text = text .. ", " .. T(561, tostring(row.actor)) end
	if row.reversed then text = text .. ", " .. T(608) end
	if type(row.note) == "string" and row.note ~= "" then text = text .. ", " .. row.note end
	return text
end

--- the items of one side as one phrase: "1 200 Cr", "500 x energy cells", "ship 12"
local function items(list)
	if type(list) ~= "table" or #list == 0 then return T(554) end
	local parts = {}
	for _, item in ipairs(list) do
		local kind = tonumber(item.kind) or 0
		if kind == 0 then
			parts[#parts + 1] = money(item.amount) .. " Cr"
		elseif kind == 1 then
			parts[#parts + 1] = tostring(item.amount) .. " x " .. tostring(item.ware or item.ware_ref or "?")
		elseif kind == 2 then
			parts[#parts + 1] = T(555, tostring(item.asset or "?"))
		else
			parts[#parts + 1] = T(556, tostring(item.asset or "?"))
		end
	end
	return table.concat(parts, ", ")
end

--- the wallet line: the mode and the money, from the wallet topic the HUD already keeps
local function walletLine()
	local w = rawget(_G, "X4MPHud") and X4MPHud.wallet or nil
	if type(w) ~= "table" then return T(557) end
	local mode = (w.mode == "shared") and T(558) or T(559)
	local text = T(517, mode, money(w["local"]), money(w.debt or 0))
	return text
end

--- E.header(): the screen header - mode, wallet, pool debt, and how many rows wait for an answer
function E.header()
	local s = E.snapshot
	if type(s) ~= "table" then
		say(T(518))
		return
	end
	say(walletLine())
	local loans, incoming = 0, 0
	for _, row in ipairs(s.loans or {}) do
		if row.state == 0 or row.state == 1 or row.state == 4 then loans = loans + 1 end
	end
	for _, row in ipairs(s.trades or {}) do
		if row.incoming and openTrade(row.state) then
			incoming = incoming + 1
		end
	end
	say(T(519, loans, incoming))
	if type(s.last) == "table" then
		local verdict = s.last.ok and T(563) or T(564, tostring(s.last.reason or "?"), tostring(s.last.detail or ""))
		say(T(565, verdict))
	end
end

--- E.loans(): one line per open loan, with the id the repay/forgive commands take
function E.loans()
	local s = E.snapshot
	if type(s) ~= "table" then say(T(518)) return end
	local shown = 0
	say(T(532))
	for _, row in ipairs(s.loans or {}) do
		if row.state == 0 or row.state == 1 or row.state == 4 then
			shown = shown + 1
			local side = row.i_lend and T(560, tostring(row.borrower)) or T(561, tostring(row.lender))
			say(T(566, shortId(row.id), loanState(row.state), side, money(row.repaid), money(row.repay_total)))
		end
	end
	if shown == 0 then say(T(553)) end
end

--- E.trades(incoming): one line per trade that is still open, the id the accept/decline commands take
function E.trades(incoming)
	local s = E.snapshot
	if type(s) ~= "table" then say(T(518)) return end
	say(incoming and T(533) or T(534))
	local shown = 0
	for _, row in ipairs(s.trades or {}) do
		if openTrade(row.state) and (row.incoming and true or false) == (incoming and true or false) then
			shown = shown + 1
			local other = incoming and T(561, tostring(row.initiator)) or T(560, tostring(row.counterparty))
			local gives, wants = row.give, row.want
			if not incoming then gives, wants = row.want, row.give end
			say(T(567, shortId(row.id), tradeState(row.state), other, items(gives), items(wants)))
		end
	end
	if shown == 0 then say(T(553)) end
end

--- E.send(op, params) -> ok, err: one request to the server, with the fields the native half reads
function E.send(op, params)
	if type(B) ~= "table" or type(B.send) ~= "function" then return false, "no bridge" end
	local body = { op = op }
	for _, key in ipairs({ "to", "amount", "id", "version", "asset", "memo", "limit", "before",
		"repay_total", "due", "auto", "accept" }) do
		if params and params[key] ~= nil then body[key] = params[key] end
	end
	local ok, err = B.send("economy", body)
	if not ok then say(T(562)) end
	return ok, err
end

--- E.ledger(): the newest transactions this node may see, as the server sent them - newest first
function E.ledger()
	local s = E.snapshot
	if type(s) ~= "table" then say(T(518)) return end
	say(T(609))
	local rows = s.ledger
	if type(rows) ~= "table" or #rows == 0 then
		say(T(553))
		return
	end
	for _, row in ipairs(rows) do say(ledgerLine(row)) end
	if s.ledger_more then say(T(610)) end
end

--- ask the server for a page of the ledger: the newest one, or the next older one after the oldest row we hold
local function requestLedger(next_page, chat)
	local s = E.snapshot
	local rows = type(s) == "table" and s.ledger or nil
	local body = { limit = 20 }
	if next_page and type(rows) == "table" and #rows > 0 then
		body.before = tostring(rows[#rows].id)
	end
	await_ledger = chat == true
	local ok = E.send("ledger", body)
	if not ok then
		await_ledger = false
		return false
	end
	if chat then say(T(613)) end
	return true
end

--- the id a command named: an exact lo:hi, or the short form matched against the rows we hold
local function resolveId(text)
	local raw = trim(text)
	if raw == "" then return nil end
	local s = E.snapshot
	if type(s) == "table" then
		for _, row in ipairs(s.loans or {}) do
			if tostring(row.id) == raw or shortId(row.id) == raw:lower() then return row.id end
		end
		for _, row in ipairs(s.trades or {}) do
			if tostring(row.id) == raw or shortId(row.id) == raw:lower() then return row.id end
		end
	end
	if raw:match("^[0-9a-fA-F]+:[0-9a-fA-F]+$") then return raw end
	return nil
end

--- the trade a command named, with its version: TradeAccept must quote the version the player saw, or the server
--- answers StaleVersion (economy.fbs:177)
local function resolveTrade(text)
	local raw = trim(text)
	if raw == "" then return nil end
	local s = E.snapshot
	if type(s) == "table" then
		for _, row in ipairs(s.trades or {}) do
			if tostring(row.id) == raw or shortId(row.id) == raw:lower() then return row end
		end
	end
	if raw:match("^[0-9a-fA-F]+:[0-9a-fA-F]+$") then return { id = raw } end
	return nil
end

--- the loan offer a command named, but only one this node may answer: state Offered and the borrower is us
local function resolveLoanOffer(text)
	local raw = trim(text)
	if raw == "" then return nil end
	local s = E.snapshot
	if type(s) ~= "table" then return nil end
	for _, row in ipairs(s.loans or {}) do
		if row.state == 0 and row.borrower == s.self and (tostring(row.id) == raw or shortId(row.id) == raw:lower()) then
			return row
		end
	end
	return nil
end

local function amount(text)
	local n = tonumber(trim(text))
	if not n or n ~= math.floor(n) or n <= 0 then return nil end
	return math.floor(n)
end

local function playerId(name)
	local P = rawget(_G, "X4MPPlayers")
	if type(P) ~= "table" then return nil end
	local row = type(P.findByName) == "function" and P.findByName(name) or nil
	return row and row.id or nil
end

--- the players this node can send credits to: everybody else the roster knows and still sees online
local function peers()
	local P = rawget(_G, "X4MPPlayers")
	local out = {}
	if type(P) ~= "table" or type(P.list) ~= "table" then return out end
	for _, p in ipairs(P.list) do
		if p.id and p.id ~= P.self and p.online ~= false then out[#out + 1] = p end
	end
	return out
end

--- redraw the window after a row changed its own state (a tab, the picked recipient, a refused amount)
local function refresh()
	local M = rawget(_G, "X4MPScreens")
	if type(M) == "table" and type(M.render) == "function" then M.render() end
end

--- E.screenRows(rows, st): the rows of the economy screen (M5-29, docs/mod-design.md 12.7). st is the menu state table, which keeps
--- the amount box, the picked recipient and the trade tab between draws. Every action goes through the same verbs the /mp commands
--- use, so the two ways into the economy cannot drift apart.
function E.screenRows(rows, st)
	local ui = st and st.economy
	if type(ui) ~= "table" then
		ui = { amount = "", tab = "incoming", pick = 1, notice = nil }
		if st then st.economy = ui end
	end
	local function add(r) rows[#rows + 1] = r end
	local function line(t, tone) add({ type = "text", text = t, tone = tone or "normal" }) end
	local function button(id, t, active, onClick)
		add({ type = "button", id = id, text = t, active = active ~= false, onClick = onClick })
	end

	local s = E.snapshot
	line(walletLine())
	if type(s) ~= "table" then
		line(T(518), "warning")
	else
		local loans, incoming = 0, 0
		for _, row in ipairs(s.loans or {}) do
			if row.state == 0 or row.state == 1 or row.state == 4 then loans = loans + 1 end
		end
		for _, row in ipairs(s.trades or {}) do
			if row.incoming and openTrade(row.state) then incoming = incoming + 1 end
		end
		line(T(519, loans, incoming))
	end
	if ui.notice then line(ui.notice, "warning") end

	-- one amount box drives deposit, withdraw and send; a value that is not a whole number of credits is refused here
	add({ type = "edit", id = "economy_amount", label = T(579), value = ui.amount, maxChars = 12, description = T(579),
		onChange = function(v) ui.amount = v end })
	local function credits()
		local n = amount(ui.amount)
		ui.notice = n and nil or T(530)
		if not n then refresh() end
		return n
	end
	button("economy_deposit", T(577), true, function()
		local n = credits()
		if n then E.send("deposit", { amount = n }) end
	end)
	button("economy_withdraw", T(578), true, function()
		local n = credits()
		if n then E.send("withdraw", { amount = n }) end
	end)

	local list = peers()
	if #list == 0 then
		line(T(515), "inactive")
	else
		if ui.pick < 1 or ui.pick > #list then ui.pick = 1 end
		local target = list[ui.pick]
		button("economy_target", T(580, tostring(target.name or target.id)), true, function()
			ui.pick = (ui.pick % #list) + 1
			refresh()
		end)
		button("economy_send", T(581), true, function()
			local n = credits()
			if n then E.send("send", { to = target.id, amount = n }) end
		end)
		-- an interest-free loan of the amount in the box: the borrower answers it with /mp accept
		button("economy_offer", T(968), true, function()
			local n = credits()
			if n then E.send("loan_offer", { to = target.id, amount = n }) end
		end)
	end

	-- loans: the borrower can repay what is left, the lender can forgive the rest
	line(T(532))
	local shown = 0
	for _, row in ipairs((s and s.loans) or {}) do
		if row.state == 0 or row.state == 1 or row.state == 4 then
			shown = shown + 1
			local side = row.i_lend and T(560, tostring(row.borrower)) or T(561, tostring(row.lender))
			line(T(566, shortId(row.id), loanState(row.state), side, money(row.repaid), money(row.repay_total)))
			local left = (tonumber(row.repay_total) or 0) - (tonumber(row.repaid) or 0)
			button("economy_repay_" .. tostring(row.id), T(582), (row.i_lend ~= true) and left > 0, function()
				E.send("repay", { id = row.id, amount = math.floor(left) })
			end)
			button("economy_forgive_" .. tostring(row.id), T(583), row.i_lend == true, function()
				E.send("forgive", { id = row.id, amount = 0 })
			end)
		end
	end
	if shown == 0 then line(T(553), "inactive") end

	-- trades: the two tabs the /mp trades and /mp outgoing lines show
	local incomingTab = ui.tab ~= "outgoing"
	button("economy_tab_in", T(533), not incomingTab, function()
		ui.tab = "incoming"
		refresh()
	end)
	button("economy_tab_out", T(534), incomingTab, function()
		ui.tab = "outgoing"
		refresh()
	end)
	shown = 0
	for _, row in ipairs((s and s.trades) or {}) do
		local incoming = row.incoming == true
		if openTrade(row.state) and incoming == incomingTab then
			shown = shown + 1
			local other = incoming and T(561, tostring(row.initiator)) or T(560, tostring(row.counterparty))
			local gives, wants = row.give, row.want
			if not incoming then gives, wants = row.want, row.give end
			line(T(567, shortId(row.id), tradeState(row.state), other, items(gives), items(wants)))
			if incoming then
				-- the accept quotes the version the player sees, or the server answers StaleVersion (economy.fbs:177)
				button("economy_accept_" .. tostring(row.id), T(584), true, function()
					E.send("accept", { id = row.id, version = tonumber(row.version) or 0 })
				end)
			end
			button("economy_decline_" .. tostring(row.id), T(585), true, function()
				E.send("cancel", { id = row.id })
			end)
		end
	end
	if shown == 0 then line(T(553), "inactive") end

	-- ledger: the newest page the server sent, and the way to ask for the next older one
	line(T(609))
	local rows = (s and s.ledger) or {}
	if #rows == 0 then
		line(T(553), "inactive")
	else
		local shownRows = math.min(#rows, 8)
		for i = 1, shownRows do line(ledgerLine(rows[i])) end
		if #rows > shownRows then line(T(967, #rows - shownRows), "inactive") end
	end
	button("economy_ledger", T(611), true, function() requestLedger(false, false) end)
	button("economy_ledger_more", T(612), (s and s.ledger_more) == true, function() requestLedger(true, false) end)
end

--- E.command(param) -> true when the line was ours; every /mp line is ours, even a wrong one
function E.command(param)
	local line = trim(param)
	if line == "" then
		E.header()
		return true
	end
	local cmd, rest = line:match("^(%S+)%s*(.*)$")
	cmd = tostring(cmd or ""):lower()
	if cmd == "economy" or cmd == "status" then
		E.header()
	elseif cmd == "loans" then
		E.loans()
	elseif cmd == "trades" then
		E.trades(true)
	elseif cmd == "outgoing" then
		E.trades(false)
	elseif cmd == "ledger" then
		requestLedger(trim(rest):lower() == "more", true)
	elseif cmd == "send" then
		local name, tail = rest:match("^(%S+)%s+(.+)$")
		local value, memo = tostring(tail or ""):match("^(%d+)%s*(.*)$")
		local id = name and playerId(name) or nil
		local n = value and amount(value) or nil
		if not id then
			say(T(568, tostring(name or "")))
		elseif not n then
			say(T(530))
		else
			E.send("send", { to = id, amount = n, memo = trim(memo) })
		end
	elseif cmd == "deposit" or cmd == "withdraw" then
		local n = amount(rest)
		if not n then say(T(530)) else E.send(cmd, { amount = n }) end
	elseif cmd == "accept" or cmd == "decline" then
		local loan = resolveLoanOffer(rest)
		if loan then
			E.send("loan_respond", { id = loan.id, accept = cmd == "accept" })
		else
			local row = resolveTrade(rest)
			if not row then
				say(T(531, tostring(rest)))
			elseif cmd == "accept" then
				E.send("accept", { id = row.id, version = tonumber(row.version) or 0 })
			else
				E.send("cancel", { id = row.id })
			end
		end
	elseif cmd == "offer" then
		local name, tail = rest:match("^(%S+)%s+(.+)$")
		local principal, more = tostring(tail or ""):match("^(%d+)%s*(.*)$")
		local repayText, dueText, memo = tostring(more or ""):match("^(%d*)%s*(%d*)%s*(.*)$")
		local id = name and playerId(name) or nil
		local n = principal and amount(principal) or nil
		local repay = repayText and repayText ~= "" and tonumber(repayText) or n
		local days = dueText and dueText ~= "" and tonumber(dueText) or 0
		if not id then
			say(T(568, tostring(name or "")))
		elseif not n then
			say(T(530))
		elseif repay and repay < n then
			say(T(969))
		else
			E.send("loan_offer", { to = id, amount = n, repay_total = repay, due = (days or 0) * 86400, memo = trim(memo) })
		end
	elseif cmd == "repay" then
		local idText, value = rest:match("^(%S+)%s+(%S+)$")
		local id, n = idText and resolveId(idText) or nil, value and amount(value) or nil
		if not id then say(T(531, tostring(idText or rest))) elseif not n then say(T(530)) else E.send("repay", { id = id, amount = n }) end
	elseif cmd == "forgive" then
		local idText, value = rest:match("^(%S+)%s*(%S*)$")
		local id = idText and resolveId(idText) or nil
		local n = (value == nil or value == "") and 0 or amount(value)
		if not id then say(T(531, tostring(idText or rest))) elseif n == nil then say(T(530)) else E.send("forgive", { id = id, amount = n }) end
	elseif cmd == "help" or cmd == "?" then
		say(T(570))
	else
		say(T(569, cmd))
	end
	return true
end

function E.onEconomy(payload)
	if type(payload) ~= "table" then return end
	-- the native half counts the pages it took: a rising count is a page this node has not seen yet
	local pages = tonumber(payload.ledger_pages) or 0
	local fresh = pages ~= ledger_pages
	ledger_pages = pages
	E.snapshot = payload
	if fresh and await_ledger then
		await_ledger = false
		E.ledger()
	end
	-- the screen shows the snapshot directly: while it is the screen in front, a change (a new offer, a result) redraws it
	local M = rawget(_G, "X4MPScreens")
	if type(M) == "table" and type(M.render) == "function" and type(M.state) == "table" and M.state.screen == "economy" then
		M.render()
	end
end

--- E.pending(): how many offers wait for this node's answer - the number the HUD badge shows
function E.pending()
	local s = E.snapshot
	if type(s) ~= "table" then return 0 end
	local n = 0
	for _, row in ipairs(s.loans or {}) do
		if row.state == 0 and row.borrower == s.self then n = n + 1 end
	end
	for _, row in ipairs(s.trades or {}) do
		if openTrade(row.state) and row.incoming then n = n + 1 end
	end
	return n
end

--- E.onNotice(p): an offer arrived (M5-30). One chat line the player can scroll back to, one toast they cannot miss, and the
--- HUD badge picks the count up from the snapshot on its next draw.
function E.onNotice(payload)
	if type(payload) ~= "table" then return end
	local id = shortId(payload.id)
	local from = tostring(payload.from or "?")
	local text
	if payload.kind == "loan" then
		text = T(571, id, from, money(payload.amount))
	elseif (tonumber(payload.amount) or 0) > 0 then
		text = T(572, id, from, money(payload.amount))
	else
		text = T(573, id, from)
	end
	say(text)
	if type(AddUITriggeredEvent) == "function" then pcall(AddUITriggeredEvent, "X4MP", "notify", text) end
	say(T(574, id, id))
end

function E.install()
	if type(B) == "table" then
		B.VERBS.economy = true
		local function topic(name)
			for _, known in ipairs(B.TOPICS or {}) do if known == name then return end end
			B.TOPICS[#B.TOPICS + 1] = name
		end
		topic("economy")
		topic("economy_notice")
		B.on("economy", E.onEconomy)
		B.on("economy_notice", E.onNotice)
	end
	return true
end

E.install()
