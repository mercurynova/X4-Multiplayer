-- x4mp_damage.lua : Lua half of the damage relay (M15; native features/damage).
--
-- native -> Lua  x4mp.damage_probe {"v":1,"rows":[[component,seq],...]}
-- Lua -> native  x4mp.damage_state {"v":1,"rows":[[component,seq,hull,shield],...]}
--
-- The client's copy of an NPC is a real game object with the game's own hull (MD never dresses an NPC ghost, so no min-hull floor
-- applies to it), and the player's weapons can hit it. Neither HullDamaged (no amount at all) nor ShieldDamaged (raw units) tells
-- us a percentage, though, so native asks for the ships it was just told about and this file reads them with GetComponentData.
-- -1 means "not readable" (the object left the world), which native treats as no reading rather than as damage.
--
-- This runs on the Lua tick after the raise, i.e. within a frame or two of the hit; native batches the answers and reports at
-- most 4 times a second (events.fbs: HitReport is "sent <= 4 Hz").

local B = rawget(_G, "X4MPBridge")
if type(B) ~= "table" then
  pcall(DebugError, "[X4MP] damage: the bridge is missing")
  return
end

local O = { probes = 0, rows = 0, bad = 0, missing = 0 }
X4MPDamage = O

local function log(msg)
  pcall(DebugError, "[X4MP] damage: " .. tostring(msg))
end

local function luaId(idString)
  local ok, v = pcall(ConvertStringToLuaID, tostring(idString))
  if ok then return v end
  return nil
end

-- Percent of maximum, or -1. The field names are the ones docs/x4-api-notes.md records for GetComponentData.
local function percent(id, field)
  local ok, v = pcall(GetComponentData, id, field)
  if ok and type(v) == "number" then return v end
  return -1
end

B.on("damage_probe", function(p)
  if type(p) ~= "table" or type(p.rows) ~= "table" then
    O.bad = O.bad + 1
    return
  end

  local out = {}
  for _, row in ipairs(p.rows) do
    local component = row and tostring(row[1]) or nil
    local seq = tonumber(row and row[2]) or 0
    if component and component ~= "" then
      local id = luaId(component)
      if id == nil then
        O.missing = O.missing + 1
      else
        local hull = percent(id, "hullpercent")
        local shield = percent(id, "shieldpercent")
        if hull < 0 and shield < 0 then
          O.missing = O.missing + 1
        end
        out[#out + 1] = { component, seq, hull, shield }
        O.rows = O.rows + 1
      end
    end
  end

  O.probes = O.probes + 1
  if #out == 0 then return end
  B.send("damage_state", { rows = out })
end)

pcall(DebugError, "[X4MP] damage: ready (M15)")
