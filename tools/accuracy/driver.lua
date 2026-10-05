-- BizHawk (melonDS core) route driver for the vanilla accuracy tests. See README.md.
-- Env (set by run.ps1): MPH_EMU_DIR = emulator work dir (states/, out/), MPH_ROUTE = route file (Lua table).
-- Route table: load = state name, save = {frame, name}, steps = {{frame, 'tap', x, y, hold} | {frame, 'key', name, hold}},
-- trace = {base = Main RAM offset, fields = {{name, offset, 'fx'|'u8'|'u16'|'s32'}}, file = 'trace.csv'} (one row per frame),
-- every = screenshot interval, shots = {[frame] = true}, dump = {[frame] = name} (Main RAM), last = frames to run,
-- pokes = {[frame] = {{Main RAM offset, value}, ...}} (fx32 writes of value * 4096, before that frame runs).
-- Only one step is active at a time: a new step (e.g. a tap) ends the held key of the one before.
-- Frames count from the loaded state (or power-on). Touch X/Y are DS bottom-screen pixels (256x192).

local base = os.getenv("MPH_EMU_DIR")
local route = dofile(os.getenv("MPH_ROUTE"))
local out = io.open(base .. "/out/driver_log.txt", "w")
local function log(s) out:write(tostring(s) .. "\n"); out:flush() end

local function dump(name)
  local f = io.open(base .. "/out/" .. name, "wb")
  local CH = 0x10000
  for addr = 0, 0x400000 - 1, CH do
    local t = memory.read_bytes_as_array(addr, CH, "Main RAM")
    local parts = {}
    for i = 1, CH, 4096 do parts[#parts + 1] = string.char(table.unpack(t, i, math.min(i + 4095, CH))) end
    f:write(table.concat(parts))
  end
  f:close()
end

client.speedmode(800)
if route.load then
  savestate.load(base .. "/states/" .. route.load)
  log("loaded " .. route.load .. " at " .. emu.framecount())
end

local trace = nil
if route.trace then
  trace = io.open(base .. "/out/" .. (route.trace.file or "trace.csv"), "w")
  local names = {"f", "emu"}
  for _, fd in ipairs(route.trace.fields) do names[#names + 1] = fd[1] end
  trace:write(table.concat(names, ",") .. "\n")
end
local function readf(fd)
  local a = route.trace.base + fd[2]
  if fd[3] == 'u8' then return memory.read_u8(a, "Main RAM") end
  if fd[3] == 'u16' then return memory.read_u16_le(a, "Main RAM") end
  local v = memory.read_s32_le(a, "Main RAM")
  if fd[3] == 'fx' then return string.format("%.6f", v / 4096) end
  return v
end

local steps = route.steps or {}
local si, hold, cur = 1, 0, nil
for f = 1, route.last or 600 do
  if route.save and route.save[1] == f then
    savestate.save(base .. "/states/" .. route.save[2])
    log("saved " .. route.save[2] .. " at f=" .. f)
  end
  while steps[si] and steps[si][1] == f do
    cur = steps[si]
    hold = (cur[2] == 'tap' and cur[5]) or (cur[2] == 'key' and cur[4]) or 6
    si = si + 1
  end
  if hold > 0 then
    if cur[2] == 'tap' then
      -- order matters: set the button first, then the axes (and the mouse must be unbound, see README)
      joypad.set({Touch = true})
      joypad.setanalog({["Touch X"] = cur[3] + 0.0, ["Touch Y"] = cur[4] + 0.0})
    else
      joypad.set({[cur[3]] = true})
    end
    hold = hold - 1
  end
  if route.pokes and route.pokes[f] then
    for _, pk in ipairs(route.pokes[f]) do memory.write_s32_le(pk[1], math.floor(pk[2] * 4096 + 0.5), "Main RAM") end
    log("poked " .. #route.pokes[f] .. " values at f=" .. f)
  end
  emu.frameadvance()
  if trace then
    local row = {f, emu.framecount()}
    for _, fd in ipairs(route.trace.fields) do row[#row + 1] = readf(fd) end
    trace:write(table.concat(row, ",") .. "\n")
  end
  if (route.every and f % route.every == 0) or (route.shots and route.shots[f]) then
    client.screenshot(base .. "/out/shots/" .. string.format("f%05d.png", f))
  end
  if route.dump and route.dump[f] then dump(route.dump[f]); log("dumped " .. route.dump[f] .. " at f=" .. f) end
end
if trace then trace:close() end
log("done " .. emu.framecount())
out:close()
client.exit()
