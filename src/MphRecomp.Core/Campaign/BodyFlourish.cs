using System;
using System.Collections.Generic;
using System.Globalization;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // The native bodies' idle FLOURISH (owner 2026-10-15): every vanilla hunter plays one after idling a while (MphRead's
    // PlayerAnimation.Flourish, BipedAnimator.FlourishDelayTicks); the Trooper and Dark Samus get one of their own game's
    // clips instead of holding their ready loop. Each set lists candidates in the owner's pick order (the first is the
    // default). A candidate is one clip, or parts made into one clip at load: a chain (into, loop, out of), a part's
    // stretch only ("clip:0-0.85", fractions of it), and Upper: the parts move the body from the torso root up while the
    // legs keep the idle loop (Corruption Dark Samus: her clips stand planted, her idle floats). The live switch is the
    // App's FlourishToggle.cs (gun-adjust pause). NativeBodyAnimator plays it (BodyClips.FlourishSet).
    public static class BodyFlourish
    {
        public sealed record Candidate(string Label, bool Upper, params string[] Parts);

        public sealed class Set
        {
            public readonly string Name;
            public readonly Candidate[] List;
            public int Index;
            // each flourish played moves on to the next candidate (Beyond Sylux: flex, then arm swipe, back and forth)
            public bool InTurn;

            public Set(string name, params Candidate[] list)
            {
                Name = name;
                List = list;
            }

            public Set(string name, bool inTurn, params Candidate[] list) : this(name, list) => InTurn = inTurn;

            public Candidate Current => List[Math.Clamp(Index, 0, List.Length - 1)];

            public void Next() => Index = (Index + 1) % List.Length;
        }

        // the Prime 1 pirate set (every trooper skin; Metroid1.pak ANCS 8169653a, the disc's only pirate set): B_taunt_pirate
        // = head thrown back, blade arm raised high, the other arm out (the move the owner remembered from the pirates'
        // cutscene introductions), from and back to his ready crouch. The others stand up straight: upper body only
        // (the first is the default: the owner's pick 2026-10-17, talk)
        public static readonly Set Trooper = new("Trooper",
            new Candidate("talk (blade arm gestures)", true, "B_talk2_pirate"),
            new Candidate("taunt (head back, arms up)", false, "B_taunt_pirate"),
            new Candidate("activate (arms spread)", true, "B_activate_pirate"),
            new Candidate("none (ready loop, as before)", false));

        // Echoes (Metroid1.pak ANCS 39900a11): her boss-fight taunts, each from and back to B_ready
        // (the first is the default: the owner's pick 2026-10-17, taunt 1)
        public static readonly Set Echoes = new("Dark Samus (Echoes)",
            new Candidate("taunt 1 (crouch, arms spread)", false, "B_taunt_1_dark_samus"),
            new Candidate("taunt 2 (crouch, arms up)", false, "B_taunt_2_dark_samus"),
            new Candidate("psycho laugh", false, "B_psychoLaugh_dark_samus"),
            new Candidate("look around", false, "R_lookAround_dark_samus"),
            new Candidate("enraged (long)", false, "R_Im_Pissed_Off_dark_samus"),
            new Candidate("none (ready loop, as before)", false));

        // Corruption (CHAR 7f305ff9): no taunt in her set; her attacks' wind-ups and her phazon recharge, upper body over
        // her floating legs. The barrage wind-up ends in a launch: cut before it, then her ready again
        public static readonly Set Corruption = new("Dark Samus (Corruption)",
            new Candidate("arms raised (barrage wind-up)", true, "a_phazon_barrage_telegraph_dark_samus:0-0.82", "b_ready_dark_samus:0-0.4"),
            new Candidate("cannon overhead (spear summon)", true, "a_summon_spears_dark_samus"),
            new Candidate("cannon charge (blaster wind-up)", true, "a_mega_blaster_telegraph_dark_samus"),
            new Candidate("phazon recharge", true, "b_ready2recharge_dark_samus", "b_recharge_dark_samus", "b_recharge2ready_dark_samus"),
            new Candidate("none (float loop, as before)", false));

        // Prime 4 Beyond Sylux (bodies/MP4Sylux, his gameplay character's taunts): the owner's pair, played in turn
        // (owner 2026-10-17: "it plays one the first time, the second the second, and then back and forth over and over").
        // Upper body only: the clips keep his hips still and step his feet (Prime 4 plants them itself), which left his feet
        // off the ground (owner 10-17 ~21:20: "his feet are not at all planted"), so his legs keep idle_sylux.
        public static readonly Set Beyond = new("Sylux (Beyond)", inTurn: true,
            new Candidate("flex", true, "taunt_flex_sylux"),
            new Candidate("arm swipe", true, "taunt_armSwipe_sylux"));

        public static readonly Set[] All = { Trooper, Echoes, Corruption, Beyond };

        // a flourish asked for now (the toggle, so the owner needn't idle 10 s first): each animator plays its pick once
        // when this changes, as soon as its body stands idle
        public static int PlayNow;

        // how long a chain's next part takes to blend in from where the last one stopped (seconds)
        public const float JoinBlend = 0.3f;

        // the set's made clips on this body, once (BodyClips' map makers, at load: the clip table is only read after);
        // idle: the map's idle clip (an Upper candidate's legs), torsoRoot: where its upper body starts
        public static Set Prepare(NativeBody body, Set set, string idle, string torsoRoot = "Spine_1")
        {
            foreach (Candidate c in set.List)
            {
                if (!Made(c)) continue;
                string name = MadeName(c);
                if (body.Clip(name) == null && Make(body, name, c, body.Clip(idle), body.Bone(torsoRoot)) is HdGun.Anim a)
                {
                    body.Data.Anims[name] = a;
                }
            }
            return set;
        }

        // a flourish starting now: the set's current one, and an InTurn set moves on to its next
        public static string? Start(NativeBody body, Set? set)
        {
            string? clip = Clip(body, set);
            if (set != null && set.InTurn) set.Next();
            return clip;
        }

        // the set's current flourish on this body, null = none / not on this body
        public static string? Clip(NativeBody body, Set? set)
        {
            if (set == null || set.Current.Parts.Length == 0) return null;
            Candidate c = set.Current;
            string name = Made(c) ? MadeName(c) : c.Parts[0];
            return body.Clip(name) != null ? name : null;
        }

        static bool Made(Candidate c) => c.Parts.Length > 1 || c.Upper || c.Parts.Length == 1 && c.Parts[0].Contains(':');

        static string MadeName(Candidate c) => "flourish:" + string.Join("+", c.Parts) + (c.Upper ? "|upper" : "");

        // "clip" or "clip:from-to" (fractions of its length)
        static (HdGun.Anim Clip, float From, float To)? Part(NativeBody body, string spec)
        {
            string[] s = spec.Split(':');
            if (body.Clip(s[0]) is not HdGun.Anim a) return null;
            float from = 0, to = 1;
            if (s.Length > 1 && s[1].Split('-') is { Length: 2 } r)
            {
                from = Single.Parse(r[0], CultureInfo.InvariantCulture);
                to = Single.Parse(r[1], CultureInfo.InvariantCulture);
            }
            return (a, from * a.Duration, to * a.Duration);
        }

        // the parts one after another at the first part's key rate, each next part blended in over JoinBlend from the
        // pose the one before ended on; Upper: bones outside the torso root's branch from the idle loop at the same time
        static HdGun.Anim? Make(NativeBody body, string name, Candidate c, HdGun.Anim? idle, int torsoRoot)
        {
            HdGun g = body.Data;
            var parts = new List<(HdGun.Anim Clip, float From, float To)>();
            foreach (string p in c.Parts)
            {
                if (Part(body, p) is not { } part) return null;
                parts.Add(part);
            }
            if (c.Upper && (idle == null || idle.Duration <= 0 || torsoRoot < 0)) return null;
            int bones = g.BoneCount, stride = g.KeyStride;
            var upper = new bool[bones];
            for (int b = 0; b < bones; b++)
            {
                for (int i = b; i >= 0; i = g.Parent[i])
                {
                    if (i == torsoRoot) upper[b] = true;
                }
            }
            float tick = parts[0].Clip.Tick > 0 ? parts[0].Clip.Tick : 1 / 30f;
            var keyCounts = new int[parts.Count];
            int keys = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                // a part after the first starts one tick in (its time 0 is the blend's start: the last part's end)
                keyCounts[i] = Math.Max(1, (int)MathF.Round((parts[i].To - parts[i].From) / tick));
                keys += i == 0 ? keyCounts[i] + 1 : keyCounts[i];
            }
            var data = new float[keys * bones * stride];
            var lastRot = new Quaternion[bones];
            var lastTrans = new Vector3[bones];
            var lastScale = new Vector3[bones];
            int k = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                (HdGun.Anim a, float from, float to) = parts[i];
                for (int j = i == 0 ? 0 : 1; j <= keyCounts[i]; j++, k++)
                {
                    float t = Math.Min(from + j * tick, to);
                    float w = i == 0 ? 1 : MathHelper.Clamp(j * tick / JoinBlend, 0, 1);
                    w = w * w * (3 - 2 * w);
                    for (int b = 0; b < bones; b++)
                    {
                        Quaternion r;
                        Vector3 p, s;
                        if (c.Upper && !upper[b]) g.Sample(idle!, k * tick % idle!.Duration, b, out r, out p, out s);
                        else
                        {
                            g.Sample(a, t, b, out r, out p, out s);
                            if (w < 1)
                            {
                                r = Quaternion.Slerp(lastRot[b], r, w);
                                p = Vector3.Lerp(lastTrans[b], p, w);
                                s = Vector3.Lerp(lastScale[b], s, w);
                            }
                        }
                        int o = (k * bones + b) * stride;
                        data[o] = r.X; data[o + 1] = r.Y; data[o + 2] = r.Z; data[o + 3] = r.W;
                        data[o + 4] = p.X; data[o + 5] = p.Y; data[o + 6] = p.Z;
                        if (stride >= 10)
                        {
                            data[o + 7] = s.X; data[o + 8] = s.Y; data[o + 9] = s.Z;
                        }
                    }
                }
                for (int b = 0; b < bones; b++) g.Sample(a, to, b, out lastRot[b], out lastTrans[b], out lastScale[b]);
            }
            return new HdGun.Anim { Name = name, Keys = keys, Tick = tick, Duration = (keys - 1) * tick, Frames = data };
        }
    }
}
