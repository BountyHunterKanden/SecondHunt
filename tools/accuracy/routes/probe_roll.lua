-- Morph ball roll on flat ground (A8, owner queue #4). From landing.State: morph, roll Up (forward) off the gunship and
-- down to the flat floor (the ball sits at y 0.5 from x ~ -17.8), let it coast until it stops against the wall at
-- x -25.35 (frame 340); then from rest: Down (backward, +X) 40 frames = acceleration + top speed, release = coasting
-- (flat until x ~ -17.8). Ball position + speed per frame (logic moves on even frames).
-- Reaching the flat floor (~frame 232) pauses the game with a one-time message box: OK on the bottom screen (128, 142).
local P = 0xDAF94 -- player 0, USA rev 1
return {
  load = 'landing.State',
  steps = {{10, 'tap', 220, 163, 4}, {120, 'key', 'Up', 130}, {260, 'tap', 128, 142, 4}, {480, 'key', 'Down', 40}},
  trace = {base = P, file = 'roll.csv', fields = {
    {'px', 0x1C, 'fx'}, {'py', 0x20, 'fx'}, {'pz', 0x24, 'fx'},
    {'vx', 0x34, 'fx'}, {'vy', 0x38, 'fx'}, {'vz', 0x3C, 'fx'},
  }},
  shots = {[259] = true, [270] = true, [479] = true, [540] = true, [800] = true},
  last = 800,
}
