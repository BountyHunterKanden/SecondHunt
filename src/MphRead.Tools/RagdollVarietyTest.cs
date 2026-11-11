using System;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // The DEATH look's variety (docs/IDEAS_SURVEY.md D1 step 3, PlayerRagdoll.Vary), run by -ragdollvariety: the main seat's body
    // at death, copied and launched the same way with Vary told something different each time, each copy stepped on its
    // own in the room. Pass: a shot in the face throws the head back past the knees, one in the back flings the arms on
    // ahead, one from the side twists the shoulders, a body off the floor or lifted by a blast tumbles, a light hunter
    // goes further than a heavy one, every copy stays finite, joined up and comes to rest, and two copies of one land bit
    // for bit together; a burn writhes without sliding off, and a Judicator death holds still, then goes over stiff
    internal static class RagdollVarietyTest
    {
        private const int Steps = 30;

        // -ragdollvariety [arena=<name>]: a bot match on DEATH's RAGDOLL; after 2 s the main seat is killed and its body
        // checked as above
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Paths.ChooseMphPath();
            string[] arenas = args.Where(a => a.StartsWith("arena=")).Select(a => a[6..]).ToArray();
            if (arenas.Length == 0) arenas = new[] { "MP3 PROVING GROUND", "MP1 SANCTORUS", "MP7 PROCESSOR CORE" };
            MphRecomp.Anim.DeathCollapse.Set("ragdoll", Environment.CurrentDirectory);
            int fails = 0;
            foreach (string arena in arenas)
            {
                using CampaignHost host = CampaignHost.StartMatch(MatchSettings.Quick(GameMode.Battle, arena, Hunter.Samus, 7));
                var input = new CampaignInput { SelectWeapon = BeamType.None };
                PlayerEntity.HostRagdollShown = true;
                for (int f = 0; f < 120; f++) host.Step(input);
                // (the main seat waits for a press to spawn: tap Shoot until it's in)
                for (int f = 0; f < 600 && PlayerEntity.Main.Health <= 0; f++)
                {
                    host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = f % 2 == 0 ? CampaignButtons.Shoot : 0 });
                }
                for (int f = 0; f < 30; f++) host.Step(input);
                // (the first seat alive on foot, killed by a shot from behind its back)
                PlayerEntity? p = PlayerEntity.Players.Take(PlayerEntity.PlayerCount)
                    .FirstOrDefault(q => q.Health > 0 && !q.IsAltForm && !q.IsMorphing);
                if (p == null)
                {
                    fails++;
                    Console.WriteLine($"  [FAIL] {arena}: no seat alive on foot to kill");
                    continue;
                }
                var facing = new Vector3(p.Field70, 0, p.Field74);
                p.TakeDamage(999u, DamageFlags.IgnoreInvuln, -facing, null);
                if (p.Ragdoll == null)
                {
                    fails++;
                    Console.WriteLine($"  [FAIL] {arena}: no ragdoll at seat {p.SlotIndex}'s death");
                    continue;
                }
                fails += Run(host, p.Ragdoll, facing, arena);
            }
            MphRecomp.Anim.DeathCollapse.Set(null, Environment.CurrentDirectory);
            Console.WriteLine(fails == 0 ? "  ALL OK" : $"  {fails} FAILED");
        }

        public static int Run(CampaignHost host, PlayerRagdoll doll, Vector3 facing, string arena)
        {
            int fails = 0;
            void Fail(string s) { fails++; Console.WriteLine($"  [FAIL] {arena} variety: {s}"); }
            Vector3 face = facing.WithY(0);
            if (face.LengthSquared < 1e-8f)
            {
                Console.WriteLine($"  [..] {arena} variety: the body has no facing");
                return 0;
            }
            face.Normalize();
            // (the same shot every time, backward from the body's own facing, so each copy goes the same way past the
            // same walls: only what Vary is told differs)
            // (the shot goes whichever of 8 ways the launch alone carries the body furthest, so no copy is stopped by a
            // wall a step in)
            Vector3 way = -face, push = way * 0.1f;
            float room = -1;
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4;
                Vector3 w = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                PlayerRagdoll d = doll.Clone();
                d.Launch(Vector3.Zero, w * 0.1f);
                for (int s = 0; s < Steps; s++) d.Step(host.Scene);
                float went = Vector3.Dot(d.Pos[PlayerRagdoll.Chest] - doll.Start[PlayerRagdoll.Chest], w);
                if (went > room)
                {
                    room = went;
                    way = w;
                }
            }
            push = way * 0.1f;
            Vector3 side = new Vector3(-way.Z, 0, way.X);

            // (vary false: the launch alone, as before the variety)
            PlayerRagdoll Try(Hunter hunter, Vector3 bodyFacing, Vector3 speed, Vector3 shot, int steps = Steps, bool vary = true,
                Action<PlayerRagdoll>? how = null)
            {
                PlayerRagdoll copy = doll.Clone();
                copy.Launch(speed, shot);
                if (vary)
                {
                    copy.Vary(hunter, bodyFacing, speed, shot);
                }
                how?.Invoke(copy);
                for (int s = 0; s < steps; s++)
                {
                    copy.Step(host.Scene);
                }
                return copy;
            }
            float Along(PlayerRagdoll d, int a, int b) => Vector3.Dot(d.Pos[a] - d.Pos[b], way);
            float Yaw(PlayerRagdoll d)
            {
                Vector3 line = d.Pos[PlayerRagdoll.RShoulder] - d.Pos[PlayerRagdoll.LShoulder];
                return MathF.Atan2(line.Z, line.X);
            }
            float Turned(float a, float b) => MathF.Abs(MathF.IEEERemainder(a - b, 2 * MathF.PI)) * 180 / MathF.PI;
            // (how far the body turned about the up axis this step, in degrees: its spin, the shape's own bending aside)
            float Spin(PlayerRagdoll d)
            {
                Vector3 middle = Vector3.Zero;
                for (int k = 0; k < PlayerRagdoll.PointCount; k++)
                {
                    middle += d.Pos[k] / PlayerRagdoll.PointCount;
                }
                float spin = 0, inertia = 0;
                for (int k = 0; k < PlayerRagdoll.PointCount; k++)
                {
                    Vector3 r = d.Pos[k] - middle;
                    Vector3 v = d.Pos[k] - d.Prev[k];
                    spin += r.Z * v.X - r.X * v.Z;
                    inertia += r.X * r.X + r.Z * r.Z;
                }
                return MathF.Abs(spin / MathF.Max(inertia, 1e-4f)) * 180 / MathF.PI;
            }
            float Tilt(PlayerRagdoll d)
            {
                Vector3 up = d.Pos[PlayerRagdoll.HeadTop] - d.Pos[PlayerRagdoll.Pelvis];
                return MathF.Acos(Math.Clamp(up.Normalized().Y, -1, 1)) * 180 / MathF.PI;
            }

            // (the head's lead is looked at a few steps in, before the plain body has fallen flat as well)
            const int headSteps = 8;
            PlayerRagdoll plain = Try(Hunter.Samus, Vector3.Zero, Vector3.Zero, push, vary: false);
            PlayerRagdoll plainEarly = Try(Hunter.Samus, Vector3.Zero, Vector3.Zero, push, headSteps, vary: false);
            PlayerRagdoll front = Try(Hunter.Samus, -way, Vector3.Zero, push, headSteps);
            PlayerRagdoll back = Try(Hunter.Samus, way, Vector3.Zero, push);
            PlayerRagdoll fromSide = Try(Hunter.Samus, side, Vector3.Zero, push);
            float headKnees(PlayerRagdoll d) => Along(d, PlayerRagdoll.HeadTop, PlayerRagdoll.LKnee) + Along(d, PlayerRagdoll.HeadTop, PlayerRagdoll.RKnee);
            float armsAhead(PlayerRagdoll d) => Along(d, PlayerRagdoll.LWrist, PlayerRagdoll.Pelvis) + Along(d, PlayerRagdoll.RWrist, PlayerRagdoll.Pelvis);
            float yaw0 = Yaw(doll);
            Console.WriteLine($"  [..] {arena} variety: head past knees at {headSteps} steps {headKnees(plainEarly):0.00} plain, {headKnees(front):0.00} in the face; at {Steps} steps "
                + $"arms ahead {armsAhead(plain):0.00} plain, {armsAhead(back):0.00} in the back; "
                + $"shoulders turned {Turned(Yaw(plain), yaw0):0}° plain, {Turned(Yaw(fromSide), yaw0):0}° from the side");
            if (headKnees(front) < headKnees(plainEarly) + 0.3f) Fail("a shot in the face doesn't throw the head back past the knees");
            if (armsAhead(back) < armsAhead(plain) + 0.1f) Fail("a shot in the back doesn't fling the arms on ahead");
            // (a turn of the shoulders, not a spin: owner 2026-11-10, Judicator bodies spun. Where the plain body turns by itself
            // already, no floor under the spot (MP7), there's nothing to tell the twist from)
            if (Turned(Yaw(plain), yaw0) < 20)
            {
                if (Turned(Yaw(fromSide), yaw0) < Turned(Yaw(plain), yaw0) + 5) Fail("a shot from the side doesn't twist the shoulders");
                if (Turned(Yaw(fromSide), yaw0) > 60) Fail("a shot from the side spins the body round");
            }

            // the tumble: in the air (rising at a jump's speed), lifted by a blast (a missile's push)
            const int tumbleSteps = 12;
            var rising = new Vector3(0, 0.12f, 0);
            Vector3 blast = (way + new Vector3(0, 0.4f, 0)) * 0.12f * 1.2f;
            PlayerRagdoll airPlain = Try(Hunter.Samus, Vector3.Zero, rising, push, tumbleSteps, vary: false);
            PlayerRagdoll air = Try(Hunter.Samus, Vector3.Zero, rising, push, tumbleSteps);
            PlayerRagdoll blastPlain = Try(Hunter.Samus, Vector3.Zero, Vector3.Zero, blast, tumbleSteps, vary: false);
            PlayerRagdoll blasted = Try(Hunter.Samus, Vector3.Zero, Vector3.Zero, blast, tumbleSteps);
            Console.WriteLine($"  [..] {arena} variety at {tumbleSteps} steps: tilt in the air {Tilt(airPlain):0}° plain, {Tilt(air):0}° tumbling; "
                + $"blast {Tilt(blastPlain):0}° plain, {Tilt(blasted):0}° tumbling");
            if (Tilt(air) < Tilt(airPlain) + 15) Fail("a body in the air doesn't tumble");
            if (Tilt(blasted) < Tilt(blastPlain) + 15) Fail("a blast doesn't tumble the body");

            // the hunters' builds: a light one goes further, a heavy one less
            PlayerRagdoll samus = Try(Hunter.Samus, Vector3.Zero, Vector3.Zero, push);
            PlayerRagdoll trace = Try(Hunter.Trace, Vector3.Zero, Vector3.Zero, push);
            PlayerRagdoll spire = Try(Hunter.Spire, Vector3.Zero, Vector3.Zero, push);
            float Went(PlayerRagdoll d) => Vector3.Dot(d.Pos[PlayerRagdoll.Chest] - doll.Start[PlayerRagdoll.Chest], way);
            Console.WriteLine($"  [..] {arena} variety: the chest went {Went(trace):0.00} Trace, {Went(samus):0.00} Samus, {Went(spire):0.00} Spire");
            if (Went(trace) < Went(spire) * 1.1f + 0.02f) Fail("a light hunter doesn't go further than a heavy one");

            // batch B: a burn writhes (changes shape well after a plain body has settled, without sliding off); a Judicator
            // death holds still, then goes over stiff as one piece. (The shape: every pair of points' distance apart, so a
            // body sliding or turning whole doesn't count)
            float[] Pairs(PlayerRagdoll d)
            {
                var pairs = new float[PlayerRagdoll.PointCount * (PlayerRagdoll.PointCount - 1) / 2];
                int n = 0;
                for (int i = 0; i < PlayerRagdoll.PointCount; i++)
                {
                    for (int j = i + 1; j < PlayerRagdoll.PointCount; j++)
                    {
                        pairs[n++] = (d.Pos[i] - d.Pos[j]).Length;
                    }
                }
                return pairs;
            }
            float Bend(float[] a, float[] b) => a.Zip(b, (x, y) => MathF.Abs(x - y)).Average();
            const int writheFrom = 50, writheTo = 180;
            PlayerRagdoll writhe = Try(Hunter.Samus, Vector3.Zero, Vector3.Zero, push, writheFrom, how: d => d.Writhe());
            PlayerRagdoll calm = Try(Hunter.Samus, Vector3.Zero, Vector3.Zero, push, writheFrom);
            Vector3 writheAt = writhe.Pos[PlayerRagdoll.Pelvis];
            float writhed = 0, calmed = 0;
            for (int s = writheFrom; s < writheTo; s++)
            {
                float[] w0 = Pairs(writhe), c0 = Pairs(calm);
                writhe.Step(host.Scene);
                calm.Step(host.Scene);
                writhed += Bend(w0, Pairs(writhe));
                calmed += Bend(c0, Pairs(calm));
            }
            float slid = (writhe.Pos[PlayerRagdoll.Pelvis] - writheAt).WithY(0).Length;
            Console.WriteLine($"  [..] {arena} variety: steps {writheFrom}-{writheTo} changed shape {calmed:0.00} plain, {writhed:0.00} writhing; "
                + $"the pelvis slid {slid:0.00}");
            // (a subtle writhe, owner 2026-11-10: it beats a still plain body, or keeps up with one still settling)
            if (writhed < MathF.Min(calmed, 0.3f) + 0.05f) Fail("a burn doesn't writhe");
            if (slid > 1.5f) Fail("a writhing body slides off");
            PlayerRagdoll frozen = Try(Hunter.Samus, -way, Vector3.Zero, push * 0.8f, 0, how: d => d.Freeze());
            PlayerRagdoll limp = Try(Hunter.Samus, -way, Vector3.Zero, push * 0.8f, 0);
            var held = (Vector3[])frozen.Pos.Clone();
            float[] heldPairs = Pairs(frozen);
            bool stayed = true;
            int s0 = 0;
            for (; frozen.Frozen; s0++)
            {
                frozen.Step(host.Scene);
            }
            for (int k = 0; k < PlayerRagdoll.PointCount; k++)
            {
                if (frozen.Pos[k] != held[k]) stayed = false;
            }
            float tiltFrozen = 0, bentFrozen = 0, bentLimp = 0;
            if (!frozen.Iced) Fail("a frozen body has no ice");
            PlayerRagdoll twin = frozen.Clone();
            int broke = -1, breaks = 0, twinBroke = -1;
            float brokeTilt = 0;
            for (int s = 0; s < 45; s++)
            {
                frozen.Step(host.Scene);
                twin.Step(host.Scene);
                limp.Step(host.Scene);
                tiltFrozen = MathF.Max(tiltFrozen, Tilt(frozen));
                if (frozen.IceBrokeNow)
                {
                    breaks++;
                    broke = s;
                    brokeTilt = Tilt(frozen);
                }
                if (twin.IceBrokeNow) twinBroke = s;
                if (s < 30)
                {
                    bentFrozen = MathF.Max(bentFrozen, Bend(heldPairs, Pairs(frozen)));
                    bentLimp = MathF.Max(bentLimp, Bend(heldPairs, Pairs(limp)));
                }
            }
            Console.WriteLine($"  [..] {arena} variety: frozen {s0} steps ({(stayed ? "held" : "MOVED")}), then over to {tiltFrozen:0}°, "
                + $"bent {bentFrozen:0.000} (limp {bentLimp:0.000})");
            if (s0 < 20 || !stayed) Fail("a Judicator death doesn't hold still first");
            if (tiltFrozen < 50) Fail("a frozen body doesn't topple");
            if (bentFrozen > bentLimp * 0.5f) Fail("a frozen body doesn't go over stiff");
            for (int s = 45; s < 200 && frozen.Iced; s++)
            {
                frozen.Step(host.Scene);
                twin.Step(host.Scene);
                if (frozen.IceBrokeNow)
                {
                    breaks++;
                    broke = s;
                    brokeTilt = Tilt(frozen);
                }
                if (twin.IceBrokeNow) twinBroke = s;
            }
            Console.WriteLine($"  [..] {arena} variety: the ice broke {breaks}x, {broke} steps into the topple at {brokeTilt:0}° (a copy: {twinBroke})");
            if (breaks != 1 || frozen.Iced) Fail("a frozen body's ice doesn't break once");
            if (brokeTilt < 45) Fail("a frozen body's ice doesn't break as it lands");
            if (twinBroke != broke) Fail("a frozen body's ice breaks at a different step on a copy");
            if (limp.Iced || limp.IceBrokeNow) Fail("an unfrozen body has ice");
            // (it goes over the way it was hit, not round: owner 2026-11-10, "the freeze death is still spinning". The turn
            // about the up axis, every step's, added up over the fall and the landing)
            float spunFrozen = 0;
            foreach (Vector3 from in new[] { -way, side })
            {
                PlayerRagdoll spun = Try(Hunter.Noxus, from, Vector3.Zero, push * 0.8f, 0, how: d => d.Freeze());
                float turned = 0;
                for (int s = 0; s < 240; s++)
                {
                    spun.Step(host.Scene);
                    turned += Spin(spun);
                }
                spunFrozen = MathF.Max(spunFrozen, turned);
            }
            Console.WriteLine($"  [..] {arena} variety: a frozen body turned {spunFrozen:0}° round as it went over and landed");
            if (spunFrozen > 25) Fail("a frozen body spins as it falls");

            // batch C: a headshot snaps the head back on the neck, well past an ordinary shot in the face's nod, early on
            float Nod(PlayerRagdoll d)
            {
                Vector3 neck = d.Pos[PlayerRagdoll.Neck] - d.Pos[PlayerRagdoll.Chest];
                Vector3 head = d.Pos[PlayerRagdoll.HeadTop] - d.Pos[PlayerRagdoll.Neck];
                if (neck.LengthSquared < 1e-10f || head.LengthSquared < 1e-10f) return 0;
                return MathF.Acos(Math.Clamp(Vector3.Dot(neck.Normalized(), head.Normalized()), -1, 1)) * 180 / MathF.PI;
            }
            // (which way the head bends off the neck's line, in the chest's own frame: + the way the shot went as the chest saw
            // it at death, so a body gone over on its back still counts its head bent back)
            (Vector3, Vector3, Vector3) Chest(PlayerRagdoll d)
            {
                Vector3 x = (d.Pos[PlayerRagdoll.RShoulder] - d.Pos[PlayerRagdoll.LShoulder]).Normalized();
                Vector3 z = Vector3.Cross(x, d.Pos[PlayerRagdoll.Neck] - d.Pos[PlayerRagdoll.Chest]).Normalized();
                return (x, Vector3.Cross(z, x), z);
            }
            (Vector3 cx, Vector3 cy, Vector3 cz) = Chest(doll);
            Vector3 wayLocal = new Vector3(Vector3.Dot(way, cx), Vector3.Dot(way, cy), Vector3.Dot(way, cz));
            float Back(PlayerRagdoll d)
            {
                (Vector3 x, Vector3 y, Vector3 z) = Chest(d);
                Vector3 neck = (d.Pos[PlayerRagdoll.Neck] - d.Pos[PlayerRagdoll.Chest]).Normalized();
                Vector3 head = (d.Pos[PlayerRagdoll.HeadTop] - d.Pos[PlayerRagdoll.Neck]).Normalized();
                Vector3 back = x * wayLocal.X + y * wayLocal.Y + z * wayLocal.Z;
                back -= neck * Vector3.Dot(back, neck);
                return back.LengthSquared < 1e-10f ? 0 : Vector3.Dot(head - neck * Vector3.Dot(head, neck), back.Normalized());
            }
            PlayerRagdoll faced = Try(Hunter.Samus, -way, Vector3.Zero, push, 0);
            PlayerRagdoll snapped = Try(Hunter.Samus, -way, Vector3.Zero, push, 0, how: d => d.HeadSnap(push));
            // (an ordinary shot in the face whips the head back too, batch A, as far in some places: a headshot's goes at once,
            // steps 0-4, and stays bent back through step 16)
            float nodFaced = 0, nodSnapped = 0, headHeld = 180, headBack = 0;
            Vector3 chestAt = snapped.Pos[PlayerRagdoll.Chest];
            float chestFaced = faced.Pos[PlayerRagdoll.Chest].Y;
            for (int s = 0; s < 17; s++)
            {
                faced.Step(host.Scene);
                snapped.Step(host.Scene);
                if (s < 5)
                {
                    nodFaced += Nod(faced) / 5;
                    nodSnapped += Nod(snapped) / 5;
                }
                else
                {
                    headHeld = MathF.Min(headHeld, Nod(snapped));
                }
                headBack += Back(snapped) / 17;
            }
            Console.WriteLine($"  [..] {arena} variety: the head's nod over steps 0-4 {nodFaced:0}° in the face, {nodSnapped:0}° headshot, "
                + $"then held at {headHeld:0}° or more; bent back the shot's way {headBack:0.00}");
            if (nodSnapped < nodFaced + 15 || headHeld < 50) Fail("a headshot doesn't snap the head back");
            if (headBack < 0.5f) Fail("a headshot's head doesn't go the way the shot went");
            // (owner 2026-11-10: the head jerks first, "a slight moment later they should crumple in that direction", "body
            // starts tilting in that direction and naturally gravity catches the tilt momentum", then "maybe 2.5 seconds is
            // right", then "make the body crumple about twice as fast on the same curve": over in about a second)
            float droppedFaced = chestFaced - faced.Pos[PlayerRagdoll.Chest].Y;
            float droppedSnapped = chestAt.Y - snapped.Pos[PlayerRagdoll.Chest].Y;
            float early = 0;
            for (int s = 17; s < 150; s++)
            {
                snapped.Step(host.Scene);
                if (s == 39) early = chestAt.Y - snapped.Pos[PlayerRagdoll.Chest].Y;
                if (s == 74) break;
            }
            float crumpled = chestAt.Y - snapped.Pos[PlayerRagdoll.Chest].Y;
            float wentWay = Vector3.Dot((snapped.Pos[PlayerRagdoll.Chest] - chestAt).WithY(0), way);
            Console.WriteLine($"  [..] {arena} variety: a headshot's chest dropped {droppedSnapped:0.00} by step 16 (in the face "
                + $"{droppedFaced:0.00}), {early:0.00} by step 39, then {crumpled:0.00} by step 74, {wentWay:0.00} the shot's way");
            // (then it tips over, not frozen: slower at first than a plain death's drop, gravity taking it on down)
            if (droppedSnapped > droppedFaced * 0.85f) Fail("a headshot's body doesn't tip over before it drops");
            if (crumpled < 0.3f || wentWay < 0.2f) Fail("a headshot's body doesn't crumple the shot's way");

            // an electric death on foot holds its pose and shakes hard for 1.5 s, then collapses (owner 2026-11-10: "they
            // sort of stop in their current pose and shake violoently for a second and a half before collpasing")
            PlayerRagdoll shocked = Try(Hunter.Sylux, -way, Vector3.Zero, push, 0, how: d => d.Twitch(onFoot: true));
            Vector3[] stood = (Vector3[])shocked.Pos.Clone();
            float height = stood[PlayerRagdoll.HeadTop].Y - MathF.Min(stood[PlayerRagdoll.LAnkle].Y, stood[PlayerRagdoll.RAnkle].Y);
            float drift = 0, shake = 0;
            Vector3[] last = (Vector3[])stood.Clone();
            for (int s = 0; s < 88; s++)
            {
                shocked.Step(host.Scene);
                for (int k = 0; k < PlayerRagdoll.PointCount; k++)
                {
                    drift = MathF.Max(drift, (shocked.Pos[k] - stood[k]).Length / height);
                    shake += (shocked.Pos[k] - last[k]).Length / height / PlayerRagdoll.PointCount / 88;
                    last[k] = shocked.Pos[k];
                }
            }
            int down = -1;
            for (int s = 88; s < 300; s++)
            {
                shocked.Step(host.Scene);
                if (down < 0 && shocked.Pos[PlayerRagdoll.HeadTop].Y - stood[PlayerRagdoll.HeadTop].Y < -height * 0.6f) down = s;
            }
            Console.WriteLine($"  [..] {arena} variety: an electric death on foot held within {drift * 100:0.0}% of its height, "
                + $"shaking {shake * 100:0.00}% a step, its head down at step {down}");
            if (drift > 0.08f) Fail("an electric death on foot doesn't hold its pose");
            if (shake < 0.005f) Fail("an electric death on foot doesn't shake");
            if (down < 90 || down > 220) Fail("an electric death on foot doesn't collapse after the shaking");

            // every copy: finite, joined up, comes to rest; and two of one land bit for bit together
            PlayerRagdoll[] all = { plain, front, back, fromSide, air, blasted, trace, spire, writhe, frozen, snapped,
                Try(Hunter.Kanden, way, rising, blast, 0, how: d => d.HeadSnap(blast)),
                Try(Hunter.Weavel, side, rising, blast, 0, how: d => d.Writhe()), Try(Hunter.Sylux, -way, Vector3.Zero, push, 0, how: d => d.Twitch(onFoot: true)), Try(Hunter.Noxus, side, rising, blast, 0, how: d => d.Freeze()),
                Try(Hunter.Kanden, side, rising, blast, 0), Try(Hunter.Kanden, side, rising, blast, 0) };
            float stretch = 0;
            for (int s = 0; s < 480; s++)
            {
                foreach (PlayerRagdoll d in all)
                {
                    d.Step(host.Scene);
                    stretch = MathF.Max(stretch, d.MaxStickError());
                }
            }
            bool finite = true, rested = true, same = true;
            foreach (PlayerRagdoll d in all)
            {
                foreach (Vector3 p in d.Pos)
                {
                    if (!float.IsFinite(p.X + p.Y + p.Z)) finite = false;
                }
                if (!d.Asleep || d.Ticks >= 360 && d.Moved >= 0.02f) rested = false;
            }
            for (int k = 0; k < PlayerRagdoll.PointCount; k++)
            {
                if (all[^1].Pos[k] != all[^2].Pos[k] || all[^1].Prev[k] != all[^2].Prev[k]) same = false;
            }
            if (!finite) Fail("NaN");
            if (stretch > 0.05f) Fail($"a stick stretched {stretch * 100:0.0}%");
            if (!rested) Fail("a copy never came to rest");
            if (!same) Fail("two copies of one landed apart (not deterministic)");
            Console.WriteLine($"  [{(fails == 0 ? "OK" : "..")}] {arena} variety: {all.Length} copies, stretch {stretch * 100:0.0}%, all at rest, deterministic");
            return fails;
        }
    }
}
