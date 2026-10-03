-- x4mp_spike_hq.lua : spike v2 blocks hq1..hq8 and hq3d (task M2-004, spike S12: per-team HQ and research, ADR-048).
--
--   hq1   S12.1  read research state (Lua + MD): research wares with HasResearched / IsKnownItem, CanResearch, GetHQs, research modules
--   hq2   S12.2  grant / revoke research_teleportation, research_mod_weapon_mk1, research_module_dock through MD, with prompts for
--                the user's workbench / scan / cancel checks; leaves research_teleportation flipped as a save/reload marker, which MD
--                verifies 15 s after the next load and restores. "hq2 restore=1" restores without reloading. CHANGES the game (reverted).
--   hq3   S12.3 a-c  HQ ownership on save A: owner -> MP Team 1, set_faction_headquarters, owner back to player (MD, refuses at the HQ)
--   hq3d  S12.3 d    save B: spawn a team-2 HQ and (default) a player-owned HQ; "hq3d noplayer=1" skips the player one. CHANGES the game.
--   hq4   S12.4  save B: re-own the team-2 HQ to "player" locally (SetComponentOwner), user opens the research menu (do NOT press
--                Start), then back to team 2 after 90 s
--   hq5   S12.5  save B: StartResearch on a research module of the team-2 HQ, log after 60 s, then clear it
--   hq6   S12.6  blueprint read + add_blueprints (cannot be undone) (MD)
--   hq7   S12.7  licences on MP Team 1: add, read, remove (MD)
--   hq8   S12.8  encyclopedia reveal: research_teleportation (done) and research_teleportation_range_01 (not done) shown in the research
--                menu / encyclopedia for 90 s, then reverted
--   hq_read      internal helper (Lua view of the research state; the MD side and the blocks call it)
--   hq4_go, hq5_go   internal, started by the MD cues Hq4_Run / Hq5_Run after they hand over the HQ (event x4mp_spike.hq_obj)
--
-- MD side (state changes, same-frame readbacks, persistence markers, load-time recovery): md/x4mp_spike_hq.xml. Log step: S12.
--
-- Sources (x4-unpacked/ui/addons/): research FFI GetHQs / GetNumHQs / GetNumWares / GetWares(tags, research=true, ...) /
-- HasResearched / StartResearch / ClearProductionItems / GetNumResearchModules / GetResearchModules and the Lua globals
-- IsKnownItem("researchables", ware), GetWareData(ware, "researchprecursors", "ismissiononly"), GetProductionModuleData(module)
-- (fields state, blueprintware, cycleprogress, remainingcycletime) all from ego_detailmonitor/menu_research.lua:8-20,86-130,271-277,
-- 418-430,707-719; C.CanResearch ego_detailmonitorhelper/helper.lua:297; CanTeleportPlayerTo ego_detailmonitor/menu_map.lua:520;
-- GetOwnerDetails menu_map.lua:750; SetComponentOwner menu_mapeditor.lua:115; GetPlayerOccupiedShipID menu_map.lua:782.

-- luacheck: globals X4MPSpike DebugError RegisterEvent IsKnownItem GetWareData GetProductionModuleData ConvertStringTo64Bit

local ffi = require("ffi")
local C = ffi.C

local S = X4MPSpike
if type(S) ~= "table" then return end
local K, log = S.K, S.log

for _, def in ipairs({
	"uint32_t GetNumHQs(const char* factionid);",
	"uint32_t GetHQs(UniverseID* result, uint32_t resultlen, const char* factionid);",
	"uint32_t GetNumWares(const char* tags, bool research, const char* licenceownerid, const char* exclusiontags);",
	"uint32_t GetWares(const char** result, uint32_t resultlen, const char* tags, bool research, const char* licenceownerid, const char* exclusiontags);",
	"bool HasResearched(const char* wareid);",
	"void StartResearch(const char* wareid, UniverseID researchmoduleid);",
	"void ClearProductionItems(UniverseID productionmoduleid);",
	"uint32_t GetNumResearchModules(UniverseID containerid);",
	"uint32_t GetResearchModules(UniverseID* result, uint32_t resultlen, UniverseID containerid);",
	"bool CanResearch(void);",
	"const char* CanTeleportPlayerTo(UniverseID controllableid, bool allowcontrolling, bool force);",
	"void SetComponentOwner(UniverseID componentid, const char* factionid);",
	"UniverseID GetPlayerOccupiedShipID(void);",
}) do
	pcall(ffi.cdef, def) -- "redefine" when vanilla declared it first is expected and harmless
