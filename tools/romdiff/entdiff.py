import struct, sys
TYPES = ['Platform', 'Object', 'PlayerSpawn', 'Door', 'ItemSpawn', 'ItemInstance', 'EnemySpawn', 'TriggerVolume',
         'AreaVolume', 'JumpPad', 'PointModule', 'MorphCamera', 'OctolithFlag', 'FlagBase', 'Teleporter',
         'NodeDefense', 'LightSource', 'Artifact', 'CameraSequence', 'ForceField']
def parse(path):
    b = open(path, 'rb').read()
    ver = struct.unpack_from('<I', b, 0)[0]
    lengths = struct.unpack_from('<16H', b, 4)
    ents = {}
    order = []
    i = 0
    while True:
        o = 36 + 24 * i
        name = b[o:o + 16].split(b'\0')[0].decode('latin-1')
        mask, ln, off = struct.unpack_from('<HHI', b, o + 16)
        if off == 0: break
        typ, eid = struct.unpack_from('<Hh', b, off)
        pos = [x / 4096 for x in struct.unpack_from('<3i', b, off + 4)]
        data = b[off:off + ln]
        ents[eid] = dict(node=name, mask=mask, len=ln, type=TYPES[typ] if typ < len(TYPES) else typ, pos=pos, data=data)
        order.append(eid)
        i += 1
    return ver, lengths, ents, order

def main(f0, f1):
    v0, l0, e0, o0 = parse(f0); v1, l1, e1, o1 = parse(f1)
    print(f'## {f0.split("/")[-1]}: entities rev0 {len(e0)}, rev1 {len(e1)}; layer counts rev0 {l0[:8]} rev1 {l1[:8]}')
    for eid in sorted(set(e0) - set(e1)):
        e = e0[eid]; print(f'  REMOVED in rev1: id {eid} {e["type"]} node={e["node"]} layers={e["mask"]:#06x} pos={[round(p, 2) for p in e["pos"]]}')
    for eid in sorted(set(e1) - set(e0)):
        e = e1[eid]; print(f'  ADDED in rev1:   id {eid} {e["type"]} node={e["node"]} layers={e["mask"]:#06x} pos={[round(p, 2) for p in e["pos"]]}')
    for eid in sorted(set(e0) & set(e1)):
        a, b = e0[eid], e1[eid]
        if a['data'] == b['data'] and a['mask'] == b['mask'] and a['node'] == b['node']: continue
        diffs = [k for k in range(min(len(a['data']), len(b['data']))) if a['data'][k] != b['data'][k]]
        print(f'  CHANGED id {eid} {a["type"]} node={a["node"]} layers {a["mask"]:#06x}->{b["mask"]:#06x} len {a["len"]}->{b["len"]} pos={[round(p, 2) for p in a["pos"]]}')
        # group differing bytes into 4-byte aligned words
        words = sorted(set(k // 4 * 4 for k in diffs))
        for w in words[:24]:
            x0 = a['data'][w:w + 4]; x1 = b['data'][w:w + 4]
            print(f'      +0x{w:03X}: {x0.hex()} -> {x1.hex()}   (u32 {int.from_bytes(x0, "little")} -> {int.from_bytes(x1, "little")})')
    if o0 != [x for x in o1 if x in e0]: print('  (entry order changed)')

if __name__ == '__main__':
  for name in sys.argv[1:]:
    main(f'u/AMHE0/fs/levels/entities/{name}', f'u/AMHE1/fs/levels/entities/{name}')
