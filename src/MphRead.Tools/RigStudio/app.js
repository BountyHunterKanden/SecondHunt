// Rig Studio page: group a trophy into the game model's per-bone chunks, set seams, preview the game's clips.
// Data comes from the local RigStudio server (MphRead.Tools -rigstudio); nothing leaves this machine.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const $ = (id) => document.getElementById(id);
const bytes = (s) => { const b = atob(s); const u = new Uint8Array(b.length); for (let i = 0; i < b.length; i++) u[i] = b.charCodeAt(i); return u.buffer; };
const f32 = (s) => new Float32Array(bytes(s));
const i32 = (s) => new Int32Array(bytes(s));
const toB64 = (t) => { const u = new Uint8Array(t.buffer, t.byteOffset, t.byteLength); let s = ''; for (let i = 0; i < u.length; i += 0x8000) s += String.fromCharCode.apply(null, u.subarray(i, i + 0x8000)); return btoa(s); };

// Friendly names: a game bone moves the part from its joint to the next one (L_elbow = left forearm).
const LABEL = {
  Dummy_Root: 'model root', Skeleton_Root: 'hips', Pelvis: 'pelvis', Spine_1: 'waist', Spine_2: 'chest', Collar: 'upper chest',
  Neck_1: 'neck', Head_1: 'head', L_collar: 'L collarbone', R_collar: 'R collarbone', L_shoulder: 'L upper arm', R_shoulder: 'R upper arm',
  L_elbow: 'L forearm', R_elbow: 'R forearm', L_wrist: 'L hand', R_wrist: 'R hand', L_hip: 'L thigh', R_hip: 'R thigh',
  L_knee: 'L shin', R_knee: 'R shin', L_ankle: 'L foot', R_ankle: 'R foot', L_varias2_SDK: 'L shoulder pad', R_varias2_SDK: 'R shoulder pad',
};
const PALETTE = {
  Skeleton_Root: '#9a9a9a', Pelvis: '#c4c4c4', Spine_1: '#e9c46a', Spine_2: '#f4a261', Collar: '#e76f51', Neck_1: '#b5838d', Head_1: '#ef476f',
  L_collar: '#8d6cab', R_collar: '#4a6fa5', L_shoulder: '#06d6a0', R_shoulder: '#118ab2', L_elbow: '#9ef01a', R_elbow: '#3a86ff',
  L_wrist: '#d9ed92', R_wrist: '#7b2cbf', L_hip: '#ff595e', R_hip: '#ff924c', L_knee: '#ffca3a', R_knee: '#c77dff',
  L_ankle: '#43aa8b', R_ankle: '#f15bb5', L_varias2_SDK: '#80ffdb', R_varias2_SDK: '#bde0fe', Dummy_Root: '#555a66',
};

const S = {
  M: null, trophy: null, owner: null, stiff: null, layer: 'bone', undo: [], redo: [], active: -1, hover: -1, tool: 'patch', brush: 0.06, mode: 'group',
  clips: new Map(), clip: -1, frame: 0, playing: false, speed: 1, seams: {}, dirty: false,
  patchVerts: [], pieceVerts: [], painting: false, colors: [],
};

// ---------------- scene
const canvas = $('view');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
const scene = new THREE.Scene(); scene.background = new THREE.Color(0x15171c);
const camera = new THREE.PerspectiveCamera(32, 1, 0.01, 100);
camera.position.set(0, 1.15, -5.6);
const controls = new OrbitControls(camera, canvas);
controls.target.set(0, 0.95, 0); controls.update();
scene.add(new THREE.HemisphereLight(0xffffff, 0x3a3f4a, 1.7));
const sun = new THREE.DirectionalLight(0xffffff, 1.5); sun.position.set(-2, 4, -4); scene.add(sun);
const fill = new THREE.DirectionalLight(0xffffff, 0.5); fill.position.set(3, 1, 3); scene.add(fill);
scene.add(new THREE.GridHelper(6, 24, 0x3a4050, 0x262a33));
// the camera looks down +Z, so screen-left is +X: game model on the left, trophy on the right
const dsGroup = new THREE.Group(); dsGroup.position.x = 0.95; scene.add(dsGroup);
const trGroup = new THREE.Group(); trGroup.position.x = -0.95; scene.add(trGroup);
let dsMesh = null, trMesh = null, trGhost = null, partMesh = null, dsBonesLine = null, trBonesLine = null;
const brushCursor = new THREE.Mesh(new THREE.SphereGeometry(1, 20, 12), new THREE.MeshBasicMaterial({ color: 0xffffff, wireframe: true, transparent: true, opacity: 0.35 }));
brushCursor.visible = false; trGroup.add(brushCursor);

