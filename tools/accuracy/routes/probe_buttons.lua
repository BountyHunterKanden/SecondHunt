-- Which DS button jumps, and does the game move Samus every frame or every 2nd (30 Hz)? From landing.State.
local P = 0xDAF94 -- player 0, USA rev 1
return {
  load = 'landing.State',
  steps = {
    {60, 'key', 'Up', 40},
    {150, 'key', 'L', 8}, {240, 'key', 'R', 8}, {330, 'key', 'A', 8}, {420, 'key', 'B', 8},
    {510, 'key', 'X', 8}, {600, 'key', 'Y', 8},
    {690, 'tap', 128, 96, 3}, {696, 'tap', 128, 96, 3},
  },
  trace = {base = P, fields = {
    {'px', 0x1C, 'fx'}, {'py', 0x20, 'fx'}, {'pz', 0x24, 'fx'},
    {'vx', 0x34, 'fx'}, {'vy', 0x38, 'fx'}, {'vz', 0x3C, 'fx'},
    {'energy', 0xDA, 'u16'},
  }},
  shots = {[100] = true, [160] = true, [250] = true, [430] = true, [700] = true},
  last = 800,
}
