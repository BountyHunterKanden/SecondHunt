using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Brawl Zero Suit's faces (owner 2026-10-15: "does she have facial expressions for getting hurt or jumping or running or
    // anything like that we can use" and, for the idle flourish, "make sure if her face changes for it we have that too").
    // Her fighter model has five face sets -- neutral, half blink, blink, hurt (Ouch), shouting (Talk) -- each on a bone of
    // its own in her body (Tools ZeroSuitBrawl.AddFaces); one is shown at a time, the others folded to a point. Which one
    // comes from Brawl's own keys for the matching animation (her motion pack's VIS0s, the body's faces.txt):
    //   stunned by a Paralyzer -> DamageElec (looping); a hit -> DamageN1 / N2 / Hi1 / Air1 from the hit (by its side);
    //   the idle flourish -> Wait2 with the clip; a jump -> JumpF; down -> DownWaitU (eyes shut);
    //   else her neutral face with a blink (Wait3's opening one) every few seconds (Brawl's Wait1 has none; ours)
    internal sealed class BrawlZeroSuitFaces
    {
        public static readonly string[] Bones = { "FaceSetF", "FaceSetBH", "FaceSetB", "FaceSetO", "FaceSetT" };
        static readonly string[] Keys = { "F", "BH", "B", "O", "T" };
        // a frame with two sets on (a key on the change) shows the first in this order; none on = neutral
        static readonly int[] Pick = { 4, 3, 2, 1, 0 };
        public const string FlourishClip = "Wait2";
        const float BlinkEvery = 4.2f;   // seconds

        sealed class Track
        {
            public int Frames;
            public readonly (int Frame, int Value)[][] Keys = new (int, int)[5][];
        }

        readonly int[] _bones;
        readonly Dictionary<string, Track> _tracks;

        BrawlZeroSuitFaces(int[] bones, Dictionary<string, Track> tracks)
        {
            _bones = bones;
            _tracks = tracks;
        }

        public int Count => _tracks.Count;

        // null: an older body (no face bones) or no faces.txt -- its one neutral face stays as it was
        public static BrawlZeroSuitFaces? Load(NativeBody body)
        {
            var bones = new int[Bones.Length];
            for (int i = 0; i < Bones.Length; i++)
            {
                if ((bones[i] = body.Bone(Bones[i])) < 0) return null;
            }
            string path = Path.Combine(body.Dir, "faces.txt");
            if (!File.Exists(path)) return null;
            var tracks = new Dictionary<string, Track>();
            // "<clip> <frames> FacePattern[C1] BH[0:0,5:1,6:0] B[..] F[..] O[C0] T[C0]"
            foreach (string line in File.ReadLines(path))
            {
                string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 3 || !int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames)) continue;
                var track = new Track { Frames = Math.Max(1, frames) };
                for (int k = 2; k < t.Length; k++)
                {
                    int open = t[k].IndexOf('[');
                    if (open <= 0 || !t[k].EndsWith(']')) continue;
                    int set = Array.IndexOf(Keys, t[k][..open]);
                    if (set < 0) continue;
                    string body1 = t[k][(open + 1)..^1];
                    var keys = new List<(int, int)>();
                    if (body1.StartsWith('C')) keys.Add((0, body1 == "C1" ? 1 : 0));
                    else
                    {
                        foreach (string kv in body1.Split(','))
                        {
                            string[] fv = kv.Split(':');
                            if (fv.Length == 2 && int.TryParse(fv[0], out int f) && int.TryParse(fv[1], out int v)) keys.Add((f, v));
                        }
                    }
                    track.Keys[set] = keys.ToArray();
                }
                tracks[t[0]] = track;
            }
            return tracks.Count > 0 ? new BrawlZeroSuitFaces(bones, tracks) : null;
        }

        // the face for p now: its set shown, the others folded away (after every other change to the pose)
        public void Apply(NativeBody body, PlayerEntity? p)
        {
            int shown = 0;
            if (Choose(p) is (string clip, float frame, bool loop) && _tracks.TryGetValue(clip, out Track? track))
            {
                int f = (int)frame;
                f = loop ? (f % track.Frames + track.Frames) % track.Frames : Math.Clamp(f, 0, track.Frames - 1);
                shown = Shown(track, f);
            }
            for (int i = 0; i < _bones.Length; i++)
            {
                if (i != shown) body.World[_bones[i]] = Matrix4.CreateScale(0) * body.World[_bones[i]];
            }
        }

        static int Shown(Track track, int frame)
        {
            foreach (int set in Pick)
            {
                if (track.Keys[set] is not (int Frame, int Value)[] keys || keys.Length == 0) continue;
                int v = keys[0].Value;
                foreach ((int kf, int kv) in keys)
                {
                    if (kf <= frame) v = kv;
                }
                if (v != 0) return set;
            }
            return 0;
        }

        // Brawl's clip for p's state and the frame in it (Brawl frames: 60 a second; MPH's clips run at 30)
        (string Clip, float Frame, bool Loop)? Choose(PlayerEntity? p)
        {
            float now = Environment.TickCount64 / 1000f;
            if (p == null) return null;
            if (p.Health == 0) return ("DownWaitU", 0, false);
            if (p.ParalyzerStunned) return ("DamageElec", now * 60, true);
            AnimationInfo torso = p.BipedModel2.AnimInfo;
            switch ((PlayerAnimation)torso.Index[0])
            {
            case PlayerAnimation.DamageFront: return ("DamageN2", torso.Frame[0] * 2, false);
            case PlayerAnimation.DamageBack: return ("DamageN1", torso.Frame[0] * 2, false);
            case PlayerAnimation.DamageLeft: return ("DamageHi1", torso.Frame[0] * 2, false);
            case PlayerAnimation.DamageRight: return ("DamageAir1", torso.Frame[0] * 2, false);
            }
            AnimationInfo legs = p.HostBipedModel1.AnimInfo;
            var state = (PlayerAnimation)legs.Index[0];
            if (state == PlayerAnimation.Flourish && legs.FrameCount[0] > 0 && _tracks.TryGetValue(FlourishClip, out Track? fl))
            {
                return (FlourishClip, legs.Frame[0] / (float)legs.FrameCount[0] * fl.Frames, false);
            }
            if (state is PlayerAnimation.JumpNeutral or PlayerAnimation.JumpForward or PlayerAnimation.JumpBack
                or PlayerAnimation.JumpLeft or PlayerAnimation.JumpRight)
            {
                return ("JumpF", legs.Frame[0] * 2, false);
            }
            // a blink now and then, each seat on its own beat
            float t = (now + p.SlotIndex * 1.37f) % BlinkEvery;
            return ("Wait3", Math.Min(t * 60, 20), false); // (its first blink only)
        }
    }
}