function resize() {
  const r = canvas.parentElement.getBoundingClientRect();
  renderer.setSize(r.width, r.height, false);
  camera.aspect = r.width / Math.max(1, r.height); camera.updateProjectionMatrix();
}
// fit both models in view (keeps the current viewing direction)
function frameAll() {
  const box = new THREE.Box3();
  for (const m of [dsMesh, trMesh]) if (m) { m.geometry.computeBoundingBox(); box.union(m.geometry.boundingBox.clone().applyMatrix4(m.matrixWorld)); }
  if (box.isEmpty()) return;
  const c = box.getCenter(new THREE.Vector3()), size = box.getSize(new THREE.Vector3());
  const vfov = THREE.MathUtils.degToRad(camera.fov), hfov = 2 * Math.atan(Math.tan(vfov / 2) * camera.aspect);
  const dist = 1.12 * Math.max((size.y / 2) / Math.tan(vfov / 2), (Math.hypot(size.x, size.z) / 2) / Math.tan(hfov / 2)) + size.z / 2;
  const dir = camera.position.clone().sub(controls.target).normalize();
  controls.target.copy(c); camera.position.copy(c).addScaledVector(dir, dist); controls.update();
}
window.addEventListener('resize', () => { resize(); frameAll(); });

// ---------------- helpers
const boneName = (i) => (S.M && i >= 0 ? S.M.bones[i].name : '');
const boneLabel = (i) => (i < 0 ? '—' : `${LABEL[boneName(i)] ?? boneName(i)}`);
function boneColor(i) {
  const c = new THREE.Color(PALETTE[boneName(i)] ?? '#888');
  return c;
}
function setStatus(t) { $('status').textContent = t; }
function markDirty(d = true) { S.dirty = d; $('dirty').textContent = d ? '● unsaved changes' : ''; updateNotice(); }
function updateNotice() {
  const n = $('notice');
  if (S.mode === 'preview' && S.dirty) { n.textContent = 'Preview shows the last SAVED rig — Save & refit to see your changes'; n.style.display = 'block'; }
  else n.style.display = 'none';
}

// ---------------- load
async function api(path, opts) {
  const r = await fetch(path, opts);
  if (!r.ok) throw new Error(`${path}: ${r.status} ${await r.text()}`);
  return r.json();
}

async function loadList() {
  const list = await api('/api/list');
  const sel = $('trophy'); sel.innerHTML = '';
  for (const t of list) {
    const o = document.createElement('option'); o.value = t.name;
    o.textContent = `${t.name}${t.chunks ? ' ✓ grouped' : ''}${t.landmarks ? '' : ' (no landmarks)'}`;
    sel.appendChild(o);
  }
  const want = new URLSearchParams(location.search).get('t') ?? 'Weavel';
  if (list.some((t) => t.name === want)) sel.value = want;
  sel.onchange = () => { if (!S.dirty || confirm('Discard unsaved changes?')) loadTrophy(sel.value); else sel.value = S.trophy; };
  await loadTrophy(sel.value);
}

async function loadTrophy(name) {
  $('loading').style.display = 'flex'; $('loading').textContent = `Fitting ${name}…`;
  setStatus(`Loading ${name} (the server fits the rig first)…`);
  const M = await api(`/api/model?t=${encodeURIComponent(name)}`);
  S.trophy = name; S.clips.clear(); S.undo = []; S.redo = [];
  applyModel(M, true);
  history.replaceState(null, '', `?t=${encodeURIComponent(name)}`);
  $('loading').style.display = 'none';
  await poseDsIdle();   // the game model's idle pose must be in place before framing
  trGroup.updateMatrixWorld(true); dsGroup.updateMatrixWorld(true); frameAll();
}

