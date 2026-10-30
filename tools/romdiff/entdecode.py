import struct, sys
sys.path.insert(0, '.')
from entdiff import parse
MSG = {0: 'None', 5: 'SetActive', 6: 'Destroyed', 7: 'Damage', 9: 'Trigger', 12: 'UpdateMusic', 15: 'Gravity', 16: 'Unlock', 17: 'Lock',
       18: 'Activate', 19: 'Complete', 20: 'Impact', 21: 'Death', 26: 'ShowPrompt', 27: 'ShowWarning', 28: 'ShowOverlay',
       33: 'UnlockConnectors', 34: 'LockConnectors', 42: 'SetTriggerState', 43: 'ClearTriggerState', 44: 'PlatformWakeup', 45: 'PlatformSleep'}
TFLAGS = ['PowerBeam', 'VoltDriver', 'Missile', 'Battlehammer', 'Imperialist', 'Judicator', 'Magmaul', 'ShockCoil', 'BeamCharged',
          'PlayerBiped', 'PlayerAlt', 'Bit11', 'IncludeBots']
PFLAGS = ['Hazard', 'ContactDamage', 'BeamSpawner', 'BeamColEffect', 'DamageReflect1', 'DamageReflect2', 'StandingColOnly', 'StartSleep',
          'SleepAtEnd', 'DripMoat', 'SkipNodeRef', 'DrawIfNodeRef', 'DrawAlways', 'HideOnSleep', 'SyluxShip', 'Bit15', 'BeamReflection',
          'UseRoomState', 'BeamTarget', 'SamusShip', 'Breakable', 'PersistRoomState', 'NoBeamIfCull', 'NoRecoil']
def flags(v, names): return '|'.join(n for i, n in enumerate(names) if v >> i & 1) or '0'
fx = lambda v: round(v / 4096, 3)
def trig(d):
    sub, = struct.unpack_from('<I', d, 0x28)
    vt, = struct.unpack_from('<I', d, 0x2C)
    vol = [fx(x) for x in struct.unpack_from('<15i', d, 0x30)]
    act, always, deact = d[0x6E], d[0x6F], d[0x70]
    rep, chk, rsb = struct.unpack_from('<HHH', d, 0x72)
    tf, thr = struct.unpack_from('<II', d, 0x78)
    pid, = struct.unpack_from('<h', d, 0x80); pm, p1, p2 = struct.unpack_from('<Iii', d, 0x84)
    cid, = struct.unpack_from('<h', d, 0x90); cm, c1, c2 = struct.unpack_from('<Iii', d, 0x94)
    return (f'subtype={["Volume","Threshold","Relay","Automatic","StateBits"][sub]} voltype={vt} active={act} alwaysActive={always} '
            f'deactAfterUse={deact} repeatDelay={rep} checkDelay={chk} flags={flags(tf, TFLAGS)} threshold={thr} '
            f'parent={pid}:{MSG.get(pm, pm)}({p1},{p2}) child={cid}:{MSG.get(cm, cm)}({c1},{c2}) vol={vol[:12]}')
def obj(d):
    fl = d[0x28]; eff, model = struct.unpack_from('<Ii', d, 0x2C); linked, scan, smt = struct.unpack_from('<hHh', d, 0x34)
    smsg, effid = struct.unpack_from('<Ii', d, 0x3C)
    return f'state={fl & 3} flags={fl:#x} effectFlags={eff:#x} model={model} linked={linked} scanId={scan} scanMsg->{smt}:{MSG.get(smsg, smsg)} effectId={effid}'
def plat(d):
    noport, model = struct.unpack_from('<II', d, 0x28); pid, = struct.unpack_from('<h', d, 0x30); act, delay = d[0x32], d[0x33]
    pc, = struct.unpack_from('<H', d, 0x3E); fs, bs = struct.unpack_from('<ii', d, 0x164)
    mv, cut, rev, fl = struct.unpack_from('<IIII', d, 0x17C)
    return (f'model={model} parent={pid} active={act} delay={delay} positions={pc} fwdSpeed={fx(fs)} backSpeed={fx(bs)} '
            f'movement={mv} forCutscene={cut} reverseType={rev} flags={flags(fl, PFLAGS)}')
DEC = {'TriggerVolume': trig, 'Object': obj, 'Platform': plat}
def show(name, ids):
    for rev in ('AMHE0', 'AMHE1'):
        _, _, e, _ = parse(f'u/{rev}/fs/levels/entities/{name}')
        for i in ids:
            if i not in e: print(f'  {rev} id {i}: (absent)'); continue
            x = e[i]
            print(f'  {rev} id {i} {x["type"]} node={x["node"]} layers={x["mask"]:#06x} pos={[round(p, 2) for p in x["pos"]]}\n      ' + DEC.get(x['type'], lambda d: '')(x['data']))
def refs(name, target):
    for rev in ('AMHE0', 'AMHE1'):
        _, _, e, _ = parse(f'u/{rev}/fs/levels/entities/{name}')
        for i, x in e.items():
            if x['type'] == 'TriggerVolume':
                d = x['data']; pid, = struct.unpack_from('<h', d, 0x80); cid, = struct.unpack_from('<h', d, 0x90)
                if target in (pid, cid): print(f'  {rev}: trigger {i} ({x["node"]}) references {target}')
            if x['type'] == 'Object':
                linked, = struct.unpack_from('<h', x['data'], 0x30)
                if linked == target: print(f'  {rev}: object {i} linked to {target}')
cmd = sys.argv[1]
if cmd == 'show': show(sys.argv[2], [int(v) for v in sys.argv[3:]])
else: refs(sys.argv[2], int(sys.argv[3]))
