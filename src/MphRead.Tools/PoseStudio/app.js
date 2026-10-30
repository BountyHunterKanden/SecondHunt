// Pose Studio page. Matrices from the server are 4x4 row-major for ROW vectors (v' = v * M, translation in row 3), like
// OpenTK on the C# side; everything here keeps that convention. A piece's skin matrix = InvBind * K * World, exactly as the
// app lays the saved pose on its rig -- so the page shows what the Odin will play.
// Posing is a doll's: turning a piece carries everything that hangs off it -- the pieces AND their joints (the joints
// below get new rest offsets, OFF), so every piece stays on its joint in every clip. The page chains the pose's local
// matrices itself (fk) with those moved joints.
import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { TransformControls } from "three/addons/controls/TransformControls.js";

const $ = (id) => document.getElementById(id);
const b64f = (s) => new Float32Array(Uint8Array.from(atob(s), (c) => c.charCodeAt(0)).buffer);
const b64i = (s) => new Int32Array(Uint8Array.from(atob(s), (c) => c.charCodeAt(0)).buffer);
const f2b64 = (a) => { const u = new Uint8Array(a.buffer, a.byteOffset, a.byteLength); let s = ""; for (let i = 0; i < u.length; i += 0x8000) s += String.fromCharCode(...u.subarray(i, i + 0x8000)); return btoa(s); };
const status = (t) => ($("status").textContent = t);
const busy = (on, t = "loading…") => { $("busy").style.display = on ? "flex" : "none"; $("busy").textContent = t; };

// ---- row-vector 4x4 helpers --------------------------------------------------------------------------------------------
const I4 = () => new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
function mul(a, b) { const o = new Float32Array(16); for (let r = 0; r < 4; r++) for (let c = 0; c < 4; c++) { let s = 0; for (let k = 0; k < 4; k++) s += a[r * 4 + k] * b[k * 4 + c]; o[r * 4 + c] = s; } return o; }
function inv(m) {
  const e = new THREE.Matrix4().fromArray(m).invert(); return new Float32Array(e.elements);   // fromArray/elements agree (both our layout)
}
const T = (x, y, z) => { const m = I4(); m[12] = x; m[13] = y; m[14] = z; return m; };
const sub = (A, i) => A.subarray(i * 16, i * 16 + 16);
const trans = (m) => [m[12], m[13], m[14]];
const xp = (p, m) => [p[0] * m[0] + p[1] * m[4] + p[2] * m[8] + m[12], p[0] * m[1] + p[1] * m[5] + p[2] * m[9] + m[13], p[0] * m[2] + p[1] * m[6] + p[2] * m[10] + m[14]];
const about = (j, R) => mul(mul(T(-j[0], -j[1], -j[2]), R), T(j[0], j[1], j[2]));   // a turn R about the point j
const rotOf = (q) => new Float32Array(new THREE.Matrix4().makeRotationFromQuaternion(q).elements);   // column-major = our row-vector layout
function mirrorRot(R) { const s = [-1, 1, 1], o = I4(); for (let r = 0; r < 3; r++) for (let c = 0; c < 3; c++) o[r * 4 + c] = R[r * 4 + c] * s[r] * s[c]; return o; }

// ---- scene ----------------------------------------------------------------------------------------------------------------
const view = $("view");
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setPixelRatio(window.devicePixelRatio);
view.appendChild(renderer.domElement);
const scene = new THREE.Scene(); scene.background = new THREE.Color(0x16171a);
const camera = new THREE.PerspectiveCamera(35, 1, 0.01, 100);
const orbit = new OrbitControls(camera, renderer.domElement);
scene.add(new THREE.HemisphereLight(0xdfe6ff, 0x2a2622, 1.1));
const key = new THREE.DirectionalLight(0xffffff, 1.6); key.position.set(-2, 4, -5); scene.add(key);
const rim = new THREE.DirectionalLight(0x9fb4ff, 0.8); rim.position.set(3, 2, 4); scene.add(rim);
const floor = new THREE.GridHelper(8, 32, 0x3a3d44, 0x26282d); scene.add(floor);
const groups = { ds: new THREE.Group(), orig: new THREE.Group(), work: new THREE.Group() };
Object.values(groups).forEach((g) => scene.add(g));
const tc = new TransformControls(camera, renderer.domElement);
tc.setSpace("world"); tc.setSize(0.8); scene.add(tc);
const proxy = new THREE.Object3D(); groups.work.add(proxy);
function resize() { const w = view.clientWidth, h = view.clientHeight; renderer.setSize(w, h); camera.aspect = w / h; camera.updateProjectionMatrix(); }
window.addEventListener("resize", resize);

