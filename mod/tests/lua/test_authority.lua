-- M2-09: the join dialog's host option + admin password handling (x4mp_menu.lua) and ui/x4mp_authority.lua (SaveGame, MD relay).
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local ADMIN = "adm1n-s3cret-PW!"
local SESSION = "sess-s3cret-pw"

local function setup()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua" })
	local r = { open = false }
	r.name = "test"
	function r.isOpen() return r.open end
	function r.show(model) r.open = true r.last = model end
	function r.close() r.open = false end
	X4MPScreens.setRenderer(r)
	return X4MPScreens, X4MPBridge, r
end

local function rowById(model, id)
	for _, row in ipairs(model.rows) do
		if row.id == id then return row end
	end
	return nil
end

local function fill(S)
	S.initState()
	S.state.address, S.state.name, S.state.password = "play.example.com", "Host", SESSION
end

test("host option is off by default: no role and no admin password in x4mp.join", function()
	local S, B = setup()
	fill(S)
	S.open("join")
	truthy(rowById(S.buildModel(), "host"), "the host toggle is on the join screen")
	falsy(rowById(S.buildModel(), "adminpassword"), "no admin password box while off")
	truthy(S.submitJoin())
	local p = B.json.decode(env.raisedNamed("x4mp.join")[1])
	eq(p.role, nil)
	eq(p.admin_password, nil)
end)

