-- x4mp_bridge.lua : Lua <-> native bridge of the X4MP extension (M2-05).
--
-- The X4Native 9.0.0 API gives native extensions no lua_State*, so the two sides talk through events:
--   Lua -> native : _G.__X4NATIVE_API.raise_event("x4mp.<verb>", "<json>")   native: x4n::on("x4mp.<verb>", cb)
--   native -> Lua : x4n::raise_lua("x4mp.<topic>", "<json>")                   Lua: RegisterEvent("x4mp.<topic>", fn)
-- Every payload is one JSON object (UTF-8). Unknown fields must be ignored by both sides. "v" is the contract version (1).
--
-- VERBS (Lua -> native)
--   x4mp.join           {"v":1,"address":"host:port","name":"Player","password":"...","team":"auto"}
--                       address is normalised and always carries a port (default 47780; IPv6 as "[::1]:47780").
--                       team is "auto" (placeholder until the server pre-query exists). The password is present ONLY here,
--                       is never logged, persisted or kept after the call. May be "" (open session).
--   x4mp.disconnect     {"v":1}
--   x4mp.ui_ready       {"v":1,"startmenu":true|false}   sent once per Lua state as soon as __X4NATIVE_API exists
--   x4mp.request_status {"v":1}                          native answers with topic x4mp.status
--   x4mp.load_session   {"v":1}                          the player agrees to load the session save that differs from the running
--                                                        game (status state save_changed); native never loads it by itself then
--   x4mp.extensions     {"v":1,"source":"load"|"gfx_ok"|"show","startmenu":bool,"modified_ui_files":"...",
--                        "count":N,"list":[{"id","name","version","date","enabled","enabledbydefault","egosoftextension",
--                        "isworkshop","personal","sync","error","warning"}, ...]}   (fields present only when the game gives them)
--
-- TOPICS (native -> Lua), handled by the bridge and forwarded to B.on(topic, fn) handlers
--   x4mp.status    {"v":1,"state":"disconnected|connecting|handshaking|checking_save|downloading|loading|matching|save_changed|
--                   ingame|rejected|error","detail":"free text","reject":"build|mod|auth|full|banned|name|...",
--                   "server":"host:port","role":"client|authority","team":2,"team_name":"Team 2","ping_ms":12,
--                   "players":3,"progress":0.0-1.0}      all fields optional except state; at most 2 Hz
--   x4mp.notify    {"v":1,"text":"...","level":"info|warn|error"}      short message for the player
--   x4mp.error     {"v":1,"code":"...","text":"..."}                   something failed; shown on the status screen
--   x4mp.load_save {"v":1,"name":"x4mp_<sha12>","fallback":true|absent}  the downloaded session save is being loaded (name WITHOUT
--                   .xml.gz). Native itself raises the vanilla Lua event "loadSave" (what the options menu listens to; session 2
--                   proved loading by name works that way), then sends this topic WITHOUT "fallback": Lua only records that a
--                   load started (B.loadingSave), so the start menu is not restored over the loading screen. If no reload
--                   happens within 15 s native sends it again WITH "fallback":true and Lua calls LoadGame(name) 0.1 s later.
--   x4mp.open      {"v":1,"screen":"main|join|status"}                 open the X4MP screen (also: chat "/x4mp [screen]")
--   x4mp.mod_refusal  (M2-X3, handled by x4mp_join_mods.lua through B.on) {"v":1,"policy_version":N,"install":[ref],"enable":[ref],
--                   "disable":[ref],"update":[ref],"<group>_more":N}  ref = {"id","name","version","have_version","nexus_url",
--                   "workshop_id","notes"}. Raised once when the join is refused with ExtensionsMismatch, before the "rejected"
--                   status (reject "mod"); lists capped at 100 entries, strings at 256 bytes; re-sent after ui_ready/request_status.
--   x4mp.mod_policy   (M2-X3) {"v":1,"version":N,"source_mode":"authority|admin","unknown_default":"client_only|block|allow_all",
--                   "enforcement":"strict|warn","entries":[{"id","name","rule":"required|allowed|blocked","enabled","version_rule",
--                   "version","nexus_url","workshop_id","notes"}],"entries_more":N}   the session mod list after Welcome and on edits
--
-- JSON subset: objects, arrays, strings (full escapes, \uXXXX incl. surrogate pairs), numbers, true/false/null.
-- decode(): null inside an object is dropped (field absent), null inside an array is json.null.
-- encode(): object keys are sorted (stable output); empty tables encode as {} unless made with json.array().

