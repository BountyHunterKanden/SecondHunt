using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Anim;
using OpenTK.Mathematics;

// The character + skin picker's pictures (D9, docs/CHARACTERS_AND_SKINS.md; owner 2026-10-08 picks A2 "smooth HD icons"):
// each skin the picker shows is drawn once by the app (CharacterPictureRender, on the GL thread, through the campaign suit's
// own HD code: the skin posed on its hunter's DS rig with the menus' HD Samus clip and frame, as the mockup's pictures) as
// two transparent pictures -- the whole body for SELECT HUNTER's big portrait and the lobby's seat pictures, and a head
// close-up for the round icons -- kept as <data>/cache/characters/<skin>_<stamp>_{full,head}.png (the stamp is its .dae's
// time, so an import made again is drawn again). A skin with no hd/<skin> folder (a native body only: bodies/<id>) has no
// picture; a host that can't render (the PC tools) shows the pictures already there, else the icons' rim alone.
namespace MphRecomp.Frontend
{
    // one skin to draw: its HD folder's root, the rig it's posed on, and the two PNGs to write
    public sealed record CharacterPictureJob(string Skin, string HdRoot, Hunter Rig, string Full, string Head);

    public static class CharacterPictures
    {
        // the pictures' sizes (2:3, the menus' HD Samus recipe aspect); the icon is a square from the head picture
        public const int FullW = 256, FullH = 384, HeadW = 256, HeadH = 384;
        // the head picture's square starts this far down: its middle, where HeadCamera puts the head's middle
        public const float HeadCropTop = (HeadH - HeadW) / 2f / HeadH;
        // v2 (owner 2026-10-09: the TROOPER and DARK SAMUS icons sat off their heads, Echoes' Dark Samus too dark to see):
        // the head framed on its own middle, dark pictures lifted
        public const string Look = "v2"; // a framing made anew is drawn again (in the pictures' names)

        // the app renders the jobs (it sets this); else nothing is queued
        public static bool HostRenders { get; set; }

        // bumped when an import ends (or anything is deleted): the menus queue every skin's pictures again, so they're
        // drawn in the background on the IMPORT GAMES page instead of on SELECT HUNTER's first visit
        public static int Generation { get; private set; }

        public static void Invalidate()
        {
            Forget();
            Generation++;
        }

        // per skin: its pictures ("" = none); jobs queued or rendering are in s_pending
        private static readonly ConcurrentDictionary<string, (string Full, string Head)> s_known = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> s_pending = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<CharacterPictureJob> s_jobs = new();

        // where a device keeps them: <data>/cache/characters (<data> = the folder of recomp_settings.json, beside hd/)
        public static string Dir(string dataRoot) => Path.Combine(dataRoot, "cache", "characters");

        // a skin's pictures (full body, head), or null: ORIGINAL, no HD folder, or not drawn yet (the first call queues it)
        public static (string Full, string Head)? For(string? hdRoot, string? picturesDir, string? skin)
        {
            if (hdRoot == null || picturesDir == null || string.IsNullOrEmpty(skin) || MphRecomp.Multiplayer.Characters.IsOriginal(skin)) return null;
            if (s_known.TryGetValue(skin, out var known)) return known.Full.Length > 0 ? known : null;
            string folder = Path.Combine(hdRoot, skin);
            string? dae = null;
            try
            {
                dae = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.dae").FirstOrDefault() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("character picture: " + folder + " unreadable (" + ex.Message + ")");
            }
            if (dae == null)
            {
                s_known[skin] = ("", "");
                return null;
            }
            string stem = Path.Combine(picturesDir, $"{skin}_{File.GetLastWriteTimeUtc(dae).Ticks:x}_{Look}");
            (string Full, string Head) pics = (stem + "_full.png", stem + "_head.png");
            if (File.Exists(pics.Full) && File.Exists(pics.Head))
            {
                s_known[skin] = pics;
                return pics;
            }
            s_known[skin] = ("", "");
            if (HostRenders && s_pending.TryAdd(skin, true))
            {
                Hunter rig = MphRecomp.Anim.TrophyRigs.TryHunterFor(skin, out Hunter h) ? h : Hunter.Samus;
                s_jobs.Enqueue(new CharacterPictureJob(skin, hdRoot, rig, pics.Full, pics.Head));
            }
            return null;
        }

        public static bool Pending(string skin) => s_pending.ContainsKey(skin);

