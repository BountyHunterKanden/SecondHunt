-- Jump pad probe, step 0: Main RAM dump from landing.State to find Celestial Gateway's jump pad
-- 14 (CJumpPad + its entity data) for probe_jumppad.lua.
return {
  load = 'landing.State',
  dump = {[4] = 'jumppad_ram.bin'},
  last = 6,
}