// (re)build everything from a model reply; keepOwner=false keeps the current edit (after a save it matches)
function applyModel(M, fresh) {
  S.M = M;
  M.posA = f32(M.pos); M.nrmA = f32(M.nrm); M.triA = i32(M.tri);
  M.biA = i32(M.bi); M.bwA = f32(M.bw); M.ownerA = i32(M.owner); M.userA = M.user ? i32(M.user) : null; M.stiffA = i32(M.stiff);
  M.hiddenA = i32(M.hidden); M.pieceA = i32(M.piece); M.patchA = i32(M.patch); M.jointsA = f32(M.joints);
  M.dsLocalA = f32(M.dsLocal); M.dsNormalA = f32(M.dsNormal); M.dsNodeA = i32(M.dsNode);
  M.partPosA = f32(M.partPos); M.partNrmA = f32(M.partNrm); M.partNodeA = i32(M.partNode);
  const nv = M.vertices;
  S.owner = new Int32Array(nv);
  for (let v = 0; v < nv; v++) S.owner[v] = M.userA && M.userA[v] >= 0 ? M.userA[v] : M.ownerA[v];
  S.stiff = new Uint8Array(nv);
  for (let v = 0; v < nv; v++) S.stiff[v] = M.stiffA[v] ? 1 : 0;
  if (fresh) {
    S.seams = {};
    for (const [k, v] of Object.entries(M.seams)) S.seams[k] = { rigid: v.rigid, width: v.width };
  }
  S.patchVerts = groupBy(M.patchA); S.pieceVerts = groupBy(M.pieceA);
  if (S.active < 0 || S.active >= M.bones.length) S.active = M.bones.findIndex((b) => b.name === 'Spine_2');
  buildDs(); buildTrophy(); buildParts(); buildSkeletons(); buildBoneList(); setActive(S.active);
  $('log').textContent = M.log;
  $('clip').innerHTML = '';
  for (const c of M.clips) { const o = document.createElement('option'); o.value = c.id; o.textContent = `${c.name} (${c.frames})`; $('clip').appendChild(o); }
  const idle = M.clips.find((c) => c.name === 'Idle');
  $('clip').value = S.clip >= 0 ? S.clip : (idle ? idle.id : 0);
  poseDsIdle();
  markDirty(false);
  const hid = M.hiddenA.reduce((a, b) => a + b, 0);
  $('stats').textContent = `${M.trophy} on the ${M.hunter} rig · ${nv} trophy vertices · ${S.patchVerts.length} patches · ${S.pieceVerts.length} pieces${hid ? ` · ${hid} hidden` : ''}`;
  setStatus(M.userA ? 'Loaded your saved grouping.' : 'No grouping saved yet — starting from the automatic one.');
  if (S.mode === 'preview') enterPreview();
}

function groupBy(ids) {
  const out = [];
  for (let v = 0; v < ids.length; v++) { const k = ids[v]; (out[k] ??= []).push(v); }
  return out;
}

// ---------------- meshes
function disposeMesh(m) { if (!m) return; m.parent?.remove(m); m.geometry.dispose(); m.material.dispose?.(); }

function buildDs() {
  disposeMesh(dsMesh);
  const n = S.M.dsNodeA.length;
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(n * 3), 3));
  g.setAttribute('normal', new THREE.BufferAttribute(new Float32Array(n * 3), 3));
  g.setAttribute('color', new THREE.BufferAttribute(new Float32Array(n * 3), 3));
  dsMesh = new THREE.Mesh(g, new THREE.MeshStandardMaterial({ vertexColors: true, flatShading: true, roughness: 0.85, metalness: 0.0, side: THREE.DoubleSide }));
  dsMesh.visible = $('showDs').checked;
  dsGroup.add(dsMesh);
}

function buildTrophy() {
  disposeMesh(trMesh); disposeMesh(trGhost);
  const M = S.M, nv = M.vertices;
  const pos = new THREE.BufferAttribute(new Float32Array(M.posA), 3);
  const nrm = new THREE.BufferAttribute(new Float32Array(M.nrmA), 3);
  const col = new THREE.BufferAttribute(new Float32Array(nv * 3), 3);
  const vis = [], ghost = [];
  for (let t = 0; t < M.triA.length; t += 3) {
    const a = M.triA[t], b = M.triA[t + 1], c = M.triA[t + 2];
    if (a < 0 || b < 0 || c < 0) continue;
    (M.hiddenA[a] || M.hiddenA[b] || M.hiddenA[c] ? ghost : vis).push(a, b, c);
  }
  const mk = (idx) => { const g = new THREE.BufferGeometry(); g.setAttribute('position', pos); g.setAttribute('normal', nrm); g.setAttribute('color', col); g.setIndex(idx); g.computeBoundingSphere(); return g; };
  trMesh = new THREE.Mesh(mk(vis), new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.7, metalness: 0.05, side: THREE.DoubleSide }));
  trGhost = new THREE.Mesh(mk(ghost), new THREE.MeshBasicMaterial({ color: 0x8899aa, transparent: true, opacity: 0.12, depthWrite: false, side: THREE.DoubleSide }));
  trGhost.visible = $('showGhost').checked && S.mode === 'group';
  trGroup.add(trMesh); trGroup.add(trGhost);
  recolor();
}

function buildParts() {
  disposeMesh(partMesh); partMesh = null;
  const n = S.M.partNodeA.length;
  if (!n) return;
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(S.M.partPosA), 3));
  g.setAttribute('normal', new THREE.BufferAttribute(new Float32Array(S.M.partNrmA), 3));
  partMesh = new THREE.Mesh(g, new THREE.MeshStandardMaterial({ color: 0x9fb4b4, roughness: 0.6, metalness: 0.2, flatShading: true, side: THREE.DoubleSide }));
  trGroup.add(partMesh);
}