        // the app's side (GL thread): the next skin to draw
        public static bool TryTake(out CharacterPictureJob job)
        {
            if (s_jobs.TryDequeue(out CharacterPictureJob? j))
            {
                job = j;
                return true;
            }
            job = null!;
            return false;
        }

        // a job ended: its pictures written (ok) or not (then the icon's rim alone until the next import)
        public static void Finished(CharacterPictureJob job, bool ok)
        {
            s_known[job.Skin] = ok && File.Exists(job.Full) && File.Exists(job.Head) ? (job.Full, job.Head) : ("", "");
            s_pending.TryRemove(job.Skin, out _);
        }

        // an import ended or something was deleted: look again (a queued job stays queued)
        public static void Forget()
        {
            foreach (string key in s_known.Keys)
            {
                if (!s_pending.ContainsKey(key)) s_known.TryRemove(key, out _);
            }
        }

        // ---- the head close-up's framing (v2): on the posed head's own middle, whatever the rig or the idle's head turn ----

        public const string HeadJoint = "Head_1";
        // the camera: HeadTurn to the head's left of straight on and HeadRise above (the mockups' Samus three-quarter view),
        // far enough that the head's size (HeadRadius) is HeadFill of the icon's round window (the square's inner rim)
        private const float HeadTurn = 16f, HeadRise = 4f, HeadFill = 0.95f; // (a trooper's crest in, a Samus helmet about as v1 had it)

