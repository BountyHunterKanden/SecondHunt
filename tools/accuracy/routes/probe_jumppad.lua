-- Jump pad control lock (question 2026-10-07: "are you certain that there is a delay and
-- that you cant influence the arc on the way up with the dpad in vanilla"). From landing.State (Celestial Gateway):
-- pokes Celestial Gateway's jump pad 14 (entity data at 0x022A0AA0; the CJumpPad at 0x022E2C80 caches what it fires
-- at load: beam * speed fx at +0xD8, control lock u16 at +0xD6 in 30 Hz ticks, world volume (cylinder) center at
-- +0x134), puts Samus on it (pos/prev pos, speed 0) at frame 10, then holds
-- a d-pad key from frame 12 (one step: the pad fires on the first logic tick she's inside). Parameters from the env:
--   PAD_AT (shaft = the pad's own spot in a narrow shaft, Samus moved there; deck = the pad's volume moved under Samus
--   on the gunship deck, open air toward -X), PAD_LOCK (ticks, default 0 = the pad's own), PAD_KEY (Up/Down/Left/Right or none), PAD_BEAM "x,y,z" (default
--   the pad's 0,4,0), PAD_SPEED (default 0.6), PAD_OUT (trace file, default jumppad.csv), PAD_RAW "off=int,..." raw
--   s32 writes at CJumpPad + hex offset.
-- Player fields: pos +0x1C, speed +0x34, Vec2 +0x4C (facing), Field70 +0x70, HSpeedMag +0x90, GravityValue +0x98,
-- JumpPadCountdown u16 +0xF8, JumpPadMin5s u8 +0xFA.
local P = 0xDAF94 -- player 0, USA rev 1
local D = 0x2A0AA0 -- jump pad 14's entity data (landing.State)
local C = 0x2E2C80 -- its CJumpPad
local lock = tonumber(os.getenv('PAD_LOCK') or '0')
local key = os.getenv('PAD_KEY') or 'none'
local speed = tonumber(os.getenv('PAD_SPEED') or '0.6')
local bx, by, bz = 0, 4, 0
local beam = os.getenv('PAD_BEAM')
if beam and beam ~= '' then
  local t = {}
  for v in string.gmatch(beam, '[^,]+') do t[#t + 1] = tonumber(v) end
  bx, by, bz = t[1], t[2], t[3]
end
local n = math.sqrt(bx * bx + by * by + bz * bz)
local pokes = {
  {C + 0xD8, bx / n * speed}, {C + 0xDC, by / n * speed}, {C + 0xE0, bz / n * speed},
  {C + 0xD4, (1 + lock * 65536) / 4096}, -- u16 +0xD4 (1 as loaded) + u16 lock +0xD6 as one s32 (driver writes value * 4096)
}
if (os.getenv('PAD_AT') or 'shaft') == 'deck' then
  -- Samus stands on the gunship at (-2.96, 7.23, 0.12): the pad's volume base just under her feet
  for _, pk in ipairs({{C + 0x134, -2.9617}, {C + 0x138, 6.95}, {C + 0x13C, 0.1194}}) do pokes[#pokes + 1] = pk end
else
  local px, py, pz = 0.1201, -10.40, 0.0923
  for _, pk in ipairs({{P + 0x1C, px}, {P + 0x20, py}, {P + 0x24, pz}, {P + 0x28, px}, {P + 0x2C, py}, {P + 0x30, pz},
      {P + 0x34, 0}, {P + 0x38, 0}, {P + 0x3C, 0}}) do pokes[#pokes + 1] = pk end
end
for off, v in string.gmatch(os.getenv('PAD_RAW') or '', '(%x+)=(%-?%d+)') do
  pokes[#pokes + 1] = {C + tonumber(off, 16), tonumber(v) / 4096}
end
local steps = {}
if key ~= 'none' then steps = {{12, 'key', key, 240}} end
return {
  load = 'landing.State',
  pokes = {[10] = pokes},
  steps = steps,
  trace = {base = P, file = os.getenv('PAD_OUT') or 'jumppad.csv', fields = {
    {'px', 0x1C, 'fx'}, {'py', 0x20, 'fx'}, {'pz', 0x24, 'fx'},
    {'vx', 0x34, 'fx'}, {'vy', 0x38, 'fx'}, {'vz', 0x3C, 'fx'},
    {'fx', 0x4C, 'fx'}, {'fz', 0x54, 'fx'},
    {'ax', 0x70, 'fx'}, {'ay', 0x74, 'fx'}, {'az', 0x78, 'fx'},
    {'hmag', 0x90, 'fx'}, {'grav', 0x98, 'fx'},
    {'lock', 0xF8, 'u16'}, {'lockmin', 0xFA, 'u8'}, {'since', 0xFB, 'u8'},
    {'padlock', C + 0xD6 - P, 'u16'}, {'padvy', C + 0x138 - P, 'fx'},
  }},
  shots = {[9] = true, [14] = true, [40] = true, [80] = true, [140] = true},
  last = 260,
}
