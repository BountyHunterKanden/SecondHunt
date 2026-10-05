-- Walking up a 45 deg ramp (owner queue #23), checks the steep end of PlayerCollision's scaled slope push-out. The
-- landing site's ledge at y 13 (x -4.5 .. 4.5, z -25.5 .. -22) has a ramp from x -4.5 (y 13) up to x -6.5 (y 15),
-- normal (0.707, 0.707, 0). From landing.State: walk Up down the 19 deg slope (message box OK at 290 ends the Up hold),
-- at 400 she is placed on the ledge at (-2.5, 13.6, -23.75) (position, previous position, speed 0), facing -X; Up
-- (forward, -X) 480-639: 2 u of ledge, the ramp, then whatever is at the top.
local P = 0xDAF94 -- player 0, USA rev 1
local X, Y, Z = -2.5, 13.6, -23.75
return {
  load = 'landing.State',
  steps = {{120, 'key', 'Up', 160}, {290, 'tap', 128, 142, 4}, {480, 'key', 'Up', 160}},
  pokes = {[400] = {{P + 0x1C, X}, {P + 0x20, Y}, {P + 0x24, Z}, {P + 0x28, X}, {P + 0x2C, Y}, {P + 0x30, Z},
    {P + 0x34, 0}, {P + 0x38, 0}, {P + 0x3C, 0}}},
  trace = {base = P, file = 'ramp45.csv', fields = {
    {'px', 0x1C, 'fx'}, {'py', 0x20, 'fx'}, {'pz', 0x24, 'fx'},
    {'vx', 0x34, 'fx'}, {'vy', 0x38, 'fx'}, {'vz', 0x3C, 'fx'},
  }},
  shots = {[402] = true, [479] = true, [540] = true, [600] = true, [660] = true},
  last = 680,
}
