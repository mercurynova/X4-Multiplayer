-- x4mp_statics.lua : Lua half of the client's station scan (M4-18; native features/statics, MD half md/x4mp_statics.xml).
--
-- Native cannot list the objects of a sector, MD can (find_object class="class.station" space="$Sector" multiple="true"), so the two
-- split the work: MD sends the station ids with their sector-space positions, native builds the manifest key
-- (onfoot::station_match_key) and binds the net id of the manifest row to the local component.
--
--   native -> Lua  x4mp.statics_request {"v":1,"sector":"<sector macro id>"}   (B.on, raised by statics_feature.cpp)
--   Lua -> MD      AddUITriggeredEvent("X4MP_Statics", "scan", { <sector macro id> })
--   MD -> Lua      x4mp.statics_scan "S;<sector macro>;<component>|<station macro>|<x>|<y>|<z>;..." and "E;<sector macro>;<count>"
--   Lua -> native  verb x4mp.statics_scan {"v":1,"data":"<the same string>"}
local B = X4MPBridge

local O = { sends = 0, requests = 0, skipped = 0, bad = 0 }
X4MPStatics = O

local function log(msg) pcall(DebugError, "[X4MP] statics: " .. tostring(msg)) end

-- MD -> native. A chunk is dropped when the native API is not there: the client asks again (its own retry throttle), so nothing is
-- lost and a start menu never queues a stale sector.
RegisterEvent("x4mp.statics_scan", function(_, param)
	if type(param) ~= "string" or param == "" then O.bad = O.bad + 1; return end
	if not B.getApi() then O.skipped = O.skipped + 1; return end
	if B.send("statics_scan", { data = param }) then O.sends = O.sends + 1 end
end)

-- native -> MD: one sector scan. The sector travels as a macro id because MD cannot name it the way the wire does.
B.on("statics_request", function(p)
	if type(p) ~= "table" or type(p.sector) ~= "string" or p.sector == "" then O.bad = O.bad + 1; return end
	if type(AddUITriggeredEvent) ~= "function" then O.skipped = O.skipped + 1; return end
	if pcall(AddUITriggeredEvent, "X4MP_Statics", "scan", { p.sector }) then O.requests = O.requests + 1
	else log("scan trigger failed for " .. p.sector) end
end)