// ---- state ----------------------------------------------------------------------------------------------------------------
let M = null;            // decoded model
let K = null;            // Float32Array nb*16: the owner's per-piece turns (bone frame)
let OFF = null;          // Float32Array nb*3: moved joints (parent-local, offset / model scale), NaN = the rig's own
let LOC = null, ANIM = null;   // local matrices of the pose shown (or the clip frame), animated flags
let W = null, WD = null; // current rig / game bone worlds (nb*16)
let poseName = "rest", sel = -1, mirror = true, follow = true, rings = false, byBone = false, playing = null, dirty = false;
let undoStack = [], redoStack = [], dragStart = null, grab = null;
const meshes = {};
const snap = () => ({ K: K.slice(), OFF: OFF.slice() });
const restore = (s) => { K = s.K.slice(); OFF = s.OFF.slice(); fk(); };

const PALETTE = [0x4e79a7, 0xf28e2b, 0xe15759, 0x76b7b2, 0x59a14f, 0xedc948, 0xb07aa1, 0xff9da7, 0x9c755f, 0xbab0ac,
  0x86bcb6, 0x8cd17d, 0xb6992d, 0xf1ce63, 0x499894, 0xd37295, 0xfabfd2, 0xd4a6c8, 0x79706e, 0xa0cbe8];
const boneCol = (i) => new THREE.Color(PALETTE[i % PALETTE.length]);

async function loadList() {
  const list = await (await fetch("/api/list")).json();
  $("trophy").innerHTML = list.map((t) => `<option value="${t.name}">${t.name}${t.posed ? "  (your pose saved)" : ""}</option>`).join("");
  const want = new URLSearchParams(location.search).get("t");
  if (want) $("trophy").value = want;
  await loadTrophy($("trophy").value);
}

async function loadTrophy(name) {
  busy(true, `loading ${name}…`);
  const j = await (await fetch(`/api/model?t=${encodeURIComponent(name)}`)).json();
  M = {
    name, nb: j.bones.length, names: j.bones.map((b) => b.name), parent: j.bones.map((b) => b.parent), nv: j.vertices, baseRig: j.baseRig,
    sculpt: b64f(j.sculpt), bind: b64f(j.bind), col: b64f(j.col), tri: b64i(j.tri), bi: b64i(j.bi), bw: b64f(j.bw),
    invBind: b64f(j.invBind), dsLocal: b64f(j.dsLocal), dsNode: b64i(j.dsNode), partPos: b64f(j.partPos), partNode: b64i(j.partNode),
    anim: b64i(j.anim),
    poses: {
      rest: { rig: b64f(j.poses.rest.rig), local: b64f(j.poses.rest.local), ds: b64f(j.poses.rest.ds) },
      idle: { rig: b64f(j.poses.idle.rig), local: b64f(j.poses.idle.local), ds: b64f(j.poses.idle.ds) },
    },
    clips: j.clips,
  };
  K = b64f(j.k); OFF = b64f(j.off); W = new Float32Array(M.nb * 16);
  undoStack = []; redoStack = []; dirty = false; sel = -1; playing = null; grab = null;
  // parents before children; children lists
  M.kids = M.names.map(() => []); M.parent.forEach((p, i) => { if (p >= 0) M.kids[p].push(i); });
  M.order = []; { const placed = new Array(M.nb).fill(false); while (M.order.length < M.nb) for (let i = 0; i < M.nb; i++) if (!placed[i] && (M.parent[i] < 0 || placed[M.parent[i]])) { placed[i] = true; M.order.push(i); } }
  M.main = new Int32Array(M.nv);
  for (let v = 0; v < M.nv; v++) { let best = 0; for (let k = 1; k < 4; k++) if (M.bw[v * 4 + k] > M.bw[v * 4 + best]) best = k; M.main[v] = M.bi[v * 4 + best]; }
  M.counterpart = M.names.map((n) => M.names.indexOf(n.startsWith("L_") ? "R_" + n.slice(2) : n.startsWith("R_") ? "L_" + n.slice(2) : ""));
  M.hasPiece = new Array(M.nb).fill(false); for (let v = 0; v < M.nv; v++) M.hasPiece[M.main[v]] = true;
  let lo = 1e9, hi = -1e9; for (let v = 0; v < M.nv; v++) { lo = Math.min(lo, M.bind[v * 3 + 1]); hi = Math.max(hi, M.bind[v * 3 + 1]); }
  M.height = hi - lo;
  buildMeshes(); buildBoneList();
  $("clip").innerHTML = M.clips.map((c) => `<option value="${c.id}">${c.name} (${c.frames})</option>`).join("");
  $("clip").value = String(M.clips.find((c) => c.name === "WalkForward")?.id ?? 0);
  // the page's own chaining must give the server's skeleton exactly (no joints moved)
  { const keep = OFF; OFF = new Float32Array(M.nb * 3).fill(NaN); LOC = M.poses.rest.local; ANIM = M.anim; fk();
    let d = 0; for (let i = 0; i < W.length; i++) d = Math.max(d, Math.abs(W[i] - M.poses.rest.rig[i]));
    if (d > 1e-3) console.warn("page skeleton differs from the server's by", d); OFF = keep; }
  setPose(poseName); frameAll(); select(-1);
  let moved = 0; for (let b = 0; b < M.nb; b++) if (!Number.isNaN(OFF[b * 3])) moved++;
  status(`${name}: on its ${M.baseRig.toUpperCase()} rig, ${M.nv} points${moved ? `, ${moved} joints moved` : ""}`);
  busy(false);
}

