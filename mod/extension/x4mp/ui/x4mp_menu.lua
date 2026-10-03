-- x4mp_menu.lua : Multiplayer / Join / Status screens of the X4MP extension (M2-05).
--
-- The screens are renderer independent. They keep the state, validate input, talk to the bridge and describe each screen as a
-- plain "model" table; a renderer (x4mp_ui_standalone.lua today, the embedded OptionsMenu entry of M2-11 later) draws it.
--
-- Renderer interface (X4MPScreens.setRenderer(r)):
--   r.name                 string, for logs
--   r.show(model)          open the window if needed and (re)draw it from the model
--   r.close()              close the window
--   r.isOpen() -> bool
--   The renderer calls X4MPScreens.onClosed() when the player closes the window.
-- Model: { screen = "main"|"join"|"status", title = string, rows = { row, ... } }
--   { type = "header",  text }
--   { type = "text",    text, tone = "normal"|"error"|"warning"|"positive"|"inactive" }
--   { type = "edit",    id, label, value, hidden = bool, maxChars, description, onChange = function(text) }
--   { type = "button",  id, text, active = bool, onClick = function() }
--   edit rows may carry readonly = true (a link to copy, onChange is a no-op) and fullWidth = true (no label column).
-- The password lives only in X4MPScreens.state.password until the join is sent; it is cleared right after the send, when the
-- window closes, and is never written to __X4MP_USER.
--
-- Entry points: chat "/x4mp [main|join|status]", Lua event "x4mp.open" {"screen":"join"}, X4MPScreens.open(screen).

-- luacheck: globals X4MPBridge X4MPScreens X4MPJoinMods __X4MP_USER ReadText DebugError ExecuteDebugCommand

if X4MPScreens and X4MPScreens.loaded then return end

local B = X4MPBridge
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] menu: x4mp_bridge.lua must load first")
	return
end

local S = { loaded = true, TEXT_PAGE = 92000, DEFAULT_PORT = 47780, NAME_MAX = 24, ADDRESS_MAX = 255 }
X4MPScreens = S
local log = B.log

------------------------------------------------------------------------------
-- text (page 92000, t/0001-l044.xml). No English in the code.
------------------------------------------------------------------------------
function S.T(id, ...)
	local text = ""
	if type(ReadText) == "function" then
		local ok, res = pcall(ReadText, S.TEXT_PAGE, id)
		if ok and type(res) == "string" then text = res end
	end
	if text == "" then text = "[" .. S.TEXT_PAGE .. "," .. id .. "]" end
	if select("#", ...) > 0 then
		local ok, res = pcall(string.format, text, ...)
		if ok then text = res end
	end
	return text
end
local T = S.T

------------------------------------------------------------------------------
-- __X4MP_USER (saved variable, userdata storage): { version, lastAddress, lastName }. Never the password.
------------------------------------------------------------------------------
local function isSecretKey(k)
	if type(k) ~= "string" then return false end
	k = k:lower()
	return k:find("pass", 1, true) ~= nil or k:find("pwd", 1, true) ~= nil or k:find("secret", 1, true) ~= nil
		or k:find("token", 1, true) ~= nil
end

function S.user()
	if type(__X4MP_USER) ~= "table" then __X4MP_USER = {} end
	local u = __X4MP_USER
	u.version = 1
	for k in pairs(u) do
		if isSecretKey(k) then u[k] = nil end -- defensive: nothing secret may ever sit in the saved variable
	end
	return u
end

------------------------------------------------------------------------------
-- validation
------------------------------------------------------------------------------
local function trim(s) return (tostring(s or ""):gsub("^%s+", ""):gsub("%s+$", "")) end

--- parseAddress("host[:port]" | "[v6][:port]") -> host, port   or nil, "bad_address"
function S.parseAddress(text)
	text = trim(text)
	if text == "" or #text > S.ADDRESS_MAX then return nil, "bad_address" end
	local host, port
	if text:sub(1, 1) == "[" then
		local rest
		host, rest = text:match("^%[([%x:%.]+)%](.*)$")
		if not host or not host:find(":", 1, true) then return nil, "bad_address" end
		if rest ~= "" then
			port = rest:match("^:(%d+)$")
			if not port then return nil, "bad_address" end
		end
	else
		host, port = text:match("^([%w%.%-_]+):(%d+)$")
		if not host then host = text:match("^([%w%.%-_]+)$") end
		if not host or host:find("^[%.%-]") or host:find("[%.%-]$") or host:find("..", 1, true) then
			return nil, "bad_address"
		end
	end
	port = port and tonumber(port) or S.DEFAULT_PORT
	if port < 1 or port > 65535 then return nil, "bad_address" end
	return host, port
