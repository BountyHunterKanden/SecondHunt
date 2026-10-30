import os, hashlib, collections
def manifest(root):
    m = {}
    for d, _, fs in os.walk(root):
        for f in fs:
            p = os.path.join(d, f)
            rel = os.path.relpath(p, root).replace(os.sep, '/')
            m[rel] = (os.path.getsize(p), hashlib.md5(open(p, 'rb').read()).hexdigest())
    return m
a = manifest('u/AMHE0/fs'); b = manifest('u/AMHE1/fs')
only_a = sorted(set(a) - set(b)); only_b = sorted(set(b) - set(a))
changed = sorted(k for k in set(a) & set(b) if a[k][1] != b[k][1])
print(f"rev0 files {len(a)}, rev1 files {len(b)}, same {len(set(a) & set(b)) - len(changed)}, changed {len(changed)}, only rev0 {len(only_a)}, only rev1 {len(only_b)}")
print("\n== ONLY IN REV 0 ==")
for k in only_a: print(' ', k, a[k][0])
print("\n== ONLY IN REV 1 ==")
for k in only_b: print(' ', k, b[k][0])
print("\n== CHANGED (size0 -> size1) ==")
for k in changed: print(' ', k, a[k][0], '->', b[k][0])
ha = collections.defaultdict(list)
for k, v in a.items(): ha[v[1]].append(k)
print("\n== rev1-only files whose content exists in rev0 under another name ==")
for k in only_b:
    if b[k][1] in ha: print(' ', k, '==', ha[b[k][1]])