end

S.hq = S.hq or {}
local H = S.hq

local TELE = "research_teleportation"
local TELE2 = "research_teleportation_range_01"
local MODW = "research_mod_weapon_mk1"
local MODD = "research_module_dock"

------------------------------------------------------------------------------
-- helpers
------------------------------------------------------------------------------
local function toid(x)
	if x == nil then return 0 end
	local ok, r = pcall(function() return ConvertStringTo64Bit(tostring(x)) end)
	if ok and r then return r end
	return 0
end

local function num(id) return string.format("%d", tonumber(id) or 0) end

local function hasResearched(w)
	local ok, r = pcall(function() return C.HasResearched(w) end)
	if ok then return r and true or false end
	return nil
end

local function isKnown(w)
	local ok, r = pcall(function() return IsKnownItem("researchables", w) end)
	if ok then return r and true or false end
	return nil
end

local function ownerOf(id)
	local ok, r = pcall(function() return ffi.string(C.GetOwnerDetails(id).factionID) end)
	if ok then return r end
	return "err"
end

local function hqIds(factionid)
	local ids = {}
	pcall(function()
		local n = tonumber(C.GetNumHQs(factionid))
		if n > 0 then
			local buf = ffi.new("UniverseID[?]", n)
			n = tonumber(C.GetHQs(buf, n, factionid))
			for i = 0, n - 1 do ids[#ids + 1] = buf[i] end
		end
	end)
	return ids
end

local function researchWares()
	local list = {}
	pcall(function()
		local n = tonumber(C.GetNumWares("", true, "", ""))
		local buf = ffi.new("const char*[?]", math.max(n, 1))
		n = tonumber(C.GetWares(buf, n, "", true, "", ""))
		for i = 0, n - 1 do list[#list + 1] = ffi.string(buf[i]) end
	end)
	table.sort(list)
	return list
end

local function researchModules(hq)
	local mods = {}
	pcall(function()
		local n = tonumber(C.GetNumResearchModules(hq))
		if n > 0 then
			local buf = ffi.new("UniverseID[?]", n)
			n = tonumber(C.GetResearchModules(buf, n, hq))
			for i = 0, n - 1 do mods[#mods + 1] = buf[i] end
		end
	end)
	return mods
end

local function logModule(tag, hq, mod)
	local ok, d = pcall(function() return GetProductionModuleData(toid(mod)) end)
	if ok and type(d) == "table" then
		log("S12", "INFO", K("what", "lua_research_module", "tag", tag, "hq", num(hq), "module", num(mod), "state", d.state,
			"blueprintware", d.blueprintware, "cycleprogress", d.cycleprogress, "remainingcycletime", d.remainingcycletime))
	else
		log("S12", "INFO", K("what", "lua_research_module", "tag", tag, "hq", num(hq), "module", num(mod), "ok", ok, "err", d))
	end
end

-- Lua view of the research state; `ware` (optional) adds one ware's details
local function hqRead(tag, ware)
	local okCan, can = pcall(function() return C.CanResearch() end)
	log("S12", "INFO", K("what", "lua_can_research", "tag", tag, "ok", okCan, "value", can))
	local players = hqIds("player")
	local entries = {}
	for _, id in ipairs(players) do entries[#entries + 1] = num(id) .. "(" .. ownerOf(id) .. ")" end
	log("S12", "INFO", K("what", "lua_get_hqs", "tag", tag, "player_count", #players, "player_hqs", table.concat(entries, ","),
		"team1_count", #hqIds("x4mp_team_1"), "team2_count", #hqIds("x4mp_team_2")))
	for _, id in ipairs(players) do
		local mods = researchModules(id)
		log("S12", "INFO", K("what", "lua_research_modules", "tag", tag, "hq", num(id), "count", #mods))
		for _, m in ipairs(mods) do logModule(tag, id, m) end
	end
	if ware and ware ~= "" then
		local okT, reason = pcall(function()
			local ship = C.GetPlayerOccupiedShipID()
			if ship == 0 then return "no_ship" end
			return ffi.string(C.CanTeleportPlayerTo(ship, false, false))
		end)
		log("S12", "INFO", K("what", "lua_ware_state", "tag", tag, "ware", ware, "HasResearched", hasResearched(ware),
			"IsKnownItem_researchables", isKnown(ware), "CanTeleportPlayerTo_own_ship", okT and reason or ("err:" .. tostring(reason))))
	end
end

S.register("hq_read", function(args)
	hqRead(args.tag or "read", args.ware)
end, "S12 helper: Lua view of the research state")

------------------------------------------------------------------------------
-- hq1 (S12.1)
------------------------------------------------------------------------------
S.register("hq1", function()
	S.startRoutine("hq1", function()
		S.toMD("hq1", "go")
		S.waitSeconds(1)
		local wares = researchWares()
		local done, known, knownNotDone = {}, {}, {}
		for _, w in ipairs(wares) do
			local r, k = hasResearched(w), isKnown(w)
			if r then done[#done + 1] = w end
			if k then known[#known + 1] = w end
			if k and not r then knownNotDone[#knownNotDone + 1] = w end
		end
		log("S12", #wares > 0 and "PASS" or "FAIL", K("what", "lua_research_wares", "total", #wares, "completed", #done, "known_in_encyclopedia", #known,
			"known_not_completed", #knownNotDone))
		log("S12", "INFO", K("what", "lua_research_completed", "list", table.concat(done, ",")))
		log("S12", "INFO", K("what", "lua_research_known_not_completed", "list", table.concat(knownNotDone, ",")))
		hqRead("hq1", TELE)
		S.notify("hq1: done. Open the research menu at your HQ now and take a screenshot: does its list of completed research match the log (" .. #done .. " completed)?")
	end)
end, "S12.1 read research state (read only)")

------------------------------------------------------------------------------
-- hq2 (S12.2)
------------------------------------------------------------------------------
local function setResearch(ware, want)
	S.toMD(want and "hq_research_add" or "hq_research_remove", ware)
end

-- grant (if not already researched), wait, log, revert; returns the original state
local function flipAndBack(tag, ware)
	local orig = hasResearched(ware) and true or false
	hqRead(tag .. "_original", ware)
	setResearch(ware, not orig)
	S.waitSeconds(2)
	hqRead(tag .. "_flipped", ware)
	setResearch(ware, orig)
	S.waitSeconds(2)
	hqRead(tag .. "_back_to_original", ware)
	return orig
end

S.register("hq2", function(args)
	if args.restore == "1" then
		S.toMD("hq2_restore", "go")
		S.notify("hq2: restore requested (see the log line hq2_marker_restored).")
		return
	end
	S.startRoutine("hq2", function()
		S.toMD("hq_log", "on")
		S.notify("hq2: started (CHANGES the game: grants and revokes research, everything is reverted). Takes about 8 minutes, follow the prompts.")
		-- 1. teleportation, both directions
		local origT = flipAndBack("hq2_teleportation", TELE)
		S.notify("hq2: research_teleportation granted and revoked (both directions logged). Next: weapon mod test.")
		-- 2. weapon mod mk1: user checks crafting
		local origW = hasResearched(MODW) and true or false
		if not origW then setResearch(MODW, true) end
		S.waitSeconds(2)
		hqRead("hq2_weapon_mod_granted", MODW)
		S.notify("hq2: research_mod_weapon_mk1 is GRANTED for 120 s (CHANGES the game, reverted afterwards). Go to a workbench / ship mod station: can a weapon mod MK1 be crafted? Note it.")
		S.waitSeconds(120)
		if not origW then setResearch(MODW, false) end
		S.waitSeconds(2)
		hqRead("hq2_weapon_mod_reverted", MODW)
		-- 3. module dock research: user checks data-leak scans
		local origD = hasResearched(MODD) and true or false
		if not origD then setResearch(MODD, true) end
		S.waitSeconds(2)
		hqRead("hq2_module_dock_granted", MODD)
		S.notify("hq2: research_module_dock is GRANTED for 120 s (reverted afterwards). If you can test it quickly: do data-leak scans now drop module blueprints? Note it.")
		S.waitSeconds(120)
		if not origD then setResearch(MODD, false) end
		S.waitSeconds(2)
		hqRead("hq2_module_dock_reverted", MODD)
		-- 4. user: start and cancel a research
		S.notify("hq2: now start ANY research in the research menu at your HQ, then cancel it. Note whether the resources come back. Next step in 120 s.")
		S.waitSeconds(120)
		-- 5. persistence marker: leave research_teleportation flipped, verify after save + load
		S.toMD("hq2_mark_orig", origT and "1" or "0")
		setResearch(TELE, not origT)
		S.waitSeconds(2)
		S.toMD("hq2_mark", TELE)
		S.toMD("hq_log", "off")
		hqRead("hq2_marker_set", TELE)
		S.notify("hq2: now SAVE to a NEW slot, then LOAD that save. research_teleportation is left flipped on purpose; hq2 checks it 15 s after the load and restores it. (Without a reload: type '/x4mpspike hq2 restore=1'.)")
	end)
end, "S12.2 grant / revoke research (changes the game, reverted; save+reload marker)")

------------------------------------------------------------------------------
-- hq3, hq3d: MD blocks
------------------------------------------------------------------------------
S.register("hq3", function(args)
	if args.restore == "1" then
		S.toMD("hq3_restore", "go")
		return
	end
	S.notify("hq3: starting (CHANGES the game: the HQ's owner is switched to MP Team 1 and back automatically; you must not be at the HQ).")
	S.toMD("hq3", "go")
end, "S12.3 HQ ownership a-c (changes the game, auto-reverted); 'hq3 restore=1' recovers")

S.register("hq3d", function(args)
	S.notify("hq3d: spawning test headquarters (CHANGES the game, save B only: a Team 2 HQ and" .. (args.noplayer == "1" and " no" or " one") .. " player-owned HQ, 12 km from your ship).")
	S.toMD("hq3d", args.noplayer == "1" and "noplayer" or "player")
end, "S12.3d spawn team-2 HQ and a player HQ on save B (changes the game)")

------------------------------------------------------------------------------
-- hq4, hq5: Lua part (the MD cues hand over the HQ through x4mp_spike.hq_obj)
------------------------------------------------------------------------------
if not H.registered then
	H.registered = true
	pcall(RegisterEvent, "x4mp_spike.hq_obj", function(_, param) H.pendingObj = param end)
end

S.registerMD("hq4", "S12.4 locally re-own the team-2 HQ to player, open the research menu (MD hands the HQ over)")
S.registerMD("hq5", "S12.5 StartResearch on the team-2 HQ research module (MD hands the HQ over)")

S.register("hq4_go", function()
	local id = toid(H.pendingObj)
	if id == 0 then
		log("S12", "FAIL", K("what", "hq4_no_object"))
		return
	end
	S.startRoutine("hq4", function()
		local before = ownerOf(id)
		local ok, err = pcall(function() C.SetComponentOwner(id, "player") end)
		log("S12", ok and "PASS" or "FAIL", K("what", "hq4_SetComponentOwner_player", "hq", num(id), "ok", ok, "err", ok and "" or err,
			"owner_before", before, "owner_after", ownerOf(id)))
		hqRead("hq4_owned_by_player")
		S.notify("hq4: the team-2 HQ is now locally owned by YOU (CHANGES the game, reverted in 90 s). Open the research menu now and look: does it open, is there a research module, is the start button there? DO NOT press Start. Take a screenshot.")
		S.waitSeconds(80)
		S.notify("hq4: close the research menu now (HQ goes back to Team 2 in 10 s).")
		S.waitSeconds(10)
		local ok2, err2 = pcall(function() C.SetComponentOwner(id, "x4mp_team_2") end)
		log("S12", ok2 and "PASS" or "FAIL", K("what", "hq4_SetComponentOwner_team2", "hq", num(id), "ok", ok2, "err", ok2 and "" or err2,
			"owner_after", ownerOf(id)))
		S.waitSeconds(1)
		S.toMD("hq4_done", "go")
		hqRead("hq4_back_to_team2")
		S.notify("hq4: done (HQ is Team 2's again). Any stray notification or error since the first re-own is worth a note.")
	end)
end, "internal: S12.4 local re-own")

local function pickResearch()
	for _, w in ipairs(researchWares()) do
		if not hasResearched(w) and isKnown(w) then
			local okM, missionOnly = pcall(function() return GetWareData(w, "ismissiononly") end)
			local okP, pre = pcall(function() return GetWareData(w, "researchprecursors") end)
			local ready = okP and type(pre) == "table"
			if ready then
				for _, p in ipairs(pre) do
					if not hasResearched(p) then ready = false end
				end
			end
			if ready and not (okM and missionOnly) then return w end
		end
	end
	return nil
end

S.register("hq5_go", function()
	local id = toid(H.pendingObj)
	if id == 0 then
		log("S12", "FAIL", K("what", "hq5_no_object"))
		return
	end
	S.startRoutine("hq5", function()
		local mods = researchModules(id)
		log("S12", "INFO", K("what", "hq5_research_modules_of_team2_hq", "hq", num(id), "owner", ownerOf(id), "count", #mods))
		local ware = pickResearch()
		if #mods == 0 or not ware then
			log("S12", "INFO", K("what", "hq5_nothing_to_start", "modules", #mods, "ware", ware, "note", #mods == 0 and "no_research_module_on_a_team_owned_hq" or "no_startable_research"))
			S.notify("hq5: nothing to start (" .. (#mods == 0 and "the team-2 HQ has no research module" or "no startable research ware") .. "). That is a result; see the log. done.")
			return
		end
		logModule("hq5_before", id, mods[1])
		local ok, err = pcall(function() C.StartResearch(ware, mods[1]) end)
		log("S12", ok and "PASS" or "FAIL", K("what", "hq5_StartResearch_called", "ware", ware, "module", num(mods[1]), "ok", ok, "err", ok and "" or err))
		S.toMD("hq5_log", "right_after_start")
		S.notify("hq5: started research '" .. ware .. "' on the team-2 HQ module (CHANGES the game; cleared again afterwards). Wait 60 s.")
		S.waitSeconds(60)
		logModule("hq5_after_60s", id, mods[1])
		S.toMD("hq5_log", "after_60s")
		local ok2, err2 = pcall(function() C.ClearProductionItems(mods[1]) end)
		log("S12", ok2 and "INFO" or "FAIL", K("what", "hq5_cleared", "ok", ok2, "err", ok2 and "" or err2))
		S.waitSeconds(1)
		logModule("hq5_after_clear", id, mods[1])
		S.notify("hq5: done.")
	end)
end, "internal: S12.5 StartResearch on the team-2 HQ")

------------------------------------------------------------------------------
-- hq6, hq7: MD blocks
------------------------------------------------------------------------------
S.register("hq6", function()
	S.notify("hq6: running (CHANGES the game: adds one module blueprint to your library, cannot be undone; throwaway save only).")
	S.toMD("hq6", "go")
end, "S12.6 blueprints: read + add_blueprints (cannot be undone)")

S.register("hq7", function()
	S.notify("hq7: running (adds licences to MP Team 1 and removes them again).")
	S.toMD("hq7", "go")
end, "S12.7 licences on a team faction (added and removed again)")

------------------------------------------------------------------------------
-- hq8 (S12.8)
------------------------------------------------------------------------------
S.register("hq8", function()
	S.startRoutine("hq8", function()
		local origRes = hasResearched(TELE) and true or false
		local knownRoot, knownSucc = isKnown(TELE), isKnown(TELE2)
		log("S12", "INFO", K("what", "hq8_before", "root", TELE, "root_researched", origRes, "root_known", knownRoot,
			"successor", TELE2, "successor_researched", hasResearched(TELE2), "successor_known", knownSucc))
		S.toMD("hq_log", "on")
		if not origRes then setResearch(TELE, true) end
		S.toMD("hq_enc_add", TELE)
		S.toMD("hq_enc_add", TELE2)
		S.waitSeconds(2)
		log("S12", (isKnown(TELE) and isKnown(TELE2)) and "PASS" or "FAIL", K("what", "hq8_after_reveal", "root_researched", hasResearched(TELE),
			"root_known", isKnown(TELE), "successor_researched", hasResearched(TELE2), "successor_known", isKnown(TELE2)))
		S.notify("hq8: revealed 'research_teleportation' (shown as done) and 'research_teleportation_range_01' (not done) (CHANGES the game, reverted in 90 s). Open the research menu (needs an HQ, e.g. the one from hq3d) or the encyclopedia and take a screenshot: do both appear, with the right done state?")
		S.waitSeconds(90)
		if not origRes then setResearch(TELE, false) end
		if not knownRoot then S.toMD("hq_enc_remove", TELE) end
		if not knownSucc then S.toMD("hq_enc_remove", TELE2) end
		S.toMD("hq_log", "off")
		S.waitSeconds(2)
		log("S12", "INFO", K("what", "hq8_reverted", "root_researched", hasResearched(TELE), "root_known", isKnown(TELE),
			"successor_known", isKnown(TELE2)))
		S.notify("hq8: done (reverted).")
	end)
end, "S12.8 encyclopedia reveal of two research entries (changes the game, reverted)")