function buildMeshes() {
  for (const k of Object.keys(meshes)) { meshes[k].parent?.remove(meshes[k]); meshes[k].geometry.dispose(); delete meshes[k]; }
  const off = M.height * 1.25;
  groups.ds.position.set(off, 0, 0); groups.orig.position.set(0, 0, 0); groups.work.position.set(-off, 0, 0);   // camera at -Z: +X is screen-left
  const mat = () => new THREE.MeshStandardMaterial({ vertexColors: true, metalness: 0.25, roughness: 0.55, side: THREE.DoubleSide });
  // statue as sculpted (static) and the working copy (skinned), sharing the triangle list
  for (const which of ["orig", "work"]) {
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.BufferAttribute(new Float32Array(M.nv * 3), 3));
    g.setAttribute("color", new THREE.BufferAttribute(new Float32Array(M.nv * 3), 3));
    g.setIndex(new THREE.BufferAttribute(new Uint32Array(M.tri), 1));
    const m = new THREE.Mesh(g, mat()); groups[which].add(m); meshes[which] = m;
  }
  meshes.orig.geometry.attributes.position.array.set(M.sculpt); meshes.orig.geometry.computeVertexNormals();
  // game model: non-indexed, flat, coloured by bone
  const nc = M.dsNode.length, gd = new THREE.BufferGeometry();
  gd.setAttribute("position", new THREE.BufferAttribute(new Float32Array(nc * 3), 3));
  const dc = new Float32Array(nc * 3); for (let c = 0; c < nc; c++) { const col = boneCol(M.dsNode[c]); dc[c * 3] = col.r; dc[c * 3 + 1] = col.g; dc[c * 3 + 2] = col.b; }
  gd.setAttribute("color", new THREE.BufferAttribute(dc, 3));
  meshes.ds = new THREE.Mesh(gd, new THREE.MeshStandardMaterial({ vertexColors: true, flatShading: true, metalness: 0, roughness: 0.8, side: THREE.DoubleSide }));
  groups.ds.add(meshes.ds);
  // game parts the rig adds to the statue (Weavel's gun block): ride their bone, not turned by K
  if (M.partNode.length) {
    const gp = new THREE.BufferGeometry(); gp.setAttribute("position", new THREE.BufferAttribute(new Float32Array(M.partNode.length * 3), 3));
    meshes.parts = new THREE.Mesh(gp, new THREE.MeshStandardMaterial({ color: 0x8a8f99, flatShading: true, side: THREE.DoubleSide }));
    groups.work.add(meshes.parts);
  }
  // joints of the working copy
  const jg = new THREE.BufferGeometry(); jg.setAttribute("position", new THREE.BufferAttribute(new Float32Array(M.nb * 3), 3));
  meshes.joints = new THREE.Points(jg, new THREE.PointsMaterial({ color: 0xffcf5a, size: 0.035, depthTest: false }));
  meshes.joints.renderOrder = 5; groups.work.add(meshes.joints);
  paintColors();
}

