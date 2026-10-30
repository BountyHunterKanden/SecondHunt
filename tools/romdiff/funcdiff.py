"""Group rev0->rev1 code differences by function and classify them.
Uses cached SequenceMatcher opcodes (ops_<mod>.pkl) from codediff.py."""
import json, struct, pickle, re, sys, collections
from capstone import Cs, CS_ARCH_ARM, CS_MODE_ARM, CS_MODE_THUMB

U = 'u'
def load(rev):
    info = json.load(open(f'{U}/{rev}/info.json'))
    mods = {'arm9': (int(info['arm9_ram'], 16), open(f'{U}/{rev}/arm9.bin', 'rb').read())}
    for o in info['overlays']:
        mods[f"ov{o['id']}"] = (int(o['ram'], 16), open(f"{U}/{rev}/overlay9_{o['id']}.bin", 'rb').read())
    return mods

ARM = Cs(CS_ARCH_ARM, CS_MODE_ARM)
def ins1(w, addr):
    for i in ARM.disasm(struct.pack('<I', w), addr):
        return i.mnemonic, i.op_str
    return None

def func_starts(base, data, all_mods):
    starts = set()
    n = len(data) // 4
    ws = struct.unpack_from(f'<{n}I', data)
    for k, w in enumerate(ws):
        a = base + k * 4
        if (w & 0xFFFF0000) == 0xE92D0000 and (w & 0x4000):   # stmdb sp!, {..lr}
            starts.add(a)
        if (w >> 24) == 0xEB:
            off = (w & 0xFFFFFF)
            if off & 0x800000: off -= 0x1000000
            t = a + 8 + off * 4
            if base <= t < base + len(data): starts.add(t)
    return sorted(starts)

NUM = re.compile(r'#?-?0x[0-9a-f]+|#-?\d+')
def classify(w0, w1, a0, a1):
    i0, i1 = ins1(w0, a0), ins1(w1, a1)
    if i0 and i1 and (w0 >> 28) == 0xE and (w1 >> 28) == 0xE:
        if i0[0] == i1[0] and NUM.sub('N', i0[1]) == NUM.sub('N', i1[1]):
            return 'imm', f'{i0[0]} {i0[1]}  ->  {i1[1]}'
        return 'ins', f'{i0[0]} {i0[1]}  ->  {i1[0]} {i1[1]}'
    return 'data', f'{w0:08X} -> {w1:08X}'

def main(mods_wanted):
    a, b = load('AMHE0'), load('AMHE1')
    for name in mods_wanted:
        base0, d0 = a[name]; base1, d1 = b[name]
        ops = pickle.load(open(f'ops_{name}.pkl', 'rb'))
        n0 = len(d0) // 4; n1 = len(d1) // 4
        ws0 = struct.unpack_from(f'<{n0}I', d0); ws1 = struct.unpack_from(f'<{n1}I', d1)
        starts = func_starts(base0, d0, a)
        import bisect
        def fn(addr):
            i = bisect.bisect_right(starts, addr) - 1
            return starts[i] if i >= 0 else base0
        groups = collections.OrderedDict()
        for tag, i1, i2, j1, j2 in ops:
            if tag == 'equal': continue
            addr0 = base0 + i1 * 4
            f = fn(addr0)
            g = groups.setdefault(f, dict(regions=[], kinds=collections.Counter(), items=[]))
            g['regions'].append((tag, i1, i2, j1, j2))
            if tag == 'replace' and i2 - i1 == j2 - j1:
                for k in range(i2 - i1):
                    kind, txt = classify(ws0[i1 + k], ws1[j1 + k], base0 + (i1 + k) * 4, base1 + (j1 + k) * 4)
                    g['kinds'][kind] += 1
                    g['items'].append(f'{base0 + (i1 + k) * 4:08X}/{base1 + (j1 + k) * 4:08X} [{kind}] {txt}')
            else:
                g['kinds']['struct'] += max(i2 - i1, j2 - j1)
                g['items'].append(f'{addr0:08X}/{base1 + j1 * 4:08X} [{tag} {i2 - i1}->{j2 - j1} words]')
        print(f'##### {name}: {len(groups)} functions touched')
        # summary table
        for f, g in groups.items():
            k = g['kinds']
            print(f'  fn {f:08X}: ' + ', '.join(f'{kk}={vv}' for kk, vv in k.most_common()))
        print()
        for f, g in groups.items():
            print(f'--- fn {f:08X}')
            for it in g['items'][:40]: print('    ' + it)
            if len(g['items']) > 40: print(f'    ... {len(g["items"]) - 40} more')

main(sys.argv[1:])
