-- First Hunt (AMFE) menu navigation, steps from the env so menus can be mapped without editing files.
-- FH_LOAD = state, FH_STEPS = "f,tap,x,y[,hold];f,key,Name[,hold];...", FH_LAST, FH_EVERY (screenshot interval),
-- FH_SAVE = "f,name.State", FH_DUMP = "f,name;..." (Main RAM dumps into out/).
local steps = {}
for s in string.gmatch(os.getenv('FH_STEPS') or '', '[^;]+') do
  local t = {}
  for v in string.gmatch(s, '[^,]+') do t[#t + 1] = tonumber(v) or v end
  steps[#steps + 1] = t
end
local save = nil
local sv = os.getenv('FH_SAVE')
if sv and sv ~= '' then local f, n = sv:match('(%d+),(.+)'); save = {tonumber(f), n} end
local dump = nil
for f, n in string.gmatch(os.getenv('FH_DUMP') or '', '(%d+),([^;]+)') do dump = dump or {}; dump[tonumber(f)] = n end
local trace = nil
local tb = os.getenv('FH_TRACE') -- player struct Main RAM offset (hex): pos +0x10, prev +0x1C, vel +0x28, flags +0x430
if tb and tb ~= '' then
  local P = tonumber(tb, 16)
  trace = {base = P, file = os.getenv('FH_TRACEFILE') or 'fh/trace.csv', fields = {
    {'px', 0x10, 'fx'}, {'py', 0x14, 'fx'}, {'pz', 0x18, 'fx'}, {'vx', 0x28, 'fx'}, {'vy', 0x2C, 'fx'}, {'vz', 0x30, 'fx'},
    {'flags', 0x430, 's32'}, {'padx', 0xB4, 'fx'}, {'pady', 0xB8, 'fx'}, {'padz', 0xBC, 'fx'}, {'lock', 0xC0, 'u16'}, {'since', 0xC2, 'u8'}, {'vmax', 0x74, 'fx'}, {'r', 0xCC, 'fx'}}}
end
-- FH_POKE = "f,off,value;..." fx32 writes (Main RAM offset hex, value in units) before frame f
local pokes = nil
for f, o, v in string.gmatch(os.getenv('FH_POKE') or '', '(%d+),(%x+),([-%d.]+)') do
  pokes = pokes or {}; f = tonumber(f); pokes[f] = pokes[f] or {}
  table.insert(pokes[f], {tonumber(o, 16), tonumber(v)})
end
-- FH_RAW = "f,off,hexvalue;..." raw s32 writes (the driver writes value * 4096, so pass value / 4096)
for f, o, v in string.gmatch(os.getenv('FH_RAW') or '', '(%d+),(%x+),(%x+)') do
  pokes = pokes or {}; f = tonumber(f); pokes[f] = pokes[f] or {}
  local n = tonumber(v, 16); if n >= 0x80000000 then n = n - 0x100000000 end
  table.insert(pokes[f], {tonumber(o, 16), n / 4096})
end
local ld = os.getenv('FH_LOAD')
return { load = (ld and ld ~= '') and ld or nil, steps = steps, save = save, dump = dump, trace = trace, pokes = pokes,
  every = (tonumber(os.getenv('FH_EVERY') or '30') or 0) > 0 and tonumber(os.getenv('FH_EVERY') or '30') or nil,
  last = tonumber(os.getenv('FH_LAST') or '600') }