function paintColors() {
  const hl = new THREE.Color(0xffcf5a);
  for (const which of ["orig", "work"]) {
    const c = meshes[which].geometry.attributes.color.array;
    for (let v = 0; v < M.nv; v++) {
      let r, g, b;
      if (byBone) { const bc = boneCol(M.main[v]); r = bc.r; g = bc.g; b = bc.b; }
      else { r = M.col[v * 3]; g = M.col[v * 3 + 1]; b = M.col[v * 3 + 2]; }
      if (sel >= 0 && (M.main[v] === sel || (mirror && M.main[v] === M.counterpart[sel]))) { r = r * 0.45 + hl.r * 0.55; g = g * 0.45 + hl.g * 0.55; b = b * 0.45 + hl.b * 0.55; }
      c[v * 3] = r; c[v * 3 + 1] = g; c[v * 3 + 2] = b;
    }
    meshes[which].geometry.attributes.color.needsUpdate = true;
  }
}

// ---- the skeleton: the pose's locals chained, with the owner's moved joints ---------------------------------------------
function fk() {
  for (const i of M.order) {
    if (!ANIM[i]) { W.set(I4(), i * 16); continue; }
    const L = LOC.slice(i * 16, i * 16 + 16);
    if (!Number.isNaN(OFF[i * 3])) { L[12] = OFF[i * 3]; L[13] = OFF[i * 3 + 1]; L[14] = OFF[i * 3 + 2]; }
    W.set(M.parent[i] >= 0 ? mul(L, sub(W, M.parent[i])) : L, i * 16);
  }
}
function subtree(b) { const out = [b]; for (let i = 0; i < out.length; i++) for (const c of M.kids[out[i]]) out.push(c); return out; }   // parents first

// Turn bone b's piece by R (rig space) about its joint, in the pose shown. With children following (a doll), everything
// below turns along: each joint below is carried round (its offset from its parent re-measured) and each piece keeps its
// place on it. Folded into K (bone frame): K_new = K * (W_old * C * W_new^-1).
function turnSubtree(b, R, fol) {
  const C = about(trans(sub(W, b)), R), W0 = W.slice(), list = fol ? subtree(b) : [b];
  if (fol) for (const d of list) {
    if (d === b || !ANIM[d]) continue;
    const p = M.parent[d], t = xp(xp(trans(sub(W0, d)), C), inv(sub(W, p)));
    OFF[d * 3] = t[0]; OFF[d * 3 + 1] = t[1]; OFF[d * 3 + 2] = t[2];
    const L = LOC.slice(d * 16, d * 16 + 16); L[12] = t[0]; L[13] = t[1]; L[14] = t[2];
    W.set(mul(L, sub(W, p)), d * 16);
  }
  for (const d of list) K.set(mul(sub(K, d), mul(mul(sub(W0, d), C), inv(sub(W, d)))), d * 16);
}
function turn(b, R, fol = follow) {
  turnSubtree(b, R, fol);
  const m = M.counterpart[b]; if (mirror && m >= 0 && m !== b) turnSubtree(m, mirrorRot(R), fol);
}

