using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using OpenTK.Mathematics;

namespace MphRecomp.Render
{
    // Bakes an MPH DS model's display lists into flat GL-ready triangle batches (grouped by texture).
    // Lives in Core (not the Android app) so the headless tools can validate it against real ROM data.
    //
    // Two output layouts:
    //  - WORLD-BAKED (Skinned=false, 11 floats/vertex: pos3 normal3 uv2 colour3): every vertex is
    //    pushed through its node / matrix-stack bone ON THE CPU at bake time -- rooms, props, and the
    //    old static idle-posed hunter statues. Pose is frozen into the vertices.
    //  - SKINNED (Skinned=true, 12 floats/vertex: + bone slot): skeletal models only. Vertices stay in
    //    their bone's LOCAL space, tagged with the DS matrix-stack slot (the MTX_RESTORE index) they
    //    bind to, so the GPU can re-pose them every frame from a bone-matrix palette
    //    (Model.UpdateMatrixStack order). Skinning math must equal the world-baked path exactly:
    //    world = (bone * local) * model.Scale -- see the -animtest equivalence check.
    public static class GeometryBaker
    {
        public const int StrideWorld = 11, StrideSkinned = 12;

        public sealed class Batch { public int[]? Pixels; public int W, H; public bool Texgen; public bool Metallic; public bool Unlit; public bool Textured; public bool MultiTex; public int MatKind; public bool CombineSwap; public float LerpScale = 2f; public float ReflScale = 2f; public float UnlitScale = 1f; public float[]? ColB; public float[] Ambient = { 1f, 1f, 1f }; public int[]? IncandPixels; public int IncandW, IncandH; public int[]? SpecPixels; public int SpecW, SpecH; public int[]? ReflPixels; public int ReflW, ReflH; public bool AlphaCutout; public int[]? OpacityPixels; public int OpacityW, OpacityH; public bool Additive; public bool Translucent; public MphRecomp.Assets.GxMaterial? Gx; public float[]? GxAux; public int[]?[]? GxPixels; public int[]? GxW; public int[]? GxH; public int[]? GxTexGen; public bool Jiggle; public float JigTop, JigBot; public bool Skinned; public int Stride => Skinned ? StrideSkinned : StrideWorld; public readonly List<float> Verts = new(); }

        // x,y,z = bake-pose world position; lx,ly,lz = bone-local position (skinned); nx..nz = bake-pose
        // world normal (0 = none); lnx..lnz = normal in the vertex's own bone frame (skinned); bone = slot.
        struct V { public float x, y, z, r, g, b, u, v, nx, ny, nz, lx, ly, lz, lnx, lny, lnz; public int bone; }

        static int S16(uint x) { int r = (int)(x & 0xFFFF); if ((r & 0x8000) != 0) r |= unchecked((int)0xFFFF0000); return r; }
        static int S10(uint x) { int r = (int)(x & 0x3FF); if ((r & 0x200) != 0) r |= unchecked((int)0xFFFFFC00); return r; }

