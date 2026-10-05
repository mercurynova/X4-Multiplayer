-- x4mp_chat.lua : multiplayer chat in the VANILLA chat window (M3-06; docs/mod-design.md 7.6, m3-plan 4.11, user answer Q14).
--
-- The vanilla chat window (ui/addons/ego_chatwindow/chatwindow.lua) reads its lines from the global OnlineGetChatMessages() and hands a
-- typed line to the global OnlineSendChatMessage(text, userid); a line that starts with "/" goes to ExecuteDebugCommand(command, rest).
-- While a multiplayer session is active this file wraps those three globals:
--   OnlineGetChatMessages  the result of the previous function (vanilla, or SirNukes' chat API) plus the multiplayer lines, by time
--   OnlineSendChatMessage  a plain typed line goes to the session (verb x4mp.chat_send, channel all); never to Ventures
--   ExecuteDebugCommand    "/t text" = team chat, "/w name text" = whisper; every other command goes to the previous function untouched
--
-- Native side (features/chat): topic x4mp.chat {"v":1,"replay":bool,"messages":[{"from","name","team","channel","text","t","self","code"}]};
-- verb x4mp.chat_send {"v":1,"channel":"all|team|whisper","to":<player id>,"text":"..."}. The server sends every message back to the sender
-- too (ChatMessage), so a sent line shows up when the server accepted it; a refusal (muted, rate limit) arrives as a system line.
--
-- Wrapper rules (SirNukes coexistence, ADR-043; the same rules as x4mp_saves.lua):
--   * the previous global is captured AT WRAP TIME, never at file load (SirNukes installs its own late, after its Lua loader signal);
--   * ensure() runs on gfx_ok, show and every native status / chat / players topic: when the global is not our newest wrapper any more
--     (somebody replaced or wrapped it) a new wrapper goes on top of whatever is there now. A wrapper that is still in somebody's chain
--     below another one of ours never acts twice (a re-entrancy guard makes the inner one a pass-through);
--   * uninstall() puts the previous function back only where ours is still on top; a wrapper left in place becomes inactive and
--     delegates everything. install() is idempotent.
--   * the wrappers are only in place while a session is active (status ingame / save_changed); /reloadui starts a fresh Lua state, native
--     replays the recent history on ui_ready and the wrappers go back on with the first status.
--
-- Author colours: the author text carries the colour escape of the sender's team colour (X4MPPlayers.teamColorHex), the vanilla window
-- appends its reset. Lines are plain entries {author, authorid, time, text, reported=false, isprivate=false}: isprivate stays false (the
-- window's private tabs belong to Ventures groups). authorid is negative per player, the own lines carry the user id of
-- OnlineGetUserName so the window treats them as "mine".
--
-- luacheck: globals X4MPBridge X4MPScreens X4MPPlayers X4MPChat OnlineGetChatMessages OnlineSendChatMessage OnlineGetUserName
-- luacheck: globals ExecuteDebugCommand Menus ReadText DebugError RegisterEvent AddUITriggeredEvent

if X4MPChat and X4MPChat.loaded then return end

local B = X4MPBridge
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] chat: x4mp_bridge.lua must load first")
	return
end

local C = {
	loaded = true, RING_MAX = 200, TOAST_MAX = 60, toast = true,
	ring = {}, wrappers = {}, busy = {}, lastTime = 0, installed = false,
}
X4MPChat = C
local log = B.log

local ACTIVE = { ingame = true, save_changed = true }
local SYSTEM_COLOR = "ffcc33"

local function T(id, ...)
	local S = rawget(_G, "X4MPScreens")
	if type(S) == "table" and type(S.T) == "function" then return S.T(id, ...) end
	local text = ""
	if type(ReadText) == "function" then
		local ok, res = pcall(ReadText, 92000, id)
		if ok and type(res) == "string" then text = res end
	end
	if text == "" then text = "[92000," .. id .. "]" end
	if select("#", ...) > 0 then
		local ok, res = pcall(string.format, text, ...)
		if ok then text = res end
	end
	return text
end

local function trim(s) return (tostring(s or ""):gsub("^%s+", ""):gsub("%s+$", "")) end

------------------------------------------------------------------------------
-- time and colours
------------------------------------------------------------------------------
local function utcMs()
	local ok, ffi = pcall(require, "ffi")
	if ok and type(ffi) == "table" then
		pcall(ffi.cdef, "int64_t GetCurrentUTCDataTime(void);") -- "redefine" is expected and harmless
		local okT, t = pcall(function() return tonumber(ffi.C.GetCurrentUTCDataTime()) end)
		if okT and t and t > 0 then return t * 1000 end
	end
	if type(os) == "table" and type(os.time) == "function" then return os.time() * 1000 end
	return 0
end

--- strictly increasing millisecond stamps (the window sorts and divides by time)
local function nextTime()
	local t = utcMs()
	if t <= C.lastTime then t = C.lastTime + 1 end
	C.lastTime = t
	return t
end

local function teamColor(team)
	local P = rawget(_G, "X4MPPlayers")
	if type(P) == "table" and type(P.teamColorHex) == "function" then return P.teamColorHex(team) end
	return "c8c8c8"
end

local function escape(hex)
	if type(hex) == "string" and hex:match("^%x%x%x%x%x%x$") then return "\027#ff" .. hex:lower() .. "#" end
	return ""
end

------------------------------------------------------------------------------
-- the chat window
------------------------------------------------------------------------------
local function chatMenu()
	local menus = rawget(_G, "Menus")
	if type(menus) ~= "table" then return nil end
	for _, m in ipairs(menus) do
		if type(m) == "table" and m.name == "ChatWindow" then return m end
	end
	return nil
end

--- true while the chat window is on screen (also while it is fading)
function C.windowShown()
	local m = chatMenu()
	return m ~= nil and m.shown and true or false
end

--- The vanilla refresh is an engine event of the Ventures chat; nothing raises it for our lines, so call the menu's own handler.
function C.refresh()
	local m = chatMenu()
	if m and m.shown and type(m.onChatMessageReceived) == "function" then
		local ok, err = pcall(m.onChatMessageReceived)
		if not ok then log("chat: window refresh failed: " .. tostring(err)) end
	end
end

------------------------------------------------------------------------------
-- lines
------------------------------------------------------------------------------
local CODE_TEXT = { not_connected = 504, no_recipient = 505, bad_request = 511 }
local CHANNEL_PREFIX = { team = 500, whisper = 501, admin = 502 }

local function ownUserId()
	if type(OnlineGetUserName) ~= "function" then return nil end
	local ok, _, id = pcall(OnlineGetUserName)
	if ok and type(id) == "number" then return id end
	return nil
end

--- entryFor(message) -> the vanilla chat entry for one native message
function C.entryFor(m)
	local from = tonumber(m.from) or 0
	local author, hex, authorid
	if m.channel == "system" or from == 0 then
		author = (type(m.name) == "string" and m.name ~= "") and m.name or T(503)
		hex = SYSTEM_COLOR
		authorid = -1
	else
		author = tostring(m.name or "?")
		hex = teamColor(m.team)
		authorid = -100 - from
		if m.self then authorid = ownUserId() or -2 end
	end
	local text = tostring(m.text or "")
	if type(m.code) == "string" and CODE_TEXT[m.code] then text = T(CODE_TEXT[m.code]) end
	local prefix = CHANNEL_PREFIX[m.channel]
	if prefix then text = T(prefix) .. " " .. text end
	return { author = escape(hex) .. author, authorid = authorid, time = nextTime(), text = text, reported = false, isprivate = false,
		x4mp = true }
end

local function push(entry)
	local ring = C.ring
	ring[#ring + 1] = entry
	if #ring > C.RING_MAX then table.remove(ring, 1) end
end

--- systemLine(text): a local line from "X4MP" in the chat window (join / leave, command help)
function C.systemLine(text)
	push(C.entryFor({ channel = "system", from = 0, name = "", text = text }))
	C.refresh()
end

local function toast(m)
	if not C.toast or m.self or m.channel == "system" or C.windowShown() then return end
	local text = tostring(m.text or "")
	if #text > C.TOAST_MAX then text = text:sub(1, C.TOAST_MAX) .. "..." end
	if type(AddUITriggeredEvent) == "function" then pcall(AddUITriggeredEvent, "X4MP", "notify", T(510, tostring(m.name or "?"), text)) end
end

--- onChat(payload): the x4mp.chat handler (public for tests)
function C.onChat(p)
	C.ensure()
	if p.replay == true then C.ring = {} end -- the history of a fresh Lua state replaces what we have
	if type(p.messages) ~= "table" then return end
	for _, m in ipairs(p.messages) do
		if type(m) == "table" then
			push(C.entryFor(m))
			if p.replay ~= true then toast(m) end
		end
	end
	C.refresh()
end

------------------------------------------------------------------------------
-- sending
------------------------------------------------------------------------------
function C.sessionActive()
	local st = B.status
	return st ~= nil and ACTIVE[st.state] == true
end

--- sendLine(channel, toPlayerId, text) -> true when handed to the bridge
function C.sendLine(channel, to, text)
	text = trim(text)
	if text == "" then return false end
	local payload = { channel = channel, text = text }
	if channel == "whisper" then payload.to = to end
	local ok, err = B.send("chat_send", payload)
	if not ok then
		log("chat: send failed: " .. tostring(err))
		C.systemLine(T(504))
		return false
	end
	return true
end

--- handleCommand(cmd, param) -> true when the command is ours ("t", "w"); everything else must go to the previous function untouched
function C.handleCommand(cmd, param)
	cmd = tostring(cmd or ""):lower()
	if cmd == "t" then
		if trim(param) == "" then
			C.systemLine(T(507))
		else
			C.sendLine("team", nil, param)
		end
		return true
	elseif cmd == "w" then
		local P = rawget(_G, "X4MPPlayers")
		local row, rest
		if type(P) == "table" and type(P.resolveWhisper) == "function" then row, rest = P.resolveWhisper(param) end
		if trim(param) == "" then
			C.systemLine(T(505))
		elseif not row then
			C.systemLine(T(506, tostring(rest or "")))
		elseif trim(rest) == "" then
			C.systemLine(T(505))
		else
			C.sendLine("whisper", row.id, rest)
		end
		return true
	elseif cmd == "x4mp" then
		-- M3-23 diagnostic: "/x4mp knowledge" logs the map-knowledge counts (X4MPDiag, x4mp_diag.lua); other "/x4mp ..." go to the previous function
		local D = rawget(_G, "X4MPDiag")
		if trim(param):lower() == "knowledge" and type(D) == "table" and type(D.requestKnowledge) == "function" then
			D.requestKnowledge()
			C.systemLine("Knowledge probe requested: see the line 'knowledge:' in the x4mp log.")
			return true
		end
	end
	return false
end

------------------------------------------------------------------------------
-- chained global wrappers
------------------------------------------------------------------------------
local function top(name)
	local list = C.wrappers[name]
	return list and list[#list] or nil
end

local function wrap(name, make)
	local cur = rawget(_G, name)
	if type(cur) ~= "function" then return "missing_global" end
	local newest = top(name)
	if newest and newest.active and newest.fn == cur then return "already_installed" end
	local rec = { prev = cur, active = true }
	rec.fn = make(cur, rec)
	local list = C.wrappers[name] or {}
	C.wrappers[name] = list
	list[#list + 1] = rec
	_G[name] = rec.fn
	return "installed"
end

local function unwrap(name)
	local list = C.wrappers[name]
	if not list then return "not_installed" end
	local result = "not_installed"
	for i = #list, 1, -1 do
		local rec = list[i]
		if rec.active then
			rec.active = false -- a wrapper still referenced from somebody's chain turns into a pass-through
			if rawget(_G, name) == rec.fn then
				_G[name] = rec.prev
				result = "removed"
			elseif result ~= "removed" then
				result = "left_in_place"
			end
		end
	end
	return result
end

local function mergeSorted(a, b)
	local items = {}
	for _, m in ipairs(a) do items[#items + 1] = { m = m, t = tonumber(m.time) or 0, i = #items } end
	for _, m in ipairs(b) do items[#items + 1] = { m = m, t = tonumber(m.time) or 0, i = #items } end
	table.sort(items, function(x, y)
		if x.t ~= y.t then return x.t < y.t end
		return x.i < y.i
	end)
	local out = {}
	for i, item in ipairs(items) do out[i] = item.m end
	return out
end

--- install() -> { OnlineGetChatMessages = "installed|already_installed|missing_global", ... }
function C.install()
	local res = {}
	res.OnlineGetChatMessages = wrap("OnlineGetChatMessages", function(prev, rec)
		return function(...)
			if not rec.active or C.busy.get then return prev(...) end
			C.busy.get = true
			local ok, theirs = pcall(prev, ...)
			C.busy.get = false
			if not ok or type(theirs) ~= "table" then theirs = {} end
			return mergeSorted(theirs, C.ring)
		end
	end)
	res.OnlineSendChatMessage = wrap("OnlineSendChatMessage", function(prev, rec)
		return function(text, userid, ...)
			-- a plain line of the main chat (userid 0 / nil) is ours; a private Ventures tab (userid > 0) is not
			if rec.active and type(text) == "string" and (userid == nil or userid == 0) then
				C.sendLine("all", nil, text)
				return
			end
			return prev(text, userid, ...)
		end
	end)
	res.ExecuteDebugCommand = wrap("ExecuteDebugCommand", function(prev, rec)
		return function(cmd, param, ...)
			if rec.active and C.handleCommand(cmd, param) then return end
			return prev(cmd, param, ...)
		end
	end)
	C.installed = true
	return res
end

--- uninstall() -> { name = "removed|left_in_place|not_installed", ... }
function C.uninstall()
	local res = {}
	for _, name in ipairs({ "OnlineGetChatMessages", "OnlineSendChatMessage", "ExecuteDebugCommand" }) do res[name] = unwrap(name) end
	C.installed = false
	return res
end

--- ensure(): the wrappers are in place on top of whatever the globals are now while a session is active, and gone otherwise
function C.ensure()
	if C.sessionActive() then
		local res = C.install()
		if not C.logged then
			C.logged = true
			log("chat: wrappers " .. tostring(res.OnlineGetChatMessages) .. " / " .. tostring(res.OnlineSendChatMessage) .. " / " ..
				tostring(res.ExecuteDebugCommand))
		end
	else
		if C.installed then
			C.logged = nil
			C.uninstall()
		end
		local st = B.status
		-- the lines of a session that ended for good go; a blip (reconnecting, matching) keeps them for when the wrappers return
		if st ~= nil and (st.state == "disconnected" or st.state == "rejected" or st.state == "error") and #C.ring > 0 then
			C.ring = {}
			C.refresh()
		end
	end
end

------------------------------------------------------------------------------
-- hooks
------------------------------------------------------------------------------
B.on("chat", function(p) C.onChat(p) end)
B.on("status", function() C.ensure() end)
B.on("players", function() C.ensure() end)
RegisterEvent("gfx_ok", function() C.ensure() end)
RegisterEvent("show", function() C.ensure() end)
C.ensure()
log("chat loaded")