function buildSkeletons() {
  disposeMesh(dsBonesLine); disposeMesh(trBonesLine);
  const nb = S.M.bones.length;
  const mk = () => {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(nb * 6), 3));
    const l = new THREE.LineSegments(g, new THREE.LineBasicMaterial({ color: 0xffffff, depthTest: false, transparent: true, opacity: 0.9 }));
    l.renderOrder = 10; l.visible = $('showBones').checked; return l;
  };
  dsBonesLine = mk(); dsGroup.add(dsBonesLine);
  trBonesLine = mk(); trGroup.add(trBonesLine);
  setSkeleton(trBonesLine, S.M.jointsA, 0);
}
function setSkeleton(line, joints, frameOffset) {
  const p = line.geometry.attributes.position.array, bones = S.M.bones;
  for (let i = 0; i < bones.length; i++) {
    const par = bones[i].parent, o = frameOffset + i * 3, q = frameOffset + par * 3;
    if (par < 0 || bones[par].name === 'Dummy_Root') { p.fill(0, i * 6, i * 6 + 6); continue; }
    p[i * 6] = joints[q]; p[i * 6 + 1] = joints[q + 1]; p[i * 6 + 2] = joints[q + 2];
    p[i * 6 + 3] = joints[o]; p[i * 6 + 4] = joints[o + 1]; p[i * 6 + 5] = joints[o + 2];
  }
  line.geometry.attributes.position.needsUpdate = true; line.geometry.computeBoundingSphere();
}

// colours: each bone its own colour; the hovered bone glows, everything else dims a little while hovering
function shade(bone) {
  const c = boneColor(bone);
  if (S.hover >= 0) { if (bone === S.hover) c.lerp(new THREE.Color(1, 1, 1), 0.45); else c.multiplyScalar(0.55); }
  return c;
}
function recolor() {
  if (!trMesh) return;
  const col = trMesh.geometry.attributes.color, a = col.array, cache = new Map(), pale = new Map();
  const showStiff = $('showStiff').checked, white = new THREE.Color(1, 1, 1);
  for (let v = 0; v < S.owner.length; v++) {
    const b = S.owner[v]; let c = cache.get(b); if (!c) { c = shade(b); cache.set(b, c); }
    if (showStiff && S.stiff[v]) { let p = pale.get(b); if (!p) { p = c.clone().lerp(white, 0.55); pale.set(b, p); } c = p; }
    a[v * 3] = c.r; a[v * 3 + 1] = c.g; a[v * 3 + 2] = c.b;
  }
  col.needsUpdate = true;
  if (dsMesh) {
    const d = dsMesh.geometry.attributes.color.array, node = S.M.dsNodeA;
    for (let i = 0; i < node.length; i++) { const b = node[i]; let c = cache.get(b); if (!c) { c = shade(b); cache.set(b, c); } d[i * 3] = c.r; d[i * 3 + 1] = c.g; d[i * 3 + 2] = c.b; }
    dsMesh.geometry.attributes.color.needsUpdate = true;
  }
  refreshCounts();
}

