-- x4mp_join_mods.lua : grouped mod refusal with links, and the session mod list (M2-X3).
--
-- Native topics handled here (documented in docs/mod-design.md section 7.1, native side features/join/join_mods_json.h):
--   x4mp.mod_refusal {"v":1,"policy_version":N,"install":[ref],"enable":[ref],"disable":[ref],"update":[ref],
--                     "install_more":N, ...}   ref = {"id","name","version","have_version","nexus_url","workshop_id","notes"}
--                     Raised once when the server refuses the join with ExtensionsMismatch, just before the "rejected" status.
--   x4mp.mod_policy  {"v":1,"version":N,"source_mode":"authority|admin","unknown_default":"...","enforcement":"strict|warn",
--                     "entries":[{"id","name","rule":"required|allowed|blocked","enabled","version_rule","version","nexus_url",
--                     "workshop_id","notes"}]}   the session mod list: after Welcome and whenever the admin edits it.
--
-- Link rule (session-2 finding D7): in game only Steam-hosted URLs open. A Workshop item gets an "Open Workshop page" button
-- (https://steamcommunity.com/sharedfiles/filedetails/?id=<n>, derived HERE from the numeric id, never taken from the server).
-- A Nexus URL (or anything else) is shown as selectable text in a read-only edit row, never as a button that does nothing.
-- If the game cannot open a browser at all the Workshop URL is shown as text too.
--
-- The screens (x4mp_menu.lua) call X4MPJoinMods.refusalRows(rows) on the status screen of a "mod" rejection and
-- X4MPJoinMods.policyRows(rows) on the Multiplayer screen while connected. Texts are on page 92000, ids 400-449.

-- luacheck: globals X4MPBridge X4MPScreens X4MPJoinMods DebugError

if X4MPJoinMods and X4MPJoinMods.loaded then return end

local B = X4MPBridge
local S = X4MPScreens
if type(B) ~= "table" or type(S) ~= "table" then
	pcall(DebugError, "[X4MP] join mods: x4mp_bridge.lua and x4mp_menu.lua must load first")
	return
end

local M = { loaded = true, MAX_PER_GROUP = 8, MAX_POLICY_ROWS = 12, MAX_STRING = 256, refusal = nil, policy = nil }
X4MPJoinMods = M
local log = B.log
local T = S.T

local GROUPS = { "install", "enable", "disable", "update" }
local GROUP_TITLE = { install = 401, enable = 402, disable = 403, update = 404 }
local GROUP_HINT = { install = 416, enable = 414, disable = 415, update = 417 }
local GROUP_TONE = { install = "warning", enable = "warning", disable = "error", update = "warning" }
M.GROUPS = GROUPS

------------------------------------------------------------------------------
-- links
------------------------------------------------------------------------------
local WORKSHOP_PREFIX = "https://steamcommunity.com/sharedfiles/filedetails/?id="

local function positiveInt(v)
	local n = tonumber(v)
	if n and n >= 1 and n == math.floor(n) and n < 2 ^ 53 then return string.format("%.0f", n) end
	return nil
end

--- workshopUrl(entry) -> the Steam Workshop URL derived from the numeric id (or from a ws_<digits> id), or nil
function M.workshopUrl(entry)
	local digits = positiveInt(entry.workshopId)
	if not digits and type(entry.id) == "string" then digits = entry.id:match("^ws_(%d+)$") end
	if not digits or #digits > 16 then return nil end
	return WORKSHOP_PREFIX .. digits
end

--- nexusUrl(text) -> normalised https://www.nexusmods.com/x4foundations/mods/<n>, or nil when it is not a valid X4 Nexus mod URL
function M.nexusUrl(text)
	if type(text) ~= "string" then return nil end
	local n = text:match("^https://www%.nexusmods%.com/x4foundations/mods/(%d+)$")
		or text:match("^https://www%.nexusmods%.com/x4foundations/mods/(%d+)/")
		or text:match("^https://nexusmods%.com/x4foundations/mods/(%d+)$")
		or text:match("^https://nexusmods%.com/x4foundations/mods/(%d+)/")
	if not n or #n > 12 then return nil end
	return "https://www.nexusmods.com/x4foundations/mods/" .. n
end

--- a URL we only show as text: plain http(s), no spaces or control characters, bounded length
local function showableText(text)
	if type(text) ~= "string" or #text < 8 or #text > 200 then return nil end
	if not (text:find("^https?://")) or text:find("[%s%c]") then return nil end
	return text
end

--- links(entry) -> list of { kind = "button"|"text", url, source = "workshop"|"nexus"|"other" }
--- Workshop -> button, Nexus -> text, anything else -> text; no link at all -> empty list.
function M.links(entry)
	local out = {}
	local ws = M.workshopUrl(entry)
	if ws then out[#out + 1] = { kind = "button", url = ws, source = "workshop" } end
	if entry.nexusUrl and entry.nexusUrl ~= "" then
		local nx = M.nexusUrl(entry.nexusUrl)
		if nx then
			out[#out + 1] = { kind = "text", url = nx, source = "nexus" }
		else
			local other = showableText(entry.nexusUrl)
			if other then out[#out + 1] = { kind = "text", url = other, source = "other" } end
		end
	end
	return out
end

--- true when the game says it can open a browser (C.CanOpenWebBrowser). Replaceable in tests (M.canOpen = function ...).
function M.canOpen()
	local okFfi, ffi = pcall(require, "ffi")
	if not okFfi then return false end
	pcall(ffi.cdef, "bool CanOpenWebBrowser(void);")
	local ok, res = pcall(function() return ffi.C.CanOpenWebBrowser() end)
	return ok and res == true
end

--- openUrl(url): only the Workshop URL form is ever opened (D7: only Steam-hosted URLs work, and the server never chooses the host)
function M.openUrl(url)
	if type(url) ~= "string" or not url:find("^" .. WORKSHOP_PREFIX:gsub("[%?%.%-]", "%%%0") .. "%d+$") then
		log("join mods: refused to open a non-Workshop URL")
		return false
	end
	local okFfi, ffi = pcall(require, "ffi")
	if not okFfi then return false end
	pcall(ffi.cdef, "void OpenWebBrowser(const char* url);")
	local ok, err = pcall(function() ffi.C.OpenWebBrowser(url) end)
	if not ok then log("join mods: OpenWebBrowser failed: " .. tostring(err)) end
	M.lastOpened = url
	return ok
end

------------------------------------------------------------------------------
-- normalisation
------------------------------------------------------------------------------
local function str(v)
	if type(v) == "string" then return v:sub(1, M.MAX_STRING) end
	if type(v) == "number" then return tostring(v) end
	return ""
end

local function normalizeRef(r)
	if type(r) ~= "table" then return nil end
	local e = { id = str(r.id), name = str(r.name), version = str(r.version), haveVersion = str(r.have_version),
		nexusUrl = str(r.nexus_url), workshopId = r.workshop_id, notes = str(r.notes) }
	if e.id == "" and e.name == "" then return nil end
	if e.name == "" then e.name = e.id end
	return e
end

--- normalizeRefusal(payload) -> { policyVersion, groups = { install = {entry...}, ... }, more = { install = n, ... }, total }
function M.normalizeRefusal(p)
	local r = { policyVersion = tonumber(p.policy_version) or 0, groups = {}, more = {}, total = 0 }
	for _, g in ipairs(GROUPS) do
		local list, src = {}, type(p[g]) == "table" and p[g] or {}
		for _, ref in ipairs(src) do
			local e = normalizeRef(ref)
			if e then list[#list + 1] = e end
		end
		r.groups[g] = list
		r.more[g] = math.max(0, tonumber(p[g .. "_more"]) or 0)
		r.total = r.total + #list + r.more[g]
	end
	return r
end

--- normalizePolicy(payload) -> { version, sourceMode, enforcement, entries = { {id,name,rule,enabled,version,...} } }
function M.normalizePolicy(p)
	local pol = { version = tonumber(p.version) or 0, sourceMode = str(p.source_mode), enforcement = str(p.enforcement),
		unknownDefault = str(p.unknown_default), entries = {}, more = math.max(0, tonumber(p.entries_more) or 0) }
	for _, raw in ipairs(type(p.entries) == "table" and p.entries or {}) do
		local e = normalizeRef({ id = raw.id, name = raw.name, version = raw.version, nexus_url = raw.nexus_url,
			workshop_id = raw.workshop_id, notes = raw.notes })
		if e then
			e.rule = str(raw.rule)
			if e.rule == "required" and raw.enabled == false then e.rule = "blocked" end -- a disabled Required entry acts as Blocked
			e.versionRule = str(raw.version_rule)
			pol.entries[#pol.entries + 1] = e
		end
	end
	return pol
end

------------------------------------------------------------------------------
-- rows
------------------------------------------------------------------------------
local function text(t, tone) return { type = "text", text = t, tone = tone or "normal" } end

local function urlRow(rows, id, url)
	rows[#rows + 1] = { type = "edit", id = id, label = T(431), value = url, readonly = true, fullWidth = true, maxChars = 256,
		description = T(431), onChange = function() end }
end

local function entryRows(rows, group, e, index)
	rows[#rows + 1] = text(e.version ~= "" and T(405, e.name, e.version) or T(406, e.name))
	if e.id ~= "" and e.id ~= e.name then rows[#rows + 1] = text(T(407, e.id), "inactive") end
	if group == "update" and e.haveVersion ~= "" then
		rows[#rows + 1] = text(T(408, e.haveVersion, e.version ~= "" and e.version or "?"), "warning")
	end
	if e.notes ~= "" then rows[#rows + 1] = text(T(409, e.notes), "inactive") end
	if group ~= "install" and group ~= "update" then return end
	local can = M.canOpen()
	for k, link in ipairs(M.links(e)) do
		local id = "link_" .. group .. "_" .. index .. "_" .. k
		if link.kind == "button" and can then
			rows[#rows + 1] = { type = "button", id = id, text = T(410), active = true, url = link.url,
				onClick = function() M.openUrl(link.url) end }
		elseif link.source == "workshop" then
			rows[#rows + 1] = text(T(412), "inactive") -- the game cannot open a browser: the Workshop URL is text too
			urlRow(rows, id, link.url)
		else
			rows[#rows + 1] = text(T(411), "inactive") -- Nexus and unknown links: copy only (D7)
			urlRow(rows, id, link.url)
		end
	end
end

--- refusalRows(rows) -> true when the grouped refusal was appended (status screen of a "mod" rejection)
function M.refusalRows(rows)
	local r = M.refusal
	if not r or r.total == 0 then return false end
	rows[#rows + 1] = text(T(400), "error")
	for _, g in ipairs(GROUPS) do
		local list = r.groups[g]
		local count = #list + r.more[g]
		if count > 0 then
			rows[#rows + 1] = text(T(GROUP_TITLE[g], count), GROUP_TONE[g])
			for i = 1, math.min(#list, M.MAX_PER_GROUP) do
				entryRows(rows, g, list[i], i)
			end
			local hidden = count - math.min(#list, M.MAX_PER_GROUP)
			if hidden > 0 then rows[#rows + 1] = text(T(418, hidden), "inactive") end
			rows[#rows + 1] = text(T(GROUP_HINT[g]), "inactive")
		end
	end
	rows[#rows + 1] = text(T(413), "inactive")
	return true
end

local RULE_TEXT = { required = 423, allowed = 424, blocked = 425 }

--- policyRows(rows) -> true when the session mod list was appended (Multiplayer screen while connected)
function M.policyRows(rows)
	local p = M.policy
	if not p then return false end
	rows[#rows + 1] = text(T(420), "normal")
	if #p.entries == 0 then
		rows[#rows + 1] = text(T(430), "inactive")
	else
		for i = 1, math.min(#p.entries, M.MAX_POLICY_ROWS) do
			local e = p.entries[i]
			local id = RULE_TEXT[e.rule] or RULE_TEXT.required
			rows[#rows + 1] = text(T(id, e.version ~= "" and (e.name .. " " .. e.version) or e.name), e.rule == "blocked" and "error" or "normal")
		end
		local hidden = #p.entries - math.min(#p.entries, M.MAX_POLICY_ROWS) + p.more
		if hidden > 0 then rows[#rows + 1] = text(T(418, hidden), "inactive") end
	end
	if p.sourceMode ~= "admin" then rows[#rows + 1] = text(T(426), "inactive") end
	rows[#rows + 1] = text(p.enforcement == "warn" and T(429) or T(428), "inactive")
	return true
end

------------------------------------------------------------------------------
-- topics
------------------------------------------------------------------------------
B.on("mod_refusal", function(p)
	M.refusal = M.normalizeRefusal(p)
	local r = M.refusal
	log(string.format("mod refusal: install=%d enable=%d disable=%d update=%d", #r.groups.install + r.more.install,
		#r.groups.enable + r.more.enable, #r.groups.disable + r.more.disable, #r.groups.update + r.more.update))
	S.render()
end)

B.on("mod_policy", function(p)
	M.policy = M.normalizePolicy(p)
	S.render()
end)

B.on("status", function(p)
	local state = p.state
	if state ~= "rejected" then M.refusal = nil end
	if state == "disconnected" or state == "rejected" or state == "error" then M.policy = nil end
end)

log("join mods loaded")
