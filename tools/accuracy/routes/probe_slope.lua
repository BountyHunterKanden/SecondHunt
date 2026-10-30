-- Walking up an incline (owner queue #23: "climbing slopes feels slow"). From landing.State: walk Up (forward, -X) off
-- the gunship, down the landing site's ~19 deg slope (x ~ -7.3 .. -17.8, y 3.56 .. 0.5) and across the flat floor to the
-- wall; then from rest: Down (backward, +X) across the flat floor and up the slope. Position + speed per frame (logic
-- moves on even frames). The one-time message box pauses the game at frame ~278 (x -18.75): OK at (128, 142) at 290.
-- Up stays held into the wall (~frame 360) until 420; at rest until 480; Down 480-739 (flat floor, then the slope).
local P = 0xDAF94 -- player 0, USA rev 1
return {
  load = 'landing.State',
  steps = {{120, 'key', 'Up', 300}, {290, 'tap', 128, 142, 4}, {480, 'key', 'Down', 260}},
  trace = {base = P, file = 'slope.csv', fields = {
    {'px', 0x1C, 'fx'}, {'py', 0x20, 'fx'}, {'pz', 0x24, 'fx'},
    {'vx', 0x34, 'fx'}, {'vy', 0x38, 'fx'}, {'vz', 0x3C, 'fx'},
  }},
  shots = {[289] = true, [300] = true, [479] = true, [560] = true, [640] = true, [720] = true, [800] = true},
  last = 800,
}
