-- Samus standing still on the landed gunship for 10 s: does the ship bob, sink, or settle? From landing.State.
local P = 0xDAF94 -- player 0, USA rev 1
return {
  load = 'landing.State',
  trace = {base = P, file = 'idle.csv', fields = {
    {'px', 0x1C, 'fx'}, {'py', 0x20, 'fx'}, {'pz', 0x24, 'fx'},
    {'vx', 0x34, 'fx'}, {'vy', 0x38, 'fx'}, {'vz', 0x3C, 'fx'},
  }},
  shots = {[2] = true, [600] = true},
  last = 600,
}
