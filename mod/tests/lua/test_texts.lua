local t, env = ...
local test, truthy, falsy = t.test, t.truthy, t.falsy

local LUA_FILES = { "x4mp_bridge.lua", "x4mp_menu.lua", "x4mp_ui_standalone.lua", "x4mp_extensions.lua", "x4mp_authority.lua", "x4mp_join_mods.lua", "x4mp_chat.lua", "x4mp_players.lua" }

test("every text id used by the Lua code exists on page 92000", function()
	env.reset()
	local texts = env.texts
	for _, file in ipairs(LUA_FILES) do
		local src = env.readFile(env.uiPath(file))
		for id in src:gmatch("[^%w_]T%((%d+)") do
			truthy(texts[tonumber(id)], file .. " uses T(" .. id .. ") which is missing in t/0001-l044.xml")
		end
		for _, block in ipairs({ "STATE_TEXT", "ERROR_TEXT", "titles", "GROUP_TITLE", "GROUP_HINT", "RULE_TEXT" }) do
			local body = src:match("local " .. block .. " = (%b{})")
			if body then
				for id in body:gmatch("=%s*(%d+)") do
					truthy(texts[tonumber(id)], file .. " " .. block .. " uses text " .. id .. " which is missing")
				end
			end
		end
	end
end)

test("text page has no X4 comment brackets or reference braces", function()
	env.reset()
	for id, body in pairs(env.texts) do
		falsy(body:find("[%(%){}]"), "text " .. id .. " contains ( ) { } which X4 interprets")
	end
end)

test("ui.xml lists every Lua file of the extension, bridge first", function()
	local xml = env.readFile(env.uiPath("../ui.xml"))
	local order = {}
	for name in xml:gmatch('<file name="ui/([%w_]+%.lua)"') do order[#order + 1] = name end
	t.eq(order[1], "x4mp_bridge.lua")
	t.eq(order[2], "x4mp_menu.lua")
	for _, f in ipairs({ "x4mp_ui_standalone.lua", "x4mp_extensions.lua", "x4mp_saves.lua", "x4mp_optionsmenu_adapter.lua",
		"x4mp_hud.lua", "x4mp_join_mods.lua" }) do
		local found = false
		for _, o in ipairs(order) do if o == f then found = true end end
		truthy(found, "ui.xml lists " .. f)
		local fh = io.open(env.uiPath(f), "rb")
		truthy(fh, f .. " exists")
		if fh then fh:close() end
	end
	t.contains(xml, 'savedvariable name="__X4MP_USER" storage="userdata"')
end)
