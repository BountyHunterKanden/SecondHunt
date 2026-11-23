"""Cut a vertical plane through an MPH arena's collision and draw the 2D cross-section (docs/SIDE_VIEW_MODE.md).

Scouting aid for side-view stages: reads a room's <room>_Collision.bin from the player's own extracted files
(%LOCALAPPDATA%/MPH Recomp/game/files/AMHE1/_archives/<archive>/), never ships any of it.

usage: python -I tools/arena_slice.py <collision.bin> <out.png> <title> [cuts...]
  cuts like z=0 x=-5 (z=c: the plane z=c, seen along X; x=c: the plane x=c, seen along Z); default z=0 x=0
  env S=<px per unit> (default 14), CUTSONLY=1 to drop the top-down floor map
Faces: layer bit 4 (always on) + node layer 1 (every arena), as Collision.GetCollision reads a match room.
"""
import struct, sys, math
from PIL import Image, ImageDraw, ImageFont

LAYER = 4 | 0x80  # always-on bit + nodeLayer 1 (Collision.GetCollision: ((1 << 1) & 0xFF) << 6)


def load(path):
    b = open(path, 'rb').read()
    typ = b[0:4].decode()
    h = struct.unpack_from('<10I3i3iIIII', b, 4)
    pc, po, plc, plo, pic, pio, dc, do, dic, dio = h[:10]
    pts = [tuple(v / 4096 for v in struct.unpack_from('<3i', b, po + i * 12)) for i in range(pc)]
    planes = [tuple(v / 4096 for v in struct.unpack_from('<4i', b, plo + i * 16)) for i in range(plc)]
    pidx = struct.unpack_from('<%dH' % pic, b, pio)
    faces = []
    for i in range(dc):
        counter, plane, flags, layer, padA, cnt, startLow = struct.unpack_from('<iHHHHHH', b, do + i * 16)
        if not (layer & LAYER):
            continue
        start = (padA << 16) | startLow if typ == 'wc32' else startLow
        poly = [pts[pidx[start + k]] for k in range(cnt)]
        n = planes[plane]
        faces.append((poly, n, flags))
    return faces


def section(faces, axis, c):
    """segments (u0, y0, u1, y1, ny) where the plane axis=c cuts each face; u = the other horizontal axis"""
    ai = 0 if axis == 'x' else 2
    ui = 2 if axis == 'x' else 0
    segs = []
    for poly, n, flags in faces:
        hits = []
        m = len(poly)
        for k in range(m):
            p, q = poly[k], poly[(k + 1) % m]
            dp, dq = p[ai] - c, q[ai] - c
            if (dp <= 0 < dq) or (dq <= 0 < dp):
                t = dp / (dp - dq)
                hits.append((p[ui] + (q[ui] - p[ui]) * t, p[1] + (q[1] - p[1]) * t))
        if len(hits) >= 2:
            segs.append((hits[0][0], hits[0][1], hits[1][0], hits[1][1], n[1]))
    return segs


def colour(ny):
    if ny > 0.5:
        return (60, 200, 90)    # floor
    if ny < -0.5:
        return (220, 80, 80)    # ceiling
    return (150, 150, 170)      # wall


def main():
    path, out, title = sys.argv[1], sys.argv[2], sys.argv[3]
    cuts = [(a.split('=')[0], float(a.split('=')[1])) for a in sys.argv[4:]] or [('z', 0.0), ('x', 0.0)]
    faces = load(path)
    xs = [p[0] for f in faces for p in f[0]]
    ys = [p[1] for f in faces for p in f[0]]
    zs = [p[2] for f in faces for p in f[0]]
    bx, by, bz = (min(xs), max(xs)), (min(ys), max(ys)), (min(zs), max(zs))
    import os
    S = float(os.environ.get("S", "14"))  # px per unit
    font = ImageFont.load_default()
    panels = []
    # top-down: floors coloured by height, slice lines on top
    w, h = int((bx[1] - bx[0]) * S) + 40, int((bz[1] - bz[0]) * S) + 40
    top = Image.new('RGB', (w, h + 20), (18, 18, 24))
    d = ImageDraw.Draw(top)
    for poly, n, flags in sorted(faces, key=lambda f: sum(p[1] for p in f[0]) / len(f[0])):
        if n[1] > 0.5:
            yy = sum(p[1] for p in poly) / len(poly)
            t = (yy - by[0]) / max(1e-6, by[1] - by[0])
            col = (int(40 + 200 * t), int(80 + 120 * t), int(200 - 150 * t))
            d.polygon([((p[0] - bx[0]) * S + 20, (p[2] - bz[0]) * S + 40) for p in poly], fill=col)
    for i, (axis, c) in enumerate(cuts):
        if axis == 'z':
            yy = (c - bz[0]) * S + 40
            d.line([(0, yy), (w, yy)], fill=(255, 255, 255), width=2)
            d.text((4, yy - 12), 'cut %d (z=%g)' % (i + 1, c), fill=(255, 255, 255), font=font)
        else:
            xx = (c - bx[0]) * S + 20
            d.line([(xx, 20), (xx, h + 20)], fill=(255, 255, 255), width=2)
            d.text((xx + 3, 22), 'cut %d (x=%g)' % (i + 1, c), fill=(255, 255, 255), font=font)
    d.text((4, 4), '%s top-down floors (blue low .. yellow high), %d faces' % (title, len(faces)), fill=(255, 255, 255), font=font)
    if not os.environ.get("CUTSONLY"):
        panels.append(top)
    for i, (axis, c) in enumerate(cuts):
        segs = section(faces, axis, c)
        ub = bz if axis == 'x' else bx
        w, h = int((ub[1] - ub[0]) * S) + 40, int((by[1] - by[0]) * S) + 60
        im = Image.new('RGB', (w, h), (18, 18, 24))
        d = ImageDraw.Draw(im)
        for gy in range(math.floor(by[0]), math.ceil(by[1]) + 1, 2):
            yy = (by[1] - gy) * S + 40
            d.line([(0, yy), (w, yy)], fill=(34, 34, 44))
        for u0, y0, u1, y1, ny in segs:
            d.line([((u0 - ub[0]) * S + 20, (by[1] - y0) * S + 40), ((u1 - ub[0]) * S + 20, (by[1] - y1) * S + 40)],
                   fill=colour(ny), width=3)
        # a hunter for scale: 1.6 units tall (MinPickupHeight -0.5 .. MaxPickupHeight 1.1)
        d.rectangle([w - 30, h - 20 - 1.6 * S, w - 30 + 0.8 * S, h - 20], outline=(255, 220, 80))
        d.text((4, 4), '%s cut %d: plane %s=%g, view along %s (green floor, grey wall, red ceiling; grid 2 u; box = hunter)'
               % (title, i + 1, axis, c, 'Z' if axis == 'x' else 'X'), fill=(255, 255, 255), font=font)
        panels.append(im)
    W = max(p.width for p in panels)
    H = sum(p.height for p in panels) + 8 * len(panels)
    sheet = Image.new('RGB', (W, H), (8, 8, 10))
    y = 0
    for p in panels:
        sheet.paste(p, (0, y))
        y += p.height + 8
    sheet.save(out)
    print(out, 'bounds x', bx, 'y', by, 'z', bz, 'faces', len(faces))


main()
