-- M2-X3: grouped mod refusal with links, and the session mod list (x4mp_join_mods.lua).
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local WS_URL = "https://steamcommunity.com/sharedfiles/filedetails/?id="

--- loads bridge + screens + join mods with a recording renderer; returns S, J, B, renderer
local function setup()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua", "x4mp_join_mods.lua" })
	local r = { open = false, shown = {} }
	r.name = "test"
	function r.isOpen() return r.open end
	function r.show(model) r.open = true r.shown[#r.shown + 1] = model r.last = model end
	function r.close() r.open = false end
	X4MPScreens.setRenderer(r)
	return X4MPScreens, X4MPJoinMods, X4MPBridge, r
end

local REFUSAL = [[{"v":1,"policy_version":4,
 "install":[{"id":"ws_2458720435","name":"Warehouse Fleets","version":"1.4","workshop_id":2458720435},
            {"id":"sn_better_traders","name":"Better Traders","version":"2.0","nexus_url":"https://www.nexusmods.com/x4foundations/mods/1234"}],
 "enable":[{"id":"sn_off","name":"Switched Off","version":"1.0","have_version":"1.0"}],
 "disable":[{"id":"ws_9000000001","name":"Extra Gadgets","have_version":"1.0","workshop_id":9000000001,"notes":"not part of this session"}],
 "update":[{"id":"ws_5","name":"Old Mod","version":"1.4","have_version":"1.3","workshop_id":5}]}]]

local function fireRejected(reason)
	env.fire("x4mp.mod_refusal", REFUSAL)
	env.fire("x4mp.status", '{"v":1,"state":"rejected","reject":"' .. (reason or "mod") .. '","detail":"your mods do not match"}')
end

local function rowsOfType(model, kind)
	local out = {}
	for _, row in ipairs(model.rows) do
		if row.type == kind then out[#out + 1] = row end
	end
	return out
end

local function hasText(model, needle)
	for _, row in ipairs(model.rows) do
		if row.type == "text" and row.text:find(needle, 1, true) then return true end
	end
	return false
end

local function openStatus(S)
	S.open("status")
end

------------------------------------------------------------------------------
-- grouping
------------------------------------------------------------------------------
test("normalizeRefusal: four groups, counts and names", function()
	local _, J = setup()
	local p = env.json_decode and env.json_decode(REFUSAL) or X4MPBridge.json.decode(REFUSAL)
	local r = J.normalizeRefusal(p)
	eq(r.policyVersion, 4)
	eq(#r.groups.install, 2)
	eq(#r.groups.enable, 1)
	eq(#r.groups.disable, 1)
	eq(#r.groups.update, 1)
	eq(r.total, 5)
	eq(r.groups.update[1].haveVersion, "1.3")
	eq(r.groups.install[1].name, "Warehouse Fleets")
	eq(r.groups.disable[1].notes, "not part of this session")
end)

test("normalizeRefusal: missing groups, junk entries and a missing name are tolerated", function()
	local _, J = setup()
	local r = J.normalizeRefusal({ install = { { id = "only_id" }, 5, {}, { name = "N" } }, update = "bad" })
	eq(#r.groups.install, 2)
	eq(r.groups.install[1].name, "only_id")
	eq(#r.groups.enable, 0)
	eq(#r.groups.update, 0)
end)

test("a mod rejection shows the four groups on the status screen", function()
	local S, _, _, r = setup()
	openStatus(S)
	fireRejected()
	local m = r.last
	truthy(hasText(m, "Install: 2"), "install header")
	truthy(hasText(m, "Enable: 1"), "enable header")
	truthy(hasText(m, "Disable: 1"), "disable header")
	truthy(hasText(m, "Update: 1"), "update header")
	truthy(hasText(m, "Warehouse Fleets, version 1.4"))
	truthy(hasText(m, "Switched Off"))
	truthy(hasText(m, "Extra Gadgets"))
	truthy(hasText(m, "You have version 1.3, this session uses 1.4"))
	truthy(hasText(m, "Your mods do not match this session."))
end)

test("order of the groups is install, enable, disable, update", function()
	local S, _, _, r = setup()
	openStatus(S)
	fireRejected()
	local pos = {}
	for i, row in ipairs(r.last.rows) do
		for _, g in ipairs({ "Install: ", "Enable: ", "Disable: ", "Update: " }) do
			if row.type == "text" and row.text:find(g, 1, true) == 1 then pos[g] = i end
		end
	end
	truthy(pos["Install: "] < pos["Enable: "] and pos["Enable: "] < pos["Disable: "] and pos["Disable: "] < pos["Update: "])
end)

test("a refusal without groups (protocol mismatch) keeps the plain text", function()
	local S, _, _, r = setup()
	openStatus(S)
	env.fire("x4mp.status", '{"v":1,"state":"rejected","reject":"mod","detail":"too old"}')
	falsy(hasText(r.last, "Install:"))
	truthy(hasText(r.last, "too old"))
end)

test("only a mod rejection lists the groups; the refusal is forgotten on the next status", function()
	local S, J, _, r = setup()
	openStatus(S)
	fireRejected("build")
	falsy(hasText(r.last, "Install:"), "reject=build shows no mod groups")
	env.fire("x4mp.status", '{"v":1,"state":"connecting"}')
	eq(J.refusal, nil)
end)

------------------------------------------------------------------------------
-- links (D7: Workshop -> button, Nexus -> text, unknown -> text)
------------------------------------------------------------------------------
test("workshopUrl is derived from the numeric id or a ws_ id, never taken from the server", function()
	local _, J = setup()
	eq(J.workshopUrl({ workshopId = 2458720435 }), WS_URL .. "2458720435")
	eq(J.workshopUrl({ id = "ws_123" }), WS_URL .. "123")
	eq(J.workshopUrl({ id = "ws_12x" }), nil)
	eq(J.workshopUrl({ id = "sn_x", workshopId = 0 }), nil)
	eq(J.workshopUrl({ id = "x", workshopId = -4 }), nil)
	eq(J.workshopUrl({ id = "x", workshopId = 1.5 }), nil)
end)

test("nexusUrl accepts only X4 mod pages and normalises them", function()
	local _, J = setup()
	eq(J.nexusUrl("https://www.nexusmods.com/x4foundations/mods/12"), "https://www.nexusmods.com/x4foundations/mods/12")
	eq(J.nexusUrl("https://nexusmods.com/x4foundations/mods/12/"), "https://www.nexusmods.com/x4foundations/mods/12")
	eq(J.nexusUrl("https://www.nexusmods.com/x4foundations/mods/12?tab=files"), nil)
	eq(J.nexusUrl("https://www.nexusmods.com/skyrim/mods/12"), nil)
	eq(J.nexusUrl("http://www.nexusmods.com/x4foundations/mods/12"), nil)
	eq(J.nexusUrl("https://evil.example/https://www.nexusmods.com/x4foundations/mods/12"), nil)
	eq(J.nexusUrl(nil), nil)
end)

test("links: Workshop is a button, Nexus is text, an unknown address is text, nothing gives nothing", function()
	local _, J = setup()
	local ws = J.links({ id = "ws_7", workshopId = 7 })
	eq(#ws, 1)
	eq(ws[1].kind, "button")
	eq(ws[1].url, WS_URL .. "7")
	local nx = J.links({ id = "sn_a", nexusUrl = "https://www.nexusmods.com/x4foundations/mods/9" })
	eq(#nx, 1)
	eq(nx[1].kind, "text")
	eq(nx[1].source, "nexus")
	local other = J.links({ id = "sn_b", nexusUrl = "https://example.org/mod/9" })
	eq(#other, 1)
	eq(other[1].kind, "text")
	eq(other[1].source, "other")
	eq(#J.links({ id = "sn_c" }), 0)
	eq(#J.links({ id = "sn_d", nexusUrl = "javascript:alert(1)" }), 0)
	eq(#J.links({ id = "sn_e", nexusUrl = "https://x.org/a b" }), 0)
	local both = J.links({ id = "ws_8", workshopId = 8, nexusUrl = "https://www.nexusmods.com/x4foundations/mods/2" })
	eq(#both, 2)
	eq(both[1].kind, "button")
	eq(both[2].kind, "text")
end)

test("rows: the Workshop item gets an Open button that opens the Steam URL; Nexus is a read-only edit row, never a button", function()
	local S, J, _, r = setup()
	J.canOpen = function() return true end
	openStatus(S)
	fireRejected()
	local buttons = {}
	for _, b in ipairs(rowsOfType(r.last, "button")) do
		if b.id:find("^link_") then buttons[#buttons + 1] = b end
	end
	eq(#buttons, 2, "one button per Workshop item of install and update")
	eq(buttons[1].text, "Open Workshop page")
	eq(buttons[1].url, WS_URL .. "2458720435")
	local edits = rowsOfType(r.last, "edit")
	eq(#edits, 1, "only the Nexus address is an edit row")
	eq(edits[1].value, "https://www.nexusmods.com/x4foundations/mods/1234")
	eq(edits[1].readonly, true)
	truthy(hasText(r.last, "Copy this address"))
	-- the button opens the URL through C.OpenWebBrowser
	local opened
	env.cfuncs.OpenWebBrowser = function(url) opened = url end
	buttons[1].onClick()
	eq(opened, WS_URL .. "2458720435")
end)

test("rows: without a browser the Workshop URL is text as well", function()
	local S, J, _, r = setup()
	J.canOpen = function() return false end
	openStatus(S)
	fireRejected()
	for _, b in ipairs(rowsOfType(r.last, "button")) do falsy(b.id:find("^link_"), "no link button") end
	eq(#rowsOfType(r.last, "edit"), 3, "two Workshop URLs and the Nexus URL")
	truthy(hasText(r.last, "cannot open a browser"))
end)

test("openUrl refuses anything but a Workshop page URL", function()
	local _, J = setup()
	local called = 0
	env.cfuncs.OpenWebBrowser = function() called = called + 1 end
	falsy(J.openUrl("https://www.nexusmods.com/x4foundations/mods/1"))
	falsy(J.openUrl("steam://url/CommunityFilePage/1"))
	falsy(J.openUrl("https://steamcommunity.com.evil.example/sharedfiles/filedetails/?id=1"))
	falsy(J.openUrl("https://steamcommunity.com/sharedfiles/filedetails/?id=1&x=2"))
	falsy(J.openUrl(nil))
	eq(called, 0)
	truthy(J.openUrl(WS_URL .. "42"))
	eq(called, 1)
end)

test("enable and disable groups carry no link rows", function()
	local S, J, _, r = setup()
	J.canOpen = function() return true end
	openStatus(S)
	env.fire("x4mp.mod_refusal", '{"enable":[{"id":"ws_3","name":"A","workshop_id":3}],"disable":[{"id":"ws_4","name":"B","workshop_id":4}]}')
	env.fire("x4mp.status", '{"state":"rejected","reject":"mod"}')
	eq(#rowsOfType(r.last, "edit"), 0)
	for _, b in ipairs(rowsOfType(r.last, "button")) do falsy(b.id:find("^link_")) end
end)

------------------------------------------------------------------------------
-- caps
------------------------------------------------------------------------------
test("long lists are capped per group with an 'and N more' line", function()
	local S, J, _, r = setup()
	J.canOpen = function() return true end
	local items = {}
	for i = 1, 30 do items[#items + 1] = '{"id":"ws_' .. i .. '","name":"Mod ' .. i .. '","workshop_id":' .. i .. "}" end
	openStatus(S)
	env.fire("x4mp.mod_refusal", '{"install":[' .. table.concat(items, ",") .. '],"install_more":70}')
	env.fire("x4mp.status", '{"state":"rejected","reject":"mod"}')
	local names = 0
	for _, row in ipairs(r.last.rows) do
		if row.type == "text" and row.text:find("^Mod %d+, version") == nil and row.text:find("^Mod %d+$") then names = names + 1 end
	end
	eq(names, J.MAX_PER_GROUP)
	truthy(hasText(r.last, "Install: 100"), "the header counts the hidden entries")
	truthy(hasText(r.last, "and 92 more"))
	truthy(#r.last.rows < 60, "bounded row count, got " .. #r.last.rows)
end)

test("strings are cut and non-table fields are ignored", function()
	local _, J = setup()
	local e = J.normalizeRefusal({ install = { { id = "x", name = string.rep("n", 1000), version = 5, notes = {} } } }).groups.install[1]
	eq(#e.name, 256)
	eq(e.version, "5")
	eq(e.notes, "")
end)

------------------------------------------------------------------------------
-- session mod list on the Multiplayer screen
------------------------------------------------------------------------------
local POLICY = [[{"v":1,"version":3,"source_mode":"admin","unknown_default":"client_only","enforcement":"strict",
 "entries":[{"id":"ws_1","name":"Alpha","rule":"required","enabled":true,"version":"2.0","version_rule":"exact"},
            {"id":"ws_2","name":"Beta","rule":"allowed","enabled":true},
            {"id":"ws_3","name":"Gamma","rule":"blocked","enabled":true},
            {"id":"ws_4","name":"Delta","rule":"required","enabled":false}]}]]

test("the Multiplayer screen lists the session mod set while connected", function()
	local S, _, _, r = setup()
	env.fire("x4mp.status", '{"state":"ingame","server":"h:1"}')
	env.fire("x4mp.mod_policy", POLICY)
	S.open("main")
	local m = r.last
	truthy(hasText(m, "Mods of this session"))
	truthy(hasText(m, "Required: Alpha 2.0"))
	truthy(hasText(m, "Allowed: Beta"))
	truthy(hasText(m, "Not allowed: Gamma"))
	truthy(hasText(m, "Not allowed: Delta"), "a disabled Required entry acts as blocked")
	truthy(hasText(m, "A player whose mods differ is refused."))
	falsy(hasText(m, "required automatically"), "admin list: no implicit authority note")
end)

test("authority-defined policy shows the implicit note; an empty list says so; warn mode is stated", function()
	local S, _, _, r = setup()
	env.fire("x4mp.status", '{"state":"ingame"}')
	env.fire("x4mp.mod_policy", '{"version":1,"source_mode":"authority","enforcement":"warn","entries":[]}')
	S.open("main")
	truthy(hasText(r.last, "No extra mods are listed"))
	truthy(hasText(r.last, "required automatically"))
	truthy(hasText(r.last, "gets a warning"))
end)

test("no mod list when not connected, and it is forgotten on disconnect", function()
	local S, J, _, r = setup()
	env.fire("x4mp.mod_policy", POLICY)
	S.open("main")
	falsy(hasText(r.last, "Mods of this session"), "not connected: no list")
	env.fire("x4mp.status", '{"state":"ingame"}')
	truthy(hasText(r.last, "Mods of this session"))
	env.fire("x4mp.status", '{"state":"disconnected"}')
	eq(J.policy, nil)
end)

test("the policy list is capped", function()
	local S, J, _, r = setup()
	local entries = {}
	for i = 1, 40 do entries[#entries + 1] = '{"id":"m' .. i .. '","name":"M' .. i .. '","rule":"required","enabled":true}' end
	env.fire("x4mp.status", '{"state":"ingame"}')
	env.fire("x4mp.mod_policy", '{"source_mode":"admin","entries":[' .. table.concat(entries, ",") .. '],"entries_more":5}')
	S.open("main")
	local lines = 0
	for _, row in ipairs(r.last.rows) do
		if row.type == "text" and row.text:find("^Required: M") then lines = lines + 1 end
	end
	eq(lines, J.MAX_POLICY_ROWS)
	truthy(hasText(r.last, "and 33 more"))
end)

test("the heartbeat status does not redraw an identical screen (keeps a link selection)", function()
	local S, _, _, r = setup()
	S.open("status")
	local status = '{"v":1,"state":"rejected","reject":"mod"}'
	env.fire("x4mp.status", status)
	local before = #r.shown
	env.fire("x4mp.status", status)
	eq(#r.shown, before)
	env.fire("x4mp.status", '{"v":1,"state":"connecting"}')
	truthy(#r.shown > before)
end)

test("standalone renderer draws the link rows: a full-width selectable edit box and a button", function()
	env.installApi()
	env.installUi()
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua", "x4mp_ui_standalone.lua", "x4mp_join_mods.lua" })
	X4MPJoinMods.canOpen = function() return true end
	X4MPScreens.open("status")
	env.fire("x4mp.mod_refusal", REFUSAL)
	env.fire("x4mp.status", '{"v":1,"state":"rejected","reject":"mod"}')
	local box = env.findCell("edit", function(c) return c.text == "https://www.nexusmods.com/x4foundations/mods/1234" end)
	truthy(box, "the Nexus address is in an edit box")
	eq(box.colspan, 2)
	eq(box.props.selectTextOnActivation, true)
	truthy(env.findCell("button", function(c) return c.text == "Open Workshop page" end), "Workshop button")
end)