        // a skin's head landmarks (trophy units) from the .joints.txt the importer writes beside its .dae: the head's middle
        // (Head_1.center, else the joint) and a point straight ahead of it (Head_1.fwd), each with the joint itself; null without
        public static (Vector3 Center, Vector3 Joint, Vector3? Ahead)? HeadLandmarks(string folder)
        {
            try
            {
                string? dae = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.dae").FirstOrDefault() : null;
                string? file = dae == null ? null : TrophyLandmarks.PathFor(dae);
                if (file == null || !File.Exists(file)) return null;
                var joints = TrophyLandmarks.Load(file).Joints;
                if (!joints.TryGetValue(HeadJoint, out Vector3 joint)) return null;
                Vector3 center = joints.TryGetValue(HeadJoint + ".center", out Vector3 c) ? c : joint;
                return (center, joint, joints.TryGetValue(HeadJoint + ".fwd", out Vector3 f) ? f : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                Console.Error.WriteLine("character picture: " + folder + ": no head landmarks (" + ex.Message + ")");
                return null;
            }
        }

        // the head in a pose (world: sk's FK on the rig's lengths): its middle, the way it faces and its size (rig space); null
        // without a head joint
        public static (Vector3 Center, Vector3 Facing, float Radius)? PosedHead(TrophyRig rig, DsSkeleton sk, Matrix4[] world,
            (Vector3 Center, Vector3 Joint, Vector3? Ahead) head)
        {
            int bone = sk.IndexOf(HeadJoint);
            if (bone < 0 || bone >= rig.InvBind.Length || bone >= world.Length) return null;
            Vector3 c = Posed(rig, world, bone, head.Center);
            Vector3 facing = head.Ahead is Vector3 a ? Posed(rig, world, bone, a) - Posed(rig, world, bone, head.Joint) : -Vector3.UnitZ;
            return (c, facing, HeadRadius(rig, world, bone, c));
        }

        // a point of the trophy (trophy units) carried by `bone`, where a pose (world: the DS skeleton's FK) puts it in the
        // rig's space; a straightened rig's (BindPos) first moved as that bone's own vertices were
        public static Vector3 Posed(TrophyRig rig, Matrix4[] world, int bone, Vector3 trophy)
        {
            Vector3 at = Vector3.TransformPosition(trophy, rig.TrophyToRig);
            if (rig.BindPos != null)
            {
                Vector3 shift = Vector3.Zero;
                int n = 0;
                foreach (int v in OwnVertices(rig, bone))
                {
                    shift += rig.BindPos[v] - Vector3.TransformPosition(rig.Vertices[v], rig.TrophyToRig);
                    n++;
                }
                if (n > 0) at += shift / n;
            }
            return Vector3.TransformPosition(at, rig.InvBind[bone] * world[bone]);
        }

        // the head's size: how far from its posed middle 95% of the vertices the head bone alone carries lie (the rest: a
        // pirate's crest, Dark Samus's spikes); 0 without any. (Not the median: a Samus helmet's hidden face inside counts.)
        public static float HeadRadius(TrophyRig rig, Matrix4[] world, int bone, Vector3 center)
        {
            Matrix4 skin = rig.InvBind[bone] * world[bone];
            var d = new List<float>();
            foreach (int v in OwnVertices(rig, bone))
            {
                Vector3 bind = rig.BindPos?[v] ?? Vector3.TransformPosition(rig.Vertices[v], rig.TrophyToRig);
                d.Add((Vector3.TransformPosition(bind, skin) - center).Length);
            }
            if (d.Count == 0) return 0;
            d.Sort();
            return d[Math.Min(d.Count - 1, d.Count * 95 / 100)];
        }

        private static IEnumerable<int> OwnVertices(TrophyRig rig, int bone)
        {
            for (int v = 0; v < rig.Vertices.Length; v++)
            {
                if (rig.Hidden.Length > v && rig.Hidden[v]) continue;
                for (int k = 0; k < 4; k++)
                {
                    if (rig.Bones[v * 4 + k] == bone && rig.Weights[v * 4 + k] >= 0.99f)
                    {
                        yield return v;
                        break;
                    }
                }
            }
        }

        // the head close-up's camera (rig space, y up): looking at the head's middle along its own facing (level), turned
        // HeadTurn to its left and HeadRise above; the head's middle lands in the middle of the picture (the icon's square)
        public static (Vector3 Eye, Vector3 Target) HeadCamera(Vector3 center, Vector3 facing, float radius, float fovY)
        {
            Vector3 f = new(facing.X, 0, facing.Z);
            f = f.LengthSquared > 1e-8f ? f.Normalized() : -Vector3.UnitZ; // (the rig faces -Z)
            Vector3 left = Vector3.Cross(Vector3.UnitY, f);
            float turn = HeadTurn * MathF.PI / 180f, rise = HeadRise * MathF.PI / 180f;
            Vector3 dir = (f * MathF.Cos(turn) + left * MathF.Sin(turn)) * MathF.Cos(rise) + Vector3.UnitY * MathF.Sin(rise);
            // the round window's radius: the inner rim (50.5 of the 128 px icon) of the square (106 px), of the picture's half height
            float window = 50.5f / 53f * HeadW / HeadH;
            float dist = MathF.Max(radius, 0.05f) / (MathF.Tan(fovY * MathF.PI / 360f) * window * HeadFill);
            return (center + dir * dist, center);
        }

        // ---- dark pictures lifted (v2): a picture whose body's mean brightness is under LiftBelow is brightened to LiftTo
        // by a gamma on its brightness alone (dark parts lifted most, colours and glows kept); brighter pictures untouched.
        // Each picture on its own: Echoes' Dark Samus is lifted, the Phazon Suit's portrait but not its brighter close-up ----

        private const float LiftBelow = 0.2f, LiftTo = 0.27f;

        // RGBA, alpha = the body; the gamma it was given (1 = left as is)
        public static float Lift(byte[] px)
        {
            var hist = new int[256];
            int n = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] >= 128) hist[Luma(px, i)]++;
                n += px[i + 3] >= 128 ? 1 : 0;
            }
            if (n == 0) return 1;
            float Mean(float g)
            {
                double s = 0;
                for (int l = 0; l < 256; l++) if (hist[l] > 0) s += hist[l] * Math.Pow(l / 255.0, g);
                return (float)(s / n);
            }
            if (Mean(1) >= LiftBelow) return 1;
            float lo = 0.3f, hi = 1f; // the mean falls as the gamma rises
            for (int it = 0; it < 24; it++)
            {
                float mid = (lo + hi) / 2;
                if (Mean(mid) > LiftTo) lo = mid; else hi = mid;
            }
            float gamma = (lo + hi) / 2;
            // per brightness: the factor that takes it to its lifted brightness, on all three colours
            var gain = new float[256];
            for (int l = 1; l < 256; l++) gain[l] = (float)(Math.Pow(l / 255.0, gamma) * 255 / l);
            for (int i = 0; i < px.Length; i += 4)
            {
                float k = gain[Luma(px, i)];
                if (k <= 0) continue;
                px[i] = (byte)Math.Min(255, (int)(px[i] * k + 0.5f));
                px[i + 1] = (byte)Math.Min(255, (int)(px[i + 1] * k + 0.5f));
                px[i + 2] = (byte)Math.Min(255, (int)(px[i + 2] * k + 0.5f));
            }
            return gamma;
        }

        private static int Luma(byte[] px, int i) => Math.Min(255, (int)(0.299f * px[i] + 0.587f * px[i + 1] + 0.114f * px[i + 2] + 0.5f));
    }
}
