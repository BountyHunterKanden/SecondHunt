using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Formats;

namespace MphRecomp.Render
{
    // Decodes one mesh's DS display list into a flat triangle list, reproducing MphRead's Scene.DoDlist exactly --
    // the GL immediate-mode state machine it drives (current colour / normal / texcoord / matrix id carried from
    // vertex to vertex; quads and strips expanded with GL's winding) -- so a GLES host can draw MphRead's RenderItems
    // with MphRead's own shader semantics. Per vertex (Stride floats):
    //   pos.xyz, normal.xyz, color.rgba, uv.st, matrix-stack index
    // color.a: 1 = COLOR command, 0 = DIF_AMB (diffuse smuggled in the colour, as MphRead does), 2 = no colour
    // command yet -> the shader uses the item's material diffuse (MphRead calls GL.Color3(item.Diffuse) before
    // each CallList).
    public static class DsDisplayList
    {
        public const int Stride = 13;
        public const float MaterialColor = 2f;

        public static float[] Decode(Model model, Mesh mesh, bool isRoom)
        {
            Material material = model.Materials[mesh.MaterialId];
            int textureWidth = 0, textureHeight = 0;
            if (material.TextureId != -1)
            {
                Texture texture = model.Recolors[0].Textures[material.TextureId];
                textureWidth = texture.Width;
                textureHeight = texture.Height;
            }
            bool texgen = material.TexgenMode == TexgenMode.Normal;
            IReadOnlyList<RenderInstruction> list = model.RenderInstructionLists[mesh.DlistId];

            var output = new List<float>(list.Count * Stride);
            var prim = new List<float>(64 * Stride);
            int primType = -1;
            float vx = 0, vy = 0, vz = 0;
            float nx = 0, ny = 0, nz = 1; // GL's initial normal
            float cr = 1, cg = 1, cb = 1, ca = MaterialColor;
            float s = texgen ? 0.5f : 0f, t = texgen ? 0.5f : 0f;
            float matrixId = 0;

            void Vertex()
            {
                prim.Add(vx); prim.Add(vy); prim.Add(vz);
                prim.Add(nx); prim.Add(ny); prim.Add(nz);
                prim.Add(cr); prim.Add(cg); prim.Add(cb); prim.Add(ca);
                prim.Add(s); prim.Add(t);
                prim.Add(matrixId);
            }

            void Copy(int index)
            {
                for (int k = 0; k < Stride; k++)
                {
                    output.Add(prim[index * Stride + k]);
                }
            }

            void Tri(int a, int b, int c)
            {
                Copy(a); Copy(b); Copy(c);
            }

            void EndPrimitive()
            {
                int n = prim.Count / Stride;
                switch (primType)
                {
                case 0: // triangles
                    for (int i = 0; i + 2 < n; i += 3)
                    {
                        Tri(i, i + 1, i + 2);
                    }
                    break;
                case 1: // quads: (0,1,2) (0,2,3)
                    for (int i = 0; i + 3 < n; i += 4)
                    {
                        Tri(i, i + 1, i + 2);
                        Tri(i, i + 2, i + 3);
                    }
                    break;
                case 2: // triangle strip, alternating winding
                    for (int i = 2; i < n; i++)
                    {
                        if ((i & 1) == 0)
                        {
                            Tri(i - 2, i - 1, i);
                        }
                        else
                        {
                            Tri(i - 1, i - 2, i);
                        }
                    }
                    break;
                case 3: // quad strip: quad k = v2k, v2k+1, v2k+3, v2k+2
                    for (int i = 0; i + 3 < n; i += 2)
                    {
                        Tri(i, i + 1, i + 3);
                        Tri(i, i + 3, i + 2);
                    }
                    break;
                }
                prim.Clear();
                primType = -1;
            }

            static int Sign10(uint v) => (v & 0x200) != 0 ? (int)(v | 0xFFFFFC00) : (int)v;
            static int Sign16(uint v) => (v & 0x8000) != 0 ? (int)(v | 0xFFFF0000) : (int)v;

            for (int i = 0; i < list.Count; i++)
            {
                RenderInstruction ins = list[i];
                switch (ins.Code)
                {
                case InstructionCode.BEGIN_VTXS:
                    if (primType != -1)
                    {
                        EndPrimitive();
                    }
                    primType = (int)ins.Arguments[0];
                    if (primType > 3)
                    {
                        throw new ProgramException("Invalid geometry type");
                    }
                    break;
                case InstructionCode.END_VTXS:
                    EndPrimitive();
                    break;
                case InstructionCode.COLOR:
                {
                    uint rgb = ins.Arguments[0];
                    cr = ((rgb >> 0) & 0x1F) / 31f;
                    cg = ((rgb >> 5) & 0x1F) / 31f;
                    cb = ((rgb >> 10) & 0x1F) / 31f;
                    ca = 1;
                    break;
                }
                case InstructionCode.DIF_AMB:
                {
                    uint rgb = ins.Arguments[0];
                    cr = ((rgb >> 0) & 0x1F) / 31f;
                    cg = ((rgb >> 5) & 0x1F) / 31f;
                    cb = ((rgb >> 10) & 0x1F) / 31f;
                    ca = 0;
                    break;
                }
                case InstructionCode.NORMAL:
                {
                    uint xyz = ins.Arguments[0];
                    nx = Sign10(xyz & 0x3FF) / 512f;
                    ny = Sign10((xyz >> 10) & 0x3FF) / 512f;
                    nz = Sign10((xyz >> 20) & 0x3FF) / 512f;
                    break;
                }
                case InstructionCode.TEXCOORD:
                {
                    uint st = ins.Arguments[0];
                    if (textureWidth > 0 && textureHeight > 0)
                    {
                        s = Sign16(st & 0xFFFF) / 16f / textureWidth;
                        t = Sign16((st >> 16) & 0xFFFF) / 16f / textureHeight;
                    }
                    break;
                }
                case InstructionCode.VTX_16:
                {
                    uint xy = ins.Arguments[0];
                    vx = Fixed.ToFloat(Sign16(xy & 0xFFFF));
                    vy = Fixed.ToFloat(Sign16((xy >> 16) & 0xFFFF));
                    vz = Fixed.ToFloat(Sign16(ins.Arguments[1] & 0xFFFF));
                    Vertex();
                    break;
                }
                case InstructionCode.VTX_10:
                {
                    uint xyz = ins.Arguments[0];
                    vx = Sign10(xyz & 0x3FF) / 64f;
                    vy = Sign10((xyz >> 10) & 0x3FF) / 64f;
                    vz = Sign10((xyz >> 20) & 0x3FF) / 64f;
                    Vertex();
                    break;
                }
                case InstructionCode.VTX_XY:
                {
                    uint xy = ins.Arguments[0];
                    vx = Fixed.ToFloat(Sign16(xy & 0xFFFF));
                    vy = Fixed.ToFloat(Sign16((xy >> 16) & 0xFFFF));
                    Vertex();
                    break;
                }
                case InstructionCode.VTX_XZ:
                {
                    uint xz = ins.Arguments[0];
                    vx = Fixed.ToFloat(Sign16(xz & 0xFFFF));
                    vz = Fixed.ToFloat(Sign16((xz >> 16) & 0xFFFF));
                    Vertex();
                    break;
                }
                case InstructionCode.VTX_YZ:
                {
                    uint yz = ins.Arguments[0];
                    vy = Fixed.ToFloat(Sign16(yz & 0xFFFF));
                    vz = Fixed.ToFloat(Sign16((yz >> 16) & 0xFFFF));
                    Vertex();
                    break;
                }
                case InstructionCode.VTX_DIFF:
                {
                    uint xyz = ins.Arguments[0];
                    vx += Fixed.ToFloat(Sign10(xyz & 0x3FF));
                    vy += Fixed.ToFloat(Sign10((xyz >> 10) & 0x3FF));
                    vz += Fixed.ToFloat(Sign10((xyz >> 20) & 0x3FF));
                    Vertex();
                    break;
                }
                case InstructionCode.MTX_RESTORE:
                    // MphRead keeps room meshes on stack entry 0 (so room node transforms can be toggled)
                    if (!isRoom)
                    {
                        matrixId = ins.Arguments[0];
                    }
                    break;
                case InstructionCode.NOP:
                    break;
                default:
                    throw new ProgramException("Unknown opcode");
                }
            }
            if (primType != -1)
            {
                EndPrimitive();
            }
            return output.ToArray();
        }
    }
}
