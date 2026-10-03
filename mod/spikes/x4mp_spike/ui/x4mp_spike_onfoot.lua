-- x4mp_spike_onfoot.lua : spike v2 blocks onfoot1 and onfoot2 (S10, on-foot presence; task M2-003).
--
--   onfoot1  = S10.1, S10.3, S10.4, S10.11, S10.12, S10.14, S10.7, S10.8   (the important ones, in this order)
--   onfoot2  = S10.2, S10.5, S10.6, S10.9, S10.13, S10.15
--   Any single step: "/x4mpspike onfoot1 step=7" (the step number is the S10 number; either block accepts any step).
--   Control of a running block: "next=1" ends the current wait early, "stop=1" aborts the run, "cleanup=1" removes every
--   test actor and lounge and switches all listeners off.
--
-- Procedures: docs/research/on-foot-presence.md section 6. The MD side is md/x4mp_spike_onfoot.xml (actor and interior
-- services; it owns the markers and the janitor). This file orchestrates, announces each step, samples the player state
-- (2 Hz), reads native positions through the FFI, drives the "mirror actor" (replays the player's own recorded track 3 s
-- late) and offers a small test menu. It logs observations only.
--
-- State-changing things this file causes (all marked, all removed again, all removed on load by the MD janitor):
--   * test actors named "Spike ..." (cue actors of md.X4MP_SpikeOnFoot.Actors), at most 8 at a time;
--   * one private dynamic interior named "Multiplayer Lounge" per attempt, on the station the player is docked at;
--   * the player's own entity is moved into the lounge and back (s12, s14, s15). Never a ship. Never removed.
--
-- Sources (x4-unpacked/): FFI signatures copied from vanilla cdefs: GetPlayerContainerID ui/addons/ego_detailmonitor/menu_map.lua:776,
-- GetPlayerOccupiedShipID menu_crafting.lua:25, GetEnvironmentObject menu_docked.lua:85, GetPlayerID/GetPlayerObjectID menu_docked.lua:104-105,
-- CanPlayerStandUp menu_docked.lua:68, UIPosRot + GetPositionalOffset/SetPositionalOffset menu_mapeditor.lua:60-116,
-- IsConversationActive ego_detailmonitorhelper/helper.lua:400; the Rotation struct, GetCameraRotation and IsPlayerOccupiedShipDocked come
-- from the X4Native SDK (sdk/x4_game_func_list.inc:315, :1438). Menu frame code follows ui/x4mp_spike_ui.lua and
-- ego_detailmonitorhelper/helper.lua (createFrameHandle, closeMenu, closeMenuAndReturn:1794, conversationMenu flag:1402).

-- luacheck: globals X4MPSpike DebugError RegisterEvent Menus Helper Color OpenMenu ConvertStringTo64Bit GetPlayerRoom

local ffi = require("ffi")
local C = ffi.C

local S = X4MPSpike
if type(S) ~= "table" then return end
local K, log = S.K, S.log

for _, def in ipairs({
	"typedef uint64_t UniverseID;",
	"typedef struct { float x; float y; float z; float yaw; float pitch; float roll; } UIPosRot;",
	"typedef struct { float yaw; float pitch; float roll; } Rotation;",
	"UniverseID GetPlayerContainerID(void);",
	"UniverseID GetPlayerOccupiedShipID(void);",
	"UniverseID GetEnvironmentObject();",
	"UniverseID GetPlayerID(void);",
	"UniverseID GetPlayerObjectID(void);",
	"bool IsPlayerOccupiedShipDocked(void);",
	"bool CanPlayerStandUp(void);",
	"UIPosRot GetPositionalOffset(UniverseID positionalid, UniverseID spaceid);",
	"void SetPositionalOffset(UniverseID positionalid, UIPosRot offset);",
	"Rotation GetCameraRotation(void);",
	"bool IsConversationActive(void);",
	"const char* GetComponentName(UniverseID componentid);",
}) do
	pcall(ffi.cdef, def) -- identical redeclarations of vanilla types are harmless; anything else is caught here
end

-- state survives a re-load of this file inside one Lua state
S.onfoot = S.onfoot or {}
local O = S.onfoot
O.actorIds = O.actorIds or {}
O.s1 = O.s1 or { active = false }
O.rec = O.rec or { active = false, list = {} }
O.mirror = O.mirror or { active = false }
O.ft = O.ft or { active = false, list = {} }
O.bodySource = O.bodySource or "player" -- which id gives the body transform: "player" = GetPlayerID, "object" = GetPlayerObjectID
O.replyHandlers = {}

local STEP_PREFIX = "S10."
local MENU_NAME = "X4MPSpikeOnFootMenu"
local MIRROR_DELAY = 3.0 -- seconds the mirror lags behind the recorded track

------------------------------------------------------------------------------
-- small helpers
------------------------------------------------------------------------------
local function num(v, default)
	local n = tonumber(v)
	if n == nil then return default end
	return n
end

local function callNum(name)
	local ok, v = pcall(function() return C[name]() end)
	if ok then return tonumber(v) end
	return nil
end

local function callBool(name)
	local ok, v = pcall(function() return C[name]() end)
	if ok then return v and true or false end
	return nil
end

local function compName(id)
	if id == nil or id == 0 then return "" end
	local ok, s = pcall(function() return ffi.string(C.GetComponentName(id)) end)
	if ok then return s end
	return "?"
end

local function readPos(id)
	if id == nil then return nil end
	local ok, p = pcall(function() return C.GetPositionalOffset(id, 0) end)
	if not ok or p == nil then return nil, p end
	return { x = tonumber(p.x), y = tonumber(p.y), z = tonumber(p.z), yaw = tonumber(p.yaw), pitch = tonumber(p.pitch), roll = tonumber(p.roll) }
end

local function bodyId()
	local fn = (O.bodySource == "object") and "GetPlayerObjectID" or "GetPlayerID"
	local ok, v = pcall(function() return C[fn]() end)
	if ok then return v end
	return nil
end

local function dist3(a, b)
	local dx, dy, dz = a.x - b.x, a.y - b.y, a.z - b.z
	return math.sqrt(dx * dx + dy * dy + dz * dz)
end