// ---------------- posing (row vectors, row-major 4x4: v' = v * M)
function xform(m, o, x, y, z, out, k) {
  out[k] = x * m[o] + y * m[o + 4] + z * m[o + 8] + m[o + 12];
  out[k + 1] = x * m[o + 1] + y * m[o + 5] + z * m[o + 9] + m[o + 13];
  out[k + 2] = x * m[o + 2] + y * m[o + 6] + z * m[o + 10] + m[o + 14];
}
function xdir(m, o, x, y, z, out, k) {
  const a = x * m[o] + y * m[o + 4] + z * m[o + 8], b = x * m[o + 1] + y * m[o + 5] + z * m[o + 9], c = x * m[o + 2] + y * m[o + 6] + z * m[o + 10];
  const l = Math.hypot(a, b, c) || 1; out[k] = a / l; out[k + 1] = b / l; out[k + 2] = c / l;
}
async function getClip(id) {
  if (!S.clips.has(id)) {
    const prev = $('status').textContent;
    setStatus('Fetching clip…');
    const C = await api(`/api/clip?t=${encodeURIComponent(S.trophy)}&c=${id}`);
    C.skin = f32(C.trophySkin); C.ds = f32(C.dsBones); C.tj = f32(C.trophyJoints); C.dj = f32(C.dsJoints);
    S.clips.set(id, C); setStatus(prev);
  }
  return S.clips.get(id);
}
function poseDs(C, f) {
  const nb = S.M.bones.length, m = C.ds, base = f * nb * 16;
  const L = S.M.dsLocalA, N = S.M.dsNormalA, node = S.M.dsNodeA;
  const P = dsMesh.geometry.attributes.position.array, Q = dsMesh.geometry.attributes.normal.array;
  for (let i = 0; i < node.length; i++) {
    const o = base + node[i] * 16;
    xform(m, o, L[i * 3], L[i * 3 + 1], L[i * 3 + 2], P, i * 3);
    xdir(m, o, N[i * 3], N[i * 3 + 1], N[i * 3 + 2], Q, i * 3);
  }
  dsMesh.geometry.attributes.position.needsUpdate = true; dsMesh.geometry.attributes.normal.needsUpdate = true;
  dsMesh.geometry.computeBoundingSphere();
  setSkeleton(dsBonesLine, C.dj, f * nb * 3);
}
function poseTrophy(C, f) {
  const M = S.M, nb = M.bones.length, sk = C.skin, base = f * nb * 16, nv = M.vertices;
  const P = trMesh.geometry.attributes.position.array, Q = trMesh.geometry.attributes.normal.array;
  const B = M.posA, BN = M.nrmA, bi = M.biA, bw = M.bwA;
  const tp = [0, 0, 0], tn = [0, 0, 0];
  for (let v = 0; v < nv; v++) {
    let px = 0, py = 0, pz = 0, nx = 0, ny = 0, nz = 0;
    for (let k = 0; k < 4; k++) {
      const w = bw[v * 4 + k]; if (w <= 0) continue;
      const o = base + bi[v * 4 + k] * 16;
      xform(sk, o, B[v * 3], B[v * 3 + 1], B[v * 3 + 2], tp, 0);
      xdir(sk, o, BN[v * 3], BN[v * 3 + 1], BN[v * 3 + 2], tn, 0);
      px += w * tp[0]; py += w * tp[1]; pz += w * tp[2]; nx += w * tn[0]; ny += w * tn[1]; nz += w * tn[2];
    }
    P[v * 3] = px; P[v * 3 + 1] = py; P[v * 3 + 2] = pz;
    const l = Math.hypot(nx, ny, nz) || 1; Q[v * 3] = nx / l; Q[v * 3 + 1] = ny / l; Q[v * 3 + 2] = nz / l;
  }
  trMesh.geometry.attributes.position.needsUpdate = true; trMesh.geometry.attributes.normal.needsUpdate = true;
  trMesh.geometry.computeBoundingSphere();
  if (partMesh) {
    const PP = partMesh.geometry.attributes.position.array, PN = partMesh.geometry.attributes.normal.array;
    const pb = M.partPosA, pn = M.partNrmA, node = M.partNodeA;
    for (let i = 0; i < node.length; i++) {
      const o = base + node[i] * 16;
      xform(sk, o, pb[i * 3], pb[i * 3 + 1], pb[i * 3 + 2], PP, i * 3);
      xdir(sk, o, pn[i * 3], pn[i * 3 + 1], pn[i * 3 + 2], PN, i * 3);
    }
    partMesh.geometry.attributes.position.needsUpdate = true; partMesh.geometry.attributes.normal.needsUpdate = true;
    partMesh.geometry.computeBoundingSphere();
  }
  setSkeleton(trBonesLine, C.tj, f * nb * 3);
}
function restTrophy() {
  const M = S.M;
  trMesh.geometry.attributes.position.array.set(M.posA); trMesh.geometry.attributes.position.needsUpdate = true;
  trMesh.geometry.attributes.normal.array.set(M.nrmA); trMesh.geometry.attributes.normal.needsUpdate = true;
  trMesh.geometry.computeBoundingSphere();
  if (partMesh) {
    partMesh.geometry.attributes.position.array.set(M.partPosA); partMesh.geometry.attributes.position.needsUpdate = true;
    partMesh.geometry.attributes.normal.array.set(M.partNrmA); partMesh.geometry.attributes.normal.needsUpdate = true;
  }
  setSkeleton(trBonesLine, M.jointsA, 0);
}
async function poseDsIdle() {
  const idle = S.M.clips.find((c) => c.name === 'Idle');
  const C = await getClip(idle ? idle.id : 0);
  if (S.mode === 'group') poseDs(C, 0);
}

