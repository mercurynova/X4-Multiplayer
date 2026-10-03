-- x4mp_spike_s13.lua : session-4 sitting-0 blocks (M3-002; spikes S13.1, S13.2c, S13.7, S13.8, S13.9, S13.11, S13.12).
-- MD half: md/x4mp_spike_s13.xml (dress, velocity, SETA, teams, persistence table, galaxy enumeration). The native probe
-- (x4mp_probe, M3-001) raises three Lua events that this file turns into MD calls:
--   x4mp.spike_dress     "id|name"          -> MD control s13_dress    (name, min hull, forced radar, known)
--   x4mp.spike_velocity  "id|vx|vy|vz"      -> MD control s13_velocity (set_object_velocity, m/s)
--   x4mp.spike_seta_off  ""                 -> MD control s13_seta_off (+ a Lua readback of IsSetaActive after 0.3 s and 1.5 s)
-- Blocks (launched with /x4mpspike <block> [k=v ...] or by run-block.ps1 through the probe's x4mp_spike.run event):
--   dress [id=<id> name=<text> minhull=<pct>]   no id: only sets the default min hull (50) used by the probe's events
--   velocity id=<id> vx=<m/s> vy=<m/s> vz=<m/s> [const=1]
--   seta_off [mode=toggle|factor]               SETA must be ON when you start it (it never switches SETA on)
--   seta_watch [secs=60]                        logs every IsSetaActive change with a clock (S13.9 detection check)
--   teams_product [minhull=50]                  S13.7: teams 1-2 active, allied/hostile vs player, two inert test ships, colour readback
--   teams_report                                hull/relation readback of the two test ships (run after shooting the hostile one)
--   teams_end                                   readback, then removes the two test ships
--   md_table                                    S13.8: reads the MD table keyed by the dressed components back (after save + reload)
--   galaxy_dump                                 S13.11: sectors + gate links -> galaxy-dump.json (and the game log in chunks)
--   chat [off | say <Name> <RRGGBB|-> <text>]   S13.12: injects messages into the vanilla chat window and captures typed lines
-- Log lines: "[X4MP-SPIKE] S13.<n> <PASS|FAIL|INFO> k=v" (X4MPSpike.log, step names S13.1 ... S13.12).
--
-- Sources (x4-unpacked/ui/addons/): chatwindow.lua (ego_chatwindow): getChatMessages calls the GLOBAL OnlineGetChatMessages()
-- (line 263) and takes message.author/authorid/time/text/reported/isprivate; the author colour is Color[GetChatAuthorColor2(author)]
-- (lines 205-215, 274); typed lines go to the global OnlineSendChatMessage(text, userid) (443), "/x" lines to ExecuteDebugCommand (464);
-- the menu table is registered in the global Menus (name "ChatWindow", line 18/43) and refreshed by menu.onChatMessageReceived (97).
-- helper.lua:409 IsSetaActive, 8844 Helper.convertColorToText ("\027#AARRGGBB#" escape, alpha 0-100 scale).
-- ConvertStringToLuaID / table -> MD list: session-1 step S3 (x4mp_spike.lua sendMD "s3_destroy_list").

-- luacheck: globals X4MPSpike DebugError RegisterEvent ConvertStringTo64Bit ConvertStringToLuaID GetFactionData GetComponentData
-- luacheck: globals OnlineGetChatMessages OnlineSendChatMessage OnlineGetUserName Menus Helper

local ffi = require("ffi")
local C = ffi.C

local S = X4MPSpike
local K, log = S.K, S.log

S.s13 = S.s13 or {}
local G = S.s13
G.minhull = G.minhull or 50
G.teamShips = G.teamShips or {}
G.chat = G.chat or { msgs = {}, typed = 0, capture = true }

for _, decl in ipairs({
	"bool IsSetaActive(void);",
	"int64_t GetCurrentUTCDataTime(void);",
	"const char* GetChatAuthorColor2(const char* authorname);",
	"const char* GetSaveFolderPath(void);",
}) do
	pcall(ffi.cdef, decl) -- already declared by the vanilla Lua is fine
end

------------------------------------------------------------------------------
-- helpers
------------------------------------------------------------------------------
local function luaid(id)
	local ok, r = pcall(function() return ConvertStringToLuaID(tostring(id)) end)
	if ok then return r end
	return nil
end

local function toid(x)
	local ok, r = pcall(function() return ConvertStringTo64Bit(tostring(x)) end)
	if ok and r and tonumber(r) and tonumber(r) ~= 0 then return r end
	return nil
end

local function splitBar(text, maxParts)
	local parts, pos = {}, 1
	text = tostring(text or "")
	while true do
		if maxParts and #parts == maxParts - 1 then
			parts[#parts + 1] = text:sub(pos)
			break
		end
		local i = text:find("|", pos, true)
		if not i then
			parts[#parts + 1] = text:sub(pos)
			break
		end
		parts[#parts + 1] = text:sub(pos, i - 1)
		pos = i + 1
	end
	return parts
end

local function setaActive()
	local ok, v = pcall(function() return C.IsSetaActive() end)
	if ok then return v and true or false end
	return nil
end

local function gcd(id, ...)
	local ok, a, b, c, d = pcall(GetComponentData, id, ...)
	if ok then return a, b, c, d end
	return nil
end

------------------------------------------------------------------------------
-- S13.1: dress (name, min hull, forced radar, known) through MD
------------------------------------------------------------------------------
local function dress(idString, name, minhull)
	local lid = luaid(idString)
	if not lid then
		log("S13.1", "FAIL", K("what", "dress", "result", "id_conversion_failed", "id", idString))
		return false
	end
	minhull = tonumber(minhull) or G.minhull
	local before = gcd(toid(idString) or 0, "name")
	log("S13.1", "INFO", K("what", "dress_request", "id", idString, "name", name, "minhull", minhull, "name_before", before))
	return S.toMD("s13_dress", { lid, tostring(name), minhull })
end

local function onDressEvent(_, param)
	local p = splitBar(param, 2)
	if not p[1] or p[1] == "" then
		log("S13.1", "FAIL", K("what", "dress_event", "result", "bad_param", "param", param))
		return
	end
	dress(p[1], p[2] or "[MP] ghost", G.minhull)
end

S.register("dress", function(args)
	if args.minhull then G.minhull = tonumber(args.minhull) or G.minhull end
	if args.id then
		dress(args.id, args.name or "[MP] ghost", G.minhull)
	end
	S.notify("dress: default min hull " .. G.minhull .. " %" .. (args.id and (", dressed " .. args.id) or " (the probe raises x4mp.spike_dress itself)"))
end, "S13.1 dress an object: MD name, min hull, forced radar, known (id from the probe log)")

------------------------------------------------------------------------------
-- S13.2c: velocity assist
------------------------------------------------------------------------------
local function velocity(idString, vx, vy, vz, const)
	local lid = luaid(idString)
	vx, vy, vz = tonumber(vx), tonumber(vy), tonumber(vz)
	if not lid or not vx or not vy or not vz then
		log("S13.2", "FAIL", K("what", "velocity", "result", "bad_args", "id", idString, "vx", vx, "vy", vy, "vz", vz, "lid", lid ~= nil))
		return false
	end
	return S.toMD("s13_velocity", { lid, vx, vy, vz, const and 1 or 0 })
end

local function onVelocityEvent(_, param)
	local p = splitBar(param)
	velocity(p[1], p[2], p[3], p[4], false)
end

S.register("velocity", function(args)
	local ok = velocity(args.id, args.vx or 0, args.vy or 0, args.vz or 0, args.const == "1")
	S.notify("velocity: " .. (ok and "sent to MD, see log" or "bad arguments"))
end, "S13.2c MD set_object_velocity: velocity id=<id> vx= vy= vz= [const=1]")

------------------------------------------------------------------------------
-- S13.9: SETA
------------------------------------------------------------------------------
local function setaOff(mode)
	local before = setaActive()
	log("S13.9", "INFO", K("what", "seta_off_request", "IsSetaActive_before", before, "mode", mode or "toggle"))
	if not before then
		S.notify("SETA is not active: turn it on first, then run seta_off")
		return
	end
	S.toMD("s13_seta_off", mode or "toggle")
	local t0 = S.now()
	S.startRoutine("s13_seta_off", function()
		S.waitSeconds(0.3)
		log("S13.9", "INFO", K("what", "seta_lua_readback", "IsSetaActive", setaActive(), "after_ms", (S.now() - t0) * 1000))
		S.waitSeconds(1.2)
		local after = setaActive()
		log("S13.9", after == false and "PASS" or "FAIL", K("what", "seta_off_result", "IsSetaActive_after", after,
			"after_ms", (S.now() - t0) * 1000, "mode", mode or "toggle"))
		S.notify("SETA is " .. (after == false and "OFF now" or "still ON (see log)"))
	end)
end

local function onSetaOffEvent()
	setaOff(G.setaMode)
end

S.register("seta_off", function(args)
	G.setaMode = args.mode
	setaOff(args.mode)
end, "S13.9 SETA off via MD (turn SETA on first). mode=toggle (default) or factor")

S.register("seta_watch", function(args)
	local secs = tonumber(args.secs) or 60
	G.setaWatchGen = (G.setaWatchGen or 0) + 1
	local gen = G.setaWatchGen
	local t0 = S.now()
	log("S13.9", "INFO", K("what", "seta_watch_start", "secs", secs, "IsSetaActive", setaActive()))
	S.notify("seta_watch: " .. secs .. " s. Turn SETA on, then off")
	S.startRoutine("s13_seta_watch", function()
		local last = setaActive()
		while G.setaWatchGen == gen and (S.now() - t0) < secs do
			S.waitFrames(1)
			local now = setaActive()
			if now ~= last then
				log("S13.9", "INFO", K("what", "seta_changed", "IsSetaActive", now, "t_s", S.now() - t0))
				last = now
			end
		end
		log("S13.9", "INFO", K("what", "seta_watch_end", "IsSetaActive", setaActive()))
	end)
end, "S13.9 log every IsSetaActive change for N seconds (detection check)")

------------------------------------------------------------------------------
-- S13.7: teams (spike-local factions modelled on the product design; M3-08 owns the product version)
------------------------------------------------------------------------------
local function factionColour(id)
	local ok, c = pcall(GetFactionData, id, "color")
	if ok and type(c) == "table" then
		return string.format("%s/%s/%s a=%s", tostring(c.r), tostring(c.g), tostring(c.b), tostring(c.a))
	end
	return "nil"
end

local function teamsReport(tag)
	for _, f in ipairs({ "x4mp_team_1", "x4mp_team_2" }) do
		local ok, name, rel = pcall(GetFactionData, f, "name", "relation")
		log("S13.7", "INFO", K("what", "faction_readback", "tag", tag, "faction", f, "name", ok and name or "err", "colour_rgb", factionColour(f),
			"relation_to_player", ok and rel or "nil"))
	end
	for k = 1, 2 do
		local id = G.teamShips[k]
		if id then
			local name, hull, uirel, owner = gcd(id, "name", "hullpercent", "uirelation", "owner")
			log("S13.7", "INFO", K("what", "ship_readback", "tag", tag, "team", k, "id", tonumber(id), "name", name, "hullpercent", hull,
				"uirelation", uirel, "owner", owner))
		else
			log("S13.7", "INFO", K("what", "ship_readback", "tag", tag, "team", k, "result", "no_ship_id_received_from_md"))
		end
	end
end

local pendingTeam
local function onTeamShipTag(_, param) pendingTeam = tonumber(param) end
local function onTeamShip(_, param)
	local id = toid(tostring(param))
	if pendingTeam and id then G.teamShips[pendingTeam] = id end
	log("S13.7", "INFO", K("what", "md_ship_id_received", "team", pendingTeam, "raw", tostring(param), "id", id and tonumber(id) or "conversion_failed"))
end

S.register("teams_product", function(args)
	local minhull = tonumber(args.minhull) or G.minhull
	G.teamShips = {}
	log("S13.7", "INFO", K("what", "teams_start", "minhull", minhull, "note", "spike_local_factions_(product_has_no_team_libraries_yet)"))
	S.toMD("s13_teams", minhull)
	S.notify("teams: two Argon fighters 1.5 km ahead (MP Team 1 allied, MP Team 2 hostile). Target them, then shoot team 2")
	S.startRoutine("s13_teams", function()
		S.waitSeconds(3)
		teamsReport("after_3s")
	end)
end, "S13.7 teams 1-2 active, relations allied/hostile vs player, two inert ships with min hull, colour readback")

S.register("teams_report", function() teamsReport("manual") end, "S13.7 hull / relation readback of the test ships")

S.register("teams_end", function()
	teamsReport("before_remove")
	S.toMD("s13_teams_end", "go")
	G.teamShips = {}
	S.notify("teams: test ships removed")
end, "S13.7 readback, then remove the two test ships")

------------------------------------------------------------------------------
-- S13.8: MD table keyed by a component
------------------------------------------------------------------------------
S.register("md_table", function()
	S.toMD("s13_persist", "go")
	S.notify("md_table: read back, see log (S13.8)")
end, "S13.8 read the MD table keyed by the dressed components (run after save + reload)")

------------------------------------------------------------------------------
-- S13.11: galaxy dump
------------------------------------------------------------------------------
local function docsX4mpDir()
	-- GetSaveFolderPath() = <Documents>\Egosoft\X4\<id>\save\  ->  <Documents>\Egosoft\X4\x4mp
	local ok, folder = pcall(function() return ffi.string(C.GetSaveFolderPath()) end)
	if not ok or not folder or folder == "" then return nil end
	folder = folder:gsub("[/\\]+$", "")
	local root = folder:match("^(.*)[/\\][^/\\]+[/\\][^/\\]+$") -- strip "<id>\save"
	if not root then return nil end
	return root .. "\\x4mp"
end

local function writeFile(path, data)
	-- (1) Lua io, if X4 left it in the sandbox; (2) kernel32 through ffi (QueryPerformanceCounter resolves the same way, session 2)
	if rawget(_G, "io") and io.open then
		local ok, f = pcall(io.open, path, "wb")
		if ok and f then
			local okW = pcall(function() f:write(data) end)
			pcall(function() f:close() end)
			if okW then return true, "lua_io" end
		end
	end
	local okDecl = pcall(ffi.cdef, [[
		void* CreateFileA(const char* lpFileName, uint32_t dwDesiredAccess, uint32_t dwShareMode, void* lpSecurityAttributes,
			uint32_t dwCreationDisposition, uint32_t dwFlagsAndAttributes, void* hTemplateFile);
		int WriteFile(void* hFile, const void* lpBuffer, uint32_t nNumberOfBytesToWrite, uint32_t* lpNumberOfBytesWritten, void* lpOverlapped);
		int CloseHandle(void* hObject);
		uint32_t GetLastError(void);
	]])
	local ok, res = pcall(function()
		local h = C.CreateFileA(path, 0x40000000, 1, nil, 2, 0x80, nil)
		if h == nil or tonumber(ffi.cast("intptr_t", h)) == -1 then
			return false, "CreateFileA_failed_err_" .. tostring(C.GetLastError())
		end
		local written = ffi.new("uint32_t[1]")
		local wrote = C.WriteFile(h, data, #data, written, nil)
		C.CloseHandle(h)
		if wrote == 0 or tonumber(written[0]) ~= #data then return false, "WriteFile_short_" .. tostring(tonumber(written[0])) end
		return true, "kernel32"
	end)
	if not ok then return false, "ffi_error_" .. tostring(res) .. (okDecl and "" or "_cdef_failed") end
	return res, nil
end

local function jsonString(s)
	return '"' .. tostring(s):gsub('[%c"\\]', function(ch) return string.format("\\u%04x", ch:byte()) end) .. '"'
end

local function finishGalaxy(recs, reported)
	table.sort(recs, function(a, b) return a.macro < b.macro end)
	local out = { '{"format":1,"generator":"x4mp_spike galaxy_dump (session 4, S13.11)","sector_count":', tostring(#recs),
		',"md_reported_count":', tostring(reported), ',"sectors":[' }
	local links = 0
	for i, r in ipairs(recs) do
		table.sort(r.gates)
		local uniq, last = {}, nil
		for _, g in ipairs(r.gates) do
			if g ~= last then uniq[#uniq + 1] = jsonString(g); last = g end
		end
		links = links + #uniq
		out[#out + 1] = (i > 1 and "," or "") .. '{"macro":' .. jsonString(r.macro) .. ',"cluster":' .. jsonString(r.cluster) ..
			',"gates":[' .. table.concat(uniq, ",") .. "]}"
	end
	out[#out + 1] = "]}\n"
	local json = table.concat(out)
	local ok = #recs >= 140
	log("S13.11", ok and "PASS" or "FAIL", K("what", "galaxy_collected", "sectors", #recs, "md_reported", reported, "gate_links", links,
		"bytes", #json, "needed_sectors", 140))

	local dir = docsX4mpDir()
	local path = dir and (dir .. "\\galaxy-dump.json") or nil
	if path then
		local wrote, how = writeFile(path, json)
		log("S13.11", wrote and "PASS" or "INFO", K("what", "galaxy_file_write", "ok", wrote, "how", how or "n/a"))
	else
		log("S13.11", "INFO", K("what", "galaxy_file_write", "ok", false, "how", "no_save_folder_path"))
	end
	-- always also log the JSON in chunks: collect-logs.ps1 / extract-galaxy-dump.ps1 rebuild the file from the game log
	local CH = 900
	local n = math.ceil(#json / CH)
	log("S13.11", "INFO", K("what", "galaxy_data_begin", "parts", n, "bytes", #json))
	for i = 1, n do
		log("S13.11", "DATA", "part=" .. i .. "/" .. n .. " json=" .. json:sub((i - 1) * CH + 1, i * CH):gsub("\n", "~"))
	end
	log("S13.11", "INFO", K("what", "galaxy_data_end", "parts", n))
	S.notify("galaxy_dump: " .. #recs .. " sectors " .. (ok and "(>= 140 ok)" or "(FEWER than 140)") .. (path and ", file written or logged" or ", logged"))
end

local function onGalaxyEvent(_, param)
	param = tostring(param or "")
	local kind, rest = param:match("^(%a);?(.*)$")
	if not G.galaxy then return end
	if kind == "R" then
		for rec in rest:gmatch("[^;]+") do
			local p = splitBar(rec)
			if p[1] and p[1] ~= "" then
				local gates = {}
				for g in tostring(p[3] or ""):gmatch("[^,]+") do gates[#gates + 1] = g end
				G.galaxy.recs[#G.galaxy.recs + 1] = { macro = p[1], cluster = p[2] or "", gates = gates }
			end
		end
	elseif kind == "E" then
		local recs = G.galaxy.recs
		G.galaxy = nil
		finishGalaxy(recs, tonumber(rest) or -1)
	end
end

S.register("galaxy_dump", function()
	G.galaxy = { recs = {} }
	log("S13.11", "INFO", K("what", "galaxy_start"))
	S.toMD("s13_galaxy", "go")
	S.notify("galaxy_dump: collecting sectors, see log")
end, "S13.11 sectors + gate links -> Documents\\Egosoft\\X4\\x4mp\\galaxy-dump.json (and the game log in chunks)")

------------------------------------------------------------------------------
-- S13.12: vanilla chat window outside Ventures
------------------------------------------------------------------------------
local function colourEscape(rrggbb)
	-- Helper.convertColorToText: "\027#AARRGGBB#" ; here alpha ff
	if type(rrggbb) == "string" and rrggbb:match("^%x%x%x%x%x%x$") then
		return "\027#ff" .. rrggbb:lower() .. "#"
	end
	return ""
end

local function chatMenu()
	if type(Menus) ~= "table" then return nil end
	for _, m in ipairs(Menus) do
		if type(m) == "table" and m.name == "ChatWindow" then return m end
	end
	return nil
end

local function chatRefresh()
	local m = chatMenu()
	if not m then
		log("S13.12", "INFO", K("what", "chat_refresh", "result", "no_ChatWindow_in_Menus"))
		return
	end
	local ok, err = pcall(function()
		if m.shown then m.onChatMessageReceived() end
	end)
	log("S13.12", ok and "INFO" or "FAIL", K("what", "chat_refresh", "menu_shown", m.shown, "ok", ok, "err", ok and "" or err))
end

local function nowMs()
	local ok, t = pcall(function() return tonumber(C.GetCurrentUTCDataTime()) end)
	if ok and t then return t * 1000 end
	return os.time and os.time() * 1000 or 0
end

local function chatInject(author, rrggbb, text, own)
	local c = G.chat
	local shownAuthor = colourEscape(rrggbb) .. author
	local okC, colorId = pcall(function() return ffi.string(C.GetChatAuthorColor2(shownAuthor)) end)
	c.msgs[#c.msgs + 1] = {
		author = shownAuthor, authorid = own and -2 or (-100 - #c.msgs), time = nowMs() + #c.msgs, text = text,
		reported = false, isprivate = false,
	}
	log("S13.12", "INFO", K("what", "chat_injected", "author", author, "embedded_colour", rrggbb or "none (palette)",
		"GetChatAuthorColor2", okC and colorId or "err", "text", text, "queued", #c.msgs))
	chatRefresh()
end

local function installChat()
	local c = G.chat
	if c.installed then return "already_installed" end
	-- OnlineGetChatMessages: original messages (Ventures, if any) + ours. Chain safe: the previous global is called through pcall.
	local prevGet = OnlineGetChatMessages
	c.prevGet = prevGet
	c.getWrapper = function(...)
		local list = {}
		if prevGet then
			local ok, res = pcall(prevGet, ...)
			if ok and type(res) == "table" then
				for _, m in ipairs(res) do list[#list + 1] = m end
			end
		end
		for _, m in ipairs(c.msgs) do list[#list + 1] = m end
		return list
	end
	OnlineGetChatMessages = c.getWrapper
	-- OnlineSendChatMessage: a typed (non-"/") line arrives here BEFORE Ventures would get it
	local prevSend = OnlineSendChatMessage
	c.prevSend = prevSend
	c.sendWrapper = function(text, userid, ...)
		c.typed = c.typed + 1
		log("S13.12", "PASS", K("what", "typed_line_captured", "n", c.typed, "text", text, "userid", userid, "capture", c.capture,
			"prev_exists", prevSend ~= nil))
		if c.capture then
			local name = "You"
			local okU, uname = pcall(function() return (OnlineGetUserName()) end)
			if okU and type(uname) == "string" and uname ~= "" then name = uname end
			chatInject(name, nil, tostring(text), true)
			S.notify("chat: captured '" .. tostring(text) .. "' (not sent to Ventures)")
			return
		end
		if prevSend then return prevSend(text, userid, ...) end
	end
	OnlineSendChatMessage = c.sendWrapper
	c.installed = true
	return (prevGet and "wrapped_get" or "no_previous_get") .. "," .. (prevSend and "wrapped_send" or "no_previous_send")
end

local function uninstallChat()
	local c = G.chat
	if not c.installed then return "not_installed" end
	-- unwrap only if ours is still the outermost wrapper (another mod may have wrapped on top of us)
	local notes = {}
	if OnlineGetChatMessages == c.getWrapper then OnlineGetChatMessages = c.prevGet; notes[#notes + 1] = "get_restored"
	else notes[#notes + 1] = "get_left_in_place" end
	if OnlineSendChatMessage == c.sendWrapper then OnlineSendChatMessage = c.prevSend; notes[#notes + 1] = "send_restored"
	else notes[#notes + 1] = "send_left_in_place" end
	c.installed = false
	c.msgs = {}
	chatRefresh()
	return table.concat(notes, ",")
end

S.register("chat", function(args)
	local first = args._[1]
	if first == "off" then
		local r = uninstallChat()
		log("S13.12", "INFO", K("what", "chat_off", "result", r))
		S.notify("chat: spike wrappers removed (" .. r .. ")")
		return
	end
	local res = installChat()
	local m = chatMenu()
	log("S13.12", "INFO", K("what", "chat_installed", "result", res, "ChatWindow_menu_found", m ~= nil, "OnlineGetChatMessages_defined",
		G.chat.prevGet ~= nil, "OnlineSendChatMessage_defined", G.chat.prevSend ~= nil))
	if first == "say" then
		local author, colour = args._[2], args._[3]
		local words = {}
		for i = 4, #args._ do words[#words + 1] = args._[i] end
		chatInject(author or "Alice", (colour and colour ~= "-") and colour or nil, table.concat(words, " "), false)
		return
	end
	chatInject("Alice", "ff8000", "hello from Alice (name coloured by an embedded colour code, orange)", false)
	chatInject("Bob", nil, "hello from Bob (name coloured by the game's own chat palette)", false)
	S.notify("chat: open the chat window (your Toggle Chat Window key): lines from Alice and Bob; type hello")
end, "S13.12 inject messages into the vanilla chat window and capture typed lines (chat off removes it)")

------------------------------------------------------------------------------
-- event registration (once per Lua state)
------------------------------------------------------------------------------
if not G.registered then
	G.registered = true
	local events = {
		{ "x4mp.spike_dress", onDressEvent },
		{ "x4mp.spike_velocity", onVelocityEvent },
		{ "x4mp.spike_seta_off", onSetaOffEvent },
		{ "x4mp_spike.s13_teams_ship_tag", onTeamShipTag },
		{ "x4mp_spike.s13_teams_ship", onTeamShip },
		{ "x4mp_spike.s13_galaxy", onGalaxyEvent },
		{ "x4mp_spike.chat_inject", function(_, param)
			local p = splitBar(param, 3)
			installChat()
			chatInject(p[1] or "Alice", (p[2] and p[2] ~= "") and p[2] or nil, p[3] or "")
		end },
	}
	for _, e in ipairs(events) do
		local ok, err = pcall(RegisterEvent, e[1], e[2])
		log("S13", ok and "INFO" or "FAIL", K("what", "register_event", "event", e[1], "err", ok and "" or err))
	end
end
log("S13", "INFO", K("what", "x4mp_spike_s13_loaded"))