-- luacheck: globals X4MPBridge DebugError RegisterEvent LoadGame getElapsedTime Helper

if X4MPBridge and X4MPBridge.loaded then return end

local B = { loaded = true, version = 1, handlers = {}, pending = {} }
X4MPBridge = B

B.VERBS = { join = true, disconnect = true, ui_ready = true, request_status = true, extensions = true, load_session = true }
B.TOPICS = { "status", "notify", "error", "load_save", "open" }
local queueable = { ui_ready = true, request_status = true, extensions = true }

------------------------------------------------------------------------------
-- logging (never takes a join payload)
------------------------------------------------------------------------------
function B.log(msg)
	pcall(DebugError, "[X4MP] " .. tostring(msg))
end
local log = B.log

------------------------------------------------------------------------------
-- minimal JSON codec
------------------------------------------------------------------------------
local json = {}
B.json = json
json.null = setmetatable({}, { __tostring = function() return "null" end })
local arrayMt = { __x4mp_array = true }
function json.array(t) return setmetatable(t or {}, arrayMt) end

local MAX_DEPTH = 32
local escapes = { ['"'] = '\\"', ["\\"] = "\\\\", ["\b"] = "\\b", ["\f"] = "\\f", ["\n"] = "\\n", ["\r"] = "\\r", ["\t"] = "\\t" }

local function encodeString(s)
	local body = s:gsub('[%c"\\]', function(c)
		return escapes[c] or string.format("\\u%04x", c:byte())
	end)
	return '"' .. body .. '"'
end

local function isArray(t)
	if getmetatable(t) == arrayMt then return true end
	local n = 0
	for _ in pairs(t) do n = n + 1 end
	if n == 0 then return false end
	for i = 1, n do
		if t[i] == nil then return false end
	end
	return true
end