// ---- skinning -------------------------------------------------------------------------------------------------------------
function skinMats() { const S = []; for (let b = 0; b < M.nb; b++) S.push(mul(mul(sub(M.invBind, b), sub(K, b)), sub(W, b))); return S; }
function skinPoint(v) {
  const x = [M.bind[v * 3], M.bind[v * 3 + 1], M.bind[v * 3 + 2]], o = [0, 0, 0];
  for (let k = 0; k < 4; k++) {
    const w = M.bw[v * 4 + k]; if (w <= 0) continue; const b = M.bi[v * 4 + k];
    const q = xp(x, mul(mul(sub(M.invBind, b), sub(K, b)), sub(W, b))); o[0] += w * q[0]; o[1] += w * q[1]; o[2] += w * q[2];
  }
  return o;
}
function skinWork() {
  const S = skinMats();
  const p = meshes.work.geometry.attributes.position.array;
  for (let v = 0; v < M.nv; v++) {
    const x = M.bind[v * 3], y = M.bind[v * 3 + 1], z = M.bind[v * 3 + 2]; let ox = 0, oy = 0, oz = 0;
    for (let k = 0; k < 4; k++) {
      const w = M.bw[v * 4 + k]; if (w <= 0) continue; const m = S[M.bi[v * 4 + k]];
      ox += w * (x * m[0] + y * m[4] + z * m[8] + m[12]); oy += w * (x * m[1] + y * m[5] + z * m[9] + m[13]); oz += w * (x * m[2] + y * m[6] + z * m[10] + m[14]);
    }
    p[v * 3] = ox; p[v * 3 + 1] = oy; p[v * 3 + 2] = oz;
  }
  meshes.work.geometry.attributes.position.needsUpdate = true; meshes.work.geometry.computeVertexNormals(); meshes.work.geometry.computeBoundingSphere();
  if (meshes.parts) {
    const pp = meshes.parts.geometry.attributes.position.array, P = [];
    for (let b = 0; b < M.nb; b++) P.push(mul(sub(M.invBind, b), sub(W, b)));
    for (let c = 0; c < M.partNode.length; c++) {
      const m = P[M.partNode[c]], x = M.partPos[c * 3], y = M.partPos[c * 3 + 1], z = M.partPos[c * 3 + 2];
      pp[c * 3] = x * m[0] + y * m[4] + z * m[8] + m[12]; pp[c * 3 + 1] = x * m[1] + y * m[5] + z * m[9] + m[13]; pp[c * 3 + 2] = x * m[2] + y * m[6] + z * m[10] + m[14];
    }
    meshes.parts.geometry.attributes.position.needsUpdate = true; meshes.parts.geometry.computeVertexNormals();
  }
  const jp = meshes.joints.geometry.attributes.position.array;
  for (let b = 0; b < M.nb; b++) { const t = trans(sub(W, b)); jp[b * 3] = t[0]; jp[b * 3 + 1] = t[1]; jp[b * 3 + 2] = t[2]; }
  meshes.joints.geometry.attributes.position.needsUpdate = true;
}
function skinDs() {
  const p = meshes.ds.geometry.attributes.position.array;
  for (let c = 0; c < M.dsNode.length; c++) {
    const m = sub(WD, M.dsNode[c]), x = M.dsLocal[c * 3], y = M.dsLocal[c * 3 + 1], z = M.dsLocal[c * 3 + 2];
    p[c * 3] = x * m[0] + y * m[4] + z * m[8] + m[12]; p[c * 3 + 1] = x * m[1] + y * m[5] + z * m[9] + m[13]; p[c * 3 + 2] = x * m[2] + y * m[6] + z * m[10] + m[14];
  }
  meshes.ds.geometry.attributes.position.needsUpdate = true; meshes.ds.geometry.computeVertexNormals();
}
function setPose(name) {
  poseName = name; LOC = M.poses[name].local; ANIM = M.anim; WD = M.poses[name].ds; playing = null;
  $("poseRest").classList.toggle("on", name === "rest"); $("poseIdle").classList.toggle("on", name === "idle");
  fk(); skinWork(); skinDs(); placeGizmo();
}
const changed = () => { dirty = true; skinWork(); markTurned(); };

function select(b) {
  sel = b;
  document.querySelectorAll("#bones button").forEach((el) => el.classList.toggle("sel", +el.dataset.b === b));
  paintColors(); placeGizmo();
  if (b >= 0) status(`${M.names[b]}${mirror && M.counterpart[b] >= 0 ? " + " + M.names[M.counterpart[b]] + " (mirror)" : ""}`);
}
function placeGizmo() {
  if (!rings || sel < 0 || playing) { tc.detach(); return; }
  const t = trans(sub(W, sel)); proxy.position.set(t[0], t[1], t[2]); proxy.quaternion.identity(); proxy.updateMatrixWorld();
  tc.attach(proxy);
}

// ---- rings (optional, precise): a turn of the selected piece (+ what hangs off it) / a move of the piece alone ------------
function applyRings() {
  if (!dragStart || sel < 0) return;
  restore(dragStart);
  if (tc.getMode() === "rotate") turn(sel, rotOf(proxy.quaternion));
  else {
    const d = [proxy.position.x - dragStart.pos[0], proxy.position.y - dragStart.pos[1], proxy.position.z - dragStart.pos[2]];
    for (const [b, s] of [[sel, 1], [mirror ? M.counterpart[sel] : -1, -1]]) {
      if (b < 0 || (s < 0 && b === sel)) continue;
      const Wb = sub(W, b); K.set(mul(sub(K, b), mul(mul(Wb, T(d[0] * s, d[1], d[2])), inv(Wb))), b * 16);
    }
  }
  changed();
}
tc.addEventListener("dragging-changed", (e) => {
  orbit.enabled = !e.value;
  if (e.value) { undoStack.push(snap()); redoStack = []; dragStart = { ...snap(), pos: [proxy.position.x, proxy.position.y, proxy.position.z] }; }
  else { dragStart = null; placeGizmo(); }
});
tc.addEventListener("objectChange", () => applyRings());

