-- Entry point of the Lua unit runner: lua mod/tests/lua/run_all.lua   (run from anywhere; paths derive from this file)
local here = (arg and arg[0] or ""):match("^(.*)[/\\][^/\\]*$") or "."
local root = here .. "/../../.."

package.path = here .. "/?.lua;" .. package.path
local T = require("lib")
local E = require("env")
E.setRoot(root)

local files = { "test_json", "test_bridge", "test_screens", "test_standalone", "test_extensions", "test_adapter_hud", "test_texts", "test_saves", "test_authority", "test_join_mods", "test_ingame_reconnect", "test_players", "test_chat", "test_teams", "test_avatars", "test_ghosts" }
for _, name in ipairs(files) do
	T.currentFile = name
	local chunk = assert(loadfile(here .. "/" .. name .. ".lua"))
	chunk(T, E)
end

print(string.format("Lua unit tests (%s), %d tests", _VERSION, #T.tests))
local ok = T.run(E.reset)
os.exit(ok and 0 or 1)
