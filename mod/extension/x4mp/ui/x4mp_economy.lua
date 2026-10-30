-- x4mp_economy.lua : the economy screen and the /mp commands (M5-29, M5-30; docs/mod-design.md 12.7, docs/m5-plan.md W7).
--
-- The server decides every economy action; this module only asks and shows. The native half (features/economy_client) publishes
-- topic x4mp.economy {"v":1,"self":<id>,"loans":[...],"trades":[...],"last":{...},"last_trade":{...}} on every change, and takes
-- verb x4mp.economy {"op":"send|deposit|withdraw|repay|forgive|accept|cancel","to":<id>,"amount":<credits>,"id":"<lo:hi>",...}.
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
		if row.incoming and row.state ~= 5 and row.state ~= 6 and row.state ~= 7 and row.state ~= 8 and row.state ~= 9 then
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
		local open = row.state ~= 5 and row.state ~= 6 and row.state ~= 7 and row.state ~= 8 and row.state ~= 9
		if open and (row.incoming and true or false) == (incoming and true or false) then
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
	for _, key in ipairs({ "to", "amount", "id", "version", "asset", "memo" }) do
		if params and params[key] ~= nil then body[key] = params[key] end
	end
	local ok, err = B.send("economy", body)
	if not ok then say(T(562)) end
	return ok, err
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
		local row = resolveTrade(rest)
		if not row then
			say(T(531, tostring(rest)))
		elseif cmd == "accept" then
			E.send("accept", { id = row.id, version = tonumber(row.version) or 0 })
		else
			E.send("cancel", { id = row.id })
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
	E.snapshot = payload
end

function E.install()
	if type(B) == "table" then
		B.VERBS.economy = true
		local found = false
		for _, topic in ipairs(B.TOPICS or {}) do if topic == "economy" then found = true end end
		if not found then B.TOPICS[#B.TOPICS + 1] = "economy" end
		B.on("economy", E.onEconomy)
	end
	return true
end

E.install()
