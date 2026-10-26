-- x4mp_capture.lua : Lua half of the authority's sector scan (M4-03; native features/authority, MD half md/x4mp_capture.xml).
--
-- Native cannot list the objects of a sector, MD can (find_object class="class.ship" space="$Sector" multiple="true"), so the two split
-- the work: MD sends ids, native owns the net ids, the index, the rates and every per-frame position read. Nothing here ever carries a
-- position.
--
--   native -> Lua  x4mp.capture_request {"v":1,"sector":"<sector macro id>"}   (B.on, raised by authority_flow.cpp)
--   Lua -> MD      AddUITriggeredEvent("X4MP_Capture", "scan", { <sector macro id> })
--   MD -> Lua      x4mp.capture_scan "S;<sector macro>;<component>|<ship macro>|<owner>;..." and "E;<sector macro>;<count>"
--   Lua -> native  verb x4mp.capture_scan {"v":1,"data":"<the same string>"}
local B = X4MPBridge

local O = { sends = 0, requests = 0, skipped = 0, bad = 0 }
X4MPCapture = O

local function log(msg) pcall(DebugError, "[X4MP] capture: " .. tostring(msg)) end

-- MD -> native. A scan chunk is dropped when the native API is not there: the authority asks again (10 s throttle), so nothing is lost
-- and a start menu never queues a stale sector.
RegisterEvent("x4mp.capture_scan", function(_, param)
	if type(param) ~= "string" or param == "" then
		O.bad = O.bad + 1
		return
	end
	if not B.getApi() then
		O.skipped = O.skipped + 1
		return
	end
	if B.send("capture_scan", { data = param }) then
		O.sends = O.sends + 1
	end
end)

-- native -> MD: one sector scan. The sector travels as a macro id because MD cannot name it the way the wire does.
B.on("capture_request", function(p)
	if type(p) ~= "table" or type(p.sector) ~= "string" or p.sector == "" then
		O.bad = O.bad + 1
		return
	end
	if type(AddUITriggeredEvent) ~= "function" then
		O.skipped = O.skipped + 1
		return
	end
	if pcall(AddUITriggeredEvent, "X4MP_Capture", "scan", { p.sector }) then
		O.requests = O.requests + 1
	else
		log("scan trigger failed for " .. p.sector)
	end
end)