test("host on needs an admin password; nothing is sent without it", function()
	local S = setup()
	fill(S)
	S.open("join")
	rowById(S.buildModel(), "host").onClick()
	truthy(rowById(S.buildModel(), "adminpassword"), "the admin password box appears")
	local ok, err = S.submitJoin()
	eq(ok, false)
	eq(err, "invalid")
	eq(S.state.errors.admin, "admin_empty")
	eq(#env.raisedNamed("x4mp.join"), 0)
end)

test("host on: x4mp.join carries role authority + admin_password, both cleared right after the send", function()
	local S, B, r = setup()
	fill(S)
	S.open("join")
	rowById(S.buildModel(), "host").onClick()
	local pw = rowById(S.buildModel(), "adminpassword")
	eq(pw.hidden, true, "admin password edit box is textHidden")
	pw.onChange(ADMIN)
	truthy(S.submitJoin())
	local raised = env.raisedNamed("x4mp.join")
	eq(#raised, 1)
	local p = B.json.decode(raised[1])
	eq(p.role, "authority")
	eq(p.admin_password, ADMIN)
	eq(p.password, SESSION)
	eq(S.state.adminPassword, "")
	eq(S.state.password, "")
	for _, secret in ipairs({ ADMIN, SESSION }) do
		eq(t.findString(__X4MP_USER, secret), nil, "not in __X4MP_USER")
		eq(t.findString(S.state, secret), nil, "not in the screen state")
		eq(t.findString(r.last, secret), nil, "not in the model drawn after the send")
		falsy(env.debugText():find(secret, 1, true), "not in the log")
	end
end)

test("switching the host option off or closing the window wipes the admin password", function()
	local S = setup()
	fill(S)
	S.open("join")
	rowById(S.buildModel(), "host").onClick()
	rowById(S.buildModel(), "adminpassword").onChange(ADMIN)
	rowById(S.buildModel(), "host").onClick() -- off again
	eq(S.state.adminPassword, "")
	rowById(S.buildModel(), "host").onClick() -- on
	rowById(S.buildModel(), "adminpassword").onChange(ADMIN)
	S.onClosed()
	eq(S.state.adminPassword, "")
	eq(S.state.host, false)
end)

------------------------------------------------------------------------------
-- x4mp_authority.lua
------------------------------------------------------------------------------
local function authSetup(opts)
	opts = opts or {}
	env.reset()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua" })
	local log = { saves = {}, md = {}, order = {} }
	_G.SaveGame = function(...) log.saves[#log.saves + 1] = { ... } log.order[#log.order + 1] = "SaveGame" end
	_G.AddUITriggeredEvent = function(a, b, c) log.md[#log.md + 1] = { a, b, c } end
	if opts.withSaves then
		_G.X4MPSaves = { allowSaves = function(fn) log.order[#log.order + 1] = "allow" return fn() end }
	end
	env.cfuncs.GetCurrentGameTime = function() return 4321.5 end
	env.load("x4mp_authority.lua")
	return log
end

local function lastRaised(name)
	local sent = env.raisedNamed(name)
	return sent[#sent] and X4MPBridge.json.decode(sent[#sent])
end

test("auth_save calls SaveGame inside allowSaves and answers with the Lua game time", function()
	local log = authSetup({ withSaves = true })
	env.fire("x4mp.auth_save", '{"v":1,"name":"x4mp_ckpt_0123456789abcdef","request_id":7,"display":"X4MP checkpoint"}')
	eq(#log.saves, 1)
	eq(log.saves[1][1], "x4mp_ckpt_0123456789abcdef")
	eq(log.saves[1][2], "X4MP checkpoint")
	eq(log.order[1], "allow")
	local reply = lastRaised("x4mp.auth_saved")
	truthy(reply, "a reply was raised")
	eq(reply.ok, true)
	eq(reply.game_time, 4321.5)
	eq(reply.request_id, 7)
end)

test("auth_save works without the save wrapper file and reports a failing SaveGame", function()
	local log = authSetup()
	env.fire("x4mp.auth_save", '{"v":1,"name":"x4mp_ckpt_0123456789abcdef"}')
	eq(#log.saves, 1)
	_G.SaveGame = function() error("disk full") end
	env.fire("x4mp.auth_save", '{"v":1,"name":"x4mp_ckpt_fedcba9876543210"}')
	local reply = lastRaised("x4mp.auth_saved")
	eq(reply.ok, false)
	truthy(tostring(reply.error):find("disk full", 1, true))
end)

test("auth_collect with ship_only asks MD for the ship only (item 3), a plain collect still asks for the galaxy", function()
	local log = authSetup()
	env.fire("x4mp.auth_collect", '{"v":1,"ship_only":true}')
	eq(#log.md, 1)
	eq(log.md[1][1], "X4MP_Authority")
	eq(log.md[1][2], "ship")
	env.fire("x4mp.md_galaxy", "N;")
	eq(lastRaised("x4mp.auth_md").data, "N;")
	env.fire("x4mp.auth_collect", '{"v":1}')
	eq(log.md[2][2], "collect")
end)

test("auth_save refuses names that are not x4mp_ckpt_ saves", function()
	local log = authSetup()
	for _, bad in ipairs({ "save_001", "../x4mp_ckpt_1", "quicksave", "" }) do
		env.fire("x4mp.auth_save", '{"v":1,"name":"' .. bad .. '"}')
		eq(lastRaised("x4mp.auth_saved").ok, false, bad)
	end
	eq(#log.saves, 0, "SaveGame was never called")
end)

test("auth_collect triggers MD; md_galaxy is forwarded to native unchanged", function()
	local log = authSetup()
	env.fire("x4mp.auth_collect", '{"v":1}')
	eq(log.md[1][1], "X4MP_Authority")
	eq(log.md[1][2], "collect")
	local msg = "G;a_macro|c|Name|argon|0|0|0|b_macro;b_macro|c|N2||1|1|1|"
	env.fire("x4mp.md_galaxy", msg)
	local fwd = lastRaised("x4mp.auth_md")
	eq(fwd.data, msg)
	env.fire("x4mp.md_galaxy", "")
	eq(#env.raisedNamed("x4mp.auth_md"), 1, "empty messages are not forwarded")
end)

------------------------------------------------------------------------------
-- M3-09: sector map + SETA
------------------------------------------------------------------------------
local function mapSetup()
	local log = authSetup()
	_G.ConvertStringTo64Bit = function(s) return tonumber(s) end
	return log
end

local function sectorMapMessages()
	local out = {}
	for _, raw in ipairs(env.raisedNamed("x4mp.sector_map")) do out[#out + 1] = X4MPBridge.json.decode(raw).data end
	return out
end

test("sector_map_collect asks MD; tag + component pairs become macro|id records, then an end marker", function()
	local log = mapSetup()
	env.fire("x4mp.sector_map_collect", '{"v":1}')
	eq(log.md[1][1], "X4MP_Authority")
	eq(log.md[1][2], "map")
	env.fire("x4mp.md_sector_tag", "cluster_01_sector001_macro")
	env.fire("x4mp.md_sector", "100001")
	env.fire("x4mp.md_sector_tag", "cluster_02_sector001_macro")
	env.fire("x4mp.md_sector", "ID: 200002") -- not convertible by the stub: dropped
	env.fire("x4mp.md_sector_tag", "cluster_03_sector001_macro")
	env.fire("x4mp.md_sector", "300003")
	env.fire("x4mp.md_sector_end", "3")
	local msgs = sectorMapMessages()
	eq(#msgs, 2)
	eq(msgs[1], "S;cluster_01_sector001_macro|100001;cluster_03_sector001_macro|300003")
	eq(msgs[2], "E;2")
end)

test("sector map: chunks of 40, table params, records with | or ; are dropped", function()
	mapSetup()
	env.fire("x4mp.sector_map_collect", '{"v":1}')
	for i = 1, 85 do
		env.fire("x4mp.md_sector_tag", "m" .. i)
		env.fire("x4mp.md_sector", tostring(1000 + i))
	end
	env.fire("x4mp.md_sector_tag", "bad|macro")
	env.fire("x4mp.md_sector", "5")
	local fn = nil
	for _, f in ipairs(env.events["x4mp.md_sector"] or {}) do fn = f end
	fn(nil, { "from_table", "7777" })
	env.fire("x4mp.md_sector_end", "87")
	local msgs = sectorMapMessages()
	eq(#msgs, 4, "40 + 40 + 6 records, then the end marker")
	eq(msgs[4], "E;86")
	local _, count = msgs[3]:gsub("|", "|")
	eq(count, 6)
	truthy(msgs[3]:find("from_table|7777", 1, true))
end)

test("md sector events without a pending collection are ignored", function()
	mapSetup()
	env.fire("x4mp.md_sector_tag", "x")
	env.fire("x4mp.md_sector", "1")
	env.fire("x4mp.md_sector_end", "1")
	eq(#env.raisedNamed("x4mp.sector_map"), 0)
end)

test("seta_block / seta_off go to MD; an MD block report goes to native", function()
	local log = authSetup()
	env.fire("x4mp.seta_block", '{"v":1,"on":true}')
	eq(log.md[1][2], "seta_block")
	eq(log.md[1][3], "1")
	env.fire("x4mp.seta_block", '{"v":1,"on":false}')
	eq(log.md[2][3], "0")
	env.fire("x4mp.seta_off", '{"v":1}')
	eq(log.md[3][2], "seta_off")
	env.fire("x4mp.md_seta", "blocked")
	eq(lastRaised("x4mp.seta_blocked").source, "blocked")
end)
