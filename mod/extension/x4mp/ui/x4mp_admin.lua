-- x4mp_admin: the in-game admin bridge (m3-plan.md Q6, docs/protocol.md 9.x).
--
-- Lua -> native : B.send("admin", {v=1, verb="kick"|"ban", player=<id>, reason="..", minutes=<n>})
--                 minutes is only for ban; absent or 0 means permanent.
-- native -> Lua : x4mp.admin_result {"v":1,"request":N,"ok":true|false,"message":".."}
--
-- The native feature sends an AdminCommand on the Control lane and the server (AdminModule) answers with AdminResult. Only a
-- node whose handshake carried the Admin role may send it; anything the bridge does not implement (broadcast, pause, team
-- commands, ...) is refused with a message that names the web admin API instead.
--
-- This file deliberately offers no UI of its own: it exposes X4MPAdmin.kick / X4MPAdmin.ban for the action bar, the chat
-- commands and the console, and reports every answer as a notification.

if X4MPAdmin and X4MPAdmin.loaded then
  return
end

local B = rawget(_G, "X4MPBridge")
if not B then
  pcall(DebugError, "[X4MP] admin: X4MPBridge is missing, the in-game admin bridge is off")
  return
end

local X4MPAdmin = {}
X4MPAdmin.loaded = true
X4MPAdmin.last = nil

local function log(msg)
  pcall(DebugError, "[X4MP] admin: " .. tostring(msg))
end

--- Sends one request. Returns false when the payload cannot be built (no verb or no player id).
local function request(fields)
  if not fields or not fields.verb then
    return false
  end
  if not fields.player or fields.player < 1 or fields.player > 65535 then
    log("a positive player id is required")
    return false
  end
  fields.v = 1
  B.send("admin", fields)
  return true
end

--- Kicks the player with the numeric session id `player`. The server answers with x4mp.admin_result.
function X4MPAdmin.kick(player, reason)
  return request({ verb = "kick", player = tonumber(player), reason = tostring(reason or "") })
end

--- Bans the player with the numeric session id `player` for `minutes` (0 or nil = permanent) and closes the session.
function X4MPAdmin.ban(player, minutes, reason)
  return request({ verb = "ban", player = tonumber(player), minutes = tonumber(minutes) or 0, reason = tostring(reason or "") })
end

B.on("admin_result", function(p)
  if type(p) ~= "table" then
    return
  end
  X4MPAdmin.last = p
  local text = tostring(p.message or "")
  if p.ok then
    log("request " .. tostring(p.request) .. " ok: " .. text)
  else
    log("request " .. tostring(p.request) .. " refused: " .. text)
  end
end)

_G.X4MPAdmin = X4MPAdmin
