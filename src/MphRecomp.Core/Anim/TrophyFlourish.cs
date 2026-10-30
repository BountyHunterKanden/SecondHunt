using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // A character's own game clips played on its imported HD body for the idle FLOURISH (owner 2026-10-17: Beyond Sylux
    // alternates taunt_flex and taunt_armSwipe). The body is skinned to MPH's DS bones, so the import (Import/Hd/
    // BeyondFlourish) bakes each clip as, per DS bone and frame, how the source joint that bone stands for moves the bind
    // mesh: D, in trophy units, row vectors (v' = v * D). A DS bone's pose is then its bind carried by that:
    // world = BindWorld * TrophyToRig^-1 * D * TrophyToRig, so each vertex follows the joint its weight was folded from.
    //   <Stem>.flourish.bin  "MPHFLR2", bone count + names, the idle clip's name ("" = none), clip count, per clip: name,
    //                        fps, frames, frames x bones x 12 floats (D's three rotation rows, then its translation row)
    // The idle (owner 2026-10-17: Sylux stands in his Prime 4 idle, the flourishes start and end on it) loops while the
    // body stands; the other clips are the flourishes, in turn.
    public sealed class TrophyFlourish
    {
        public const string Suffix = ".flourish.bin";
        const string Magic = "MPHFLR2", Magic1 = "MPHFLR1";

        public sealed class Clip
        {
            public string Name = "";
            public float Fps = 30;
            public int Frames;
            public Matrix4[] D = Array.Empty<Matrix4>();   // [frame * bones + bone]
            public float Duration => Frames > 1 ? (Frames - 1) / Fps : 0;
        }

        public string[] Bones = Array.Empty<string>();
        public string Idle = "";
        public readonly List<Clip> Clips = new();

        public static string PathFor(string dae) => Path.Combine(Path.GetDirectoryName(dae) ?? "", Path.GetFileNameWithoutExtension(dae) + Suffix);

        public void Write(string path)
        {
            using var w = new BinaryWriter(File.Create(path), Encoding.UTF8);
            w.Write(Encoding.ASCII.GetBytes(Magic));
            w.Write(Bones.Length);
            foreach (string b in Bones) w.Write(b);
            w.Write(Idle);
            w.Write(Clips.Count);
            foreach (Clip c in Clips)
            {
                w.Write(c.Name);
                w.Write(c.Fps);
                w.Write(c.Frames);
                foreach (Matrix4 m in c.D)
                {
                    w.Write(m.M11); w.Write(m.M12); w.Write(m.M13);
                    w.Write(m.M21); w.Write(m.M22); w.Write(m.M23);
                    w.Write(m.M31); w.Write(m.M32); w.Write(m.M33);
                    w.Write(m.M41); w.Write(m.M42); w.Write(m.M43);
                }
            }
        }

        public static TrophyFlourish? Load(string path)
        {
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            string magic = Encoding.ASCII.GetString(r.ReadBytes(Magic.Length));
            if (magic != Magic && magic != Magic1) throw new InvalidDataException($"{Path.GetFileName(path)}: not a flourish file");
            var f = new TrophyFlourish { Bones = new string[r.ReadInt32()] };
            for (int i = 0; i < f.Bones.Length; i++) f.Bones[i] = r.ReadString();
            if (magic == Magic) f.Idle = r.ReadString();
            for (int n = r.ReadInt32(); n > 0; n--)
            {
                var c = new Clip { Name = r.ReadString(), Fps = r.ReadSingle(), Frames = r.ReadInt32() };
                c.D = new Matrix4[c.Frames * f.Bones.Length];
                for (int i = 0; i < c.D.Length; i++)
                {
                    c.D[i] = new Matrix4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), 0, r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), 0,
                        r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), 0, r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), 1);
                }
                f.Clips.Add(c);
            }
            return f;
        }

        // the file's bone for each skeleton bone (-1 = none: it keeps the game's pose)
        public int[] MapTo(IReadOnlyList<string> skeleton)
        {
            var map = new int[skeleton.Count];
            for (int i = 0; i < map.Length; i++) map[i] = Array.IndexOf(Bones, skeleton[i]);
            return map;
        }

        // clip c at time t (seconds, held at its ends) for the mapped bones, blended over `pose` by weight w (0 = the game's
        // pose as it is, 1 = the clip's): rotation slerped, translation and scale lerped, each bone in rig space.
        // only: the bones it moves (null = all mapped); anchor >= 0: those bones carried so the anchor bone's joint stays
        // where the game's pose has it (owner 2026-10-17: the hips and up play the clip, the legs stay planted)
        public void Blend(int c, float t, float w, TrophyRig rig, int[] map, Matrix4[] pose, bool[]? only = null, int anchor = -1)
        {
            if (w <= 0 || c < 0 || c >= Clips.Count) return;
            Clip clip = Clips[c];
            int nb = Bones.Length;
            float ft = Math.Clamp(t * clip.Fps, 0, Math.Max(clip.Frames - 1, 0));
            int f0 = (int)ft, f1 = Math.Min(f0 + 1, clip.Frames - 1);
            float u = ft - f0;
            Matrix4 toRig = rig.TrophyToRig, fromRig = toRig.Inverted();
            Matrix4 Target(int i)
            {
                int b = map[i];
                Matrix4 d = clip.D[f0 * nb + b] * (1 - u) + clip.D[f1 * nb + b] * u;
                return rig.BindWorld[i] * fromRig * d * toRig;
            }
            Vector3 shift = anchor >= 0 && anchor < map.Length && map[anchor] >= 0 ? pose[anchor].ExtractTranslation() - Target(anchor).ExtractTranslation() : Vector3.Zero;
            for (int i = 0; i < map.Length && i < pose.Length && i < rig.BindWorld.Length; i++)
            {
                if (map[i] < 0 || only != null && (i >= only.Length || !only[i])) continue;
                Matrix4 target = Target(i);
                target.Row3 += new Vector4(shift, 0);
                pose[i] = w >= 1 ? target : Mix(pose[i], target, w);
            }
        }

        static Matrix4 Mix(Matrix4 a, Matrix4 b, float w)
        {
            Quaternion r = Quaternion.Slerp(a.ExtractRotation(), b.ExtractRotation(), w);
            Vector3 s = Vector3.Lerp(a.ExtractScale(), b.ExtractScale(), w);
            Vector3 p = Vector3.Lerp(a.ExtractTranslation(), b.ExtractTranslation(), w);
            return Matrix4.CreateScale(s) * Matrix4.CreateFromQuaternion(r) * Matrix4.CreateTranslation(p);
        }
    }
}