function markTurned() {
  document.querySelectorAll("#bones button").forEach((el) => {
    const b = +el.dataset.b; let d = 0; for (let i = 0; i < 16; i++) d += Math.abs(K[b * 16 + i] - (i % 5 === 0 ? 1 : 0));
    el.classList.toggle("turned", d > 1e-4);
  });
}
function buildBoneList() {
  $("bones").innerHTML = M.names.map((n, b) => M.hasPiece[b] ? `<button data-b="${b}">${n.replace("_SDK", "").replace("varias2", "pad")}</button>` : "").join("");
  document.querySelectorAll("#bones button").forEach((el) => (el.onclick = () => select(+el.dataset.b)));
  markTurned();
}

// ---- hands on: pull a limb (left-drag), spin a piece (right-drag, or left-drag on the body) ------------------------------
// The limb a piece belongs to, as the joints a pull bends: nearest first, up to the shoulder / hip. A hand or foot pulled
// keeps its own angle (its wrist / ankle is not bent); anything else also bends at its own joint.
const LIMB_ROOT = /^(L|R)_(shoulder|hip)$/;
function limbChain(g) {
  const chain = []; let b = g;
  while (b >= 0 && !LIMB_ROOT.test(M.names[b])) b = M.parent[b];
  if (b < 0) return [];                                    // not in a limb: body, head, pads
  if (!/wrist|ankle/.test(M.names[g])) chain.push(g);
  for (let a = M.parent[g]; a >= 0 && chain[chain.length - 1] !== b; a = M.parent[a]) { chain.push(a); if (a === b) break; }
  if (!chain.length) chain.push(b);
  return chain;
}
const ray = new THREE.Raycaster();
function ndc(e) { const r = renderer.domElement.getBoundingClientRect(); return new THREE.Vector2(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1); }
function screenOf(pLocal) {
  const v = groups.work.localToWorld(new THREE.Vector3(...pLocal)).project(camera), r = renderer.domElement.getBoundingClientRect();
  return [r.left + (v.x + 1) / 2 * r.width, r.top + (1 - v.y) / 2 * r.height];
}
function arc(x, y) {   // the arcball: a point on a sphere round the joint (camera space); outside it, a twist about the view
  let px = (x - grab.c[0]) / grab.R, py = (grab.c[1] - y) / grab.R; const d = px * px + py * py;
  if (d > 1) { const s = 1 / Math.sqrt(d); return new THREE.Vector3(px * s, py * s, 0); }
  return new THREE.Vector3(px, py, Math.sqrt(1 - d));
}
function planePoint(e) {
  ray.setFromCamera(ndc(e), camera); const hit = new THREE.Vector3();
  return ray.ray.intersectPlane(grab.plane, hit) ? groups.work.worldToLocal(hit) : null;
}
// CCD: each joint of the limb in turn swings what hangs off it so the grabbed point heads for the target
function pullTo(target) {
  for (let it = 0; it < 12; it++) {
    let moved = 0;
    for (const J of grab.chain) {
      const j = trans(sub(W, J)), e = skinPoint(grab.v);
      const a = new THREE.Vector3(e[0] - j[0], e[1] - j[1], e[2] - j[2]), t = new THREE.Vector3(target[0] - j[0], target[1] - j[1], target[2] - j[2]);
      if (a.lengthSq() < 1e-10 || t.lengthSq() < 1e-10) continue;
      a.normalize(); t.normalize();
      const ang = Math.acos(Math.min(1, Math.max(-1, a.dot(t)))); if (ang < 2e-4) continue;
      const q = new THREE.Quaternion().setFromUnitVectors(a, t);
      if (ang > 0.25) q.slerpQuaternions(new THREE.Quaternion(), q, 0.25 / ang);   // small steps: the limb bends, not flips
      turn(J, rotOf(q), true); moved += ang;
    }
    if (moved < 1e-3) break;
  }
}

