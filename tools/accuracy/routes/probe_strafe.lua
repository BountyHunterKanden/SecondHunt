-- Strafe/walk camera tilt (player +0x684 strafe, +0x688 forward), standing on the landed gunship. From landing.State.
-- Right held 40 frames, released 60; Up held 40, released 60. MphRead: _field684 / _field688 (PlayerInput.cs ~581-650).
local P = 0xDAF94 -- player 0, USA rev 1
return {
  load = 'landing.State',
  steps = {{20, 'key', 'Right', 40}, {120, 'key', 'Up', 40}},
  trace = {base = P, file = 'strafe.csv', fields = {
    {'tilt684', 0x684, 'fx'}, {'tilt688', 0x688, 'fx'}, {'px', 0x1C, 'fx'}, {'pz', 0x24, 'fx'},
  }},
  last = 240,
}
