"""For touched functions: size, string refs (via literal pool), callees, diff summary."""
import json, struct, pickle, re, sys, bisect, collections
U = 'u'
def load(rev):
    info = json.load(open(f'{U}/{rev}/info.json'))
    mods = {'arm9': (int(info['arm9_ram'], 16), open(f'{U}/{rev}/arm9.bin', 'rb').read())}
    for o in info['overlays']:
        mods[f"ov{o['id']}"] = (int(o['ram'], 16), open(f"{U}/{rev}/overlay9_{o['id']}.bin", 'rb').read())
    return mods

rev = sys.argv[1]; mod = sys.argv[2]; addrs = [int(x, 16) for x in sys.argv[3:]]
mods = load(rev)
base, data = mods[mod]
arm9b, arm9 = mods['arm9']
def read_str(addr):
    for b, d in ((base, data), (arm9b, arm9)):
        if b <= addr < b + len(d):
            o = addr - b
            m = re.match(rb'[\x20-\x7e]{3,80}', d[o:o + 80])
            if m and (o + len(m.group()) < len(d)) and d[o + len(m.group())] == 0:
                return m.group().decode()
    return None
n = len(data) // 4
ws = struct.unpack_from(f'<{n}I', data)
starts = set()
for k, w in enumerate(ws):
    a = base + k * 4
    if (w & 0xFFFF0000) == 0xE92D0000 and (w & 0x4000): starts.add(a)
    if (w >> 24) == 0xEB:
        off = w & 0xFFFFFF
        if off & 0x800000: off -= 0x1000000
        t = a + 8 + off * 4
        if base <= t < base + len(data): starts.add(t)
starts = sorted(starts)
for f in addrs:
    i = bisect.bisect_left(starts, f)
    end = starts[i + 1] if i + 1 < len(starts) else base + len(data)
    k0, k1 = (f - base) // 4, (end - base) // 4
    strs, callees, consts = [], collections.Counter(), []
    for k in range(k0, k1):
        w = ws[k]
        s = read_str(w) if 0x02000000 <= w < 0x02400000 else None
        if s: strs.append(s)
        if (w >> 24) == 0xEB:
            off = w & 0xFFFFFF
            if off & 0x800000: off -= 0x1000000
            callees[base + k * 4 + 8 + off * 4] += 1
    print(f'{mod} fn {f:08X}..{end:08X} ({end - f} B) strings={strs[:12]} callees={len(callees)} top={[hex(c) for c, _ in callees.most_common(6)]}')
