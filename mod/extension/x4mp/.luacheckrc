-- luacheck config for the X4MP extension Lua (X4 runs LuaJIT with the game's own globals)
std = "luajit"
max_line_length = false
-- globals this extension defines or assigns
globals = {
	"X4MPBridge", "X4MPScreens", "X4MPMenu", "X4MPExtensions", "__X4MP_USER",
	"ExecuteDebugCommand", -- wrapped by x4mp_menu.lua for the /x4mp chat command
	"Menus",               -- vanilla menu list, appended to by the standalone renderer
}
-- globals provided by the game / X4Native
read_globals = {
	"DebugError", "RegisterEvent", "AddUITriggeredEvent", "ReadText", "OpenMenu", "LoadGame", "getElapsedTime",
	"GetExtensionList", "Helper", "Color",
}