// ---------------- bone list, active bone, seams
function buildBoneList() {
  const box = $('bones'); box.innerHTML = '';
  const bones = S.M.bones;
  const depth = (i) => { let d = 0; for (let p = bones[i].parent; p >= 0; p = bones[p].parent) d++; return d; };
  for (let i = 0; i < bones.length; i++) {
    const b = bones[i];
    if (b.name === 'Dummy_Root') continue;
    const row = document.createElement('div');
    row.className = 'bone' + (b.ownsDs ? '' : ' nomesh'); row.dataset.bone = i;
    row.style.paddingLeft = `${6 + Math.max(0, depth(i) - 1) * 8}px`;
    const c = '#' + boneColor(i).getHexString();
    row.innerHTML = `<span class="sw" style="background:${c}"></span><span class="nm">${LABEL[b.name] ?? b.name}<small>${b.name}</small></span><span class="cnt" id="cnt${i}"></span>`;
    const par = b.parent;
    if (par >= 0 && bones[par].name !== 'Dummy_Root') {
      const s = S.seams[b.name] ?? { rigid: false, width: S.M.defaultSeamWidth };
      const seam = document.createElement('div'); seam.className = 'seam';
      seam.innerHTML = `seam to ${LABEL[bones[par].name] ?? bones[par].name}: <select><option value="blend">blend</option><option value="rigid">rigid</option></select><input type="number" min="0" max="10" step="0.5" title="blend width, % of body height"><span>%</span>`;
      const sel = seam.querySelector('select'), inp = seam.querySelector('input'), pct = seam.querySelector('span');
      sel.value = s.rigid ? 'rigid' : 'blend'; inp.value = (s.width * 100).toFixed(1);
      const sync = () => { const rigid = sel.value === 'rigid'; inp.style.display = pct.style.display = rigid ? 'none' : ''; };
      sync();
      const commit = () => { pushUndo(); S.seams[b.name] = { rigid: sel.value === 'rigid', width: Math.max(0, parseFloat(inp.value) || 0) / 100 }; sync(); markDirty(); };
      sel.onchange = commit; inp.onchange = commit;
      seam.onclick = (e) => e.stopPropagation();
      row.appendChild(seam);
    }
    row.onclick = () => setActive(i);
    row.onmouseenter = () => setHover(i); row.onmouseleave = () => setHover(-1);
    box.appendChild(row);
  }
  refreshCounts();
}
function refreshCounts() {
  if (!S.M) return;
  const n = new Array(S.M.bones.length).fill(0);
  for (let v = 0; v < S.owner.length; v++) n[S.owner[v]]++;
  for (let i = 0; i < n.length; i++) { const el = $(`cnt${i}`); if (el) el.textContent = n[i] ? n[i] : ''; }
}
function setActive(i) {
  S.active = i;
  $('activeSw').style.background = i >= 0 ? '#' + boneColor(i).getHexString() : 'transparent';
  $('activeName').innerHTML = i >= 0 ? `<b>${boneLabel(i)}</b> <span class="help">${boneName(i)}</span>` : '—';
  $('activeHint').textContent = i >= 0 && !S.M.bones[i].ownsDs ? 'This bone carries no mesh in the game model' : 'Click or drag on the trophy to give it parts';
  for (const el of document.querySelectorAll('.bone')) el.classList.toggle('on', +el.dataset.bone === i);
}
function setHover(i) { if (S.hover === i) return; S.hover = i; recolor(); }

// ---------------- painting
const ray = new THREE.Raycaster();
const ndc = new THREE.Vector2();
function pick(e, objs) {
  const r = canvas.getBoundingClientRect();
  ndc.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
  ray.setFromCamera(ndc, camera);
  return ray.intersectObjects(objs.filter((o) => o && o.visible), false)[0] ?? null;
}
function nearestCorner(hit) {
  const p = trGroup.worldToLocal(hit.point.clone()), P = S.M.posA, f = hit.face;
  let best = f.a, bd = Infinity;
  for (const v of [f.a, f.b, f.c]) { const d = (P[v * 3] - p.x) ** 2 + (P[v * 3 + 1] - p.y) ** 2 + (P[v * 3 + 2] - p.z) ** 2; if (d < bd) { bd = d; best = v; } }
  return { v: best, local: p };
}
function paint(hit) {
  if (S.active < 0 && S.layer === 'bone') return;
  const { v, local } = nearestCorner(hit);
  let verts;
  if (S.tool === 'patch') verts = S.patchVerts[S.M.patchA[v]];
  else if (S.tool === 'piece') verts = S.pieceVerts[S.M.pieceA[v]];
  else {
    verts = [];
    const P = S.M.posA, N = S.M.nrmA, r2 = S.brush * S.brush, cam = trGroup.worldToLocal(camera.position.clone());
    for (let u = 0; u < S.M.vertices; u++) {
      const dx = P[u * 3] - local.x, dy = P[u * 3 + 1] - local.y, dz = P[u * 3 + 2] - local.z;
      if (dx * dx + dy * dy + dz * dz > r2) continue;
      // front-facing only, so the brush doesn't paint through to the far side
      if ((cam.x - P[u * 3]) * N[u * 3] + (cam.y - P[u * 3 + 1]) * N[u * 3 + 1] + (cam.z - P[u * 3 + 2]) * N[u * 3 + 2] < 0) continue;
      verts.push(u);
    }
  }
  let changed = 0;
  if (S.layer === 'bone') { for (const u of verts) if (S.owner[u] !== S.active) { S.owner[u] = S.active; changed++; } }
  else { const want = S.layer === 'stiff' ? 1 : 0; for (const u of verts) if (S.stiff[u] !== want) { S.stiff[u] = want; changed++; } }
  if (changed) { recolor(); markDirty(); }
}
const snap = () => ({ owner: S.owner.slice(), stiff: S.stiff.slice(), seams: JSON.parse(JSON.stringify(S.seams)) });
const restore = (x) => { S.owner = x.owner; S.stiff = x.stiff; S.seams = x.seams; buildBoneList(); setActive(S.active); recolor(); markDirty(); };
function pushUndo() { S.undo.push(snap()); if (S.undo.length > 80) S.undo.shift(); S.redo = []; }
function undo() { if (!S.undo.length) return; S.redo.push(snap()); restore(S.undo.pop()); }
function redo() { if (!S.redo.length) return; S.undo.push(snap()); restore(S.redo.pop()); }