local function percentile(list, p)
	if #list == 0 then return 0 end
	local c = {}
	for i = 1, #list do c[i] = list[i] end
	table.sort(c)
	local idx = math.max(1, math.min(#c, math.ceil(#c * p)))
	return c[idx]
end

local function avg(list)
	if #list == 0 then return 0 end
	local s = 0
	for i = 1, #list do s = s + list[i] end
	return s / #list
end

local function angDiff(a, b) -- degrees, result in [-180, 180]
	local d = (a - b) % 360
	if d > 180 then d = d - 360 end
	return d
end

local function md(control, list)
	return S.toMD(control, list)
end

local function isWalking()
	local occ = callNum("GetPlayerOccupiedShipID")
	local cont = callNum("GetPlayerContainerID")
	return occ == 0 and cont ~= nil and cont ~= 0, occ, cont
end

------------------------------------------------------------------------------
-- frame hook: frame-time statistics, S10.1 room-change detection, track recorder, mirror controller
------------------------------------------------------------------------------
local trackAt, mirrorFrame

local function onFrame()
	local now = S.now()

	if O.ft.active then
		local dt = (now - (O.ft.last or now)) * 1000
		O.ft.last = now
		if dt > 0 and dt < 1000 then O.ft.list[#O.ft.list + 1] = dt end
	end

	if O.s1.active then
		local env = callNum("GetEnvironmentObject")
		local grok, gr = false, nil
		local fn = rawget(_G, "GetPlayerRoom")
		if type(fn) == "function" then grok, gr = pcall(fn) end
		local key = tostring(env) .. "|" .. (grok and tostring(gr) or "n/a")
		if O.s1.lastKey ~= nil and key ~= O.s1.lastKey then
			O.s1.lastChangeFrame = S.frame()
			O.s1.lastChangeT = now
			O.s1.roomChanges = (O.s1.roomChanges or 0) + 1
			log("S10.1", "INFO", K("what", "lua_room_change_detected", "frame", S.frame(), "t", now, "env_before", O.s1.lastEnv, "env_now", env,
				"lua_room_before", O.s1.lastRoom, "lua_room_now", grok and gr or "n/a"))
		end
		O.s1.lastKey, O.s1.lastEnv, O.s1.lastRoom = key, env, grok and gr or "n/a"
	end

	if O.rec.active then
		local id = bodyId()
		local p = id and readPos(id)
		if p then
			local list = O.rec.list
			list[#list + 1] = { t = now, x = p.x, y = p.y, z = p.z, yaw = p.yaw }
			if #list > 1500 then -- keep roughly the last 10 s
				local keep, cut = {}, now - 10
				for i = 1, #list do if list[i].t >= cut then keep[#keep + 1] = list[i] end end
				O.rec.list = keep
			end
		end
	end

	if O.mirror.active then mirrorFrame(now) end
end
O.onFrame = onFrame
if not O.hookInstalled then
	O.hookInstalled = true
	S.addUpdate(function() if O.onFrame then O.onFrame() end end)
end

------------------------------------------------------------------------------
-- track recorder + mirror controller (S10.4, S10.15)
------------------------------------------------------------------------------
function trackAt(t)
	local list = O.rec.list
	for i = #list, 2, -1 do
		local a, b = list[i - 1], list[i]
		if a.t <= t and b.t >= t then
			local f = (b.t > a.t) and (t - a.t) / (b.t - a.t) or 0
			return {
				x = a.x + (b.x - a.x) * f, y = a.y + (b.y - a.y) * f, z = a.z + (b.z - a.z) * f,
				yaw = (a.yaw or 0) + angDiff(b.yaw or 0, a.yaw or 0) * f,
			}
		end
	end
	return nil
end

function mirrorFrame(now)
	local m = O.mirror
	local tgt = trackAt(now - MIRROR_DELAY)
	if not tgt then return end
	-- error sample (5 Hz): where the actor really is vs where it was commanded to be
	if now >= (m.nextErr or 0) then
		m.nextErr = now + 0.2
		local id = O.actorIds[m.tag or "mirror"]
		local p = id and readPos(id)
		if p then
			m.errs[#m.errs + 1] = dist3(p, tgt)
		else
			m.noPos = (m.noPos or 0) + 1
		end
	end
	if m.mode == "A" then
		if now >= (m.nextSend or 0) then
			m.nextSend = now + 0.2
			m.sent = m.sent + 1
			md("of_place", { m.step, m.tag or "mirror", tgt.x, tgt.y, tgt.z, tgt.yaw, "player" })
		end
	elseif m.mode == "B" then
		if now >= (m.nextSend or 0) then
			m.nextSend = now + 1 / (m.hz or 2)
			local prev = trackAt(now - MIRROR_DELAY - 0.5)
			local speed = 1.5
			if prev then speed = math.max(0.8, math.min(7, dist3(tgt, prev) / 0.5)) end
			m.sent = m.sent + 1
			md("of_walk", { m.step, m.tag or "mirror", tgt.x, tgt.y, tgt.z, tgt.yaw, speed, "player" })
		end
	elseif m.mode == "C" then
		local id = O.actorIds[m.tag or "mirror"]
		if id then
			local ok, err = pcall(function()
				local off = ffi.new("UIPosRot", { x = tgt.x, y = tgt.y, z = tgt.z, yaw = tgt.yaw, pitch = 0, roll = 0 })
				C.SetPositionalOffset(id, off)
			end)
			m.sent = m.sent + 1
			if not ok and not m.errLogged then
				m.errLogged = true
				log(m.step, "FAIL", K("what", "SetPositionalOffset_error", "err", err))
			end
		end
	end
end

------------------------------------------------------------------------------
-- MD replies
------------------------------------------------------------------------------
local function handleReply(param)
	local text = tostring(param or "")
	local kind, rest = text:match("^([^;]*);?(.*)$")
	local kv = {}
	for k, v in rest:gmatch("([%w_]+)=([^;]*)") do kv[k] = v end
	local h = O.replyHandlers[kind]
	if h then
		local ok, err = pcall(h, kv)
		if not ok then log("S10.MD", "FAIL", K("what", "reply_handler_error", "kind", kind, "err", err)) end
	else
		log("S10.MD", "INFO", K("what", "reply_unhandled", "kind", kind, "raw", rest))
	end
end

O.replyHandlers.pos = function(kv)
	local s = O.s1.samples and O.s1.samples[tonumber(kv.n)]
	if not s then return end
	local md_ = { x = tonumber(kv.x), y = tonumber(kv.y), z = tonumber(kv.z), yaw = tonumber(kv.yaw) }
	local line = { n = s.n }
	for _, src in ipairs({ "player", "object" }) do
		local p = s[src]
		if p and md_.x then
			local e = dist3(p, md_) * 100
			line["err_" .. src .. "_cm"] = e
			line["yawdiff_" .. src] = angDiff(p.yaw or 0, md_.yaw or 0)
			if s.still then
				O.s1.stillErr[src] = O.s1.stillErr[src] or {}
				local l = O.s1.stillErr[src]
				l[#l + 1] = e
			else
				O.s1.movingErr[src] = O.s1.movingErr[src] or {}
				local l = O.s1.movingErr[src]
				l[#l + 1] = e
			end
		end
	end
	log("S10.1", "MEASURE", K("what", "pos_compare", "n", s.n, "still", s.still, "md_x", md_.x, "md_y", md_.y, "md_z", md_.z, "md_yaw_deg", md_.yaw,
		"err_player_cm", line.err_player_cm, "err_object_cm", line.err_object_cm, "yawdiff_player", line.yawdiff_player, "yawdiff_object", line.yawdiff_object))
	O.s1.samples[tonumber(kv.n)] = nil -- keep memory flat
end

O.replyHandlers.evt = function(kv)
	local now = S.now()
	local s1 = O.s1
	log("S10.1", "INFO", K("what", "md_event_received_in_lua", "kind", kv.kind, "frame", S.frame(), "t", now,
		"frames_since_lua_room_change", s1.lastChangeFrame and (S.frame() - s1.lastChangeFrame) or "n/a",
		"seconds_since_lua_room_change", s1.lastChangeT and (now - s1.lastChangeT) or "n/a", "new_room", kv["new"], "prev_room", kv.prev, "leftover", kv.leftover))
	if kv.kind == "changed_room" then s1.mdChangedRoom = (s1.mdChangedRoom or 0) + 1 end
	if kv.kind == "transport_finished" then s1.mdTransport = (s1.mdTransport or 0) + 1 end
	if kv.kind == "started_control" then s1.mdStarted = (s1.mdStarted or 0) + 1 end
	if kv.kind == "stopped_control" then s1.mdStopped = (s1.mdStopped or 0) + 1 end
	if kv.kind == "interiors_despawning" then O.despawnSeen = true; O.despawnLeftover = tonumber(kv.leftover) end
end

O.replyHandlers.lounge = function(kv)
	O.lounge = { ok = kv.ok == "1", variant = kv.variant, why = kv.why }
	if kv.ok == "1" then
		O.created = O.created or {}
		O.created[#O.created + 1] = kv.variant
	end
	log("S10.11", "INFO", K("what", "lounge_reply", "ok", kv.ok, "variant", kv.variant, "why", kv.why, "keep", kv.keep))
end

O.replyHandlers.conv = function(kv)
	log("S10.7", "INFO", K("what", "md_conv_event_in_lua", "event", kv.what, "section", kv.section, "outcome", kv.outcome, "frame", S.frame()))
	O.conv = O.conv or {}
	O.conv[kv.what] = (O.conv[kv.what] or 0) + 1
	if kv.section then O.conv[kv.section] = (O.conv[kv.section] or 0) + 1 end
end

local function onActorTag(_, param)
	O.pendingTag = tostring(param or "")
end

local function onActor(_, param)
	local tag = O.pendingTag or "?"
	local raw = tostring(param)
	local ok, id = pcall(function() return ConvertStringTo64Bit(raw) end)
	if ok and id and tonumber(id) and tonumber(id) ~= 0 then
		O.actorIds[tag] = id
		log("S10.MD", "INFO", K("what", "actor_id_received", "tag", tag, "raw", raw, "id", tonumber(id), "position_readable", readPos(id) ~= nil))
	else
		O.actorIds[tag] = nil
		log("S10.MD", "FAIL", K("what", "actor_id_conversion_failed", "tag", tag, "raw", raw, "ok", ok, "err", ok and "" or id))
	end
end

if not O.eventsRegistered then
	O.eventsRegistered = true
	local ok, err = pcall(function()
		RegisterEvent("x4mp_spike.of", function(_, param) if O.handleReply then O.handleReply(param) end end)
		RegisterEvent("x4mp_spike.of_actor_tag", function(e, p) if O.onActorTag then O.onActorTag(e, p) end end)
		RegisterEvent("x4mp_spike.of_actor", function(e, p) if O.onActor then O.onActor(e, p) end end)
	end)
	if not ok then log("S10.MD", "FAIL", K("what", "RegisterEvent_failed", "err", err)) end
end
O.handleReply, O.onActorTag, O.onActor = handleReply, onActorTag, onActor

------------------------------------------------------------------------------
-- run control and waiting (inside a block's routine)
------------------------------------------------------------------------------
local function waitFor(seconds, run)
	local t0 = S.now()
	while S.now() - t0 < seconds do
		if run.skip or run.stop then break end
		S.waitSeconds(0.25)
	end
	local reason = run.stop and "stop" or (run.skip and "skip" or "timeout")
	run.skip = false
	return reason
end

-- like waitFor but does NOT consume a pending "next": for loops that check run.skip themselves
local function waitSlice(seconds, run)
	local t0 = S.now()
	while S.now() - t0 < seconds do
		if run.skip or run.stop then break end
		S.waitSeconds(0.25)
	end
end

local function waitUntil(cond, timeout, run)
	local t0 = S.now()
	while S.now() - t0 < timeout do
		if cond() then return true end
		if run and run.stop then return false end
		S.waitSeconds(0.2)
	end
	return cond() and true or false
end

local function say(stepNo, text)
	S.notify("S10." .. tostring(stepNo) .. ": " .. text)
end

-- the player must be walking (not in a cockpit seat, not in space): ask once, wait up to `timeout` s
local function needWalking(stepNo, run, timeout)
	local walking = isWalking()
	if walking then return true end
	say(stepNo, "you must be on foot for this step: get up from the pilot seat (docked) and stand in a corridor or the trader corner. Waiting.")
	local ok = waitUntil(function() return (isWalking()) end, timeout or 180, run)
	log("S10." .. stepNo, ok and "INFO" or "FAIL", K("what", "player_on_foot", "walking", ok))
	return ok
end

local function spawn(step, tag, variant, name, custom, temp, dist, ang, roomkey)
	O.actorIds[tag] = nil
	md("of_spawn", { step, tag, variant, name, custom and 1 or 0, temp and 1 or 0, dist, ang, roomkey or "player" })
end

local function removeActors(step, which)
	md("of_remove", { step, which or "all" })
end

local function waitActorId(tag, run)
	return waitUntil(function() return O.actorIds[tag] ~= nil end, 3, run)
end

local function ensureLounge(step, variant, run)
	if O.loungeKept then return true end
	O.lounge = nil
	md("of_lounge_create", { step, variant or "bar", 4711, 1 })
	waitUntil(function() return O.lounge ~= nil end, 8, run)
	if O.lounge and O.lounge.ok then
		O.loungeKept = true
		return true
	end
	return false
end

local function goLounge(step, x, y, z)
	md("of_lounge_go", { step, num(x, 0), num(y, 0), num(z, 0) })
	O.inLounge = true
end

local function leaveLounge(step)
	md("of_lounge_leave", { step })
	O.inLounge = false
end

------------------------------------------------------------------------------
-- test menu (S10.12 "Go to lounge" / "Leave", S10.7 "Open MP menu")
------------------------------------------------------------------------------
local menu = { name = MENU_NAME, mode = "lounge" }
O.menu = menu
local WIN_LAYER = 4

local function closeMenu(dueTo)
	if menu.conversationMenu and dueTo == "back" then
		Helper.closeMenuAndReturn(menu, "x4mp_spike_back")
	else
		Helper.closeMenu(menu, dueTo or "close")
	end
	menu.cleanup()
end

function menu.display()
	Helper.clearDataForRefresh(menu, WIN_LAYER)
	local width = Helper.scaleX(420)
	menu.frame = Helper.createFrameHandle(menu, {
		layer = WIN_LAYER,
		standardButtons = Helper.standardButtons_Close,
		width = width,
		x = math.floor((Helper.viewWidth - width) / 2),
		y = math.floor(Helper.viewHeight / 4),
		autoFrameHeight = true,
		startAnimation = false,
		blurBackground = false,
		playerControls = false,
	})
	menu.frame:setBackground("solid", { color = Color["frame_background_semitransparent"] })
	local ftable = menu.frame:addTable(1, { tabOrder = 1, highlightMode = "off" })
	local row = ftable:addRow(false, { fixed = true })
	row[1]:createText(menu.mode == "conv" and "X4MP spike: MP menu (test)" or "X4MP spike: lounge test", Helper.headerRowCenteredProperties)
	if menu.mode == "conv" then
		local r = ftable:addRow(false, {})
		r[1]:createText("S10.7: this menu was opened from the conversation with a test character.", { wordwrap = true })
		local b = ftable:addRow(true, {})
		b[1]:createButton({}):setText("Back to conversation", { halign = "center" })
		b[1].handlers.onClick = function()
			log("S10.7", "INFO", K("what", "conv_menu_back_clicked"))
			closeMenu("back")
		end
	else
		local r = ftable:addRow(false, {})
		r[1]:createText("S10.12: Go to lounge teleports you into the test lounge (private dynamic interior). Leave brings you back to where you were.", { wordwrap = true })
		local b1 = ftable:addRow(true, {})
		b1[1]:createButton({}):setText("Go to lounge", { halign = "center" })
		b1[1].handlers.onClick = function()
			log("S10.12", "INFO", K("what", "menu_go_to_lounge_clicked"))
			goLounge("S10.12", O.goX, O.goY, O.goZ)
			closeMenu("close")
		end
		local b2 = ftable:addRow(true, {})
		b2[1]:createButton({}):setText("Leave", { halign = "center" })
		b2[1].handlers.onClick = function()
			log("S10.12", "INFO", K("what", "menu_leave_clicked"))
			leaveLounge("S10.12")
			closeMenu("close")
		end
		local b3 = ftable:addRow(true, {})
		b3[1]:createButton({}):setText("Close", { halign = "center" })
		b3[1].handlers.onClick = function() closeMenu("close") end
	end
	menu.frame:display()
end

function menu.onShowMenu()
	menu.shown = true
	local okC, conv = pcall(function() return C.IsConversationActive() end)
	if okC and conv then menu.mode = "conv" end
	log("S10.7", "INFO", K("what", "menu_onShowMenu", "mode", menu.mode, "conversation_active", okC and conv or "n/a", "param", tostring(menu.param)))
	local ok, err = pcall(menu.display)
	log("S10.7", ok and "PASS" or "FAIL", K("what", "menu_displayed", "mode", menu.mode, "err", ok and "" or err))
end

function menu.onCloseElement(dueTo)
	log("S10.7", "INFO", K("what", "menu_close_element", "mode", menu.mode, "due_to", dueTo))
	closeMenu(dueTo)
end

function menu.cleanup()
	menu.shown = nil
	menu.frame = nil
	menu.conversationMenu = nil
end

local function ensureMenuRegistered()
	if O.menuRegistered then return true end
	if type(Helper) ~= "table" or type(Helper.registerMenu) ~= "function" then
		log("S10.12", "FAIL", K("what", "helper_missing", "helper", type(Helper)))
		return false
	end
	Menus = Menus or {}
	for i = #Menus, 1, -1 do
		if type(Menus[i]) == "table" and Menus[i].name == MENU_NAME then table.remove(Menus, i) end
	end
	table.insert(Menus, menu)
	local ok, err = pcall(Helper.registerMenu, menu)
	O.menuRegistered = ok
	log("S10.12", ok and "INFO" or "FAIL", K("what", "menu_registered", "name", MENU_NAME, "err", ok and "" or err))
	return ok
end

local function openLoungeMenu()
	if not ensureMenuRegistered() then return false end
	menu.mode = "lounge"
	local ok, err = pcall(OpenMenu, MENU_NAME, { 0, 0 }, nil)
	log("S10.12", ok and "INFO" or "FAIL", K("what", "OpenMenu_called", "err", ok and "" or err))
	return ok
end

-- the conversation menu must exist before the conversation asks for it
pcall(ensureMenuRegistered)

------------------------------------------------------------------------------
-- steps
------------------------------------------------------------------------------
local STEPS = {}

-- S10.1 -------------------------------------------------------------------
local function s1Sample(n)
	local s1 = O.s1
	local now = S.now()
	local pid, oid = nil, nil
	pcall(function() pid = C.GetPlayerID(); oid = C.GetPlayerObjectID() end)
	local pp, op = readPos(pid), readPos(oid)
	local cam = nil
	pcall(function() cam = C.GetCameraRotation() end)
	local grok, gr = false, nil
	local fn = rawget(_G, "GetPlayerRoom")
	if type(fn) == "function" then grok, gr = pcall(fn) end
	local cont = callNum("GetPlayerContainerID")
	local occ = callNum("GetPlayerOccupiedShipID")
	local speedP, speedO = nil, nil
	if s1.prev and pp and s1.prev.player and (now > s1.prev.t) and s1.prev.container == cont then
		speedP = dist3(pp, s1.prev.player) / (now - s1.prev.t)
		if op and s1.prev.object then speedO = dist3(op, s1.prev.object) / (now - s1.prev.t) end
	end
	local sample = { n = n, t = now, container = cont, player = pp, object = op, still = (speedP ~= nil and speedP < 0.3) }
	s1.prev = sample
	s1.samples = s1.samples or {}
	s1.samples[n] = sample
	s1.count = (s1.count or 0) + 1
	if cont and cont ~= 0 then s1.withContainer = (s1.withContainer or 0) + 1 end
	if grok and gr ~= nil then s1.withLuaRoom = (s1.withLuaRoom or 0) + 1 end
	if occ == 0 then s1.walkingSamples = (s1.walkingSamples or 0) + 1 end
	if speedP then s1.maxSpeed = math.max(s1.maxSpeed or 0, speedP) end
	log("S10.1", "MEASURE", K("n", n, "side", "lua", "t", now, "frame", S.frame(),
		"container", cont, "container_name", compName(cont), "env_object", callNum("GetEnvironmentObject"), "occupied_ship", occ,
		"occupied_docked", callBool("IsPlayerOccupiedShipDocked"), "can_stand_up", callBool("CanPlayerStandUp"),
		"lua_GetPlayerRoom_ok", grok, "lua_room", grok and gr or "n/a",
		"player_x", pp and pp.x, "player_y", pp and pp.y, "player_z", pp and pp.z, "player_yaw", pp and pp.yaw, "player_pitch", pp and pp.pitch,
		"object_x", op and op.x, "object_y", op and op.y, "object_z", op and op.z, "object_yaw", op and op.yaw,
		"camera_yaw", cam and tonumber(cam.yaw), "camera_pitch", cam and tonumber(cam.pitch),
		"speed_player_mps", speedP, "speed_object_mps", speedO))
	md("of_s1_sample", n)
end

local function s1Summary()
	local s1 = O.s1
	local function stats(l)
		if not l or #l == 0 then return 0, -1, -1 end
		return #l, percentile(l, 0.95), percentile(l, 1.0)
	end
	local np, p95p, maxp = stats(s1.stillErr and s1.stillErr.player)
	local no, p95o, maxo = stats(s1.stillErr and s1.stillErr.object)
	local nmp, _, maxmp = stats(s1.movingErr and s1.movingErr.player)
	local best = (np > 0 and (no == 0 or maxp <= maxo)) and "player" or ((no > 0) and "object" or "none")
	local bestMax = (best == "player") and maxp or ((best == "object") and maxo or -1)
	if best == "object" then O.bodySource = "object" end
	local container_ok = (s1.count or 0) > 0 and (s1.withContainer or 0) >= (s1.count or 0) * 0.9
	local pos_ok = best ~= "none" and bestMax <= 1.0
	local speedOk = (s1.maxSpeed or 0) >= 1 and (s1.maxSpeed or 0) <= 8
	log("S10.1", (container_ok and pos_ok and speedOk) and "PASS" or "FAIL", K("what", "summary", "samples", s1.count, "with_container", s1.withContainer,
		"with_lua_room", s1.withLuaRoom, "walking_samples", s1.walkingSamples, "max_walk_speed_mps", s1.maxSpeed,
		"still_samples_player", np, "still_err_p95_cm_player", p95p, "still_err_max_cm_player", maxp,
		"still_samples_object", no, "still_err_p95_cm_object", p95o, "still_err_max_cm_object", maxo,
		"moving_samples_player", nmp, "moving_err_max_cm_player", maxmp,
		"best_body_source", best, "best_still_err_max_cm", bestMax,
		"lua_room_changes", s1.roomChanges, "md_changed_room_events", s1.mdChangedRoom, "md_transport_events", s1.mdTransport,
		"md_started_control", s1.mdStarted, "md_stopped_control", s1.mdStopped,
		"criteria", "container_and_room_everywhere_pos_within_1cm_when_still_speed_1_to_6"))
end

STEPS[1] = { title = "read on-foot state", fn = function(a, run)
	local dur = num(a.dur, 600)
	say(1, "walk now: pad, elevator, corridor, trader corner, (bar), transporter, then sit back in the pilot seat. Note the clock time you enter each place. About " ..
		math.floor(dur / 60) .. " min; '/x4mpspike onfoot1 next=1' ends it early.")
	O.s1 = { active = true, stillErr = {}, movingErr = {} }
	md("of_s1", 1)
	local t0 = S.now()
	local n = 0
	local nextHint = 120
	while S.now() - t0 < dur and not run.skip and not run.stop do
		n = n + 1
		s1Sample(n)
		if S.now() - t0 > nextHint then
			nextHint = nextHint + 120
			say(1, "still sampling (" .. math.floor(S.now() - t0) .. " s). Keep walking the route; sit back in the pilot seat when done, then 'onfoot1 next=1'.")
		end
		S.waitSeconds(0.5)
	end
	run.skip = false
	O.s1.active = false
	md("of_s1", 0)
	s1Summary()
	say(1, "done. Next step starts shortly.")
end }

-- S10.2 -------------------------------------------------------------------
STEPS[2] = { title = "room-key determinism", fn = function(a, run)
	local dur = num(a.dur, 900)
	local label = a.label or "visit"
	say(2, "stay docked and walk through the rooms of this station (pad, corridor, trader corner, bar, transporter). Room keys are logged on every room change. " ..
		"Then visit a second station, fly > 50 km away and come back, and finally quit, reload the same save and revisit both. Run '/x4mpspike onfoot2 step=2 label=<name>' at each new station or after each reload. 'next=1' ends this step.")
	md("of_s2", 1)
	md("of_s2_keys", { "S10.2", label })
	waitFor(dur, run)
	md("of_s2", 0)
	log("S10.2", "INFO", K("what", "step_end", "label", label, "criterion", "identical_key_sets_per_station_across_revisit_reload"))
end }

-- S10.3 -------------------------------------------------------------------
STEPS[3] = { title = "spawn an MP character", fn = function(a, run)
	if not needWalking(3, run) then return end
	spawn("S10.3", "alice", "player", "Spike Alice", false, false, 2.0, 0, "player")
	spawn("S10.3", "bob", "crew", "Spike Bob", false, false, 3.0, 40, "player")
	S.waitSeconds(2)
	say(3, "look at the test character 'Spike Alice' in front of you (and 'Spike Bob' to the side): appearance (your own look or a crew look), name, title. " ..
		"After 5 min you will be told to walk into another room and come back.")
	local wait5 = num(a.wait, 300)
	local t0 = S.now()
	md("of_lookat", { "S10.3", "alice", 1 })
	while S.now() - t0 < wait5 and not run.skip and not run.stop do
		md("of_status", { "S10.3", "alice" })
		md("of_status", { "S10.3", "bob" })
		waitSlice(30, run)
	end
	run.skip = false
	say(3, "now walk into another room and come back to the characters (about 2 min).")
	local t1 = S.now()
	while S.now() - t1 < num(a.away, 120) and not run.skip and not run.stop do
		waitSlice(20, run)
		md("of_status", { "S10.3", "alice" })
		md("of_status", { "S10.3", "bob" })
	end
	run.skip = false
	md("of_status", { "S10.3", "alice" })
	md("of_status", { "S10.3", "bob" })
	S.waitSeconds(1)
	log("S10.3", "INFO", K("what", "step_end", "criteria", "visible_with_look_name_title_survives_5min_vanilla_does_not_move_or_remove (see actor_status lines: exists, moved_from_spawn_m)"))
	if a.keep ~= "1" then removeActors("S10.3", "all") end
end }

-- S10.4 -------------------------------------------------------------------
local function mirrorPhase(run, step, label, mode, hz, seconds, tag)
	O.mirror = { active = true, mode = mode, hz = hz, errs = {}, sent = 0, step = step, tag = tag or "mirror", nextErr = 0, nextSend = 0 }
	O.ft = { active = true, list = {}, last = S.now() }
	local t0 = S.now()
	while S.now() - t0 < seconds and not run.skip and not run.stop do
		md("of_status", { step, tag or "mirror" })
		waitSlice(10, run)
	end
	run.skip = false
	local m = O.mirror
	m.active = false
	O.ft.active = false
	local p50, p95, pmax = percentile(m.errs, 0.5), percentile(m.errs, 0.95), percentile(m.errs, 1.0)
	local ok = #m.errs > 0 and p95 < 1.0 and pmax < 2.0
	log(step, #m.errs > 0 and (ok and "PASS" or "INFO") or "FAIL", K("what", "mirror_phase", "mode", label, "seconds", seconds,
		"error_samples", #m.errs, "no_position_samples", m.noPos, "err_p50_m", p50, "err_p95_m", p95, "err_max_m", pmax,
		"frame_ms_avg", avg(O.ft.list), "frame_ms_p95", percentile(O.ft.list, 0.95), "commands_sent", m.sent,
		"criterion", "p95_below_1m_and_max_below_2m_plus_tester_rating_at_least_4"))
	return ok
end

STEPS[4] = { title = "movement modes", fn = function(a, run)
	if not needWalking(4, run) then return end
	if a.body == "object" then O.bodySource = "object" end
	local dur = num(a.dur, 60)
	spawn("S10.4", "mirror", "player", "Spike Mirror", false, false, 3.0, 20, "player")
	S.waitSeconds(1.5)
	O.rec = { active = true, list = {} }
	log("S10.4", "INFO", K("what", "recorder_started", "body_source", O.bodySource, "mirror_delay_s", MIRROR_DELAY, "actor_id_known", O.actorIds.mirror ~= nil))
	local only = a.mode
	local function wants(m) return only == nil or only == m end
	local rounds = {}
	if wants("A") then rounds[#rounds + 1] = "A" end
	if wants("B") then rounds[#rounds + 1] = "B" end
	if wants("C") then rounds[#rounds + 1] = "C" end
	for _, m in ipairs(rounds) do
		local text = { A = "mode A: the character is teleported 5 times per second",
			B = "mode B: the character walks to your track (30 s at 2 Hz, then 30 s at 4 Hz)",
			C = "mode C: the character is moved every frame through the native call" }
		say(4, text[m] .. ". Now: walk an S-curve in the trader corner, then run, then stand still and turn. " .. dur .. " s. Rate 1-5 for smoothness, walk animation, foot sliding.")
		S.waitSeconds(3.5) -- let the recorder fill the 3 s delay
		if m == "A" then
			mirrorPhase(run, "S10.4", "A_teleport_5Hz", "A", 5, dur)
		elseif m == "B" then
			mirrorPhase(run, "S10.4", "B_walk_2Hz", "B", 2, dur / 2)
			mirrorPhase(run, "S10.4", "B_walk_4Hz", "B", 4, dur / 2)
		else
			if not waitActorId("mirror", run) then
				log("S10.4", "FAIL", K("what", "mode_C_no_actor_id", "note", "cannot_call_SetPositionalOffset_without_the_actor_id"))
			else
				mirrorPhase(run, "S10.4", "C_SetPositionalOffset_every_frame", "C", 60, dur)
			end
		end
		say(4, "mode " .. m .. " finished: note your rating (1-5).")
		if run.stop then break end
		S.waitSeconds(5)
	end
	O.rec.active = false
	removeActors("S10.4", "all")
end }

-- S10.5 -------------------------------------------------------------------
STEPS[5] = { title = "facing and emotes", fn = function(a, run)
	if not needWalking(5, run) then return end
	spawn("S10.5", "face", "player", "Spike Face", false, false, 3.0, 0, "player")
	S.waitSeconds(1.5)
	if not waitActorId("face", run) then
		log("S10.5", "FAIL", K("what", "no_actor_id_cannot_read_back_yaw"))
	end
	local id = O.actorIds.face
	local base = id and readPos(id)
	if base then
		say(5, "watch the test character 'Spike Face': it is turned to 0, 90, 180 and 270 degrees, then plays body gestures and face emotes. Note what visibly happens.")
		for _, yaw in ipairs({ 0, 90, 180, 270 }) do
			md("of_place", { "S10.5", "face", base.x, base.y, base.z, yaw, "player" })
			S.waitSeconds(1.5)
			local now = readPos(id)
			local err = now and angDiff(now.yaw or 0, yaw) or nil
			log("S10.5", (err and math.abs(err) <= 15) and "PASS" or "INFO", K("what", "yaw_after_placement", "requested_deg", yaw, "read_back_yaw", now and now.yaw,
				"error_deg", err, "note", "if_read_back_is_in_radians_the_UIPosRot_unit_is_not_degrees"))
			md("of_status", { "S10.5", "face" })
			S.waitSeconds(2)
		end
		md("of_walk", { "S10.5", "face", base.x + 1.0, base.y, base.z, 90, 1.5, "player" })
		S.waitSeconds(4)
		local after = readPos(id)
		log("S10.5", "INFO", K("what", "yaw_after_walk_end_with_rotation_90", "read_back_yaw", after and after.yaw))
	end
	for _, seq in ipairs({ { "idle", "" }, { "conversation", "" }, { "facepalm01", "" }, { "nod01", "" }, { "turnleft90", "" }, { "sitdown", "" }, { "standup", "" } }) do
		say(5, "gesture '" .. seq[1] .. "'")
		md("of_seq", { "S10.5", "face", seq[1], seq[2] })
		waitFor(4, run)
		if run.stop then break end
	end
	for _, emo in ipairs({ "smile", "angry", "sad", "fear" }) do
		say(5, "face emote '" .. emo .. "'")
		md("of_emote", { "S10.5", "face", emo })
		waitFor(3.5, run)
		if run.stop then break end
	end
	md("of_emote", { "S10.5", "face", "clear" })
	say(5, "look-at: the character should follow you with its eyes/head for 8 s. Walk around it.")
	md("of_lookat", { "S10.5", "face", 1 })
	waitFor(8, run)
	md("of_lookat", { "S10.5", "face", 0 })
	log("S10.5", "INFO", K("what", "step_end", "criteria", "yaw_within_15_deg_at_least_2_body_gestures_and_1_face_emote_work_lookat_works (the gesture results are the tester notes)"))
	removeActors("S10.5", "all")
end }

-- S10.6 -------------------------------------------------------------------
STEPS[6] = { title = "transitions and teardown", fn = function(a, run)
	if not needWalking(6, run) then return end
	local dur = num(a.dur, 600)
	O.despawnSeen, O.despawnLeftover = false, nil
	spawn("S10.6", "follow", "crew", "Spike Follower", false, false, 2.0, 30, "player")
	S.waitSeconds(1.5)
	md("of_s6", { "S10.6", 1, "follow" })
	say(6, "use a transporter or walk to another room: the test character 'Spike Follower' is re-placed next to you each time. Then undock, fly more than 50 km away and come back and dock. " ..
		"(With an L/XL ship of your own: dock at it, walk to the bridge and run '/x4mpspike onfoot1 step=3'.) 'next=1' ends this step.")
	local t0 = S.now()
	while S.now() - t0 < dur and not run.skip and not run.stop do
		waitSlice(30, run)
		md("of_status", { "S10.6", "follow" })
	end
	run.skip = false
	md("of_s6", { "S10.6", 0, "follow" })
	log("S10.6", O.despawnSeen and "PASS" or "INFO", K("what", "step_end", "interiors_despawning_seen", O.despawnSeen, "leftover_actors", O.despawnLeftover,
		"criteria", "re_placement_works_in_new_rooms_and_cleanup_cue_logs_0_leftover_on_interiors_despawning"))
	removeActors("S10.6", "all")
end }

-- S10.7 -------------------------------------------------------------------
STEPS[7] = { title = "conversation", fn = function(a, run)
	if not needWalking(7, run) then return end
	local dur = num(a.dur, 240)
	O.conv = {}
	spawn("S10.7", "carol", "crew", "Spike Carol", true, false, 2.0, 0, "player")
	S.waitSeconds(1.5)
	md("of_lookat", { "S10.7", "carol", 1 })
	say(7, "walk up to the test character 'Spike Carol' and talk to it (Talk on the character). Try the three choices: 'Message', 'Wave', 'Open MP menu'. " ..
		"Note whether the choices appeared and whether any normal NPC chatter appeared instead.")
	waitFor(dur, run)
	local c = O.conv or {}
	log("S10.7", (c.started or 0) > 0 and (((c.x4mp_spike_message or 0) + (c.x4mp_spike_wave or 0) + (c.x4mp_spike_menu or 0)) > 0 and "PASS" or "INFO") or "FAIL",
		K("what", "step_end", "conversations_started", c.started or 0, "message_chosen", c.x4mp_spike_message or 0, "wave_chosen", c.x4mp_spike_wave or 0,
		"menu_chosen", c.x4mp_spike_menu or 0, "finished", c.finished or 0,
		"criteria", "choices_render_each_logs_next_section_menu_opens_and_returns_vanilla_default_comm_absent (the last part is the tester note)"))
	removeActors("S10.7", "all")
end }

-- S10.8 -------------------------------------------------------------------
local function crowdSpawn(step, temporaryFrom)
	for i = 1, 8 do
		spawn(step, "crowd" .. i, "crew", "Spike NPC " .. i, false, temporaryFrom ~= nil and i >= temporaryFrom, 3 + (i % 3), (i - 1) * 45, "player")
	end
end

STEPS[8] = { title = "cost and save hygiene", fn = function(a, run)
	if not needWalking(8, run) then return end
	local phase = tostring(a.phase or "1")
	if phase == "2" then
		crowdSpawn("S10.8", 5) -- crowd5..8 are temporary=true
		S.waitSeconds(3)
		say(8, "RUN 2: the 8 test characters stay now (4 normal, 4 temporary). Save to a NEW slot NOW, then load that save. The janitor logs what it finds 5 s after the load (S10.JAN lines). Do not remove them yourself.")
		log("S10.8", "INFO", K("what", "run2_instruction_given", "expected_after_reload", "janitor_finds_and_removes_the_actors_temporary_ones_may_be_absent"))
		S.waitSeconds(20)
		return
	end
	local dur = num(a.dur, 120)
	say(8, "stand in the bar or trader corner. First 10 s without characters (baseline), then 8 test characters walk around for " .. math.floor(dur / 60) .. " min.")
	O.ft = { active = true, list = {}, last = S.now() }
	waitFor(10, run)
	O.ft.active = false
	local base = { avg = avg(O.ft.list), p95 = percentile(O.ft.list, 0.95), n = #O.ft.list }
	log("S10.8", "MEASURE", K("what", "frame_time", "phase", "baseline_0_actors", "frames", base.n, "frame_ms_avg", base.avg, "frame_ms_p95", base.p95))
	crowdSpawn("S10.8", nil)
	S.waitSeconds(5)
	O.ft = { active = true, list = {}, last = S.now() }
	local t0 = S.now()
	local nextWalk = 0
	while S.now() - t0 < dur and not run.skip and not run.stop do
		if S.now() - t0 >= nextWalk then
			nextWalk = nextWalk + 3
			md("of_crowd_walk", { "S10.8", 8, 4, 1.3 })
		end
		S.waitSeconds(0.5)
	end
	run.skip = false
	O.ft.active = false
	local withA = { avg = avg(O.ft.list), p95 = percentile(O.ft.list, 0.95), n = #O.ft.list }
	local delta = withA.avg - base.avg
	log("S10.8", delta < 0.5 and "PASS" or "FAIL", K("what", "frame_time_delta", "baseline_ms_avg", base.avg, "with_8_actors_ms_avg", withA.avg, "with_8_actors_ms_p95", withA.p95,
		"delta_ms", delta, "frames", withA.n, "criterion", "delta_below_0.5_ms_per_frame (fps is dominated by vanilla stations: also note your FPS before/during)"))
	removeActors("S10.8", "all")
	S.waitSeconds(1)
	say(8, "RUN 1: the test characters are removed (stripped). Save to a NEW slot now and load it: 0 test characters should exist. " ..
		"Then run '/x4mpspike onfoot1 step=8 phase=2' for run 2 (characters left in the save; the janitor must find and remove them).")
	S.waitSeconds(20)
end }

-- S10.9 -------------------------------------------------------------------
STEPS[9] = { title = "interior differences", fn = function(a, run)
	say(9, "logging the dynamic interiors and their condition inputs of the stations near you. Repeat '/x4mpspike onfoot2 step=9' at four more stations (5 in total).")
	md("of_s9", { "S10.9", num(a.count, 5) })
	waitFor(3, run)
end }

-- S10.11 ------------------------------------------------------------------
STEPS[11] = { title = "create the MP lounge", fn = function(a, run)
	local _, _, cont = isWalking()
	if not (cont and cont ~= 0) then
		log("S10.11", "FAIL", K("what", "player_not_docked_or_inside_a_station", "container", cont))
	end
	say(11, "stay docked. Creating the test lounge three times (bar, player office, venturer room), one after the other; each is removed again, the bar variant is kept for the next steps.")
	O.created = {}
	for _, v in ipairs({ "bar", "office", "venturer" }) do
		O.lounge = nil
		md("of_lounge_create", { "S10.11", v, 4711, 0 })
		waitUntil(function() return O.lounge ~= nil end, 8, run)
		S.waitSeconds(1)
		if run.stop then break end
		md("of_lounge_remove", { "S10.11" })
		S.waitSeconds(2)
	end
	local keepVariant = a.variant or "bar"
	O.loungeKept = false
	local kept = ensureLounge("S10.11", keepVariant, run)
	local created = table.concat(O.created, ",")
	log("S10.11", (#O.created >= 1) and "PASS" or "FAIL", K("what", "summary", "variants_created", created, "kept_for_next_steps", kept, "kept_variant", keepVariant,
		"criterion", "interior_created_for_at_least_1_macro_on_2_stations_incl_one_with_a_vanilla_bar (repeat this step at a second station)"))
end }

-- S10.12 ------------------------------------------------------------------
STEPS[12] = { title = "enter and leave the lounge", fn = function(a, run)
	O.goX, O.goY, O.goZ = num(a.x, 0), num(a.y, 0), num(a.z, 0)
	if a.go == "1" then goLounge("S10.12", O.goX, O.goY, O.goZ); return end
	if a.leave == "1" then leaveLounge("S10.12"); return end
	if not ensureLounge("S10.12", a.variant or "bar", run) then
		log("S10.12", "FAIL", K("what", "no_lounge_available"))
		return
	end
	say(12, "use the test menu: 'Go to lounge', then 'Leave'. Open the station's transporter: is 'Multiplayer Lounge' listed? Walk out of the lounge door: where does it lead? " ..
		"Black screen or stuck? (If the menu closed: '/x4mpspike onfoot1 step=12 go=1' / 'leave=1'.)")
	openLoungeMenu()
	waitFor(num(a.dur, 240), run)
	log("S10.12", "INFO", K("what", "step_end", "player_in_lounge_flag", O.inLounge,
		"criteria", "teleport_in_and_out_without_stuck_or_black_screen_transporter_listing_and_door_behaviour_are_tester_notes"))
end }

-- S10.13 ------------------------------------------------------------------
STEPS[13] = { title = "lounge determinism", fn = function(a, run)
	if not ensureLounge("S10.13", a.variant or "bar", run) then
		log("S10.13", "FAIL", K("what", "no_lounge_available"))
		return
	end
	md("of_lounge_slots", { "S10.13" })
	S.waitSeconds(1)
	say(13, "slot offsets logged. To compare: save, load that save, then run '/x4mpspike onfoot2 step=13' again and compare slots= and checksum= of the two lines (S10.13 MEASURE).")
	S.waitSeconds(15)
end }

-- S10.14 ------------------------------------------------------------------
STEPS[14] = { title = "lounge save safety", fn = function(a, run)
	if not ensureLounge("S10.14", a.variant or "bar", run) then
		log("S10.14", "FAIL", K("what", "no_lounge_available"))
		return
	end
	local win = num(a.dur, 90)
	say(14, "run 1: the lounge exists and you are OUTSIDE. Save now to a NEW slot (name it lounge-outside). You have " .. win .. " s ('next=1' to continue).")
	waitFor(win, run)
	if run.stop then return end
	say(14, "moving you INTO the lounge in 5 s.")
	S.waitSeconds(5)
	goLounge("S10.14", num(a.x, 0), num(a.y, 0), num(a.z, 0))
	S.waitSeconds(3)
	say(14, "run 2: you are INSIDE the lounge. Save now to a NEW slot (name it lounge-inside). You have " .. win .. " s ('next=1' to continue).")
	waitFor(win, run)
	leaveLounge("S10.14")
	S.waitSeconds(2)
	say(14, "done. LATER, after the session: disable x4mp_spike in Settings > Extensions, load both saves (do they load, are you stuck?), then re-enable and load one: the janitor logs what it removed.")
	log("S10.14", "INFO", K("what", "step_end", "saves_to_make", "lounge-outside,lounge-inside", "criteria",
		"both_saves_load_without_the_mod_player_not_stuck_janitor_removes_the_orphan_with_the_mod (janitor lines: S10.JAN lounge_sweep)"))
end }

-- S10.15 ------------------------------------------------------------------
STEPS[15] = { title = "lounge with actors and talk", fn = function(a, run)
	if not ensureLounge("S10.15", a.variant or "bar", run) then
		log("S10.15", "FAIL", K("what", "no_lounge_available"))
		return
	end
	local mode = a.mode or "A"
	local dur = num(a.dur, 300)
	if a.body == "object" then O.bodySource = "object" end
	goLounge("S10.15", num(a.x, 0), num(a.y, 0), num(a.z, 0))
	S.waitSeconds(3)
	spawn("S10.15", "mirror", "player", "Spike Mirror", false, false, 3.0, 20, "player")
	spawn("S10.15", "lg1", "crew", "Spike Seat 1", false, false, 2.0, 120, "lounge")
	spawn("S10.15", "lg2", "crew", "Spike Seat 2", false, false, 2.0, 240, "lounge")
	spawn("S10.15", "crowd1", "crew", "Spike Walker", false, false, 2.5, 180, "lounge")
	spawn("S10.15", "carol", "crew", "Spike Carol", true, false, 2.0, 60, "lounge")
	S.waitSeconds(2)
	md("of_slot_seat", { "S10.15", "lg1", 1 })
	md("of_slot_seat", { "S10.15", "lg2", 2 })
	O.conv = {}
	O.rec = { active = true, list = {} }
	say(15, "you are in the lounge with 5 test characters: a mirror of you (mode " .. mode .. "), two seated, one walking, and 'Spike Carol' to talk to. Walk around and talk for " ..
		math.floor(dur / 60) .. " min, then rate it 1-5.")
	S.waitSeconds(3.5)
	O.mirror = { active = true, mode = (mode == "C") and "C" or ((mode == "B") and "B" or "A"), hz = 4, errs = {}, sent = 0, step = "S10.15", tag = "mirror", nextErr = 0, nextSend = 0 }
	O.ft = { active = true, list = {}, last = S.now() }
	local t0 = S.now()
	local nextWalk = 0
	while S.now() - t0 < dur and not run.skip and not run.stop do
		if S.now() - t0 >= nextWalk then
			nextWalk = nextWalk + 3
			md("of_crowd_walk", { "S10.15", 1, 3, 1.3 })
		end
		S.waitSeconds(0.5)
	end
	run.skip = false
	O.mirror.active = false
	O.ft.active = false
	O.rec.active = false
	local m = O.mirror
	log("S10.15", "MEASURE", K("what", "lounge_summary", "mirror_mode", mode, "mirror_err_p95_m", percentile(m.errs, 0.95), "mirror_err_max_m", percentile(m.errs, 1.0),
		"frame_ms_avg", avg(O.ft.list), "frame_ms_p95", percentile(O.ft.list, 0.95), "conversations_started", O.conv and O.conv.started or 0,
		"criteria", "same_as_S10.4_and_S10.7_inside_the_lounge_seats_work (ratings are tester notes)"))
	say(15, "time is up. Rate the lounge with characters 1-5. Leaving the lounge now.")
	removeActors("S10.15", "all")
	S.waitSeconds(1)
	leaveLounge("S10.15")
end }

------------------------------------------------------------------------------
-- cleanup, run control, registration
------------------------------------------------------------------------------
local function cleanupAll(reason)
	log("S10.JAN", "INFO", K("what", "cleanup_requested", "reason", reason, "inLounge", O.inLounge, "loungeKept", O.loungeKept))
	O.s1.active = false
	O.rec.active = false
	O.mirror.active = false
	O.ft.active = false
	md("of_s1", 0)
	md("of_s2", 0)
	md("of_s6", { "S10.JAN", 0, "" })
	md("of_remove", { "S10.JAN", "all" })
	if O.inLounge then
		md("of_lounge_leave", { "S10.JAN" })
		O.inLounge = false
	end
	md("of_lounge_remove", { "S10.JAN" })
	O.loungeKept = false
end

local ORDER = {
	onfoot1 = { 1, 3, 4, 11, 12, 14, 7, 8 },
	onfoot2 = { 2, 5, 6, 9, 13, 15 },
}

local function runBlock(block, args)
	if args.cleanup == "1" then
		if O.run then O.run.stop = true end
		cleanupAll("block_argument")
		S.notify(block .. ": cleanup requested: test characters and lounges are being removed (see the log).")
		return
	end
	if args.stop == "1" then
		if O.run then O.run.stop = true end
		S.notify(block .. ": stop requested.")
		return
	end
	if args.next == "1" then
		if O.run then
			O.run.skip = true
			log("S10.CTL", "INFO", K("what", "next_requested", "block", O.run.block))
		else
			S.notify(block .. ": nothing is running.")
		end
		return
	end
	if O.run and args.force ~= "1" then
		S.notify(block .. ": " .. O.run.block .. " is still running. Use 'next=1' to skip its current wait, 'stop=1' to abort it.")
		log("S10.CTL", "FAIL", K("what", "already_running", "running", O.run.block, "requested", block))
		return
	end
	local steps = {}
	if args.step then
		for token in tostring(args.step):gmatch("[^,]+") do
			local id = tonumber(token)
			if id and STEPS[id] then steps[#steps + 1] = id
			else
				log("S10.CTL", "FAIL", K("what", "unknown_step", "step", token, "known", "1,2,3,4,5,6,7,8,9,11,12,13,14,15"))
			end
		end
	else
		steps = ORDER[block]
	end
	if #steps == 0 then
		S.notify(block .. ": no valid step. Steps: 1,2,3,4,5,6,7,8,9,11,12,13,14,15")
		return
	end
	local run = { block = block, skip = false, stop = false, args = args }
	O.run = run
	log("S10.CTL", "INFO", K("what", "run_start", "block", block, "steps", table.concat(steps, ",")))
	S.startRoutine(block, function()
		for _, id in ipairs(steps) do
			if run.stop then break end
			local def = STEPS[id]
			log("S10." .. id, "INFO", K("what", "step_begin", "block", block, "title", def.title))
			local ok, err = pcall(def.fn, args, run)
			if not ok then
				log("S10." .. id, "FAIL", K("what", "step_error", "err", err))
			end
			log("S10." .. id, "INFO", K("what", "step_end_marker", "block", block))
			S.waitSeconds(1)
		end
		log("S10.CTL", "INFO", K("what", "run_done", "block", block, "stopped", run.stop))
		if O.run == run then O.run = nil end
		S.notify(block .. ": finished" .. (run.stop and " (stopped)" or "") .. ". If anything is left over: '/x4mpspike " .. block .. " cleanup=1'.")
	end)
end

S.register("onfoot1", function(args) runBlock("onfoot1", args) end,
	"S10 on foot, part 1: S10.1 state, .3 actor, .4 movement modes, .11 lounge, .12 enter/leave, .14 lounge saves, .7 conversation, .8 cost/saves (args: step=N, next=1, stop=1, cleanup=1, dur=, phase=)")
S.register("onfoot2", function(args) runBlock("onfoot2", args) end,
	"S10 on foot, part 2: S10.2 room keys, .5 facing/emotes, .6 transitions, .9 interiors, .13 lounge slots, .15 lounge with actors (args as onfoot1)")

log("S10.JAN", "INFO", K("what", "lua_loaded", "file", "x4mp_spike_onfoot", "markers", "actor_name_prefix_Spike_title_X4MP_spike_entity_vars_x4mp_spike_and_x4mp_tag_md_lists_X4MP_OF_Actors_and_X4MP_OF_Lounges",
	"janitor", "md_cues_OF_Janitor_and_OF_JanitorLounge_5s_and_6s_after_every_game_load_log_prefix_S10.JAN", "timer", S.timerKind))