end

function S.normalizeAddress(text)
	local host, port = S.parseAddress(text)
	if not host then return nil end
	if host:find(":", 1, true) then return "[" .. host .. "]:" .. port end
	return host .. ":" .. port
end

local function charCount(s)
	return select(2, s:gsub("[^\128-\191]", ""))
end

--- sanitizeName(raw) -> trimmed name without control characters
function S.sanitizeName(raw)
	return trim((tostring(raw or ""):gsub("%c", "")))
end

--- validateJoin(state) -> ok, errors   (errors = { address = code, name = code } for the failing fields)
function S.validateJoin(state)
	local errs = {}
	if not S.parseAddress(state.address) then errs.address = "bad_address" end
	local name = S.sanitizeName(state.name)
	local n = charCount(name)
	if n < 1 then
		errs.name = "name_empty"
	elseif n > S.NAME_MAX then
		errs.name = "name_long"
	end
	if state.host and (state.adminPassword or "") == "" then errs.admin = "admin_empty" end
	return next(errs) == nil, errs
end

local ERROR_TEXT = { bad_address = 20, name_empty = 21, name_long = 22, admin_empty = 25 }

------------------------------------------------------------------------------
-- state
------------------------------------------------------------------------------
local ACTIVE_STATES = { connecting = true, handshaking = true, checking_save = true, downloading = true, loading = true,
	matching = true, ingame = true }
-- Reject tokens of x4mp.status.reject (native: features/join/join_json.cpp reject_for_code, or "build" from a refused host).
-- Anything else (or a missing token) uses the generic text 38 with the server's message.
local REJECT_TEXT = { build = 70, mod = 71, auth = 72, full = 73, banned = 74, name = 75 }
local STATE_TEXT = { disconnected = 30, connecting = 31, handshaking = 32, checking_save = 33, downloading = 34,
	loading = 35, matching = 36, ingame = 37, rejected = 38, error = 39 }

S.state = nil

function S.initState()
	if S.state then return S.state end
	local u = S.user()
	S.state = {
		screen = "main",
		address = type(u.lastAddress) == "string" and u.lastAddress or "",
		name = type(u.lastName) == "string" and u.lastName or "",
		password = "",
		host = false, -- M2-09: "Host this session as the authority"
		adminPassword = "",
		team = "auto", -- placeholder: the team list needs the server pre-query (later task)
		errors = {},
		notice = nil,
	}
	return S.state
end

--- true while a connection attempt or session is running (from the last x4mp.status)
function S.connectionActive()
	local st = B.status
	return st ~= nil and ACTIVE_STATES[st.state] == true
end

------------------------------------------------------------------------------
-- model building
------------------------------------------------------------------------------
local function text(t, tone) return { type = "text", text = t, tone = tone or "normal" } end

