"""Side-by-side normalized disassembly diff of one function (rev0 addr given; rev1 found via cached alignment)."""
import json, struct, pickle, re, sys, bisect, difflib
from capstone import Cs, CS_ARCH_ARM, CS_MODE_ARM
U = 'u'
def load(rev):
    info = json.load(open(f'{U}/{rev}/info.json'))
    mods = {'arm9': (int(info['arm9_ram'], 16), open(f'{U}/{rev}/arm9.bin', 'rb').read())}
    for o in info['overlays']:
        mods[f"ov{o['id']}"] = (int(o['ram'], 16), open(f"{U}/{rev}/overlay9_{o['id']}.bin", 'rb').read())
    return mods
A, B = load('AMHE0'), load('AMHE1')
ARM = Cs(CS_ARCH_ARM, CS_MODE_ARM)

def starts_of(base, data):
    n = len(data) // 4; ws = struct.unpack_from(f'<{n}I', data); st = set()
    for k, w in enumerate(ws):
        a = base + k * 4
        if (w & 0xFFFF0000) == 0xE92D0000 and (w & 0x4000): st.add(a)
        if (w >> 24) == 0xEB:
            off = w & 0xFFFFFF
            if off & 0x800000: off -= 0x1000000
            t = a + 8 + off * 4
            if base <= t < base + len(data): st.add(t)
    return sorted(st)

def map01(mod, a0):
    base0 = A[mod][0]; base1 = B[mod][0]
    ops = pickle.load(open(f'ops_{mod}.pkl', 'rb'))
    i = (a0 - base0) // 4
    for tag, i1, i2, j1, j2 in ops:
        if i1 <= i < i2 or (i1 == i2 == i):
            if tag == 'equal' or (i2 - i1 == j2 - j1): return base1 + (j1 + i - i1) * 4
            return base1 + j1 * 4
    return None

def lines(mod, mods, addr, end, other_map=None):
    base, data = mods[mod]
    out = []
    d = data[addr - base:end - base]
    words = struct.unpack_from(f'<{len(d) // 4}I', d)
    for k, w in enumerate(words):
        a = addr + k * 4
        dis = list(ARM.disasm(struct.pack('<I', w), a))
        if dis and (w >> 28) == 0xE or (dis and (w >> 28) < 0xE and not (0x02000000 <= w < 0x02400000)):
            ins = dis[0]
            txt = f'{ins.mnemonic} {ins.op_str}'
            # resolve pc-relative literal loads to their value
            m = re.match(r'ldr(\w*) (\w+), \[pc, #(-?0x[0-9a-f]+|-?\d+)\]', txt)
            if m:
                lit = a + 8 + int(m.group(3), 0)
                if base <= lit < base + len(data) - 3:
                    v = struct.unpack_from('<I', data, lit - base)[0]
                    txt = f'ldr{m.group(1)} {m.group(2)}, =LIT({v:#x})'
            txt = re.sub(r'#0x2[0-9a-f]{6}\b', 'ADDR', txt)  # branch targets
            out.append((a, w, txt))
        else:
            out.append((a, w, f'.word {w:#010x}' if not (0x02000000 <= w < 0x02400000) else '.word PTR'))
    return out

def norm(t):
    t = re.sub(r'LIT\(0x2[0-9a-f]{6}\)', 'LIT(PTR)', t)
    return t

def show(mod, a0):
    base0, d0 = A[mod]; base1, d1 = B[mod]
    s0 = starts_of(base0, d0); s1 = starts_of(base1, d1)
    i = bisect.bisect_left(s0, a0); e0 = s0[i + 1] if i + 1 < len(s0) else base0 + len(d0)
    a1 = map01(mod, a0)
    j = bisect.bisect_left(s1, a1); a1 = s1[j] if j < len(s1) and abs(s1[j] - a1) < 64 else a1
    j = bisect.bisect_left(s1, a1); e1 = s1[j + 1] if j + 1 < len(s1) else base1 + len(d1)
    L0 = lines(mod, A, a0, e0); L1 = lines(mod, B, a1, e1)
    t0 = [norm(x[2]) for x in L0]; t1 = [norm(x[2]) for x in L1]
    print(f'===== {mod} rev0 {a0:08X}..{e0:08X} ({e0 - a0} B)  rev1 {a1:08X}..{e1:08X} ({e1 - a1} B)')
    sm = difflib.SequenceMatcher(None, t0, t1, autojunk=False)
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag == 'equal': continue
        print(f'  @{L0[i1][0] if i1 < len(L0) else e0:08X}/{L1[j1][0] if j1 < len(L1) else e1:08X} {tag}')
        for k in range(max(0, i1 - 3), i1): print(f'      {L0[k][0]:08X}   {L0[k][2]}')
        for k in range(i1, i2): print(f'    - {L0[k][0]:08X}   {L0[k][2]}')
        for k in range(j1, j2): print(f'    + {L1[k][0]:08X}   {L1[k][2]}')
        for k in range(i2, min(len(L0), i2 + 2)): print(f'      {L0[k][0]:08X}   {L0[k][2]}')

for spec in sys.argv[1:]:
    mod, a = spec.split(':')
    show(mod, int(a, 16))