canvas.addEventListener('pointerdown', (e) => {
  if (e.button !== 0 || S.mode !== 'group' || !S.M) return;
  const hitT = pick(e, [trMesh]);
  if (hitT) {
    if (e.altKey) { setActive(S.owner[nearestCorner(hitT).v]); e.preventDefault(); return; }
    controls.enabled = false; S.painting = true; canvas.setPointerCapture(e.pointerId);
    pushUndo(); paint(hitT); return;
  }
  const hitD = pick(e, [dsMesh]);
  if (hitD) { setActive(S.M.dsNodeA[hitD.face.a]); }
}, true);
canvas.addEventListener('pointerup', (e) => {
  if (S.painting) { S.painting = false; controls.enabled = true; canvas.releasePointerCapture(e.pointerId); }
});
let hoverQueued = null;
canvas.addEventListener('pointermove', (e) => { hoverQueued = e; });
canvas.addEventListener('pointerleave', () => { hoverQueued = null; $('hoverTip').style.display = 'none'; brushCursor.visible = false; setHover(-1); });
function handleHover(e) {
  if (!S.M) return;
  const tip = $('hoverTip');
  if (S.mode !== 'group') { tip.style.display = 'none'; brushCursor.visible = false; return; }
  const hitT = pick(e, [trMesh]);
  if (S.painting && hitT) paint(hitT);
  let bone = -1, text = '';
  if (hitT) {
    const { v, local } = nearestCorner(hitT);
    bone = S.owner[v];
    const pv = S.patchVerts[S.M.patchA[v]].length, pc = S.pieceVerts[S.M.pieceA[v]].length;
    text = `trophy: <b>${boneLabel(bone)}</b>${S.stiff[v] ? ' · <b>keeps shape</b>' : ''} · patch ${pv} verts · piece ${pc} verts`;
    brushCursor.visible = S.tool === 'brush'; brushCursor.position.copy(local); brushCursor.scale.setScalar(S.brush);
  } else {
    brushCursor.visible = false;
    const hitD = pick(e, [dsMesh]);
    if (hitD) { bone = S.M.dsNodeA[hitD.face.a]; text = `game model: <b>${boneLabel(bone)}</b> <span style="color:#9aa1b1">${boneName(bone)}</span> — click to paint with it`; }
  }
  if (!S.painting) setHover(bone);
  const r = canvas.getBoundingClientRect();
  if (text) { tip.innerHTML = text; tip.style.display = 'block'; tip.style.left = `${e.clientX - r.left + 14}px`; tip.style.top = `${e.clientY - r.top + 14}px`; }
  else tip.style.display = 'none';
}

