-- Walking up a 26.6 deg ramp (owner queue #23), the steeper case: PlayerCollision scales the slope push-out on floors
-- with normal Y < 0.9 (> 25.8 deg). The landing site's ramp from the floor at x -9.5 (z -22 .. -18, y 0) up to x -3.5
-- (y 3), normal (-0.447, 0.894, 0). From landing.State: walk Up down the 19 deg slope (message box OK at 290 ends the
-- Up hold), then at 400 she is placed at (-11, 0.5, -19.8) (position, previous position, speed 0; same room, so no
-- node change), still facing -X; Down (backward, +X) 480-599: ~1.5 u of floor, the ramp, the flat top at y 3.
local P = 0xDAF94 -- player 0, USA rev 1
local X, Y, Z = -11.0, 0.5, -19.8
return {
  load = 'landing.State',
  steps = {{120, 'key', 'Up', 160}, {290, 'tap', 128, 142, 4}, {480, 'key', 'Down', 120}},
  pokes = {[400] = {{P + 0x1C, X}, {P + 0x20, Y}, {P + 0x24, Z}, {P + 0x28, X}, {P + 0x2C, Y}, {P + 0x30, Z},
    {P + 0x34, 0}, {P + 0x38, 0}, {P + 0x3C, 0}}},
  trace = {base = P, file = 'ramp26.csv', fields = {
    {'px', 0x1C, 'fx'}, {'py', 0x20, 'fx'}, {'pz', 0x24, 'fx'},
    {'vx', 0x34, 'fx'}, {'vy', 0x38, 'fx'}, {'vz', 0x3C, 'fx'},
  }},
  shots = {[399] = true, [402] = true, [479] = true, [530] = true, [600] = true},
  last = 640,
}
