using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Anim;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // The DEATH look option's RAGDOLL (docs/IDEAS_SURVEY.md D1 step 2, Entities/Players/PlayerRagdoll.cs).
    // -ragdoll [root=<import root>] [arena=<name>]: a bot match; after 2 s every seat is killed from its own direction and
    // held dead (every other one electric, every third with the Omega Cannon's throw), and each fall is stepped 8 s.
    // Pass: no NaN, no stick stretched past 5%, no bone sunk in the room, every body comes to rest, and two copies taken at death, stepped on their own, land bit for bit where the game's did.
    // The App's suits' ragdoll (Anim/SuitRagdoll) on the DS rig's own lengths draws each bone where MphRead's pose does,
    // and on 20% longer bones stays finite. The native bodies' fit (SuitRagdoll on a body's own rig, CampaignRetroBody):
    // the DS rig taken as a native one lands near MphRead's pose, and with body=<dir> (an imported native body, e.g.
    // bodies/MP4Sylux) that body, fit at death in its idle pose, stays finite, joined up and on the floor.
    // With root= (a First Hunt import) the collapse clip is under it as well; without, the ragdoll alone (no import needed).
    internal static class RagdollTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string? root = args.FirstOrDefault(a => a.StartsWith("root="))?[5..];
            if (args.FirstOrDefault(a => a.StartsWith("body="))?[5..] is string bodyDir)
            {
                if (!NativeBody.Exists(bodyDir))
                {
                    Console.WriteLine($"  [SKIP] no native body under '{bodyDir}'.");
                    return;
                }
                _body = NativeBody.Load(bodyDir);
                _clips = BodyClips.For(_body);
                if (_clips?.Ragdoll == null)
                {
                    Console.WriteLine($"  [FAIL] the body under '{bodyDir}' has no ragdoll map");
                    return;
                }
                Console.WriteLine($"  (with the native body {Path.GetFileName(bodyDir.TrimEnd('/', '\\'))})");
            }
            if (root != null && !MphRecomp.FirstHunt.FirstHuntGame.Register(root))
            {
                Console.WriteLine($"  [SKIP] no First Hunt import under '{root}'.");
                return;
            }
            _withFirstHunt = root != null;
            Console.WriteLine(_withFirstHunt ? "  (with First Hunt imported: RAGDOLL must still leave its collapse clip off)" : "  (no First Hunt import)");
            Paths.ChooseMphPath();
            string[] arenas = args.Where(a => a.StartsWith("arena=")).Select(a => a[6..]).ToArray();
            if (arenas.Length == 0) arenas = new[] { "MP3 PROVING GROUND", "MP1 SANCTORUS", "MP7 PROCESSOR CORE" };
            // (DEATH on RAGDOLL, the app's data dir: independent of First Hunt, owner 2026-11-09)
            MphRecomp.Anim.DeathCollapse.Set("ragdoll", root ?? Environment.CurrentDirectory);
            int fails = 0;
            foreach (string arena in arenas)
            {
                fails += RunArena(arena);
            }
            fails += NetThrowCheck(arenas[0]);
            // (DEATH on FIRST HUNT: the collapse alone, no ragdoll)
            if (_withFirstHunt)
            {
                MphRecomp.Anim.DeathCollapse.Set("firsthunt", root!);
                using (CampaignHost fh = CampaignHost.StartMatch(MatchSettings.Quick(GameMode.Battle, arenas[0], Hunter.Samus, 7)))
                {
                    if (PlayerEntity.HostRagdoll || !PlayerEntity.HostDeathCollapse)
                    {
                        fails++;
                        Console.WriteLine($"  [FAIL] FIRST HUNT: ragdoll {PlayerEntity.HostRagdoll}, collapse {PlayerEntity.HostDeathCollapse} (want off, on)");
                    }
                    else Console.WriteLine("  [OK] FIRST HUNT: the collapse alone, no ragdoll");
                }
            }
            MphRecomp.Anim.DeathCollapse.Set(null, Environment.CurrentDirectory);
            Console.WriteLine(fails == 0 ? "  ALL OK" : $"  {fails} FAILED");
        }

        static bool _withFirstHunt;
        static NativeBody? _body;
        static BodyClips? _clips;

        static int RunArena(string arena)
        {
            int fails = 0;
            void Fail(string s) { fails++; Console.WriteLine($"  [FAIL] {arena}: {s}"); }
            using CampaignHost host = CampaignHost.StartMatch(MatchSettings.Quick(GameMode.Battle, arena, Hunter.Samus, 7));
            if (!PlayerEntity.HostRagdoll)
            {
                Fail("the ragdoll didn't turn on");
                return fails;
            }
            if (PlayerEntity.HostDeathCollapse)
            {
                Fail($"the collapse clip is on under RAGDOLL{(_withFirstHunt ? " (First Hunt imported)" : "")}");
                return fails;
            }
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            // (drawn as a ragdoll, with DEATH's FOLLOW CAM: the main seat's view follows its body, then hands over)
            PlayerEntity.HostRagdollShown = true;
            PlayerEntity.HostDeathFollow = true;
            for (int f = 0; f < 120; f++) host.Step(input);
            // (the main seat waits for a press to spawn: tap Shoot until it's in)
            for (int f = 0; f < 600 && PlayerEntity.Main.Health <= 0; f++)
            {
                host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = f % 2 == 0 ? CampaignButtons.Shoot : 0 });
            }
            for (int f = 0; f < 30; f++) host.Step(input);
            int seats = PlayerEntity.PlayerCount;
            var dolls = new PlayerRagdoll?[seats];
            var copies = new PlayerRagdoll?[seats, 2];
            var startHeight = new float[seats];
            var deathAt = new (Vector3 Position, Vector3 Facing)[seats];
            var twitched = new bool[seats];
            var thrown = new bool[seats];
            for (int i = 0; i < seats; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                if (p.Health <= 0)
                {
                    Console.WriteLine($"  [..] {arena} seat {i} {p.Hunter}: not alive at the kill");
                    continue;
                }
                float a = i * MathF.PI / 4;
                deathAt[i] = (p.Position, new Vector3(p.Field70, 0, p.Field74));
                p.TakeDamage(999u, DamageFlags.IgnoreInvuln, new Vector3(MathF.Cos(a), 0, MathF.Sin(a)), null);
                dolls[i] = p.Ragdoll;
                if (dolls[i] == null)
                {
                    if (!p.IsAltForm && !p.IsMorphing) Fail($"seat {i} {p.Hunter}: no ragdoll at death");
                    else Console.WriteLine($"  [..] {arena} seat {i} {p.Hunter}: {(p.IsAltForm ? "alt form" : "morphing")} (dies as MPH's)");
                    continue;
                }
                // every other seat dies electric (a Shock Coil kill, owner 2026-11-09): it twitches, then lies still
                if (i % 2 == 1)
                {
                    dolls[i]!.Twitch();
                    twitched[i] = dolls[i]!.Twitching;
                    if (!twitched[i]) Fail($"seat {i} {p.Hunter}: no twitch after Twitch()");
                }
                // every third seat takes the Omega Cannon's throw (owner 2026-11-09: "ramped way up"), the hardest there is
                if (i % 3 == 2)
                {
                    dolls[i]!.Launch(Vector3.Zero, PlayerRagdoll.BigThrow(new Vector3(MathF.Cos(a), 0, MathF.Sin(a))));
                    thrown[i] = true;
                }
                if (i == 2 && Environment.GetEnvironmentVariable("RAGDOLL_THROWS") == "1") ThrowProbe(dolls[i]!, host, new Vector3(MathF.Cos(a), 0, MathF.Sin(a)));
                if (HeldCheck(dolls[i]!, p.Hunter, deathAt[i].Position, deathAt[i].Facing, host) is string held) Fail($"seat {i} {p.Hunter}: {held}");
                copies[i, 0] = dolls[i]!.Clone();
                copies[i, 1] = dolls[i]!.Clone();
                startHeight[i] = Height(dolls[i]!);
            }
            var stretch = new float[seats];
            var nan = new bool[seats];
            var restedTwitching = new bool[seats];
            // DEATH's VANILLA ORBS under the collapse clip: MPH's own clips play on beside it (owner 2026-11-09: the orbs
            // "don't animate or anything like vanilla does")
            var orbStart = new (int Clip, int Frame, bool Ended, bool Kept)[seats];
            var orbMoved = new bool[seats];
            for (int i = 0; i < seats; i++) orbStart[i] = PlayerEntity.Players[i].DeathOrbClip;
            int main = PlayerEntity.MainPlayerIndex;
            int followFrom = -1, followTo = -1;
            string? followBad = null;
            HostView? followPrev = null;
            // past the handover, a stick held forward and a turn: the free camera steers the view from where it stopped
            var steer = new CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(0, 1), AimDelta = new System.Numerics.Vector2(2, 0) };
            HostView? steerFrom = null, steerTo = null;
            for (int f = 0; f < 480; f++)
            {
                for (int i = 0; i < seats; i++)
                {
                    if (dolls[i] != null) PlayerEntity.Players[i].RespawnTimer = 1000;
                }
                host.Step(f >= 300 && f < 340 ? steer : input);
                for (int i = 0; i < seats; i++)
                {
                    PlayerRagdoll? d = dolls[i];
                    if (d == null) continue;
                    if (PlayerEntity.Players[i].Ragdoll != d) { Fail($"seat {i}: ragdoll dropped at frame {f}"); dolls[i] = null; continue; }
                    copies[i, 0]!.Step(host.Scene);
                    copies[i, 1]!.Step(host.Scene);
                    stretch[i] = MathF.Max(stretch[i], d.MaxStickError());
                    if (d.Asleep && d.Twitching) restedTwitching[i] = true;
                    var orbNow = PlayerEntity.Players[i].DeathOrbClip;
                    if (orbNow.Frame != orbStart[i].Frame || orbNow.Ended) orbMoved[i] = true;
                    foreach (Vector3 pt in d.Pos)
                    {
                        if (!float.IsFinite(pt.X) || !float.IsFinite(pt.Y) || !float.IsFinite(pt.Z)) nan[i] = true;
                    }
                }
                if (dolls[main] != null && PlayerEntity.Players[main].DeathFollowView is HostView view)
                {
                    if (followFrom < 0) followFrom = f;
                    followTo = f;
                    float far = (view.Position - (dolls[main]!.Pos[PlayerRagdoll.Pelvis] + dolls[main]!.Pos[PlayerRagdoll.Chest]) / 2).Length;
                    if (!float.IsFinite(view.Position.X + view.Position.Y + view.Position.Z + view.Target.X + view.Target.Y + view.Target.Z))
                        followBad ??= $"not finite at frame {f}";
                    else if (followTo == f && f - followFrom < 100 && far > 12)
                        followBad ??= $"{far:0.0} from the body at frame {f}";
                    else if (BuriedDepth(view.Position, view.Position, host.Scene) > 0.05f)
                        followBad ??= $"in the room's solid at frame {f}";
                    else if (followPrev is HostView last && FollowJump(last, view, f - followFrom >= 10) is string jump)
                        followBad ??= $"{jump} at frame {f}";
                    followPrev = view;
                    if (f == 299) steerFrom = view;
                    if (f == 339) steerTo = view;
                }
            }
            PlayerEntity.HostRagdollShown = false;
            PlayerEntity.HostDeathFollow = false;
            // DEATH's FOLLOW CAM: on from the death (first step or so) to the respawn (the free camera takes over silently,
            // owner 2026-11-09), every view finite, near the body while following, out of walls and with no jump
            if (dolls[main] != null)
            {
                int before = fails;
                if (followFrom < 0 || followFrom > 2) Fail($"follow cam: {(followFrom < 0 ? "never on" : $"on only at frame {followFrom}")}");
                else if (followTo != 479) Fail($"follow cam: off at frame {followTo + 1}, before the respawn");
                if (followBad != null) Fail($"follow cam: {followBad}");
                if (steerFrom is HostView a && steerTo is HostView b)
                {
                    Vector3 fa = (a.Target - a.Position).Normalized(), fb = (b.Target - b.Position).Normalized();
                    float ahead = Vector3.Dot(b.Position - a.Position, fa);
                    float turned = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(fa, fb), -1, 1)));
                    if (turned < 5) Fail($"follow cam: the free camera's turn didn't turn the view ({turned:0.0} degrees)");
                    // (forward along a turning facing, and walls may stop it: some of the 40 steps' 8 units)
                    else if (ahead < 0.5f && (b.Position - a.Position).Length < 0.5f) Fail($"follow cam: the free camera's move didn't move the view ({ahead:0.00})");
                    Console.WriteLine($"  [..] {arena} follow cam steered: {ahead:0.0} ahead, {turned:0} degrees turned");
                }
                Console.WriteLine($"  [{(fails == before ? "OK" : "..")}] {arena} follow cam on seat {main}: frames {followFrom}-{followTo}");
            }
            else
            {
                Console.WriteLine($"  [..] {arena} follow cam: the main seat has no ragdoll");
            }
            for (int i = 0; i < seats; i++)
            {
                PlayerRagdoll? d = dolls[i];
                if (d == null) continue;
                PlayerEntity p = PlayerEntity.Players[i];
                string name = $"seat {i} {p.Hunter}{(twitched[i] ? " (twitch)" : "")}{(thrown[i] ? " (omega)" : "")}";
                // no bone sunk in the room: a bone may cut a ledge's corner (only its points and middles collide), but
                // the middle of the part inside the room's solid must be within 5 cm of its surface
                string? through = null;
                foreach ((int a, int b) in Bones)
                {
                    float depth = BuriedDepth(d.Pos[a], d.Pos[b], host.Scene);
                    if (depth > 0.05f) through = $"bone {a}-{b} {depth * 100:0} cm deep";
                }
                bool same = true;
                for (int c = 0; c < 2; c++)
                {
                    for (int k = 0; k < PlayerRagdoll.PointCount; k++)
                    {
                        if (copies[i, c]!.Pos[k] != d.Pos[k] || copies[i, c]!.Prev[k] != d.Prev[k]) same = false;
                    }
                }
                Vector3 pelvis = d.Pos[PlayerRagdoll.Pelvis], pelvis0 = d.Start[PlayerRagdoll.Pelvis];
                string at = $"pelvis ({pelvis0.X:0.00},{pelvis0.Y:0.00},{pelvis0.Z:0.00}) -> ({pelvis.X:0.00},{pelvis.Y:0.00},{pelvis.Z:0.00}), "
                    + $"height {startHeight[i]:0.00} -> {Height(d):0.00}, rest at {d.Ticks} ticks, stretch {stretch[i] * 100:0.0}%";
                int before = fails;
                if (nan[i]) Fail($"{name}: NaN");
                if (stretch[i] > 0.05f) Fail($"{name}: a stick stretched {stretch[i] * 100:0.0}%");
                if (through != null) Fail($"{name}: a bone went through the room ({through})");
                // (a body on a slope can creep a few mm a tick until the 6 s cap stops it: that cap is the designed
                // fallback, so only a body still visibly moving when it's stopped fails)
                if (!d.Asleep || (d.Ticks >= 360 && d.Moved >= 0.02f)) Fail($"{name}: never came to rest ({d.Ticks} ticks, moving {d.Moved:0.000} a tick)");
                else if (d.Ticks >= 360) Console.WriteLine($"  [..] {arena} {name}: crept until the 6 s cap stopped it ({d.Moved:0.000} a tick)");
                var orb = orbStart[i];
                if (orb.Kept) Fail($"{name}: orb clips kept without the collapse");
                else if (orb.Clip == PlayerEntity.DeathClip) Fail($"{name}: the orbs' clip is the collapse");
                else if (!orbMoved[i]) Fail($"{name}: the orbs' clip {orb.Clip} never played on (frame {orb.Frame})");
                if (restedTwitching[i]) Fail($"{name}: came to rest before its twitch ended");
                if (!same) Fail($"{name}: the copies stepped on their own landed elsewhere (not deterministic)");
                string? suit = SuitCheck(d);
                if (suit != null) Fail($"{name}: {suit}");
                string? native = NativeCheck(d, p.Hunter, deathAt[i].Position, deathAt[i].Facing, out string nativeAt);
                if (native != null) Fail($"{name}: {native}");
                at += nativeAt;
                Console.WriteLine($"  [{(fails == before ? "OK" : "..")}] {arena} {name}: {at}");
            }
            return fails;
        }

        // online's exact throw (owner 2026-11-09, "I want exact throw online"): a host death's launch rides the snapshot's
        // throws block bit for bit, an older host's snapshot (no block) reads as none, a client's NetApply death launches
        // the doll on the host's numbers; and a replay's watched seat (not the main one) gets the follow cam
        // a follow cam step that jumps: the view moved or turned more in one step than the chase camera's easing or MPH's
        // free camera's steps would (the snap the owner saw at the old handover). Turns: past the first steps, where the
        // view swings from the eyes down to the body on purpose
        static string? FollowJump(HostView last, HostView view, bool turns)
        {
            float moved = (view.Position - last.Position).Length;
            if (moved > 0.5f) return $"view jumped {moved:0.00}";
            Vector3 a = last.Target - last.Position, b = view.Target - view.Position;
            if (!turns || a.LengthSquared < 1e-8f || b.LengthSquared < 1e-8f) return null;
            float turn = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(a.Normalized(), b.Normalized()), -1, 1)));
            return turn > 12 ? $"view turned {turn:0.0} degrees" : null;
        }

        static int NetThrowCheck(string arena)
        {
            int fails = 0;
            void Fail(string s) { fails++; Console.WriteLine($"  [FAIL] net throw: {s}"); }
            using CampaignHost host = CampaignHost.StartMatch(MatchSettings.Quick(GameMode.Battle, arena, Hunter.Samus, 7));
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            for (int f = 0; f < 120; f++) host.Step(input);
            // (the main seat waits for a press to spawn: tap Shoot until it's in)
            for (int f = 0; f < 600 && PlayerEntity.Main.Health <= 0; f++)
            {
                host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = f % 2 == 0 ? CampaignButtons.Shoot : 0 });
            }
            for (int f = 0; f < 30; f++) host.Step(input);
            int seats = PlayerEntity.PlayerCount, main = PlayerEntity.MainPlayerIndex;
            int seat = -1;
            for (int i = 0; i < seats; i++)
            {
                PlayerEntity c = PlayerEntity.Players[i];
                if (i != main && c.Health > 0 && !c.IsAltForm && !c.IsMorphing) { seat = i; break; }
            }
            if (seat < 0)
            {
                Console.WriteLine("  [..] net throw: no biped bot alive to kill");
                return 0;
            }
            PlayerEntity p = PlayerEntity.Players[seat];
            // the host shows no ragdolls (its DEATH off) but still works the launch out for its guests
            PlayerEntity.HostRagdoll = false;
            p.TakeDamage(999u, DamageFlags.IgnoreInvuln, Vector3.UnitX, null);
            PlayerEntity.HostRagdoll = true;
            if (!p.NetThrowSet || p.Ragdoll != null) Fail($"DEATH off on the host: launch set {p.NetThrowSet}, doll {p.Ragdoll != null} (want set, none)");
            p.TakeDamage(0u, DamageFlags.IgnoreInvuln, null, null);
            // (a fresh life for the main check)
            for (int f = 0; f < 900 && p.Health <= 0; f++) { p.RespawnTimer = 0; host.Step(input); }
            if (p.Health <= 0 || p.IsAltForm) { Fail("the seat never came back as a biped"); return fails; }
            p.TakeDamage(999u, DamageFlags.IgnoreInvuln, new Vector3(0.6f, 0, 0.8f), null);
            if (!p.NetThrowSet || p.Ragdoll == null) { Fail("no launch at the host's death"); return fails; }
            var snap = new MphRecomp.Multiplayer.Net.Snapshot
            {
                Seats = new PlayerNetState[seats], Points = new short[seats], Kills = new short[seats], Deaths = new short[seats]
            };
            for (int i = 0; i < seats; i++) snap.Seats[i] = PlayerEntity.Players[i].NetCapture();
            PlayerNetState sent = snap.Seats[seat];
            if (!sent.HasThrow || sent.ThrowSpeed != p.NetThrowSpeed || sent.ThrowPush != p.NetThrowPush) Fail("NetCapture left the launch out");
            var bytes = new byte[snap.Size];
            int length = snap.Write(bytes);
            if (length != bytes.Length) Fail($"Size {bytes.Length}, wrote {length}");
            var read = MphRecomp.Multiplayer.Net.Snapshot.Read(bytes.AsSpan(0, length));
            PlayerNetState got = read.Seats[seat];
            if (!got.HasThrow || got.ThrowSpeed != sent.ThrowSpeed || got.ThrowPush != sent.ThrowPush)
                Fail($"round trip: speed {sent.ThrowSpeed} -> {got.ThrowSpeed}, push {sent.ThrowPush} -> {got.ThrowPush}");
            for (int i = 0; i < seats; i++) if (i != seat && read.Seats[i].HasThrow != snap.Seats[i].HasThrow) Fail($"seat {i}: a launch it wasn't sent");
            // an older host: the same bytes up to World, no throws block -- the same snapshot, no launch
            for (int i = 0; i < seats; i++) snap.Seats[i].HasThrow = false;
            var plain = new byte[snap.Size];
            int plainLength = snap.Write(plain);
            if (!plain.AsSpan(0, plainLength - 1).SequenceEqual(bytes.AsSpan(0, plainLength - 1))) Fail("the bytes before the throws block changed");
            var old = MphRecomp.Multiplayer.Net.Snapshot.Read(plain.AsSpan(0, plainLength - 1));
            if (old.Seats[seat].HasThrow || old.Seats.Length != seats) Fail("an older host's snapshot read a launch");
            // a client: the host's death applied (NetApply), launched on the host's numbers, not its own
            for (int f = 0; f < 900 && p.Health <= 0; f++) { p.RespawnTimer = 0; host.Step(input); }
            if (p.Health <= 0 || p.IsAltForm) { Fail("the seat never came back for the client check"); return fails; }
            PlayerNetState death = p.NetCapture();
            death.Health = 0;
            death.HasThrow = true;
            death.ThrowSpeed = new Vector3(0.0625f, 0.125f, -0.25f);
            death.ThrowPush = new Vector3(-0.5f, 0.25f, 0.75f);
            p.NetApply(death, pose: false);
            PlayerRagdoll? doll = p.Ragdoll;
            if (doll == null) Fail("no ragdoll from the applied death");
            else if (p.NetThrowSpeed != death.ThrowSpeed || p.NetThrowPush != death.ThrowPush)
                Fail($"client launch {p.NetThrowSpeed}/{p.NetThrowPush}, host sent {death.ThrowSpeed}/{death.ThrowPush}");
            else
            {
                // (a point with the full push weight moved by the speed + push: Pos - Prev)
                Vector3 moved = doll.Pos[PlayerRagdoll.Chest] - doll.Prev[PlayerRagdoll.Chest];
                if ((moved - death.ThrowSpeed).Length < 1e-4f) Fail("the doll took the speed but not the push");
            }
            // a replay watching this (non-main) seat: the follow cam follows its body to the respawn (a replay's deaths are
            // its own sim's, so a fresh life and a sim kill)
            for (int f = 0; f < 900 && p.Health <= 0; f++) { p.RespawnTimer = 0; host.Step(input); }
            if (p.Health <= 0 || p.IsAltForm) { Fail("the seat never came back for the replay check"); return fails; }
            PlayerEntity.HostRagdollShown = true;
            PlayerEntity.HostDeathFollow = true;
            PlayerEntity.HostDeathFollowSeat = seat;
            p.TakeDamage(999u, DamageFlags.IgnoreInvuln, new Vector3(0.6f, 0, 0.8f), null);
            int followFrom = -1, followTo = -1;
            HostView? last = null;
            for (int f = 0; f < 200; f++)
            {
                p.RespawnTimer = 1000;
                host.Step(input);
                if (p.DeathFollowView is HostView view)
                {
                    if (followFrom < 0) followFrom = f;
                    followTo = f;
                    if (last is HostView was && FollowJump(was, view, f - followFrom >= 10) is string jump) { Fail($"replay seat {seat}: {jump} at frame {f}"); break; }
                    last = view;
                }
                if (!p.DeathFollowed && f >= 2) { Fail($"replay seat {seat}: not followed at frame {f}"); break; }
            }
            PlayerEntity.HostDeathFollowSeat = -1;
            PlayerEntity.HostDeathFollow = false;
            PlayerEntity.HostRagdollShown = false;
            if (followFrom < 0 || followFrom > 2) Fail($"replay seat {seat}: follow cam {(followFrom < 0 ? "never on" : $"on only at frame {followFrom}")}");
            else if (followTo != 199) Fail($"replay seat {seat}: follow cam off at frame {followTo + 1}, before the respawn");
            if (fails == 0) Console.WriteLine($"  [OK] net throw: seat {seat} {p.Hunter} launch {sent.ThrowSpeed}/{sent.ThrowPush} round trip exact, "
                + $"older host none, client NetApply on the host's numbers, replay follow cam frames {followFrom}-{followTo}");
            return fails;
        }

        // RAGDOLL_THROWS=1 (RAGDOLL_WAY=<0-15>, a 16th of a turn): how far each throw goes, on seat 2's body at its death,
        // along the most open way (for tuning; walls cut the far ones short)
        static void ThrowProbe(PlayerRagdoll doll, CampaignHost host, Vector3 dir)
        {
            // the open way: of 16, the one an Omega Cannon throw goes furthest along before a wall
            float best = -1;
            for (int k = 0; k < 16; k++)
            {
                float a = k * MathF.PI / 8;
                var d = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                float far = Throw(doll, host, (d + new Vector3(0, 0.3f, 0)) * 0.96f * 3).rest;
                if (far > best) { best = far; dir = d; }
            }
            if (Environment.GetEnvironmentVariable("RAGDOLL_WAY") is string w) { float wa = float.Parse(w) * MathF.PI / 8; dir = new Vector3(MathF.Cos(wa), 0, MathF.Sin(wa)); }
            Console.WriteLine($"  open way {dir.X:0.00},{dir.Z:0.00}: {best:0.0}");
            var throws = new List<(string, Vector3)> { ("omega 11-08", (dir + new Vector3(0, 0.3f, 0)) * 0.96f), ("omega now", PlayerRagdoll.BigThrow(dir)) };
            foreach ((Hunter h, float sp) in new[] { (Hunter.Trace, 1f), (Hunter.Trace, 0.5f), (Hunter.Noxus, 0.1f), (Hunter.Spire, 0.3f), (Hunter.Spire, 0f) })
            {
                PlayerValues v = Metadata.PlayerValues[(int)h];
                Vector3 speed = h == Hunter.Trace ? dir * Fixed.ToFloat(v.LungeHSpeed) * sp + Vector3.UnitY * Fixed.ToFloat(v.LungeVSpeed) * sp : dir * sp;
                throws.Add(($"{h} speed {speed.Length:0.00}", PlayerRagdoll.AltAttackThrow(h, v, speed, dir * 2)));
            }
            foreach (float k in new[] { 1.5f, 3f, 5f })
            {
                throws.Add(($"x{k} omega", (dir + new Vector3(0, 0.3f, 0)) * 0.96f * k));
            }
            foreach ((string name, Vector3 push) in throws)
            {
                var r = Throw(doll, host, push);
                Console.WriteLine($"  THROW {name}: push {push.Length:0.00} -> speed {r.v5:0.00}/step, {r.at15:0.0} at 15, {r.at30:0.0} at 30, {r.rest:0.0} at rest, {r.top:0.0} up, fast {r.air}");
            }
        }

        static (float v5, float at15, float at30, float rest, float top, int air) Throw(PlayerRagdoll doll, CampaignHost host, Vector3 push)
        {
            PlayerRagdoll d = doll.Clone();
            d.Launch(Vector3.Zero, push);
            Vector3 c0 = Com(d);
            float top = c0.Y;
            int air = 0;
            float v5 = 0, at15 = 0, at30 = 0;
            for (int f = 0; f < 360 && !d.Asleep; f++)
            {
                Vector3 before = Com(d);
                d.Step(host.Scene);
                Vector3 c = Com(d);
                top = MathF.Max(top, c.Y);
                if (d.Moved > 0.05f) air = f;
                if (f == 4) v5 = new Vector2(c.X - before.X, c.Z - before.Z).Length;
                if (f == 14) at15 = new Vector2(c.X - c0.X, c.Z - c0.Z).Length;
                if (f == 29) at30 = new Vector2(c.X - c0.X, c.Z - c0.Z).Length;
            }
            Vector3 p = Com(d);
            return (v5, at15, at30, new Vector2(p.X - c0.X, p.Z - c0.Z).Length, top - c0.Y, air);
        }

        static Vector3 Com(PlayerRagdoll d)
        {
            Vector3 sum = Vector3.Zero;
            for (int i = 0; i < d.Pos.Length; i++) sum += d.Pos[i];
            return sum / d.Pos.Length;
        }

        // SuitRagdoll on lengths 1 vs PlayerRagdoll.Pose (null: the same, to 1 mm / 0.001), and on lengths 1.2 finite
        static string? SuitCheck(PlayerRagdoll d)
        {
            Model ds = d.Models[0];
            var skel = new DsSkeleton(ds);
            var fit = new SuitRagdoll(d, skel, null, null, MphRecomp.Anim.HunterRig.ModelMatrix(ds, d.Placement));
            if (!fit.Ok) return $"suit ragdoll: no bone {fit.Missing}";
            var world = new Matrix4[skel.Count];
            if (!fit.Pose(d, world)) return "suit ragdoll: no pose";
            d.Pose(ds, d.Rest[0], d.Drivers[0]);
            float worst = 0;
            string at = "";
            for (int i = 0; i < skel.Count; i++)
            {
                if (!fit.Animated[i]) continue;
                Matrix4 a = world[i] * fit.Model, b = ds.Nodes[i].Animation;
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        float e = MathF.Abs(a[r, c] - b[r, c]);
                        if (e > worst) { worst = e; at = skel.Names[i]; }
                    }
                }
            }
            if (worst > 1e-3f) return $"suit ragdoll off MphRead's pose by {worst:0.0000} at {at} (model scale {ds.Scale.X})";
            var longer = new float[skel.Count];
            Array.Fill(longer, 1.2f);
            var fit2 = new SuitRagdoll(d, skel, longer, null, fit.Model);
            if (!fit2.Pose(d, world)) return "suit ragdoll on longer bones: no pose";
            foreach (Matrix4 m in world)
            {
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        if (!float.IsFinite(m[r, c])) return "suit ragdoll on longer bones: NaN";
                    }
                }
            }
            return null;
        }

        // the skeleton's bones, each from the end nearer the pelvis
        // the native fit: the DS rig as a native one (each bone's DS name its own) vs MphRead's pose (null: within 2 cm),
        // then the native body if any: finite, every bone as far from its parent as at death (10% or 3 cm), and its lowest
        // joint at rest near the ragdoll's lowest point (15 cm under to 25 cm over)
        static string? NativeCheck(PlayerRagdoll d, Hunter hunter, Vector3 position, Vector3 facing, out string at)
        {
            at = "";
            Model ds = d.Models[0];
            Matrix4[] rest = d.Rest[0];
            Matrix4 toRig = d.Placement.Inverted();
            int n = Math.Min(ds.Nodes.Count, rest.Length);
            var bones = new Matrix4[n];
            var parent = new int[n];
            var names = new string?[n];
            for (int j = 0; j < n; j++)
            {
                bones[j] = rest[j] * toRig;
                parent[j] = ds.Nodes[j].ParentIndex;
                names[j] = ds.Nodes[j].Name;
            }
            var fit = new SuitRagdoll(d, bones, parent, names, MphRecomp.Anim.HunterRig.ModelMatrix(ds, d.Placement));
            if (!fit.Ok)
            {
                // a rig without the biped's names (Noxus, Trace): no native body rides one
                at = $", no native fit (no {fit.Missing})";
                return null;
            }
            var world = new Matrix4[n];
            if (!fit.Pose(d, world)) return "native fit on the DS rig: no pose";
            d.Pose(ds, d.Rest[0], d.Drivers[0]);
            float worst = 0;
            string where = "";
            for (int j = 0; j < n; j++)
            {
                if (Near(bones[j], Matrix4.Identity)) continue; // unanimated (identity, as DsSkeleton.Fk's)
                float e = ((world[j] * fit.Model).Row3.Xyz - ds.Nodes[j].Animation.Row3.Xyz).Length;
                if (e > worst) { worst = e; where = ds.Nodes[j].Name; }
            }
            float dsLow = float.MaxValue;
            string dsLowAt = "";
            for (int j = 0; j < n; j++)
            {
                if (parent[j] < 0 || Near(bones[j], Matrix4.Identity)) continue; // its root (Dummy_Root) isn't drawn
                float y = ds.Nodes[j].Animation.Row3.Y;
                if (y < dsLow) { dsLow = y; dsLowAt = ds.Nodes[j].Name; }
            }
            at = $", native on DS {worst * 100:0.0} cm, DS low {dsLow - d.Pos.Min(p => p.Y):+0.00;-0.00} m ({dsLowAt})";
            if (worst > 0.02f) return $"native fit on the DS rig off MphRead's pose by {worst * 100:0.0} cm at {where}";
            if (_body == null || _clips?.Ragdoll == null) return null;
            NativeBody body = _body;
            int count = body.World.Length;
            var dsName = new string?[count];
            foreach ((string bone, string dsBone) in _clips.Ragdoll)
            {
                int b = body.Bone(bone);
                if (b >= 0 && b < count) dsName[b] = dsBone;
            }
            if (body.Clip(_clips.Idle) is HdGun.Anim idle) body.Pose(idle, 0);
            Matrix4 model = body.ModelMatrix(hunter, position, facing, _clips.Lift);
            var fit2 = new SuitRagdoll(d, (Matrix4[])body.World.Clone(), body.Data.Parent, dsName, model);
            if (!fit2.Ok) return $"native body: no bone for {fit2.Missing}";
            var death = new Vector3[count];
            for (int i = 0; i < count; i++) death[i] = (body.World[i] * model).Row3.Xyz;
            var now = new Matrix4[count];
            if (!fit2.Pose(d, now)) return "native body: no pose";
            var end = new Vector3[count];
            float stretch = 0, low = float.MaxValue;
            string stretchAt = "", lowAt = "";
            for (int i = 0; i < count; i++)
            {
                end[i] = (now[i] * model).Row3.Xyz;
                if (!float.IsFinite(end[i].X) || !float.IsFinite(end[i].Y) || !float.IsFinite(end[i].Z)) return "native body: NaN";
                // its root and unanimated bones (Zero Suit's TopN at her feet, its face sets) aren't drawn
                bool drawn = body.Data.Parent[i] >= 0 && !Near(body.World[i], Matrix4.Identity);
                if (drawn && end[i].Y < low) { low = end[i].Y; lowAt = body.Data.Names[i]; }
            }
            // a driver's joint goes with the doll's own give there (its hip points slide about its pelvis point: the DS
            // hunters' hips do the same), so it's allowed that much more
            Span<Matrix3> turns = stackalloc Matrix3[PlayerRagdoll.DriverTotal];
            d.Turns(turns);
            for (int i = 0; i < count; i++)
            {
                int up = body.Data.Parent[i];
                if (up < 0 || up >= count) continue;
                float l0 = (death[i] - death[up]).Length, l1 = (end[i] - end[up]).Length;
                float off = MathF.Abs(l1 - l0);
                int dv = dsName[i] is string ds2 ? PlayerRagdoll.DriverOf(ds2) : -1;
                if (dv > 0)
                {
                    int dvUp = PlayerRagdoll.DriverUp(dv), pa = PlayerRagdoll.DriverPoint(dv), pf = PlayerRagdoll.DriverPoint(dvUp);
                    Vector3 sp = d.Start[pa] - d.Start[pf];
                    Vector3 turned = sp.X * turns[dvUp].Row0 + sp.Y * turns[dvUp].Row1 + sp.Z * turns[dvUp].Row2;
                    off = MathF.Max(0, off - (d.Pos[pa] - d.Pos[pf] - turned).Length);
                }
                if (off > 0.03f && off > l0 * 0.1f && off > stretch) { stretch = off; stretchAt = $"{body.Data.Names[i]} {l0:0.000} -> {l1:0.000}"; }
            }
            // each limb laid along the doll's: the thigh/shin/upper arm/forearm within 15 degrees of its segment
            float bent = 0;
            string bentAt = "";
            foreach ((string from, string to, int pa, int pb) in Limbs)
            {
                int a = Array.IndexOf(dsName, from), b = Array.IndexOf(dsName, to);
                if (a < 0 || b < 0) continue;
                Vector3 own = end[b] - end[a], doll2 = d.Pos[pb] - d.Pos[pa];
                float deg = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(own.Normalized(), doll2.Normalized()), -1, 1)));
                if (deg > bent) { bent = deg; bentAt = $"{from}-{to}"; }
            }
            if (bent > 15) return $"native body's {bentAt} {bent:0} degrees off the ragdoll's";
            float floor = d.Pos.Min(p => p.Y);
            at += $", native body low {low - floor:+0.00;-0.00} m ({lowAt})";
            if (stretchAt != "") return $"native body came apart at {stretchAt}";
            if (low < floor - 0.15f || low > floor + 0.25f) return $"native body's lowest joint {low - floor:+0.00;-0.00} m off the ragdoll's";
            return null;
        }

        // a held death (a freeze's, owner 2026-11-10: "the trooper for some reason stands up straight and unposes for all the
        // variety deaths"): while the doll keeps its shape, the native body keeps its pose at death too, every joint within
        // 6 cm of where it was (the doll's own points move a little as it's held)
        static string? HeldCheck(PlayerRagdoll dead, Hunter hunter, Vector3 position, Vector3 facing, CampaignHost host)
        {
            if (_body == null || _clips?.Ragdoll == null) return null;
            NativeBody body = _body;
            int count = body.World.Length;
            var dsName = new string?[count];
            foreach ((string bone, string dsBone) in _clips.Ragdoll)
            {
                int b = body.Bone(bone);
                if (b >= 0 && b < count) dsName[b] = dsBone;
            }
            if (body.Clip(_clips.Idle) is HdGun.Anim idle) body.Pose(idle, 0);
            Matrix4 model = body.ModelMatrix(hunter, position, facing, _clips.Lift);
            PlayerRagdoll d = dead.Clone();
            d.Freeze();
            var fit = new SuitRagdoll(d, (Matrix4[])body.World.Clone(), body.Data.Parent, dsName, model);
            if (!fit.Ok) return null;
            var death = new Vector3[count];
            for (int i = 0; i < count; i++) death[i] = (body.World[i] * model).Row3.Xyz;
            for (int s = 0; s < 30; s++) d.Step(host.Scene);
            float doll = 0;
            for (int k = 0; k < PlayerRagdoll.PointCount; k++) doll = MathF.Max(doll, (d.Pos[k] - d.Start[k]).Length);
            var now = new Matrix4[count];
            if (!d.Frozen || !fit.Pose(d, now)) return null;
            float worst = 0;
            string worstAt = "";
            for (int i = 0; i < count; i++)
            {
                if (body.Data.Parent[i] < 0 || Near(body.World[i], Matrix4.Identity)) continue;
                float e = ((now[i] * model).Row3.Xyz - death[i]).Length;
                if (e > worst) { worst = e; worstAt = body.Data.Names[i]; }
            }
            Console.WriteLine($"  [..] seat {hunter} held 30 steps: native body moved {worst * 100:0.0} cm at {worstAt}, the doll {doll * 100:0.0} cm");
            return worst > doll + 0.06f ? $"a held death's native body left its pose ({worst * 100:0.0} cm at {worstAt}, the doll {doll * 100:0.0} cm)" : null;
        }

        static readonly (string, string, int, int)[] Limbs =
        {
            ("L_shoulder", "L_elbow", PlayerRagdoll.LShoulder, PlayerRagdoll.LElbow), ("L_elbow", "L_wrist", PlayerRagdoll.LElbow, PlayerRagdoll.LWrist),
            ("R_shoulder", "R_elbow", PlayerRagdoll.RShoulder, PlayerRagdoll.RElbow), ("R_elbow", "R_wrist", PlayerRagdoll.RElbow, PlayerRagdoll.RWrist),
            ("L_hip", "L_knee", PlayerRagdoll.LHip, PlayerRagdoll.LKnee), ("L_knee", "L_ankle", PlayerRagdoll.LKnee, PlayerRagdoll.LAnkle),
            ("R_hip", "R_knee", PlayerRagdoll.RHip, PlayerRagdoll.RKnee), ("R_knee", "R_ankle", PlayerRagdoll.RKnee, PlayerRagdoll.RAnkle),
        };

        static bool Near(Matrix4 a, Matrix4 b)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (MathF.Abs(a[r, c] - b[r, c]) > 1e-4f) return false;
                }
            }
            return true;
        }

        static readonly (int, int)[] Bones =
        {
            (PlayerRagdoll.Pelvis, PlayerRagdoll.Chest), (PlayerRagdoll.Chest, PlayerRagdoll.Neck), (PlayerRagdoll.Neck, PlayerRagdoll.HeadTop),
            (PlayerRagdoll.Chest, PlayerRagdoll.LShoulder), (PlayerRagdoll.LShoulder, PlayerRagdoll.LElbow), (PlayerRagdoll.LElbow, PlayerRagdoll.LWrist),
            (PlayerRagdoll.Chest, PlayerRagdoll.RShoulder), (PlayerRagdoll.RShoulder, PlayerRagdoll.RElbow), (PlayerRagdoll.RElbow, PlayerRagdoll.RWrist),
            (PlayerRagdoll.Pelvis, PlayerRagdoll.LHip), (PlayerRagdoll.LHip, PlayerRagdoll.LKnee), (PlayerRagdoll.LKnee, PlayerRagdoll.LAnkle),
            (PlayerRagdoll.Pelvis, PlayerRagdoll.RHip), (PlayerRagdoll.RHip, PlayerRagdoll.RKnee), (PlayerRagdoll.RKnee, PlayerRagdoll.RAnkle),
        };

        // how deep the part of bone a-b inside the room's solid is: the middle of the stretch from where it goes into a
        // face to where it comes out of one (or its end), measured to the nearer of the two faces
        static float BuriedDepth(Vector3 a, Vector3 b, Scene scene)
        {
            var enter = new CollisionResult();
            if (!CollisionDetection.CheckBetweenPoints(a, b, TestFlags.None, scene, ref enter))
            {
                return 0;
            }
            var exit = new CollisionResult();
            bool exits = CollisionDetection.CheckBetweenPoints(b, a, TestFlags.None, scene, ref exit);
            Vector3 mid = (enter.Position + (exits ? exit.Position : b)) * 0.5f;
            float depth = enter.Plane.W - Vector3.Dot(mid, enter.Plane.Xyz);
            if (exits)
            {
                depth = MathF.Min(depth, exit.Plane.W - Vector3.Dot(mid, exit.Plane.Xyz));
            }
            return depth;
        }

        // the body's height: the head top over the lowest point
        static float Height(PlayerRagdoll d)
        {
            float min = d.Pos.Min(p => p.Y);
            return d.Pos[PlayerRagdoll.HeadTop].Y - min;
        }
    }
}
