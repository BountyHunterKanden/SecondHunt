-- Power-on -> Adventure, new file A, briefing SKIP, LAND SHIP -> Samus on the Celestial Archives landing site.
-- USA rev 1 (AMHE1), BizHawk 2.11.1 melonDS defaults. Saves every checkpoint state on the way (states/).
-- Frame numbers were found with screenshots on 2026-09-30; touch points are DS bottom-screen pixels.
return {
  steps = {
    {1300, 'key', 'Start'},          -- skip the Nintendo logo
    {2000, 'key', 'A', 10},          -- skip the intro movie
    {2700, 'tap', 63, 78, 8},        -- title: TOUCH TO START
    {2970, 'tap', 63, 78, 8},        -- main menu: focus ADVENTURE MODE
    {3150, 'key', 'A', 6},           -- activate it (a tap only focuses)
    {3510, 'key', 'A', 6},           -- file select: focused slot -> "create a new game?"
    {3590, 'key', 'A', 6},           -- yes (saves to the card)
    {4050, 'key', 'A', 6},           -- the new file
    {4250, 'key', 'A', 6},           -- BEGIN GAME
    {4670, 'tap', 234, 183, 6},      -- briefing: SKIP
    {5950, 'tap', 30, 165, 6},       -- star map: LAND SHIP
  },
  save = {7800, 'landing.State'},
  shots = {[2650] = true, [3000] = true, [3500] = true, [4600] = true, [5900] = true, [7800] = true},
  last = 7800,
}
