-- M3-08: ui/x4mp_teams.lua carries the team faction plan from native to md/x4mp_teams.xml and the answer back.
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local function setup()
	env.reset()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua" })
	local log = { md = {} }
	_G.AddUITriggeredEvent = function(a, b, c) log.md[#log.md + 1] = { a, b, c } end
	env.load("x4mp_teams.lua")
	return log
end

local function lastRaised(name)
	local sent = env.raisedNamed(name)
	return sent[#sent] and X4MPBridge.json.decode(sent[#sent])
end

local PLAN = '{"v":1,"seq":3,"reason":"teams","own":1,"slots":[1,2],"rel":[[1,2,-1.0],[0,1,1.0],[0,2,-1.0]]}'

test("teams_apply becomes one flat list for MD: seq, counts, slots, then triples", function()
	local log = setup()
	env.fire("x4mp.teams_apply", PLAN)
	eq(#log.md, 1)
	eq(log.md[1][1], "X4MP_Teams")
	eq(log.md[1][2], "apply")
	local l = log.md[1][3]
	eq(#l, 3 + 2 + 3 * 3)
	eq(l[1], 3)
	eq(l[2], 2)
	eq(l[3], 3)
	eq(l[4], 1)
	eq(l[5], 2)
	eq(l[6], 1) eq(l[7], 2) eq(l[8], -1)
	eq(l[9], 0) eq(l[10], 1) eq(l[11], 1)
	eq(l[12], 0) eq(l[13], 2) eq(l[14], -1)
	eq(#env.raisedNamed("x4mp.teams_md"), 0, "no error was reported")
end)

test("skip_known (M3-28 diag.no_set_faction_known) appends ONE trailing 1; 0.99 is a normal relation value", function()
	local log = setup()
	env.fire("x4mp.teams_apply", '{"v":1,"seq":4,"reason":"teams","own":1,"slots":[1,2],"rel":[[0,1,0.99]],"skip_known":true}')
	local l = log.md[1][3]
	eq(#l, 3 + 2 + 3 + 1)
	eq(l[8], 0.99)
	eq(l[9], 1)
	env.fire("x4mp.teams_apply", '{"v":1,"seq":5,"reason":"teams","own":1,"slots":[1,2],"rel":[[0,1,1.0]]}')
	eq(#log.md[2][3], 3 + 2 + 3, "no trailing flag without the switch")
end)

test("a plan with no relations and one slot is valid", function()
	local log = setup()
	env.fire("x4mp.teams_apply", '{"v":1,"seq":1,"slots":[1],"rel":[]}')
	eq(#log.md, 1)
	eq(#log.md[1][3], 4)
end)

test("an unusable plan is answered with an E report and never reaches MD", function()
	local log = setup()
	local bad = {
		'{"v":1,"seq":2,"slots":[9],"rel":[]}',                  -- slot out of range
		'{"v":1,"seq":2,"slots":[0],"rel":[]}',                  -- player is not a slot
		'{"v":1,"seq":2,"slots":[1],"rel":[[1,2,3]]}',           -- value out of range
		'{"v":1,"seq":2,"slots":[1],"rel":[[1,"x",0.5]]}',       -- not a number
		'{"v":1,"seq":2,"slots":[1]}',                           -- no rel
		'{"v":1,"slots":[1],"rel":[]}',                          -- no seq
	}
	for _, p in ipairs(bad) do env.fire("x4mp.teams_apply", p) end
	eq(#log.md, 0)
	eq(#env.raisedNamed("x4mp.teams_md"), #bad)
	eq(lastRaised("x4mp.teams_md").data, "E;0;bad_payload")
end)

test("without AddUITriggeredEvent the failure is reported, not thrown", function()
	setup()
	_G.AddUITriggeredEvent = nil
	env.fire("x4mp.teams_apply", PLAN)
	eq(lastRaised("x4mp.teams_md").data, "E;3;no_md_event")
end)

test("an AddUITriggeredEvent that throws is reported", function()
	setup()
	_G.AddUITriggeredEvent = function() error("boom") end
	env.fire("x4mp.teams_apply", PLAN)
	eq(lastRaised("x4mp.teams_md").data, "E;3;md_event_failed")
end)

test("the MD report is forwarded to native unchanged; empty ones are dropped", function()
	setup()
	env.fire("x4mp.md_teams", "R;3;2;0;0;2;3")
	eq(lastRaised("x4mp.teams_md").data, "R;3;2;0;0;2;3")
	env.fire("x4mp.md_teams", "")
	eq(#env.raisedNamed("x4mp.teams_md"), 1)
end)

test("the Lua file is registered in ui.xml and the MD cue listens on the same screen and control", function()
	env.reset()
	truthy(env.readFile(env.uiPath("../ui.xml")):find('ui/x4mp_teams.lua', 1, true), "ui.xml lists x4mp_teams.lua")
	local md = env.readFile(env.uiPath("../md/x4mp_teams.xml"))
	truthy(md:find("screen=\"'X4MP_Teams'\"", 1, true))
	truthy(md:find("control=\"'apply'\"", 1, true))
	truthy(md:find("'x4mp.md_teams'", 1, true))
	falsy(md:find('faction="faction.player" locked', 1, true), "MD never locks or unlocks player")
end)

-- ---- M5b: player-to-player team invitations ------------------------------------------------------------------------------
-- The Lua half only carries the words: the server decides who may invite and whether an answer is allowed. What a test can pin
-- is the JSON the native half reads, the id /mp teamaccept and /mp teamdecline take, and the lines the player sees.
local function inviteSetup()
	local log = setup()
	local lines = {}
	_G.X4MPChat = { systemLine = function(text) lines[#lines + 1] = text end }
	_G.X4MPScreens = {
		T = function(id, ...)
			local body = env.texts[id]
			if type(body) ~= "string" then return "[92000," .. id .. "]" end
			if select("#", ...) > 0 then
				local ok, res = pcall(string.format, body, ...)
				if ok then body = res end
			end
			return body
		end,
	}
	X4MPTeams.pending()
	return log, lines
end

local function lastInvite()
	local sent = env.raisedNamed("x4mp.teams_invite")
	return sent[#sent] and X4MPBridge.json.decode(sent[#sent])
end

test("M5b: a Talk-menu invite asks native for a TeamInviteRequest", function()
	local log = inviteSetup()
	truthy(X4MPTeams.invite(7))
	local body = lastInvite()
	eq(body.op, "invite")
	eq(body.to, 7)
	eq(#log.md, 0, "an invitation is not a faction plan")
	falsy(X4MPTeams.invite(nil), "a pid is required")
	eq(#env.raisedNamed("x4mp.teams_invite"), 1)
end)

test("M5b: a notice is remembered and says how to answer it", function()
	local _, lines = inviteSetup()
	env.fire("x4mp.teams_invite", '{"v":1,"kind":"notice","id":5,"from":2,"team":1,"name":"Wolves","expires":120}')
	local pending = X4MPTeams.pending()
	eq(#pending, 1)
	eq(pending[1].id, 5)
	eq(pending[1].from, 2)
	eq(pending[1].team, "Wolves")
	eq(#lines, 1)
	truthy(lines[1]:find("Wolves", 1, true), lines[1])
	truthy(lines[1]:find("/mp teamaccept 5", 1, true), lines[1])
	truthy(lines[1]:find("player 2", 1, true), "an unknown id falls back to a plain name: " .. lines[1])
end)

test("M5b: /mp teamaccept answers the invitation and clears it", function()
	inviteSetup()
	env.fire("x4mp.teams_invite", '{"v":1,"kind":"notice","id":5,"from":2,"team":1,"name":"Wolves","expires":120}')
	eq(#X4MPTeams.pending(), 1)
	truthy(X4MPTeams.command("teamaccept", "5"))
	local body = lastInvite()
	eq(body.op, "respond")
	eq(body.id, 5)
	eq(body.accept, true)
	eq(#X4MPTeams.pending(), 0, "an answered invitation is not pending any more")
end)

test("M5b: /mp teamdecline sends a refusal and a foreign word is not ours", function()
	inviteSetup()
	truthy(X4MPTeams.command("teamdecline", "9"))
	local body = lastInvite()
	eq(body.op, "respond")
	eq(body.id, 9)
	eq(body.accept, false)
	falsy(X4MPTeams.command("bogus", ""), "only the two invitation words are handled here")
	truthy(X4MPTeams.command("teamaccept", "abc"), "an id that is not a number is still our word")
	eq(#env.raisedNamed("x4mp.teams_invite"), 1, "no request went out for a bad id")
end)

test("M5b: a refused invitation says why, the same way native reported it", function()
	local _, lines = inviteSetup()
	env.fire("x4mp.teams_invite", '{"v":1,"kind":"result","op":"invite","ok":false,"reason":"not_leader"}')
	eq(#lines, 1)
	truthy(lines[1]:find("not_leader", 1, true), lines[1])
	env.fire("x4mp.teams_invite", '{"v":1,"kind":"result","op":"invite","ok":true,"team":1}')
	truthy(lines[2]:find("sent", 1, true), lines[2])
end)

test("M5b: an answered invitation says which way it went", function()
	local _, lines = inviteSetup()
	env.fire("x4mp.teams_invite", '{"v":1,"kind":"notice","id":5,"from":2,"team":1,"name":"Wolves","expires":120}')
	X4MPTeams.command("teamaccept", "5")
	env.fire("x4mp.teams_invite", '{"v":1,"kind":"result","op":"respond","ok":true,"team":1}')
	truthy(lines[#lines]:find("team now", 1, true), lines[#lines])
	X4MPTeams.command("teamdecline", "6")
	env.fire("x4mp.teams_invite", '{"v":1,"kind":"result","op":"respond","ok":true}')
	truthy(lines[#lines]:find("declined", 1, true), lines[#lines])
	env.fire("x4mp.teams_invite", '{"v":1,"kind":"error","op":"invite","error":"no session"}')
	truthy(lines[#lines]:find("no session", 1, true), lines[#lines])
end)
