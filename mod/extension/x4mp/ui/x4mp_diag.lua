-- x4mp_diag.lua : Lua half of the knowledge probe (M3-23, Finding 4; native features/diag/knowledge_feature.*, MD half md/x4mp_diag.xml).
--
--   native -> Lua  x4mp.knowledge_ask {"v":1,"seq":N}     -> MD control "probe", value N (count what the player knows)
--   MD -> Lua      x4mp.md_knowledge <string>             "K;<seq>;<age>;..." (format: md/x4mp_diag.xml), forwarded unchanged as
--   Lua -> native  x4mp.knowledge_md {"v":1,"data":"<string>"}
--   chat command   "/x4mp knowledge" (x4mp_chat.lua) calls X4MPDiag.requestKnowledge(): Lua -> native x4mp.knowledge_cmd {"v":1}; native asks for a probe.
--
--   M3-28: chat command "/x4mp knowledge watch [off]" -> X4MPDiag.requestWatch(): native x4mp.knowledge_cmd {"v":1,"watch":"toggle|on|off"}; native then asks
--   every 2 s: native -> Lua x4mp.knowledge_watch {"v":1,"seq":N,"max":12} -> MD control "watch" { seq, max }; MD answers 'W;...' via x4mp.md_knowledge.
--
-- A diagnostic only; nothing here changes the game.
--
-- luacheck: globals X4MPBridge X4MPDiag DebugError RegisterEvent AddUITriggeredEvent

local B = rawget(_G, "X4MPBridge")
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] diag: x4mp_bridge.lua must load first")
	return
end

local D = { MD_SCREEN = "X4MP_Diag", asked = 0, forwarded = 0 }
X4MPDiag = D

local function log(msg) pcall(DebugError, "[X4MP] diag: " .. tostring(msg)) end

local function sendRaw(verb, payload)
	local api = B.getApi()
	if not api then return false, "no_api" end
	payload.v = 1
	local text, err = B.json.encode(payload)
	if not text then return false, err end
	local ok, perr = pcall(api.raise_event, "x4mp." .. verb, text)
	if not ok then return false, tostring(perr) end
	return true
end

RegisterEvent("x4mp.knowledge_ask", function(_, param)
	local p = B.json.decode(type(param) == "string" and param or "")
	local seq = type(p) == "table" and tonumber(p.seq) or nil
	if not seq then
		log("knowledge_ask: unreadable payload")
		return
	end
	if type(AddUITriggeredEvent) ~= "function" then
		log("AddUITriggeredEvent is missing, MD cannot be asked")
		return
	end
	D.asked = D.asked + 1
	local ok, err = pcall(AddUITriggeredEvent, D.MD_SCREEN, "probe", seq)
	if not ok then log("AddUITriggeredEvent probe failed: " .. tostring(err)) end
end)

RegisterEvent("x4mp.md_knowledge", function(_, param)
	if type(param) ~= "string" or param == "" then return end
	local ok, err = sendRaw("knowledge_md", { data = param })
	if ok then
		D.forwarded = D.forwarded + 1
	else
		log("could not forward a knowledge answer: " .. tostring(err))
	end
end)

--- the chat command "/x4mp knowledge": native asks MD and logs the line
function D.requestKnowledge()
	local ok, err = sendRaw("knowledge_cmd", {})
	if not ok then log("knowledge command failed: " .. tostring(err)) end
	return ok
end

--- M3-28 "/x4mp knowledge watch [on|off]": mode "toggle" | "on" | "off"; native owns the 2 s timer and logs the lines
function D.requestWatch(mode)
	if mode ~= "on" and mode ~= "off" then mode = "toggle" end
	local ok, err = sendRaw("knowledge_cmd", { watch = mode })
	if not ok then log("knowledge watch command failed: " .. tostring(err)) end
	return ok
end

--- native -> MD: one watch question {"v":1,"seq":N,"max":12} -> control "watch" { seq, max } (MD answers 'W;...' through x4mp.md_knowledge)
RegisterEvent("x4mp.knowledge_watch", function(_, param)
	local p = B.json.decode(type(param) == "string" and param or "")
	local seq = type(p) == "table" and tonumber(p.seq) or nil
	if not seq then return end
	if type(AddUITriggeredEvent) ~= "function" then return end
	local ok, err = pcall(AddUITriggeredEvent, D.MD_SCREEN, "watch", { seq, tonumber(p.max) or 12 })
	if not ok then log("AddUITriggeredEvent watch failed: " .. tostring(err)) end
end)

log("loaded")
