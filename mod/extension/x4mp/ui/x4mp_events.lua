-- x4mp_events.lua : Lua half of the event capture ring (M5-01; native features/events, MD half md/x4mp_events.xml).
--
-- The MD file formats one compact line per occurrence and raises it as the Lua event "x4mp.event". This file's only job is to
-- hand that line to native, unchanged, and to keep counters a tester (and tests/lua/test_events.lua) can read:
--
--   MD    raise_lua_event "x4mp.event" "<line>"
--   here  RegisterEvent("x4mp.event") -> B.send("event", { data = <line> })
--   native features/events parses, dedupes, bounds and counts it
--
-- The line is never parsed, trimmed or escaped here: the MD grammar is positional and a Lua-side transform would only be a
-- second place to get it wrong. Nothing is queued either - an event that happens at the start menu is not an event in a game,
-- and B.send already drops it when the native API is absent.
--
-- Global X4MPEvents = { received, sends, skipped, bad } is the counter table (same shape as X4MPCapture).

local B = X4MPBridge
local O = { received = 0, sends = 0, skipped = 0, bad = 0 }
X4MPEvents = O

local function log(msg)
	pcall(DebugError, "[X4MP] events: " .. tostring(msg))
end

RegisterEvent("x4mp.event", function(_, param)
	O.received = O.received + 1
	if type(param) ~= "string" or param == "" then
		O.bad = O.bad + 1
		return
	end
	if not B.getApi() then
		O.skipped = O.skipped + 1
		return
	end
	if B.send("event", { data = param }) then
		O.sends = O.sends + 1
	else
		O.skipped = O.skipped + 1
	end
end)

log("event capture armed (MD half md/x4mp_events.xml)")
