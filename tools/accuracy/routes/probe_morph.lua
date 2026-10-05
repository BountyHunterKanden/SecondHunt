-- Morph ball camera follow (A3): tap the morph icon, roll forward, stop. Trace player + camera position (CameraInfo at
-- player +0x55C, position first). From landing.State (standing on the landed gunship).
local P = 0xDAF94 -- player 0, USA rev 1
return {
  load = 'landing.State',
  steps = {{10, 'tap', 220, 163, 4}, {120, 'key', 'Up', 60}},
  trace = {base = P, file = 'morph.csv', fields = {
    {'px', 0x1C, 'fx'}, {'py', 0x20, 'fx'}, {'pz', 0x24, 'fx'},
    {'cx', 0x55C, 'fx'}, {'cy', 0x560, 'fx'}, {'cz', 0x564, 'fx'},
  }},
  shots = {[8] = true, [60] = true, [119] = true, [150] = true, [200] = true},
  last = 260,
}