local function buildMain(_, rows)
	local u = S.user()
	if u.lastAddress and u.lastAddress ~= "" then rows[#rows + 1] = text(T(15, u.lastAddress), "inactive") end
	if S.connectionActive() then
		rows[#rows + 1] = { type = "button", id = "status", text = T(11), active = true, onClick = function() S.go("status") end }
		rows[#rows + 1] = { type = "button", id = "disconnect", text = T(9), active = true, onClick = function() S.disconnect() end }
	else
		rows[#rows + 1] = { type = "button", id = "join", text = T(2), active = true, onClick = function() S.go("join") end }
	end
	-- M2-X3: the session's mod list while connected (x4mp_join_mods.lua loads after this file)
	if S.connectionActive() and X4MPJoinMods then X4MPJoinMods.policyRows(rows) end
	rows[#rows + 1] = text(T(51), "inactive")
end

local function buildJoin(st, rows)
	local busy = S.connectionActive()
	rows[#rows + 1] = { type = "edit", id = "address", label = T(3), value = st.address, maxChars = S.ADDRESS_MAX,
		description = T(3), onChange = function(v) st.address = v end }
	rows[#rows + 1] = text(T(12), "inactive")
	if st.errors.address then rows[#rows + 1] = text(T(ERROR_TEXT[st.errors.address]), "error") end
	rows[#rows + 1] = { type = "edit", id = "name", label = T(4), value = st.name, maxChars = 64, description = T(4),
		onChange = function(v) st.name = v end }
	if st.errors.name then rows[#rows + 1] = text(T(ERROR_TEXT[st.errors.name], S.NAME_MAX), "error") end
	rows[#rows + 1] = { type = "edit", id = "password", label = T(5), value = st.password, hidden = true, maxChars = 128,
		description = T(5), onChange = function(v) st.password = v end }
	rows[#rows + 1] = text(T(13), "inactive")
	-- M2-09: host option. The admin password only exists while the toggle is on; it is cleared after the send and when the window closes.
	rows[#rows + 1] = { type = "button", id = "host", text = T(16, st.host and T(17) or T(18)), active = not busy,
		onClick = function() st.host = not st.host if not st.host then st.adminPassword = "" end S.render() end }
	if st.host then
		rows[#rows + 1] = { type = "edit", id = "adminpassword", label = T(19), value = st.adminPassword, hidden = true, maxChars = 128,
			description = T(19), onChange = function(v) st.adminPassword = v end }
		if st.errors.admin then rows[#rows + 1] = text(T(ERROR_TEXT[st.errors.admin]), "error") end
		rows[#rows + 1] = text(T(26), "inactive")
	end
	rows[#rows + 1] = text(T(6) .. ": " .. T(7), "normal")
	rows[#rows + 1] = text(T(14), "inactive")
	if st.notice then rows[#rows + 1] = text(st.notice, "warning") end
	rows[#rows + 1] = { type = "button", id = "connect", text = T(8), active = not busy, onClick = function() S.submitJoin() end }
	rows[#rows + 1] = { type = "button", id = "back", text = T(10), active = true, onClick = function() S.go("main") end }
end

local function buildStatus(st, rows)
	local status = B.status
	local state = status and status.state or "disconnected"
	local label
	if state == "downloading" and status and type(status.progress) == "number" then
		label = T(34, string.format("%d%%", math.floor(status.progress * 100 + 0.5)))
	elseif state == "rejected" then
		local detail = tostring(status and status.detail or "")
		local id = status and REJECT_TEXT[status.reject]
		if id then
			label = T(id, detail)
		else
			label = T(STATE_TEXT.rejected, detail ~= "" and detail or tostring(status and status.reject or ""))
		end
		label = label:gsub("%s+$", "")
	elseif state == "error" then
		label = T(STATE_TEXT.error, tostring(status and (status.detail or status.reject) or ""))
	else
		label = T(STATE_TEXT[state] or 39, "")
	end
	local tone = (state == "ingame") and "positive" or ((state == "rejected" or state == "error") and "error" or "normal")
	rows[#rows + 1] = text(label, tone)
	-- M2-X3: a mod refusal lists what to install / enable / disable / update, with links (x4mp_join_mods.lua)
	if state == "rejected" and status and status.reject == "mod" and X4MPJoinMods then X4MPJoinMods.refusalRows(rows) end
	if status then
		if status.server then rows[#rows + 1] = text(T(40, status.server)) end
		if status.role then rows[#rows + 1] = text(T(41, status.role)) end
		if status.team_name or status.team then rows[#rows + 1] = text(T(42, status.team_name or status.team)) end
		if status.ping_ms then rows[#rows + 1] = text(T(43, status.ping_ms)) end
		if status.players then rows[#rows + 1] = text(T(44, status.players)) end
	end
	if B.lastError then rows[#rows + 1] = text(tostring(B.lastError.text or B.lastError.code), "error") end
	if st.notice then rows[#rows + 1] = text(st.notice, "warning") end
	if S.connectionActive() then
		rows[#rows + 1] = { type = "button", id = "disconnect", text = T(9), active = true, onClick = function() S.disconnect() end }
	else
		rows[#rows + 1] = { type = "button", id = "join", text = T(2), active = true, onClick = function() S.go("join") end }
	end
	rows[#rows + 1] = { type = "button", id = "back", text = T(10), active = true, onClick = function() S.go("main") end }
end

local builders = { main = buildMain, join = buildJoin, status = buildStatus }
local titles = { main = 1, join = 2, status = 11 }

function S.buildModel()
	local st = S.initState()
	local screen = builders[st.screen] and st.screen or "main"
	local rows = {}
	builders[screen](st, rows)
	return { screen = screen, title = T(titles[screen]), rows = rows }
end

------------------------------------------------------------------------------
-- renderer + navigation
------------------------------------------------------------------------------
S.renderer = nil

function S.setRenderer(r)
	S.renderer = r
end

function S.render()
	local r = S.renderer
	if r and r.isOpen() then r.show(S.buildModel()) end
end

function S.normalizeScreen(name)
	name = trim(name):lower()
	return builders[name] and name or "main"
end

function S.go(screen)
	local st = S.initState()
	st.screen = S.normalizeScreen(screen)
	st.errors = {}
	st.notice = nil
	S.render()
end

--- open(screen): opens the window on a screen. Returns false (and logs) when no renderer is registered.
function S.open(screen)
	B.loadingSave = nil -- opening a screen again means any earlier load is over (failed or done)
	local st = S.initState()
	st.screen = S.normalizeScreen(screen)
	st.errors = {}
	st.notice = nil
	B.retry("open")
	local r = S.renderer
	if not r then
		log("menu: no renderer registered, cannot open the X4MP screen")
		return false
	end
	r.show(S.buildModel())
	return true
end

function S.close()
	if S.renderer and S.renderer.isOpen() then S.renderer.close() end
	S.onClosed()
end

--- called by the renderer when the window is gone: the password does not outlive the window
function S.onClosed()
	if S.state then
		S.state.password = ""
		S.state.adminPassword = ""
		S.state.host = false
	end
end

------------------------------------------------------------------------------
-- actions
------------------------------------------------------------------------------
function S.disconnect()
	B.send("disconnect", {})
	S.go("status")
end

--- Validates the join form and sends x4mp.join. The password is cleared right after the send (also when it failed).
function S.submitJoin()
	local st = S.initState()
	local ok, errs = S.validateJoin(st)
	st.errors = errs
	st.notice = nil
	if not ok then
		S.render()
		return false, "invalid"
	end
	local address = S.normalizeAddress(st.address)
	local name = S.sanitizeName(st.name)
	local payload = { address = address, name = name, password = st.password or "", team = st.team or "auto" }
	if st.host then
		payload.role = "authority"
		payload.admin_password = st.adminPassword
	end
	st.password = ""
	st.adminPassword = ""
	local sent, err = B.send("join", payload)
	payload.password = nil
	payload.admin_password = nil
	if sent then
		local u = S.user()
		u.lastAddress = address
		u.lastName = name
		B.lastError = nil
		B.status = { v = 1, state = "connecting" } -- optimistic until native reports
		st.address = address
		st.name = name
		st.screen = "status"
	else
		st.notice = T(24)
		log("join not sent: " .. tostring(err))
	end
	S.render()
	return sent, err
end

------------------------------------------------------------------------------
-- hooks
------------------------------------------------------------------------------
-- Native sends status at up to 2 Hz. A redraw of the join form would steal the focus of an active edit box, so while the
-- join screen is up it is only redrawn when the connection state changes.
B.on("status", function(p, raw)
	local changed = p.state ~= S.lastState
	S.lastState = p.state
	-- an identical status (the native heartbeat) is not redrawn: a redraw would drop the selection in a read-only link box (M2-X3)
	local same = type(raw) == "string" and raw == S.lastStatusText
	S.lastStatusText = raw
	if same and not changed then return end
	if changed or not S.state or S.state.screen ~= "join" then S.render() end
end)
B.on("error", function() S.render() end)
B.on("notify", function(p)
	if type(p.text) == "string" and S.state then
		S.state.notice = p.text
		S.render()
	end
end)
B.on("open", function(p) S.open(p.screen) end)

-- chat "/x4mp [screen]": the vanilla chat window calls ExecuteDebugCommand("x4mp", "[screen]")
local function installChatCommand()
	if S.edcWrapper and ExecuteDebugCommand == S.edcWrapper then return end
	local prev = ExecuteDebugCommand
	if type(prev) ~= "function" then return end
	local wrapper = function(cmd, param, ...)
		if cmd == "x4mp" then
			S.open(param)
			return
		end
		return prev(cmd, param, ...)
	end
	S.edcWrapper = wrapper
	ExecuteDebugCommand = wrapper
end
installChatCommand()
log("menu loaded")
