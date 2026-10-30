"""Unpack an NDS ROM: NitroFS tree, arm9 (BLZ-decompressed if needed), arm7, overlays (decompressed), header."""
import os, struct, sys, json, hashlib

def u32(b, o): return struct.unpack_from('<I', b, o)[0]
def u16(b, o): return struct.unpack_from('<H', b, o)[0]

def blz_decompress(data):
    """Nintendo backward LZ (used for arm9/overlays)."""
    data = bytearray(data)
    if len(data) < 8: return bytes(data)
    footer = u32(data, len(data) - 8)
    extra = u32(data, len(data) - 4)
    hdr_len = footer >> 24
    enc_len = footer & 0xFFFFFF
    out_len = len(data) + extra
    out = bytearray(out_len)
    out[:len(data)] = data
    src = len(data) - hdr_len
    dst = out_len
    end = len(data) - enc_len
    while src > end:
        src -= 1
        flags = out[src]
        for _ in range(8):
            if src <= end: break
            if flags & 0x80:
                src -= 2
                v = out[src] | (out[src + 1] << 8)
                n = (v >> 12) + 3
                disp = (v & 0xFFF) + 3
                for _ in range(n):
                    dst -= 1
                    out[dst] = out[dst + disp]
            else:
                src -= 1
                dst -= 1
                out[dst] = out[src]
            flags = (flags << 1) & 0xFF
    return bytes(out)

def unpack(rom_path, out_dir):
    rom = open(rom_path, 'rb').read()
    os.makedirs(out_dir, exist_ok=True)
    info = {
        'title': rom[0:12].rstrip(b'\0').decode('ascii', 'replace'),
        'gamecode': rom[0xC:0x10].decode('ascii'),
        'rev': rom[0x1E],
        'sha1': hashlib.sha1(rom).hexdigest(),
    }
    arm9_off, arm9_entry, arm9_ram, arm9_size = struct.unpack_from('<4I', rom, 0x20)
    arm7_off, arm7_entry, arm7_ram, arm7_size = struct.unpack_from('<4I', rom, 0x30)
    fnt_off, fnt_size, fat_off, fat_size = struct.unpack_from('<4I', rom, 0x40)
    ovt9_off, ovt9_size, ovt7_off, ovt7_size = struct.unpack_from('<4I', rom, 0x50)
    info.update(arm9_ram=hex(arm9_ram), arm9_entry=hex(arm9_entry), arm9_size=arm9_size,
                arm7_ram=hex(arm7_ram), arm7_size=arm7_size)
    fat = [struct.unpack_from('<2I', rom, fat_off + i * 8) for i in range(fat_size // 8)]

    arm9 = rom[arm9_off:arm9_off + arm9_size]
    # module params: nitrocode trailer after arm9 in ROM gives its offset
    mp_off = None
    if u32(rom, arm9_off + arm9_size) == 0xDEC00621:
        mp_off = u32(rom, arm9_off + arm9_size + 4)
    arm9_dec = arm9
    if mp_off is not None:
        comp_end = u32(arm9, mp_off + 0x14)
        info['arm9_modparams'] = hex(mp_off)
        info['arm9_compressed_end'] = hex(comp_end)
        if comp_end:
            cend = comp_end - arm9_ram
            arm9_dec = arm9[:0x4000] + blz_decompress(arm9[0x4000:cend]) + arm9[cend:]
            # patch compressed-end to 0 like ndstool does
            arm9_dec = bytearray(arm9_dec)
            struct.pack_into('<I', arm9_dec, mp_off + 0x14, 0)
            arm9_dec = bytes(arm9_dec)
    open(os.path.join(out_dir, 'arm9.bin'), 'wb').write(arm9_dec)
    open(os.path.join(out_dir, 'arm7.bin'), 'wb').write(rom[arm7_off:arm7_off + arm7_size])
    info['arm9_dec_size'] = len(arm9_dec)

    overlays = []
    ov_file_ids = set()
    for i in range(ovt9_size // 32):
        ov_id, ram, ram_size, bss, sinit_s, sinit_e, file_id, flags = struct.unpack_from('<8I', rom, ovt9_off + i * 32)
        s, e = fat[file_id]
        data = rom[s:e]
        compressed = bool(flags & 0x01000000)
        dec = blz_decompress(data) if compressed else data
        open(os.path.join(out_dir, f'overlay9_{ov_id}.bin'), 'wb').write(dec)
        ov_file_ids.add(file_id)
        overlays.append(dict(id=ov_id, ram=hex(ram), ram_size=ram_size, bss=bss, file_id=file_id,
                             compressed=compressed, stored=len(data), size=len(dec)))
    info['overlays'] = overlays

    # FNT walk
    files = {}
    def walk(dir_id, path):
        base = fnt_off + (dir_id & 0xFFF) * 8
        sub_off, first_id, _ = struct.unpack_from('<IHH', rom, base)
        p = fnt_off + sub_off
        fid = first_id
        while True:
            ln = rom[p]; p += 1
            if ln == 0: break
            name = rom[p:p + (ln & 0x7F)].decode('latin-1'); p += ln & 0x7F
            if ln & 0x80:
                sub = u16(rom, p); p += 2
                walk(sub, path + name + '/')
            else:
                files[path + name] = fid
                fid += 1
    walk(0xF000, '')
    fs_dir = os.path.join(out_dir, 'fs')
    for name, fid in files.items():
        s, e = fat[fid]
        dst = os.path.join(fs_dir, name)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        open(dst, 'wb').write(rom[s:e])
    info['file_count'] = len(files)
    json.dump(info, open(os.path.join(out_dir, 'info.json'), 'w'), indent=1)
    return info

if __name__ == '__main__':
    i = unpack(sys.argv[1], sys.argv[2])
    print({k: v for k, v in i.items() if k != 'overlays'})
    for o in i['overlays']:
        print(o)