view.addEventListener("pointerdown", (e) => {
  if (!M || playing || (e.button !== 0 && e.button !== 2)) return;
  if (rings && tc.object && tc.axis) return;               // on a ring: the rings have it
  ray.setFromCamera(ndc(e), camera);
  const hit = ray.intersectObject(meshes.work, false)[0];
  if (!hit) return;                                        // empty space: orbit / pan as usual
  e.stopPropagation(); e.preventDefault();
  grab = { pending: true, x0: e.clientX, y0: e.clientY, button: e.button, v: hit.face.a, g: M.main[hit.face.a], hitW: hit.point.clone() };
}, { capture: true });
window.addEventListener("pointermove", (e) => {
  if (!grab) return;
  if (grab.pending) {
    if (Math.hypot(e.clientX - grab.x0, e.clientY - grab.y0) < 4) return;
    grab.pending = false; undoStack.push(snap()); redoStack = [];
    const chain = limbChain(grab.g);
    grab.mode = grab.button === 0 && chain.length ? "pull" : "spin";
    select(grab.g);
    if (grab.mode === "pull") {
      grab.chain = chain; grab.eff0 = skinPoint(grab.v);
      const n = new THREE.Vector3(); camera.getWorldDirection(n); grab.plane = new THREE.Plane().setFromNormalAndCoplanarPoint(n, grab.hitW);
      grab.p0 = groups.work.worldToLocal(grab.hitW.clone());
    } else {
      const r = renderer.domElement.getBoundingClientRect();
      grab.c = screenOf(trans(sub(W, grab.g))); grab.R = 0.3 * Math.min(r.width, r.height); grab.last = arc(grab.x0, grab.y0);
    }
  }
  if (grab.mode === "pull") {
    const p = planePoint(e); if (!p) return;
    pullTo([grab.eff0[0] + p.x - grab.p0.x, grab.eff0[1] + p.y - grab.p0.y, grab.eff0[2] + p.z - grab.p0.z]);
  } else {
    const v1 = arc(e.clientX, e.clientY), q = new THREE.Quaternion().setFromUnitVectors(grab.last, v1); grab.last = v1;
    const cq = camera.quaternion; q.premultiply(cq).multiply(cq.clone().invert());   // camera space -> the figure's
    turn(grab.g, rotOf(q));
  }
  changed();
});
window.addEventListener("pointerup", () => {
  if (!grab) return;
  if (grab.pending) select(grab.g);
  grab = null; placeGizmo();
});

// ---- animation preview ----------------------------------------------------------------------------------------------------
async function play() {
  busy(true, "fetching the clip…");
  const c = +$("clip").value; const j = await (await fetch(`/api/clip?t=${encodeURIComponent(M.name)}&c=${c}`)).json(); busy(false);
  playing = { frames: j.frames, local: b64f(j.local), anim: b64i(j.anim), ds: b64f(j.ds), t: 0 }; tc.detach();
}
function stepPlay(dt) {
  if (!playing) return;
  playing.t += dt * 30 * +$("speed").value; const f = Math.floor(playing.t) % playing.frames, n = M.nb * 16;
  LOC = playing.local.subarray(f * n, f * n + n); ANIM = playing.anim; WD = playing.ds.subarray(f * n, f * n + n);
  fk(); skinWork(); skinDs();
}

