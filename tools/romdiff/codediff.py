"""Align rev0/rev1 code binaries word-by-word (relocation-tolerant) and list real differences."""
import json, struct, sys, difflib, os
from capstone import Cs, CS_ARCH_ARM, CS_MODE_ARM, CS_MODE_THUMB

U = 'u'
def load(rev):
    info = json.load(open(f'{U}/{rev}/info.json'))
    mods = {'arm9': (int(info['arm9_ram'], 16), open(f'{U}/{rev}/arm9.bin', 'rb').read())}
    for o in info['overlays']:
        mods[f"ov{o['id']}"] = (int(o['ram'], 16), open(f"{U}/{rev}/overlay9_{o['id']}.bin", 'rb').read())
    return mods

def is_ptr(w): return 0x02000000 <= w < 0x02400000

def tokens(data):
    n = len(data) // 4
    ws = struct.unpack_from(f'<{n}I', data)
    out = []
    for w in ws:
        if (w >> 25) & 7 == 5 and (w >> 28) != 0xF:      # ARM B/BL
            out.append(('B', w >> 24))
        elif (w >> 25) == 0x7D:                            # ARM BLX imm
            out.append(('BLX', w >> 24))
        elif is_ptr(w):
            out.append(('P',))
        elif (w & 0xF800) == 0xF000 and ((w >> 16) & 0xE800) in (0xE800, 0xF800):   # thumb BL/BLX pair
            out.append(('TBL', (w >> 16) & 0xF800))
        else:
            out.append(w)
    return ws, out

ARM = Cs(CS_ARCH_ARM, CS_MODE_ARM); THUMB = Cs(CS_ARCH_ARM, CS_MODE_THUMB)
def dis(data, base, off, n, thumb=False):
    md = THUMB if thumb else ARM
    chunk = data[off:off + n]
    lines = []
    for ins in md.disasm(chunk, base + off):
        lines.append(f'    {ins.address:08X}: {ins.bytes.hex():<8} {ins.mnemonic} {ins.op_str}')
    return lines

def looks_thumb(data, off, n):
    # crude: count ARM cond=E words
    n = min(n, len(data) - off) // 4 * 4
    ws = struct.unpack_from(f'<{n // 4}I', data, off) if n >= 4 else ()
    if not ws: return False
    arm = sum(1 for w in ws if (w >> 28) == 0xE)
    return arm < len(ws) * 0.4

def main():
    a, b = load('AMHE0'), load('AMHE1')
    report = []
    for name in a:
        base0, d0 = a[name]; base1, d1 = b[name]
        ws0, t0 = tokens(d0); ws1, t1 = tokens(d1)
        import pickle
        cache = f'ops_{name}.pkl'
        if os.path.exists(cache): ops = pickle.load(open(cache, 'rb'))
        else:
            ops = difflib.SequenceMatcher(None, t0, t1, autojunk=False).get_opcodes()
            pickle.dump(ops, open(cache, 'wb'))
        # address map from equal blocks (for pointer / branch-target checks)
        eq = [(i1, j1, i2 - i1) for tag, i1, i2, j1, j2 in ops if tag == 'equal']
        delta_code = base1 - base0
        diffs = [op for op in ops if op[0] != 'equal']
        # pointer/branch consistency inside equal blocks: record words whose raw values differ inconsistently
        ptr_deltas = {}
        for i, j, n in eq:
            for k in range(n):
                w0, w1 = ws0[i + k], ws1[j + k]
                if w0 != w1 and is_ptr(w0):
                    ptr_deltas.setdefault(w1 - w0, 0)
                    ptr_deltas[w1 - w0] += 1
        changed_words = sum(max(i2 - i1, j2 - j1) for _, i1, i2, j1, j2 in diffs)
        report.append(f'## {name}: base {base0:08X}->{base1:08X}, size {len(d0)}->{len(d1)}, diff regions {len(diffs)}, words {changed_words}')
        top = sorted(ptr_deltas.items(), key=lambda x: -x[1])[:6]
        report.append('   pointer deltas seen in equal blocks (delta:count): ' + ', '.join(f'{d:+#x}:{c}' for d, c in top))
        for tag, i1, i2, j1, j2 in diffs:
            o0, o1 = i1 * 4, j1 * 4
            n0, n1 = (i2 - i1) * 4, (j2 - j1) * 4
            report.append(f'  [{tag}] rev0 {base0 + o0:08X} (+{n0}B)  rev1 {base1 + o1:08X} (+{n1}B)')
            th = looks_thumb(d0, max(0, o0 - 16), n0 + 32) if n0 else looks_thumb(d1, max(0, o1 - 16), n1 + 32)
            ctx = 8
            report.append('   rev0:')
            report += dis(d0, base0, max(0, o0 - ctx), n0 + 2 * ctx, th) if n0 else ['    (none)']
            report.append('   rev1:')
            report += dis(d1, base1, max(0, o1 - ctx), n1 + 2 * ctx, th) if n1 else ['    (none)']
            # raw words too (for data)
            report.append('   raw0: ' + ' '.join(f'{w:08X}' for w in ws0[i1:i2][:24]))
            report.append('   raw1: ' + ' '.join(f'{w:08X}' for w in ws1[j1:j2][:24]))
        print(report[-1] if False else f'{name}: {len(diffs)} regions, {changed_words} words', file=sys.stderr)
    open('codediff.txt', 'w').write('\n'.join(report))

main()
