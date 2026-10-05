-- Example mph-recomp mod: a small play-stat tracker.
-- Demonstrates every part of the mod API: logging, event hooks, persistent per-mod storage
-- (including tables), reading config, and reading an asset. Copy this folder to start your own.

mod.log("stat tracker " .. mod.version .. " loaded")

-- persistent counters survive across sessions (stored in your save, isolated to this mod)
mod.on("start", function()
  local launches = mod.load("launches", 0) + 1
  mod.save("launches", launches)
  mod.log("launch #" .. launches)

  -- read a tuning value from game config, and an (override-able) asset
  mod.log("field of view is " .. mod.config("Camera.FovDegrees", 0))
  local welcome = mod.asset("mods/example_stat_tracker/welcome.txt")
  if welcome then mod.log(welcome) end
end)

-- count visits per room (a table, persisted as-is)
mod.on("room_enter", function(room)
  local visits = mod.load("visits", {})
  visits[room] = (visits[room] or 0) + 1
  mod.save("visits", visits)
  mod.log("entered " .. room .. " (visit #" .. visits[room] .. ")")
end)

-- accumulate playtime every fixed sim tick
mod.on("tick", function(dt)
  mod.save("playSeconds", mod.load("playSeconds", 0) + dt)
end)