        // skinned: requires useMatrixStack and a model with a matrix stack (hunters); bakes the pose
        // currently in node.Animation (useAnimation) only for bounds + the rare face-normal fallback.
        public static List<Batch> Bake(Model model, float[] centerOut, out float radius, bool useAnimation = false, int recolor = 0,
            bool useMatrixStack = false, bool skipDisabledNodes = false, bool skinned = false)
        {
            if (!useAnimation) model.ComputeNodeMatrices(0); // bind pose; else node.Animation is already posed
            int rc = (recolor >= 0 && recolor < model.Recolors.Count) ? recolor : 0;
            float scale = model.Scale.X;
            // Skeletal models (hunters) put ALL their mesh geometry on ONE node but bind each vertex
            // to a bone via the DS MTX_RESTORE command, selecting Nodes[NodeMatrixIds[id]] from the
            // matrix stack. Rooms deliberately DON'T use the stack (matrixId stays 0 in MphRead), and
            // rigid props/pickups map 1:1 so the stack is a no-op for them -- verified. So: use the
            // stack for non-room models; per-node otherwise.
            bool stack = useMatrixStack && model.NodeMatrixIds.Count > 0;
            if (skinned && !stack)
                throw new ArgumentException($"skinned bake needs a matrix-stack model ('{model.Name}' has none or useMatrixStack=false)");
            Matrix4 BoneMat(int mid)
            {
                int bone = model.NodeMatrixIds[mid];
                return useAnimation ? model.Nodes[bone].Animation : model.Nodes[bone].Transform;
            }
            // Inverse of a slot's bake-pose rotation/scale -- expresses a world-space normal in that
            // bone's local frame. Only needed when a normal and its vertex sit on different slots, or
            // for the face-normal fallback; the common case keeps the DS local normal untouched.
            var invRot = new Matrix3?[stack ? model.NodeMatrixIds.Count : 0];
            Vector3 ToBoneLocal(int slot, Vector3 wn)
            {
                if (invRot[slot] == null)
                {
                    var m3 = new Matrix3(BoneMat(slot));
                    invRot[slot] = MathF.Abs(m3.Determinant) > 1e-12f ? m3.Inverted() : Matrix3.Identity;
                }
                Vector3 l = wn * invRot[slot]!.Value; // row-vector convention, matching TransformPosition
                float len = l.Length;
                return len > 1e-8f ? l / len : Vector3.UnitY;
            }
            Matrix4 curMat = Matrix4.Identity;
            int curSlot = 0;
            var byKey = new Dictionary<long, Batch>();
            List<float> outv = new();
            float minX = 1e9f, minY = 1e9f, minZ = 1e9f, maxX = -1e9f, maxY = -1e9f, maxZ = -1e9f;
            var group = new List<V>(64);

            void Push(in V p, float nx, float ny, float nz, float lnx, float lny, float lnz)
            {
                if (skinned)
                {
                    outv.Add(p.lx); outv.Add(p.ly); outv.Add(p.lz); outv.Add(lnx); outv.Add(lny); outv.Add(lnz);
                    outv.Add(p.u); outv.Add(p.v); outv.Add(p.r); outv.Add(p.g); outv.Add(p.b); outv.Add(p.bone);
                }
                else
                {
                    outv.Add(p.x); outv.Add(p.y); outv.Add(p.z); outv.Add(nx); outv.Add(ny); outv.Add(nz);
                    outv.Add(p.u); outv.Add(p.v); outv.Add(p.r); outv.Add(p.g); outv.Add(p.b);
                }
                if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.z < minZ) minZ = p.z;
                if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; if (p.z > maxZ) maxZ = p.z;
            }
            static bool HasN(in V v) => v.nx * v.nx + v.ny * v.ny + v.nz * v.nz > 1e-8f;
            void Tri(in V a, in V b, in V c)
            {
                // smooth: use the DS per-vertex normals when present; else a computed face normal (flat)
                if (HasN(a) && HasN(b) && HasN(c))
                {
                    Push(a, a.nx, a.ny, a.nz, a.lnx, a.lny, a.lnz); Push(b, b.nx, b.ny, b.nz, b.lnx, b.lny, b.lnz); Push(c, c.nx, c.ny, c.nz, c.lnx, c.lny, c.lnz);
                    return;
                }
                FaceNormalFallbacks++;
                float ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z, vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz); if (len < 1e-6f) len = 1f;
                nx /= len; ny /= len; nz /= len;
                if (skinned)
                {
                    // the triangle may span several bones: express the bake-pose face normal in each
                    // vertex's own bone frame (exact at the bake pose, deforms with the bone after)
                    var fn = new Vector3(nx, ny, nz);
                    Vector3 la = ToBoneLocal(a.bone, fn), lb = ToBoneLocal(b.bone, fn), lc = ToBoneLocal(c.bone, fn);
                    Push(a, nx, ny, nz, la.X, la.Y, la.Z); Push(b, nx, ny, nz, lb.X, lb.Y, lb.Z); Push(c, nx, ny, nz, lc.X, lc.Y, lc.Z);
                    return;
                }
                Push(a, nx, ny, nz, 0, 0, 0); Push(b, nx, ny, nz, 0, 0, 0); Push(c, nx, ny, nz, 0, 0, 0);
            }
            void Flush(int prim)
            {
                int n = group.Count;
                if (prim == 0) { for (int i = 0; i + 2 < n; i += 3) Tri(group[i], group[i + 1], group[i + 2]); }
                else if (prim == 1) { for (int i = 0; i + 3 < n; i += 4) { Tri(group[i], group[i + 1], group[i + 2]); Tri(group[i], group[i + 2], group[i + 3]); } }
                else if (prim == 2) { for (int i = 0; i + 2 < n; i++) { if ((i & 1) == 0) Tri(group[i], group[i + 1], group[i + 2]); else Tri(group[i + 1], group[i], group[i + 2]); } }
                else if (prim == 3) { for (int i = 0; i + 3 < n; i += 2) { Tri(group[i], group[i + 1], group[i + 3]); Tri(group[i], group[i + 3], group[i + 2]); } }
                group.Clear();
            }

