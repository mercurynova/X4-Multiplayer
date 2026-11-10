-- x4mp_map.lua : the strategic map screen (T1.6).
--
-- The authority polls its running universe and sends KnownSectors to the server; the server keeps the map and fans out:
--   GalaxyMetadata  S->N  the checkpoint's map, used only until a live map arrives (owners are empty: it has no faction id)
--   MapSnapshot     S->N  the full live map
--   MapDelta        S->N  sector/link upserts and removed sectors
-- The native half (features/map) keeps the merged map and publishes topic x4mp.map
-- {"v":1,"version":N,"source":"live|checkpoint|none","sectors":[{index,macro,cluster,name,owner,x,y,z}],
--  "links":[{from,to,kind}]} on every change, and answers verb x4mp.map_request {"v":1} with the map in hand.
--
-- This module only shows and asks: it has no authority of its own, so two nodes never disagree about who owns a sector.
-- The screen is reachable only inside a session: the strategic map belongs to the shared universe, not to the main menu.

-- luacheck: globals X4MPBridge X4MPScreens X4MPMap

local B = X4MPBridge

local V = { snapshot = nil, requested = false }
X4MPMap = V

local PAGE = 16
local SOURCE_TEXT = { live = 643, checkpoint = 644, none = 645 }

local function T(id, ...)
	if type(ReadText) ~= "function" then return tostring(id) end
	local text = ReadText(92000, id)
	if text == nil or text == "" then return tostring(id) end
	if select("#", ...) > 0 then
		local ok, out = pcall(string.format, text, ...)
		if ok then return out end
	end
	return text
end

local function refresh()
	local M = rawget(_G, "X4MPScreens")
	if type(M) == "table" and type(M.render) == "function" then M.render() end
end

local function sourceName(source)
	return T(SOURCE_TEXT[tostring(source or "")] or 645)
end

local function ownerName(owner)
	owner = tostring(owner or "")
	if owner == "" then return T(649) end
	return owner
end

--- V.request(force): ask the native half for the map in hand. The first build of the screen asks once; Refresh forces.
function V.request(force)
	if type(B) ~= "table" or type(B.send) ~= "function" then return false end
	if V.requested and force ~= true then return false end
	V.requested = true
	local ok = B.send("map_request", {})
	if not ok then V.requested = false end
	return ok
end

--- V.onMap(payload): the native half published a new map. While the screen is in front, redraw it.
function V.onMap(payload)
	if type(payload) ~= "table" then return end
	V.snapshot = payload
	V.requested = false
	local M = rawget(_G, "X4MPScreens")
	if type(M) == "table" and type(M.render) == "function" and type(M.state) == "table" and M.state.screen == "map" then
		M.render()
	end
end

--- V.screenRows(rows, st): the rows of the strategic map. st.map keeps the page and the owner filter between draws.
function V.screenRows(rows, st)
	local ui = st and st.map
	if type(ui) ~= "table" then
		ui = { page = 1, filter = 0 }
		if st then st.map = ui end
	end
	local function add(r) rows[#rows + 1] = r end
	local function line(t, tone) add({ type = "text", text = t, tone = tone or "normal" }) end
	local function button(id, t, active, onClick)
		add({ type = "button", id = id, text = t, active = active ~= false, onClick = onClick })
	end

	local s = V.snapshot
	if type(s) ~= "table" then
		line(T(646), "warning")
		V.request()
		button("map_refresh", T(652), true, function() V.request(true) end)
		return
	end

	local sectors = type(s.sectors) == "table" and s.sectors or {}
	local links = type(s.links) == "table" and s.links or {}
	line(T(641, #sectors, #links))
	line(T(642, sourceName(s.source), tonumber(s.version) or 0), "inactive")

	if s.source == "none" or #sectors == 0 then
		line(T(646), "warning")
		button("map_refresh", T(652), true, function() V.request(true) end)
		return
	end

	-- the owners the map actually carries, once each, sorted; "" is the unowned filter and sorts first
	local owners, seen = {}, {}
	for _, row in ipairs(sectors) do
		local owner = tostring(row.owner or "")
		if not seen[owner] then
			seen[owner] = true
			owners[#owners + 1] = owner
		end
	end
	table.sort(owners)
	if type(ui.filter) ~= "number" or ui.filter < 0 or ui.filter > #owners then ui.filter = 0 end
	local filterLabel = ui.filter == 0 and T(648) or ownerName(owners[ui.filter])
	line(T(647, filterLabel), "inactive")
	button("map_filter", T(651), #owners > 0, function()
		ui.filter = (ui.filter + 1) % (#owners + 1)
		ui.page = 1
		refresh()
	end)

	local list = {}
	for _, row in ipairs(sectors) do
		if ui.filter == 0 or tostring(row.owner or "") == owners[ui.filter] then list[#list + 1] = row end
	end

	local pages = math.max(1, math.ceil(#list / PAGE))
	if type(ui.page) ~= "number" or ui.page < 1 then ui.page = 1 end
	if ui.page > pages then ui.page = pages end
	local first = (ui.page - 1) * PAGE + 1
	local last = math.min(#list, first + PAGE - 1)
	if #list == 0 then
		line(T(657), "inactive")
	else
		for i = first, last do
			local row = list[i]
			local name = tostring(row.name or "")
			if name == "" then name = tostring(row.macro or "") end
			if name == "" then name = "#" .. tostring(row.index or 0) end
			line(T(650, tonumber(row.index) or 0, name, ownerName(row.owner)))
		end
		if pages > 1 then line(T(655, ui.page, pages), "inactive") end
	end

	button("map_prev", T(653), ui.page > 1, function()
		ui.page = ui.page - 1
		refresh()
	end)
	button("map_next", T(654), ui.page < pages, function()
		ui.page = ui.page + 1
		refresh()
	end)
	button("map_refresh", T(652), true, function() V.request(true) end)
end

function V.install()
	if type(B) == "table" then
		B.VERBS.map_request = true
		local function topic(name)
			for _, known in ipairs(B.TOPICS or {}) do if known == name then return end end
			B.TOPICS[#B.TOPICS + 1] = name
		end
		topic("map")
		B.on("map", V.onMap)
	end
	return true
end

V.install()