// ---- buttons & keys -------------------------------------------------------------------------------------------------------
$("trophy").onchange = (e) => { if (dirty && !confirm("Unsaved pose changes -- switch anyway?")) { e.target.value = M.name; return; } loadTrophy(e.target.value); };
$("poseRest").onclick = () => setPose("rest"); $("poseIdle").onclick = () => setPose("idle");
const setMode = (m) => { tc.setMode(m); $("modeRot").classList.toggle("on", m === "rotate"); $("modeMove").classList.toggle("on", m === "translate"); };
$("modeRot").onclick = () => setMode("rotate"); $("modeMove").onclick = () => setMode("translate");
setMode("rotate");
$("rings").onclick = () => { rings = !rings; $("rings").classList.toggle("on", rings); $("ringTools").style.display = rings ? "" : "none"; placeGizmo(); };
$("follow").onclick = () => { follow = !follow; $("follow").classList.toggle("on", follow); };
$("mirror").onclick = () => { mirror = !mirror; $("mirror").classList.toggle("on", mirror); paintColors(); };
$("space").onclick = () => { const s = tc.space === "world" ? "local" : "world"; tc.setSpace(s); $("space").textContent = "Axes: " + (s === "world" ? "world" : "piece"); };
$("resetPiece").onclick = () => {
  if (sel < 0) return; undoStack.push(snap()); redoStack = [];
  for (const b of [sel, mirror ? M.counterpart[sel] : -1]) if (b >= 0)
    for (const d of subtree(b)) { K.set(I4(), d * 16); if (d !== b) OFF.fill(NaN, d * 3, d * 3 + 3); }
  fk(); changed(); placeGizmo();
};
$("resetAll").onclick = () => {
  if (!confirm("Reset every piece and joint to the rig as it was?")) return; undoStack.push(snap()); redoStack = [];
  for (let b = 0; b < M.nb; b++) K.set(I4(), b * 16); OFF.fill(NaN); fk(); changed(); placeGizmo();
};
function undo() { if (!undoStack.length) return; redoStack.push(snap()); restore(undoStack.pop()); skinWork(); markTurned(); placeGizmo(); }
function redo() { if (!redoStack.length) return; undoStack.push(snap()); restore(redoStack.pop()); skinWork(); markTurned(); placeGizmo(); }
$("undo").onclick = undo; $("redo").onclick = redo;
$("play").onclick = play; $("stop").onclick = () => setPose(poseName);
$("colBones").onclick = () => { byBone = !byBone; $("colBones").classList.toggle("on", byBone); paintColors(); };
$("showOrig").onclick = () => { groups.orig.visible = !groups.orig.visible; $("showOrig").classList.toggle("on", groups.orig.visible); };
$("save").onclick = async () => {
  busy(true, "saving…");
  const r = await (await fetch(`/api/save?t=${encodeURIComponent(M.name)}`, { method: "POST", body: JSON.stringify({ k: f2b64(K), off: f2b64(OFF) }) })).json();
  busy(false); dirty = false; status(`saved ${r.path}: ${r.turned} pieces turned, ${r.moved} joints moved. Push it to the device to see it there.`);
};
window.addEventListener("keydown", (e) => {
  if (e.target.tagName === "SELECT") return;
  if (e.ctrlKey && e.key === "z") { undo(); e.preventDefault(); } else if (e.ctrlKey && e.key === "y") { redo(); e.preventDefault(); }
  else if (e.key === "r") setMode("rotate"); else if (e.key === "g") setMode("translate");
  else if (e.key === "m") $("mirror").click(); else if (e.key === "q") $("space").click(); else if (e.key === "c") $("follow").click();
  else if (e.key === "Escape") select(-1);
});
window.addEventListener("beforeunload", (e) => { if (dirty) { e.preventDefault(); e.returnValue = ""; } });
renderer.domElement.addEventListener("contextmenu", (e) => e.preventDefault());

function frameAll() {
  // all three figures in view: fit their total width and one figure's height, whichever needs the camera further back
  const h = M.height, off = h * 1.25, span = 2 * off + h * 1.1;
  const vf = THREE.MathUtils.degToRad(camera.fov) / 2, hf = Math.atan(Math.tan(vf) * camera.aspect);
  const dist = Math.max((span / 2) / Math.tan(hf), (h * 0.62) / Math.tan(vf)) * 1.05;
  camera.position.set(0, h * 0.55, -dist); orbit.target.set(0, h * 0.5, 0); orbit.update();
  floor.position.y = 0; floor.scale.setScalar(off / 1.2);
}
// focus on the working copy (it's what you pose): the camera closer, still facing the figures
function frameWork() {
  const h = M.height, off = h * 1.25, vf = THREE.MathUtils.degToRad(camera.fov) / 2, dist = (h * 0.62) / Math.tan(vf) * 1.1;
  camera.position.set(-off, h * 0.55, -dist); orbit.target.set(-off, h * 0.5, 0); orbit.update();
}
window.addEventListener("keydown", (e) => { if (e.key === "f" && M) frameAll(); if (e.key === "w" && M) frameWork(); });
$("frameAll").onclick = () => frameAll(); $("frameWork").onclick = () => frameWork();

let last = performance.now();
renderer.setAnimationLoop((now) => { const dt = (now - last) / 1000; last = now; stepPlay(dt); renderer.render(scene, camera); });
resize(); loadList().catch((e) => { busy(false); status("error: " + e.message); console.error(e); });
