-- x4mp_spike_hq.lua : STUB owned by M2-004 (blocks hq1..hq8).
-- Loadable and inert: registers nothing yet. The owning task replaces this file's body and registers its blocks with
--   X4MPSpike.register("<block>", function(args) ... end, "description")      (Lua block)
--   X4MPSpike.registerMD("<block>", "description")                            (block implemented as an MD cue)
-- See mod/spikes/README.md, "Writing a block". Only the owning task edits this file.

-- luacheck: globals X4MPSpike

local S = X4MPSpike
if type(S) ~= "table" then return end

S.log("LUA", "INFO", S.K("what", "stub_loaded", "file", "x4mp_spike_hq"))
