using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// An HD skin's own game idle and idle FLOURISH (owner 2026-10-17: Beyond Sylux stands in his Prime 4 idle and plays
// taunt_flex then taunt_armSwipe, one after the other, over and over): the import's <Stem>.flourish.bin (Core Anim/
// TrophyFlourish, made by Import/Hd/BeyondFlourish) blended over MphRead's pose.
//   idle       while MphRead's legs stand (Idle / Flourish), the file's idle loops on the whole body; the upper body
//              (Spine_1 and up) keeps MphRead's while the torso layer aims or shoots
//   flourish   starts when MphRead's legs enter Flourish (after ~10 s with no input, again every ~10 s while it lasts) or
//              on BodyFlourish.PlayNow (the gun-adjust pause, FlourishToggle.cs) while it stands; each start plays the next
//              flourish in the file's order, Spine_1 and up only, its hips kept where the idle has them (the legs stay
//              planted in the idle); it plays out at its own length while the body stands and fades out when it moves
// One per skin (CampaignSuit, shared by its seats): the clocks are per seat, stepped after each sim step.
internal sealed class SuitFlourish
{
    const float Step = 1f / 60f; // one MphRead step
    const float Ease = 0.25f, CutFade = 0.3f, IdleFade = 0.2f;

    readonly TrophyFlourish _file;
    readonly int[] _map;
    readonly bool[]? _upper; // Spine_1 and up
    readonly bool[]? _lower; // the pelvis and legs
    readonly int _hips;      // Spine_1
    readonly int _idle;      // the idle clip (-1 = none)
    readonly int[] _flourishes;
    readonly Dictionary<PlayerEntity, Seat> _seats = new();

    sealed class Seat
    {
        public int Next, Clip = -1, PlayNow = BodyFlourish.PlayNow;
        public float T, Cut = 1, IdleT, IdleLegs, IdleTop;
        public bool InFlourish;
        public Vector3 Last;
    }

    SuitFlourish(TrophyFlourish file, int[] map, bool[]? upper, int hips)
    {
        _file = file;
        _map = map;
        _upper = upper;
        _lower = upper != null ? Array.ConvertAll(upper, u => !u) : null;
        _hips = hips;
        _idle = file.Idle != "" ? file.Clips.FindIndex(c => c.Name == file.Idle) : -1;
        var fl = new List<int>();
        for (int i = 0; i < file.Clips.Count; i++) if (i != _idle) fl.Add(i);
        _flourishes = fl.ToArray();
    }

    // dae's flourish file on this skeleton, null = none (or none of its bones here)
    public static SuitFlourish? Load(string dae, DsSkeleton sk, Action<string> log)
    {
        string path = TrophyFlourish.PathFor(dae);
        TrophyFlourish? f = TrophyFlourish.Load(path);
        if (f == null || f.Clips.Count == 0) return null;
        int[] map = f.MapTo(sk.Names);
        if (Array.TrueForAll(map, b => b < 0))
        {
            log($"flourish: none of {System.IO.Path.GetFileName(path)}'s bones are on this skeleton");
            return null;
        }
        int hips = sk.IndexOf("Spine_1");
        var fl = new SuitFlourish(f, map, hips >= 0 ? sk.Subtree("Spine_1") : null, hips);
        log($"flourish: idle {(fl._idle >= 0 ? f.Idle : "(MPH's)")}; "
            + string.Join(", ", Array.ConvertAll(fl._flourishes, i => $"{f.Clips[i].Name} {f.Clips[i].Duration:0.0} s")) + " (in turn)");
        return fl;
    }

    // after each sim step
    public void Tick(PlayerEntity p)
    {
        if (!_seats.TryGetValue(p, out Seat? s)) _seats[p] = s = new Seat { Last = p.Position };
        var state = (PlayerAnimation)p.HostBipedModel1.AnimInfo.Index[0];
        var top = (PlayerAnimation)p.BipedModel2.AnimInfo.Index[0];
        bool still = (p.Position - s.Last).LengthSquared < 1e-6f;
        s.Last = p.Position;
        bool idle = !p.IsAltForm && (state == PlayerAnimation.Idle || state == PlayerAnimation.Flourish || state == PlayerAnimation.None && still);
        bool topIdle = idle && (top == PlayerAnimation.Idle || top == PlayerAnimation.Flourish || top == PlayerAnimation.None);
        if (_idle >= 0)
        {
            float d = _file.Clips[_idle].Duration;
            s.IdleT = d > 0 ? (s.IdleT + Step) % d : 0;
            s.IdleLegs = Math.Clamp(s.IdleLegs + (idle ? Step : -Step) / IdleFade, 0, 1);
            s.IdleTop = Math.Clamp(s.IdleTop + (topIdle ? Step : -Step) / IdleFade, 0, 1);
        }
        bool start = false;
        if (idle && s.PlayNow != BodyFlourish.PlayNow) start = true;
        if (state == PlayerAnimation.Flourish && !s.InFlourish) start = true;
        s.PlayNow = BodyFlourish.PlayNow;
        s.InFlourish = state == PlayerAnimation.Flourish;
        if (start && topIdle && _flourishes.Length > 0)
        {
            s.Clip = _flourishes[s.Next % _flourishes.Length];
            s.Next = (s.Next + 1) % _flourishes.Length;
            s.T = 0;
            s.Cut = 1;
            return;
        }
        if (s.Clip < 0) return;
        s.T += Step;
        if (!topIdle) s.Cut -= Step / CutFade;
        if (s.Cut <= 0 || s.T >= _file.Clips[s.Clip].Duration) s.Clip = -1;
    }

    // over the pose MphRead's clips gave (rig space, CampaignSuit.Pose's world matrices)
    public void Apply(PlayerEntity p, TrophyRig rig, Matrix4[] world)
    {
        if (!_seats.TryGetValue(p, out Seat? s)) return;
        if (_idle >= 0)
        {
            _file.Blend(_idle, s.IdleT, s.IdleLegs, rig, _map, world, _lower);
            _file.Blend(_idle, s.IdleT, Math.Min(s.IdleLegs, s.IdleTop), rig, _map, world, _upper);
        }
        if (s.Clip < 0) return;
        float d = _file.Clips[s.Clip].Duration;
        float w = Math.Clamp(Math.Min(s.T, d - s.T) / Ease, 0, 1) * Math.Clamp(s.Cut, 0, 1);
        _file.Blend(s.Clip, s.T, w, rig, _map, world, _upper, _hips);
    }
}