// ---------------- save
async function save() {
  $('save').disabled = true;
  $('loading').style.display = 'flex'; $('loading').textContent = 'Saving and refitting…';
  try {
    const M = await api(`/api/save?t=${encodeURIComponent(S.trophy)}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ owner: toB64(S.owner), stiff: toB64(Int32Array.from(S.stiff)), seams: S.seams }) });
    const keepUndo = S.undo, keepClip = S.clip;
    S.clips.clear();
    applyModel(M, true);
    S.undo = keepUndo; S.clip = keepClip;
    setStatus(`Saved and refitted ${S.trophy}. The app picks up the new rig next time it loads the trophy.`);
  } catch (err) { setStatus('Save failed: ' + err.message); alert('Save failed:\n' + err.message); }
  finally { $('save').disabled = false; $('loading').style.display = 'none'; }
}

// ---------------- modes & preview
async function enterPreview() {
  S.mode = 'preview';
  trGhost.visible = false; brushCursor.visible = false; $('hoverTip').style.display = 'none';
  $('preview').style.display = 'flex';
  S.clip = +$('clip').value;
  const C = await getClip(S.clip);
  $('frame').max = C.frames - 1; S.frame = Math.min(S.frame, C.frames - 1);
  showFrame();
  updateNotice();
}
function enterGroup() {
  S.mode = 'group'; S.playing = false; $('play').textContent = 'Play';
  $('preview').style.display = 'none';
  trGhost.visible = $('showGhost').checked;
  restTrophy(); poseDsIdle(); updateNotice();
}
function showFrame() {
  const C = S.clips.get(S.clip); if (!C) return;
  const f = Math.max(0, Math.min(C.frames - 1, Math.floor(S.frame)));
  poseDs(C, f); poseTrophy(C, f);
  $('frame').value = f; $('frameVal').textContent = `frame ${f} / ${C.frames - 1}`;
}
for (const b of document.querySelectorAll('#modeSeg button')) b.onclick = () => setMode(b.dataset.mode);
function setMode(m) {
  for (const b of document.querySelectorAll('#modeSeg button')) b.classList.toggle('on', b.dataset.mode === m);
  if (m === 'preview') enterPreview(); else enterGroup();
}
$('clip').onchange = async () => { S.clip = +$('clip').value; S.frame = 0; const C = await getClip(S.clip); $('frame').max = C.frames - 1; showFrame(); };
$('frame').oninput = () => { S.frame = +$('frame').value; showFrame(); };
$('play').onclick = () => { S.playing = !S.playing; $('play').textContent = S.playing ? 'Pause' : 'Play'; };
$('speed').onchange = () => { S.speed = +$('speed').value; };

// ---------------- controls
for (const b of document.querySelectorAll('#toolSeg button')) b.onclick = () => setTool(b.dataset.tool);
for (const b of document.querySelectorAll('#layerSeg button')) b.onclick = () => setLayer(b.dataset.layer);
function setLayer(l) {
  S.layer = l;
  for (const b of document.querySelectorAll('#layerSeg button')) b.classList.toggle('on', b.dataset.layer === l);
  $('activeHint').textContent = l === 'bone' ? 'Click or drag on the trophy to give it parts'
    : l === 'stiff' ? 'Painting KEEP SHAPE: these parts will never bend' : 'Painting LET BEND: clears keep-shape';
}
$('showStiff').onchange = () => recolor();
function setTool(t) {
  S.tool = t;
  for (const b of document.querySelectorAll('#toolSeg button')) b.classList.toggle('on', b.dataset.tool === t);
  $('brushRow').style.opacity = t === 'brush' ? 1 : 0.45;
}
$('brush').oninput = () => { S.brush = +$('brush').value; $('brushVal').textContent = S.brush.toFixed(3); };
$('brush').oninput();
$('showDs').onchange = () => { dsMesh.visible = $('showDs').checked; };
$('showGhost').onchange = () => { trGhost.visible = $('showGhost').checked && S.mode === 'group'; };
$('showBones').onchange = () => { dsBonesLine.visible = trBonesLine.visible = $('showBones').checked; };
$('undo').onclick = undo; $('redo').onclick = redo; $('save').onclick = save;
$('logToggle').onclick = (e) => { e.preventDefault(); const l = $('log'); const open = l.style.display !== 'block'; l.style.display = open ? 'block' : 'none'; $('logToggle').textContent = open ? 'Fit log ▾' : 'Fit log ▸'; };
window.addEventListener('keydown', (e) => {
  if (e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement) return;
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z') { e.preventDefault(); e.shiftKey ? redo() : undo(); }
  else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'y') { e.preventDefault(); redo(); }
  else if (e.key.toLowerCase() === 'b') setLayer('bone'); else if (e.key.toLowerCase() === 'k') setLayer('stiff'); else if (e.key.toLowerCase() === 'l') setLayer('soft');
  else if (e.key === '1') setTool('patch'); else if (e.key === '2') setTool('brush'); else if (e.key === '3') setTool('piece');
  else if (e.key === '[') { $('brush').value = Math.max(0.01, S.brush / 1.25); $('brush').oninput(); }
  else if (e.key === ']') { $('brush').value = Math.min(0.3, S.brush * 1.25); $('brush').oninput(); }
  else if (e.key.toLowerCase() === 'p') setMode(S.mode === 'group' ? 'preview' : 'group');
  else if (e.key === ' ' && S.mode === 'preview') { e.preventDefault(); $('play').onclick(); }
});
window.addEventListener('beforeunload', (e) => { if (S.dirty) { e.preventDefault(); e.returnValue = ''; } });

// ---------------- loop
let last = performance.now(), hoverTick = 0;
function loop(now) {
  const dt = (now - last) / 1000; last = now;
  if (hoverQueued && now - hoverTick > 30) { handleHover(hoverQueued); hoverQueued = null; hoverTick = now; }
  if (S.mode === 'preview' && S.playing) {
    const C = S.clips.get(S.clip);
    if (C) { S.frame = (S.frame + dt * 30 * S.speed) % C.frames; showFrame(); }
  }
  controls.update();
  renderer.render(scene, camera);
  requestAnimationFrame(loop);
}
resize();
requestAnimationFrame(loop);
setTool('patch');
loadList().catch((err) => { setStatus('Error: ' + err.message); $('loading').textContent = 'Could not reach the Rig Studio server: ' + err.message; });
