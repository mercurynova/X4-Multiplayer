-- x4mp_server.lua : the dedicated server screen and the /mp server commands (M5-32).
--
-- The native half (features/server) starts the server bundle that ships with the extension, stops it, and publishes topic x4mp.server
-- {"v":1,"state":"stopped|starting|running|exited","pid":N,"exit_code":N,"listening":bool,"external":bool,"port":47780,
-- "http_port":47790,"auto_stop":bool,"exe":"...","data_dir":"...","log":"...","error":"..."} on every change and once after a status request. This module
-- only asks and shows: verb x4mp.server {"op":"start|stop|status"}, the state as one chat line, and the rows of the server screen.
--
-- The screen is reachable without a session on purpose: starting the server is what makes a session possible. Refusals arrive as
-- x4mp.notify, which the HUD and the chat already show, so this file does not repeat them.

-- luacheck: globals X4MPBridge X4MPChat X4MPEconomy X4MPScreens X4MPServer

local B = X4MPBridge
local C = X4MPChat

local V = { state = nil }
X4MPServer = V

-- a /mp server status line is waiting for the answer the native half publishes after the status request
local await_status = false

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

local STATE_TEXT = { stopped = 616, starting = 617, running = 618, exited = 619 }

--- the wire state as the word the player reads; a state a newer native half adds is not called "stopped"
local function stateName(state) return T(STATE_TEXT[tostring(state or "")] or 636) end

local function listeningText(value) return value == true and T(621) or T(622) end

--- send(op) -> ok, err: one request to the native launcher. A refusal or a failed start comes back as x4mp.notify, not here.
local function send(op)
	if type(B) ~= "table" or type(B.send) ~= "function" then
		say(T(635))
		return false, "no bridge"
	end
	local ok, err = B.send("server", { op = op })
	if not ok then say(T(635)) end
	return ok, err
end

--- V.printState(): the whole state as one chat line, with the error the launcher reported when there is one
function V.printState()
	local s = V.state
	if type(s) ~= "table" then say(T(632)) return end
	local text = T(634, stateName(s.state), tonumber(s.pid) or 0, tonumber(s.port) or 0, listeningText(s.listening),
		tostring(s.data_dir or "?"), tostring(s.log or "?"))
	if s.external == true then text = text .. ", " .. T(639) end
	if type(s.error) == "string" and s.error ~= "" then text = text .. ", " .. T(628, s.error) end
	say(text)
end

--- V.status(wait_for_answer): ask the launcher. The first ask has no state yet, so the answer is printed when it lands.
function V.status(wait_for_answer)
	await_status = wait_for_answer == true
	local ok = send("status")
	if not ok then await_status = false end
	return ok
end

function V.start() return send("start") end

function V.stop() return send("stop") end

--- V.command(param) -> true when the line was ours; the chat adapter gives every /mp line to the economy module first, which
--- hands a "server" prefix back here before it answers. A wrong subcommand answers with the command list.
function V.command(param)
	local line = trim(param)
	local head, rest = line:match("^(%S+)%s*(.*)$")
	if tostring(head or ""):lower() == "server" then line = trim(rest) end
	local cmd = tostring(line:match("^(%S*)") or ""):lower()
	if cmd == "" or cmd == "status" then
		-- the first status of a session has no payload yet: print the answer when it arrives, not an empty line now
		V.status(type(V.state) ~= "table")
		if type(V.state) == "table" then V.printState() end
	elseif cmd == "start" then
		V.start()
	elseif cmd == "stop" then
		V.stop()
	else
		say(T(633))
	end
	return true
end

--- V.screenRows(rows, st): the rows of the server screen. Every action goes through the same verbs the /mp commands use, so the
--- two ways into the launcher cannot drift apart. st is unused today; the signature matches the other screen builders.
function V.screenRows(rows, st)
	local function add(r) rows[#rows + 1] = r end
	local function line(t, tone) add({ type = "text", text = t, tone = tone or "normal" }) end
	local function button(id, t, active, onClick)
		add({ type = "button", id = id, text = t, active = active ~= false, onClick = onClick })
	end

	local s = V.state
	if type(s) ~= "table" then
		line(T(632), "warning")
	else
		line(T(615, stateName(s.state)))
		if s.external == true then line(T(639), "inactive") end
		line(T(620, tonumber(s.pid) or 0, tonumber(s.port) or 0, listeningText(s.listening)))
		line(T(623, tonumber(s.http_port) or 0))
		line(T(624, tostring(s.data_dir or "?")))
		line(T(625, tostring(s.log or "?")))
		-- The game started this server, so it knows the password it handed it: the authority join needs exactly this one.
		if type(s.admin_password) == "string" and s.admin_password ~= "" then line(T(637, s.admin_password)) end
		if type(s.admin_password_file) == "string" and s.admin_password_file ~= "" then line(T(638, s.admin_password_file), "inactive") end
		line(s.auto_stop and T(626) or T(627), "inactive")
		if type(s.error) == "string" and s.error ~= "" then line(T(628, s.error), "error") end
	end

	-- the native half refuses a start while it runs and a stop when nothing was started; the screen greys those out first.
	-- An external server (the launcher's sidecar) is never stopped from the game: the launcher owns that process.
	local known = type(s) == "table"
	local busy = known and (s.state == "running" or s.state == "starting")
	local stopped = not known or s.state == "stopped" or s.external == true
	button("server_start", T(629), not busy, function() V.start() end)
	button("server_stop", T(630), not stopped, function() V.stop() end)
	button("server_refresh", T(631), true, function() V.status(false) end)
end

function V.onServer(payload)
	if type(payload) ~= "table" then return end
	V.state = payload
	if await_status then
		await_status = false
		V.printState()
	end
	-- the screen shows the state directly: while it is the screen in front, a change redraws it
	local M = rawget(_G, "X4MPScreens")
	if type(M) == "table" and type(M.render) == "function" and type(M.state) == "table" and M.state.screen == "server" then
		M.render()
	end
end

function V.install()
	if type(B) == "table" then
		B.VERBS.server = true
		local function topic(name)
			for _, known in ipairs(B.TOPICS or {}) do if known == name then return end end
			B.TOPICS[#B.TOPICS + 1] = name
		end
		topic("server")
		B.on("server", V.onServer)
	end
	-- The chat adapter gives every "/mp ..." line to the economy module. Wrap its command once so "/mp server ..." reaches this
	-- file before the economy answers "unknown command"; the mark on the economy table keeps a reload from stacking a second wrap.
	local E = rawget(_G, "X4MPEconomy")
	if type(E) == "table" and type(E.command) == "function" and not E.serverRouted then
		local economyCommand = E.command
		E.command = function(param)
			local head = tostring(param or ""):match("^%s*(%S*)")
			if tostring(head or ""):lower() == "server" then return V.command(param) end
			return economyCommand(param)
		end
		E.serverRouted = true
	end
	return true
end

V.install()
