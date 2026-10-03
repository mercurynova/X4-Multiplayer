-- x4mp_extensions.lua : extension list for the mod-compatibility check (M2-X1).
--
-- Gathers GetExtensionList() and C.GetModifiedBasegameUIFilesExtensions() when this file loads (that is the start menu on a
-- game start, and again after every /reloadui, because X4 re-runs all UI files) and sends them to native as x4mp.extensions
-- (payload documented in x4mp_bridge.lua). It works before any save is loaded. If the game has no list yet at load time, the
-- gather is repeated once on the first gfx_ok / show event.
-- Sources (x4-unpacked/ui/addons/ego_gameoptions/gameoptions.lua): GetExtensionList() :5899 (fields id, name, version, date,
-- enabled, enabledbydefault, egosoftextension, isworkshop, personal, sync, error, warning, ...), GetModifiedBasegameUIFilesExtensions
-- :167 and :4089.

-- luacheck: globals X4MPBridge X4MPExtensions GetExtensionList RegisterEvent

if X4MPExtensions and X4MPExtensions.loaded then return end

local B = X4MPBridge
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] extensions: x4mp_bridge.lua must load first")
	return
end

local X = { loaded = true, MAX_EXTENSIONS = 512, MAX_STRING = 256 }
X4MPExtensions = X

local FIELDS = { "id", "name", "version", "date", "enabled", "enabledbydefault", "egosoftextension", "isworkshop", "personal",
	"sync", "error", "warning" }

local function scalar(v)
	local t = type(v)
	if t == "string" then return v:sub(1, X.MAX_STRING) end
	if t == "number" or t == "boolean" then return v end
	return nil
end

local function modifiedUiFiles()
	local okFfi, ffi = pcall(require, "ffi")
	if not okFfi then return "" end
	pcall(ffi.cdef, "const char* GetModifiedBasegameUIFilesExtensions(void);")
	local ok, res = pcall(function() return ffi.string(ffi.C.GetModifiedBasegameUIFilesExtensions()) end)
	return ok and type(res) == "string" and res or ""
end

--- collect() -> payload table (without "v"/"source"), or nil, err when the game gave no list
function X.collect()
	if type(GetExtensionList) ~= "function" then return nil, "no GetExtensionList" end
	local ok, list = pcall(GetExtensionList)
	if not ok or type(list) ~= "table" then return nil, "GetExtensionList failed: " .. tostring(list) end
	local out = B.json.array({})
	for _, ext in ipairs(list) do
		if type(ext) == "table" and #out < X.MAX_EXTENSIONS then
			local entry = {}
			for _, field in ipairs(FIELDS) do entry[field] = scalar(ext[field]) end
			out[#out + 1] = entry
		end
	end
	return { startmenu = B.isStartMenu(), modified_ui_files = modifiedUiFiles(), count = #out, list = out }
end

--- logs one line per extension (acceptance of M2-X1: id, version, enabled)
function X.logList(payload)
	B.log("extensions: " .. payload.count .. " found, modified base-game UI files: "
		.. (payload.modified_ui_files == "" and "(none)" or payload.modified_ui_files))
	for _, e in ipairs(payload.list) do
		B.log(string.format("extension id=%s version=%s enabled=%s", tostring(e.id), tostring(e.version), tostring(e.enabled)))
	end
end

--- gather(source) -> ok. Sends x4mp.extensions (queued by the bridge until the native API exists).
function X.gather(source)
	local payload, err = X.collect()
	if not payload then
		B.log("extensions: " .. tostring(err))
		return false
	end
	payload.source = source
	X.logList(payload)
	X.gathered = true
	B.send("extensions", payload)
	return true
end

local function retry(source)
	if not X.gathered then X.gather(source) end
end

RegisterEvent("gfx_ok", function() retry("gfx_ok") end)
RegisterEvent("show", function() retry("show") end)
X.gather("load")
