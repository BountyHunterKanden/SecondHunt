import re, sys, os
root = sys.argv[1]
names = ['arm9'] + [f'overlay9_{i}' for i in range(18)]
for n in names:
    d = open(os.path.join(root, n + '.bin'), 'rb').read()
    ss = [m.group().decode() for m in re.finditer(rb'[\x20-\x7e]{6,}', d)]
    ss = [s for s in ss if sum(c.isalpha() for c in s) >= 4]
    print(f'== {n} ({len(d)} B, {len(ss)} strings): ' + ' | '.join(ss[:40])[:900])