            FaceNormalFallbacks = 0; NormalSlotMismatches = 0;
            foreach (Node node in model.Nodes)
            {
                if (skipDisabledNodes && !node.Enabled) continue; // room layer filtering (FilterNodes)
                curMat = useAnimation ? node.Animation : node.Transform;
                foreach (int meshId in node.GetMeshIds())
                {
                    Mesh mesh = model.Meshes[meshId];
                    if (!mesh.Visible) continue;
                    Material mat = model.Materials[mesh.MaterialId];
                    int texId = mat.TextureId, palId = mat.PaletteId;
                    bool texgen = mat.TexgenMode == TexgenMode.Normal;
                    // key by texgen too: env-mapped and plain uses of one texture need separate batches
                    long key = (texId < 0 ? -1 : ((long)texId << 20) | (uint)palId) ^ (texgen ? (1L << 42) : 0);
                    if (!byKey.TryGetValue(key, out Batch? batch))
                    {
                        batch = new Batch { Texgen = texgen, Skinned = skinned };
                        if (texId >= 0)
                        {
                            Texture tex = model.Recolors[rc].Textures[texId];
                            batch.W = tex.Width; batch.H = tex.Height;
                            batch.Pixels = model.GetPixels(texId, palId, rc).Select(p => (int)p.ToUint()).ToArray();
                        }
                        byKey[key] = batch;
                    }
                    int tw = batch.W, th = batch.H;

                    outv = batch.Verts; // Push writes into this batch
                    IReadOnlyList<RenderInstruction> list = model.RenderInstructionLists[mesh.DlistId];
                    float px = 0, py = 0, pz = 0, cr = 1f, cg = 1f, cb = 1f, cu = texgen ? 0.5f : 0f, cv = texgen ? 0.5f : 0f;
                    float cnx = 0f, cny = 0f, cnz = 0f; // current DS vertex normal (world space, 0 = unset)
                    float clx = 0f, cly = 0f, clz = 0f; // same normal in the frame of the slot active at NORMAL time
                    int nSlot = -1;                     // matrix-stack slot active when that NORMAL was issued
                    int prim = -1; group.Clear();
                    if (stack) { curMat = BoneMat(0); curSlot = 0; } // skeletal: start on matrix-stack slot 0, updated by MTX_RESTORE
                    foreach (RenderInstruction ins in list)
                    {
                        IReadOnlyList<uint> a = ins.Arguments;
                        switch (ins.Code)
                        {
                            case InstructionCode.MTX_RESTORE:
                                // select the bone matrix for the vertices that follow (skeletal models only)
                                if (stack) { int mid = (int)a[0]; if (mid >= 0 && mid < model.NodeMatrixIds.Count) { curMat = BoneMat(mid); curSlot = mid; } }
                                break;
                            case InstructionCode.BEGIN_VTXS: prim = (int)a[0]; group.Clear(); break;
                            case InstructionCode.END_VTXS: Flush(prim); prim = -1; break;
                            case InstructionCode.COLOR:
                            case InstructionCode.DIF_AMB:
                                { uint rgb = a[0]; cr = (rgb & 0x1F) / 31f; cg = ((rgb >> 5) & 0x1F) / 31f; cb = ((rgb >> 10) & 0x1F) / 31f; break; }
                            case InstructionCode.TEXCOORD:
                                { if (tw > 0 && th > 0) { int s = S16(a[0] & 0xFFFF), tt = S16((a[0] >> 16) & 0xFFFF); cu = s / 16f / tw; cv = tt / 16f / th; } break; }
                            case InstructionCode.NORMAL:
                                {
                                    // DS normal: 3x signed 10-bit / 512, rotated into world space by the node transform
                                    float ln0 = S10(a[0] & 0x3FF) / 512f, ln1 = S10((a[0] >> 10) & 0x3FF) / 512f, ln2 = S10((a[0] >> 20) & 0x3FF) / 512f;
                                    var wn = Vector3.TransformPosition(new Vector3(ln0, ln1, ln2), curMat)
                                           - new Vector3(curMat.M41, curMat.M42, curMat.M43);
                                    float nl = wn.Length; if (nl > 1e-6f) { cnx = wn.X / nl; cny = wn.Y / nl; cnz = wn.Z / nl; }
                                    float ll = MathF.Sqrt(ln0 * ln0 + ln1 * ln1 + ln2 * ln2);
                                    if (ll > 1e-6f) { clx = ln0 / ll; cly = ln1 / ll; clz = ln2 / ll; }
                                    nSlot = curSlot;
                                    break;
                                }
                            case InstructionCode.VTX_16:
                                { px = S16(a[0] & 0xFFFF) / 4096f; py = S16((a[0] >> 16) & 0xFFFF) / 4096f; pz = S16(a[1] & 0xFFFF) / 4096f; group.Add(ToV(px, py, pz)); break; }
                            case InstructionCode.VTX_10:
                                { px = S10(a[0] & 0x3FF) / 64f; py = S10((a[0] >> 10) & 0x3FF) / 64f; pz = S10((a[0] >> 20) & 0x3FF) / 64f; group.Add(ToV(px, py, pz)); break; }
                            case InstructionCode.VTX_XY:
                                { px = S16(a[0] & 0xFFFF) / 4096f; py = S16((a[0] >> 16) & 0xFFFF) / 4096f; group.Add(ToV(px, py, pz)); break; }
                            case InstructionCode.VTX_XZ:
                                { px = S16(a[0] & 0xFFFF) / 4096f; pz = S16((a[0] >> 16) & 0xFFFF) / 4096f; group.Add(ToV(px, py, pz)); break; }
                            case InstructionCode.VTX_YZ:
                                { py = S16(a[0] & 0xFFFF) / 4096f; pz = S16((a[0] >> 16) & 0xFFFF) / 4096f; group.Add(ToV(px, py, pz)); break; }
                            case InstructionCode.VTX_DIFF:
                                { px += S10(a[0] & 0x3FF) / 4096f; py += S10((a[0] >> 10) & 0x3FF) / 4096f; pz += S10((a[0] >> 20) & 0x3FF) / 4096f; group.Add(ToV(px, py, pz)); break; }
                        }
                    }

                    V ToV(float lx, float ly, float lz)
                    {
                        var w = Vector3.TransformPosition(new Vector3(lx, ly, lz), curMat);
                        var p = new V
                        {
                            x = w.X * scale, y = w.Y * scale, z = w.Z * scale, r = cr, g = cg, b = cb, u = cu, v = cv,
                            nx = cnx, ny = cny, nz = cnz, lx = lx, ly = ly, lz = lz, bone = curSlot
                        };
                        if (skinned && nSlot >= 0)
                        {
                            if (nSlot == curSlot) { p.lnx = clx; p.lny = cly; p.lnz = clz; }  // exact, pose-independent
                            else
                            {
                                NormalSlotMismatches++;
                                Vector3 l = ToBoneLocal(curSlot, new Vector3(cnx, cny, cnz));
                                p.lnx = l.X; p.lny = l.Y; p.lnz = l.Z;
                            }
                        }
                        return p;
                    }
                }
            }

            int total = 0; foreach (var b in byKey.Values) total += b.Verts.Count / b.Stride;
            if (total == 0) { centerOut[0] = centerOut[1] = centerOut[2] = 0; radius = 10f; return new(); }
            centerOut[0] = (minX + maxX) / 2f; centerOut[1] = (minY + maxY) / 2f; centerOut[2] = (minZ + maxZ) / 2f;
            float dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            radius = 0.5f * (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); if (radius < 1f) radius = 1f;
            return byKey.Values.ToList();
        }

        // Diagnostics from the most recent Bake (single-threaded use): triangles that had no DS normal
        // and fell back to a computed face normal, and skinned vertices whose NORMAL was issued under a
        // different matrix-stack slot than the vertex itself (normal re-expressed via the bake pose).
        [ThreadStatic] public static int FaceNormalFallbacks;
        [ThreadStatic] public static int NormalSlotMismatches;
    }
}