local function encodeValue(v, depth)
	if depth > MAX_DEPTH then error("json: nesting too deep", 0) end
	local t = type(v)
	if v == nil or v == json.null then return "null" end
	if t == "boolean" then return v and "true" or "false" end
	if t == "number" then
		if v ~= v or v == math.huge or v == -math.huge then return "null" end
		if v == math.floor(v) and math.abs(v) < 1e15 then return string.format("%d", v) end
		return string.format("%.14g", v)
	end
	if t == "string" then return encodeString(v) end
	if t ~= "table" then error("json: cannot encode " .. t, 0) end
	local out = {}
	if isArray(v) then
		for i = 1, #v do out[i] = encodeValue(v[i], depth + 1) end
		return "[" .. table.concat(out, ",") .. "]"
	end
	local keys = {}
	for k in pairs(v) do
		if type(k) ~= "string" then error("json: object keys must be strings", 0) end
		keys[#keys + 1] = k
	end
	table.sort(keys)
	for i, k in ipairs(keys) do out[i] = encodeString(k) .. ":" .. encodeValue(v[k], depth + 1) end
	return "{" .. table.concat(out, ",") .. "}"
end

--- encode(value) -> string, or nil, err
function json.encode(value)
	local ok, res = pcall(encodeValue, value, 0)
	if ok then return res end
	return nil, tostring(res)
end

local function utf8char(cp)
	if cp < 0x80 then return string.char(cp) end
	if cp < 0x800 then return string.char(0xC0 + math.floor(cp / 64), 0x80 + cp % 64) end
	if cp < 0x10000 then
		return string.char(0xE0 + math.floor(cp / 4096), 0x80 + math.floor(cp / 64) % 64, 0x80 + cp % 64)
	end
	return string.char(0xF0 + math.floor(cp / 262144), 0x80 + math.floor(cp / 4096) % 64, 0x80 + math.floor(cp / 64) % 64,
		0x80 + cp % 64)
end

local unescapes = { ['"'] = '"', ["\\"] = "\\", ["/"] = "/", b = "\b", f = "\f", n = "\n", r = "\r", t = "\t" }
local literals = { { "true", true }, { "false", false }, { "null", json.null } }

--- decode(text) -> value, or nil, err
function json.decode(text)
	if type(text) ~= "string" then return nil, "json: not a string" end
	local pos = 1
	local function fail(msg) error(string.format("json: %s at %d", msg, pos), 0) end
	local function skip() pos = text:find("[^ \t\r\n]", pos) or #text + 1 end
	local parseValue

	local function parseString()
		pos = pos + 1 -- opening quote
		local parts = {}
		while true do
			local s, e = text:find('^[^"\\%c]+', pos)
			if s then
				parts[#parts + 1] = text:sub(s, e)
				pos = e + 1
			end
			local c = text:sub(pos, pos)
			if c == '"' then
				pos = pos + 1
				return table.concat(parts)
			elseif c == "\\" then
				local n = text:sub(pos + 1, pos + 1)
				if n == "u" then
					local hex = text:match("^%x%x%x%x", pos + 2)
					if not hex then fail("bad \\u escape") end
					local cp = tonumber(hex, 16)
					pos = pos + 6
					if cp >= 0xD800 and cp <= 0xDBFF then
						local lo = text:match("^\\u(%x%x%x%x)", pos)
						local lcp = lo and tonumber(lo, 16)
						if not lcp or lcp < 0xDC00 or lcp > 0xDFFF then fail("lone surrogate") end
						cp = 0x10000 + (cp - 0xD800) * 1024 + (lcp - 0xDC00)
						pos = pos + 6
					elseif cp >= 0xDC00 and cp <= 0xDFFF then
						fail("lone surrogate")
					end
					parts[#parts + 1] = utf8char(cp)
				elseif unescapes[n] then
					parts[#parts + 1] = unescapes[n]
					pos = pos + 2
				else
					fail("bad escape")
				end
			else
				fail(c == "" and "unterminated string" or "control character in string")
			end
		end
	end

	local function parseNumber()
		local s, e = text:find("^%-?%d+%.?%d*[eE]?[%+%-]?%d*", pos)
		local n = s and tonumber(text:sub(s, e))
		if not n then fail("bad number") end
		pos = e + 1
		return n
	end

	local function parseContainer(depth, open)
		local close = open == "{" and "}" or "]"
		local result = {}
		pos = pos + 1
		skip()
		if text:sub(pos, pos) == close then
			pos = pos + 1
			return open == "[" and json.array(result) or result
		end
		local index = 0
		while true do
			skip()
			local key
			if open == "{" then
				if text:sub(pos, pos) ~= '"' then fail("object key expected") end
				key = parseString()
				skip()
				if text:sub(pos, pos) ~= ":" then fail("':' expected") end
				pos = pos + 1
			end
			local value = parseValue(depth + 1)
			if open == "{" then
				if value ~= json.null then result[key] = value end
			else
				index = index + 1
				result[index] = value
			end
			skip()
			local c = text:sub(pos, pos)
			pos = pos + 1
			if c == close then return open == "[" and json.array(result) or result end
			if c ~= "," then fail("',' or '" .. close .. "' expected") end
		end
	end

	function parseValue(depth)
		if depth > MAX_DEPTH then fail("nesting too deep") end
		skip()
		local c = text:sub(pos, pos)
		if c == "{" or c == "[" then return parseContainer(depth, c) end
		if c == '"' then return parseString() end
		if c == "-" or c:match("%d") then return parseNumber() end
		for _, lit in ipairs(literals) do
			if text:sub(pos, pos + #lit[1] - 1) == lit[1] then
				pos = pos + #lit[1]
				return lit[2]
			end
		end
		return fail("unexpected character")
	end

	local ok, res = pcall(function()
		local value = parseValue(0)
		skip()
		if pos <= #text then fail("trailing data") end
		return value
	end)
	if ok then return res end
	return nil, tostring(res)
end

------------------------------------------------------------------------------
-- native API handle (set by extensions/x4native/ui/x4native.lua, which may load after us)
------------------------------------------------------------------------------
function B.getApi()
	local api = rawget(_G, "__X4NATIVE_API")
	if type(api) == "table" and type(api.raise_event) == "function" then return api end
	return nil
end

function B.isStartMenu()
	local okFfi, ffi = pcall(require, "ffi")
	if not okFfi then return false end
	pcall(ffi.cdef, "bool IsStartmenu();") -- "redefine" is expected and harmless
	local ok, res = pcall(function() return ffi.C.IsStartmenu() end)
	return ok and res == true
end

local function rawSend(verb, payload)
	local api = B.getApi()
	if not api then return false, "no_api" end
	local text, err = json.encode(payload)
	if not text then return false, "encode: " .. tostring(err) end
	local ok, perr = pcall(api.raise_event, "x4mp." .. verb, text)
	if not ok then return false, tostring(perr) end
	return true
end

--- send(verb, payload) -> ok, err. Adds "v":1. join and disconnect are never queued; the other verbs wait (latest wins)
--- until __X4NATIVE_API exists. The payload is never logged (a join carries the password).
function B.send(verb, payload)
	if not B.VERBS[verb] then return false, "unknown_verb" end
	payload = payload or {}
	payload.v = payload.v or 1
	local ok, err = rawSend(verb, payload)
	if ok then return true end
	if err == "no_api" and queueable[verb] then
		B.pending[verb] = payload
		return true, "queued"
	end
	log("send " .. verb .. " failed: " .. tostring(err))
	return false, err
end

--- Called on load and on gfx_ok / show: announces ui_ready once the API exists and flushes queued verbs.
function B.retry(reason)
	if not B.getApi() then return false end
	if not B.readySent then
		B.readySent = true
		B.send("ui_ready", { startmenu = B.isStartMenu() })
		log("bridge ready (api present, trigger=" .. tostring(reason) .. ")")
	end
	local queued = B.pending
	B.pending = {}
	for verb, payload in pairs(queued) do rawSend(verb, payload) end
	return true
end

------------------------------------------------------------------------------
-- topics
------------------------------------------------------------------------------
--- on(topic, fn): fn(payloadTable, rawText) is called inside pcall for every x4mp.<topic>. Registers the Lua event lazily.
function B.on(topic, fn)
	local list = B.handlers[topic]
	if not list then
		list = {}
		B.handlers[topic] = list
		RegisterEvent("x4mp." .. topic, function(_, param) B.dispatch(topic, param) end)
	end
	list[#list + 1] = fn
end

function B.dispatch(topic, param)
	local text = type(param) == "string" and param or tostring(param or "")
	local payload, err = json.decode(text)
	if type(payload) ~= "table" or payload == json.null then
		log("topic " .. topic .. ": bad payload (" .. tostring(err) .. ")")
		return false
	end
	for _, fn in ipairs(B.handlers[topic] or {}) do
		local ok, herr = pcall(fn, payload, text)
		if not ok then log("topic " .. topic .. " handler raised: " .. tostring(herr)) end
	end
	return true
end

-- built-in state
B.status = nil
B.lastError = nil
B.lastNotice = nil

B.on("status", function(p) B.status = p end)
B.on("notify", function(p) B.lastNotice = p end)
B.on("error", function(p)
	B.lastError = p
	log("native error " .. tostring(p.code) .. ": " .. tostring(p.text))
end)
B.on("open", function() end) -- the screens (x4mp_menu.lua) add their own handler

--- Save name sanity: no path parts, no extension, printable.
function B.validSaveName(name)
	if type(name) ~= "string" then return nil end
	name = name:gsub("%.xml%.gz$", ""):gsub("%.xml$", "")
	if name == "" or #name > 120 or name:find("[/\\:%c]") or name:find("..", 1, true) then return nil end
	return name
end

B.loadingSave = nil -- name of the save native is loading (set by x4mp.load_save, cleared when a screen opens again)

B.on("load_save", function(p)
	local name = B.validSaveName(p.name)
	if not name then
		B.lastError = { code = "bad_save_name", text = tostring(p.name) }
		log("load_save: rejected save name " .. tostring(p.name))
		return
	end
	B.loadingSave = name
	if p.fallback ~= true then
		log("load_save: native raised the vanilla loadSave event for " .. name)
		return
	end
	log("load_save: fallback, calling LoadGame for " .. name)
	local function go() LoadGame(name) end
	if type(getElapsedTime) == "function" and type(Helper) == "table" and Helper.addDelayedOneTimeCallbackOnUpdate then
		Helper.addDelayedOneTimeCallbackOnUpdate(go, true, getElapsedTime() + 0.1)
	else
		go()
	end
end)

------------------------------------------------------------------------------
-- start-up: the API may not exist yet, retry on gfx_ok / show
------------------------------------------------------------------------------
RegisterEvent("gfx_ok", function() B.retry("gfx_ok") end)
RegisterEvent("show", function() B.retry("show") end)
B.retry("load")
log("bridge loaded (api present: " .. tostring(B.getApi() ~= nil) .. ")")
