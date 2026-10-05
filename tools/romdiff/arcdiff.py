import struct, sys, hashlib
def lz10(d):
    assert d[0] == 0x10
    n = d[1] | d[2] << 8 | d[3] << 16
    out = bytearray(); i = 4
    while len(out) < n:
        flags = d[i]; i += 1
        for b in range(8):
            if len(out) >= n: break
            if flags & (0x80 >> b):
                x = d[i] << 8 | d[i + 1]; i += 2
                ln = (x >> 12) + 3; disp = (x & 0xFFF) + 1
                for _ in range(ln): out.append(out[-disp])
            else:
                out.append(d[i]); i += 1
    return bytes(out)
def arc(path):
    d = open(path, 'rb').read()
    if d[0] == 0x10: d = lz10(d)
    assert d[:7] == b'SNDFILE', d[:8]
    cnt, total = struct.unpack_from('>II', d, 8)
    files = {}
    for k in range(cnt):
        o = 32 + 64 * k
        name = d[o:o + 32].split(b'\0')[0].decode()
        off, padded, size = struct.unpack_from('>III', d, o + 32)
        files[name] = d[off:off + size]
    return files
a = arc(sys.argv[1]); b = arc(sys.argv[2])
for n in sorted(set(a) | set(b)):
    x, y = a.get(n), b.get(n)
    if x == y: print(f'  same     {n} ({len(x)} B)'); continue
    if x is None or y is None: print(f'  {"ADDED" if x is None else "REMOVED"}  {n}'); continue
    diffs = [i for i in range(min(len(x), len(y))) if x[i] != y[i]]
    print(f'  CHANGED  {n}: {len(x)} -> {len(y)} B, {len(diffs)} differing bytes, first at {diffs[0] if diffs else "-"}')
    if len(sys.argv) > 3:
        open(f'{sys.argv[3]}_0_{n}', 'wb').write(x); open(f'{sys.argv[3]}_1_{n}', 'wb').write(y)
