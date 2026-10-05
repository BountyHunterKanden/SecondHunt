using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats.Collision;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // Vanilla accuracy (docs/ACCURACY.md): MphRead's side of the emulator comparisons in tools/accuracy/.
    // Vanilla ticks its game logic at 30 Hz (every 2nd DS frame); MphRead ticks at 60 Hz, so a vanilla tick is
    // compared against every 2nd MphRead frame.
    //
    //   -accuracy jump [trace.csv]   Samus jumps from standing on the Celestial Archives landing site (UNIT2_LAND),
    //                                at the spot the emulator route probe_buttons.lua stands her. Prints the arc per
    //                                vanilla tick, apex and air time; with a BizHawk trace.csv also the vanilla arc.
    internal static class AccuracyTest
    {
        private static readonly CultureInfo _inv = CultureInfo.InvariantCulture;

        public static void Run(string[] args)
        {
            string test = args.Length >= 2 ? args[1] : "jump";
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            if (test == "jump")
            {
                Jump(args.Length >= 3 ? args[2] : null);
            }
            else if (test == "walk" && args.Length >= 3)
            {
                Walk(args[2]);
            }
            else if (test == "ramp")
            {
                // Stronghold Void A (UNIT1_TP1) door #2 -> the corridor -> far door #4: the door sweep's straight walk
                // ("ramp ball": the same in the morph ball, which morphs once the door is open)
                bool ball = args.Length >= 3 && args[2] == "ball";
                using CampaignHost host = CampaignHost.Start("UNIT1_TP1", collectDrawItems: true);
                for (int i = 0; i < 300; i++)
                {
                    host.Step(default);
                }
                DoorEntity door = host.FindDoor(2)!;
                host.PlacePlayer(door.Position + door.FacingVector * 2 + door.UpVector * 0.5f, -door.FacingVector);
                for (int i = 0; i < 20; i++)
                {
                    host.Step(default);
                }
                bool open = false;
                int openFrame = 0;
                for (int f = 1; f <= (ball ? 600 : 420); f++)
                {
                    if (!open && (door.Flags.TestFlag(DoorFlags.ShotOpen) || door.Flags.TestFlag(DoorFlags.Open)))
                    {
                        open = true;
                        openFrame = f;
                    }
                    bool morphing = ball && open && f - openFrame < 40;
                    host.Step(new CampaignInput
                    {
                        Move = new System.Numerics.Vector2(0, open && !morphing ? 1 : 0),
                        Buttons = !open && f % 30 < 2 ? CampaignButtons.Shoot
                            : morphing && f - openFrame < 4 ? CampaignButtons.Morph : CampaignButtons.None
                    });
                    if (f % 6 == 0 && host.Player.Position.Z < 2.5f && host.Player.Position.Z > -14)
                    {
                        Console.WriteLine($"  {f,4} pos {Fmt(host.Player.Position)} speed {Fmt(host.Player.Speed)} {host.Player.Flags1}");
                    }
                }
            }
            else if (test == "scanlog")
            {
                // the ROM's ScanLog table: position, record id, category, name (logbook bits are keyed by record id)
                IReadOnlyList<StringTableEntry> log = MphRead.Text.Strings.ReadStringTable(MphRead.Text.StringTables.ScanLog);
                for (int i = 0; i < log.Count && i < (args.Length >= 3 ? Int32.Parse(args[2]) : 40); i++)
                {
                    StringTableEntry e = log[i];
                    Console.WriteLine($"  [{i,3}] {e.Prefix}{e.Id} cat {e.Category} {e.Value1}");
                }
                Console.WriteLine($"  {log.Count} records; equipment (cat E): {log.Count(e => e.Category == 'E')}");
            }
            else if (test == "logbook")
            {
                // a new game's logbook (UI session's report 2026-09-30): the starting equipment by scan id = record number.
                // A real DS shows EQUIPMENT 33% (9 of 27) and no scans on a fresh file.
                var save = new StorySave();
                string[] want = { "SCAN VISOR", "THERMAL POSITIONER", "ARM CANNON", "POWER BEAM", "MISSILE LAUNCHER",
                    "MORPH BALL", "MORPH BALL BOMB", "JUMP BOOTS", "CHARGE SHOT" };
                IReadOnlyList<StringTableEntry> log = MphRead.Text.Strings.ReadStringTable(MphRead.Text.StringTables.ScanLog);
                List<string> marked = log.Where(e => save.CheckLogbook(e)).Select(e => e.Value1).ToList();
                int equipMax = save.GetLogbookCount(false, 'E');
                int pass = 0, fail = 0;
                void Check(bool ok, string what)
                {
                    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
                    if (ok) { pass++; } else { fail++; }
                }
                Check(marked.SequenceEqual(want), $"marked records: {String.Join(", ", marked)}");
                Check(!save.CheckLogbook(0), "no bit 0 (there is no record L000)");
                Check(save.EquipmentCount == 9 && save.GetLogbookCount(true, 'E') == 9 && equipMax == 27,
                    $"equipment {save.EquipmentCount} (counted {save.GetLogbookCount(true, 'E')}) of {equipMax} = {save.EquipmentCount * 100 / equipMax}%");
                Check(save.ScanCount == 0 && save.GetLogbookCount(true, 'L', 'B', 'O') == 0, $"scans {save.ScanCount}");
                Console.WriteLine($"logbook: {pass} passed, {fail} failed");
            }
            else if (test == "strafe" && args.Length >= 3)
            {
                // probe_strafe.lua: standing on the landed gunship, Right held 40 frames, released; Up held 40, released.
                // The camera tilts _field684 (strafe) / _field688 (forward), private, read by reflection
                Dictionary<int, string[]> v = ReadTrace(args[2], out Dictionary<string, int> col);
                var f684 = typeof(PlayerEntity).GetField("_field684", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var f688 = typeof(PlayerEntity).GetField("_field688", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                using CampaignHost host = StartLanding();
                Console.WriteLine("  frame  vanilla 684  MphRead 684 | vanilla 688  MphRead 688");
                for (int f = 1; f <= 240; f++)
                {
                    var move = new System.Numerics.Vector2(f >= 20 && f < 60 ? 1 : 0, f >= 120 && f < 160 ? 1 : 0);
                    host.Step(new CampaignInput { Move = move });
                    if (Environment.GetEnvironmentVariable("ACC_DEBUG") == "1" && f >= 18 && f <= 60)
                    {
                        Console.WriteLine($"    dbg f{f}: 684 {(float)f684.GetValue(host.Player)!:F3} speed {Fmt(host.Player.Speed)} {host.Player.Flags1} zoom {host.Player.EquipInfo.Zoomed}");
                    }
                    if (f % 2 == 0 && v.ContainsKey(f) && (f % 8 == 0 || f is 20 or 22 or 24 or 120 or 122))
                    {
                        Console.WriteLine($"  {f,5}  {F(v[f][col["tilt684"]]),11:F3}  {(float)f684.GetValue(host.Player)!,11:F3} | "
                            + $"{F(v[f][col["tilt688"]]),11:F3}  {(float)f688.GetValue(host.Player)!,11:F3}");
                    }
                }
            }
            else if (test == "morph")
            {
                // probe_morph.lua: morph at frame 10, roll forward frames 120-179, then let go. Ball and camera positions to a
                // CSV like the emulator's (morph_mphread.csv next to the given path), for the camera-follow comparison (A3)
                string outPath = args.Length >= 3 ? args[2] : "morph_mphread.csv";
                using CampaignHost host = StartLanding();
                using var w = new StreamWriter(outPath);
                w.WriteLine("f,px,py,pz,cx,cy,cz");
                for (int f = 1; f <= 260; f++)
                {
                    host.Step(new CampaignInput
                    {
                        Buttons = f >= 10 && f < 14 ? CampaignButtons.Morph : CampaignButtons.None,
                        Move = new System.Numerics.Vector2(0, f >= 120 && f < 180 ? 1 : 0)
                    });
                    Vector3 p = host.Player.Position, c = host.Player.CameraInfo.Position;
                    w.WriteLine(String.Join(",", new[] { f, p.X, p.Y, p.Z, c.X, c.Y, c.Z }.Select(x => Convert.ToSingle(x).ToString("F6", _inv))));
                }
                Console.WriteLine($"  wrote {outPath}; alt form {host.Player.IsAltForm}");
            }
            else if (test == "roll" && args.Length >= 3)
            {
                Roll(args[2]);
            }
            else if (test == "slope" && args.Length >= 3)
            {
                Slope(args[2], ramp26: false);
            }
            else if ((test == "ramp26" || test == "ramp45") && args.Length >= 3)
            {
                Slope(args[2], ramp26: test == "ramp26", ramp45: test == "ramp45");
            }
            else if (test == "steep")
            {
                // walkable steep floors (normal Y 0.5 .. 0.9 = 26-60 deg, where PlayerCollision's slope push-out is scaled):
                // "steep <ROOM>" lists a room's biggest faces, "steep all" sums the area per room
                Steep(args.Length >= 3 ? args[2] : "UNIT2_LAND", args);
            }
            else if (test == "idle")
            {
                // probe_idle.lua: standing on the landed gunship for 10 s (vanilla bobs ~7.05-7.22 and drifts -X)
                using CampaignHost host = StartLanding(draw: !(args.Length >= 3 && args[2] == "nodraw"));
                for (int f = 1; f <= 600; f++)
                {
                    host.Step(default);
                    if (f % 40 == 1)
                    {
                        Console.WriteLine($"  {f,4} {Fmt(host.Player.Position)}");
                    }
                }
            }
            else
            {
                Console.WriteLine($"  unknown test '{test}' (jump [trace.csv], walk <trace.csv>, idle [nodraw])");
            }
        }

        // collectDrawItems = true is how the Android app runs it, and it matters: the draw pass is also where model
        // animations advance, and platform collision follows them (without it the landed gunship never bobs)
        private static CampaignHost StartLanding(bool draw = true)
        {
            CampaignHost host = CampaignHost.Start("UNIT2_LAND", collectDrawItems: draw);
            for (int i = 0; i < 1000 && !host.Ended; i++) // input is held until ~952
            {
                host.Step(new CampaignInput
                {
                    DialogButton = !host.DialogPaused || i % 10 != 0 ? HostDialogButton.None
                        : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance
                });
            }
            return host;
        }

        private static void Jump(string? tracePath)
        {
            // where probe_buttons.lua leaves her standing (vanilla tick before the A press)
            var spot = new Vector3(-7.420166f, 3.528809f, 0.016113f);
            using CampaignHost host = StartLanding();
            host.PlacePlayer(spot + new Vector3(0, 0.25f, 0), new Vector3(-1, 0, 0));
            for (int i = 0; i < 120; i++)
            {
                host.Step(default);
            }
            float floor = host.Player.Position.Y;
            Console.WriteLine($"  MphRead: standing at {Fmt(host.Player.Position)} (vanilla {Fmt(spot)})");
            // vanilla probe: A held for 8 DS frames; same here (8 MphRead frames)
            var ys = new List<float>();
            for (int i = 0; i < 140; i++)
            {
                host.Step(new CampaignInput { Buttons = i < 8 ? CampaignButtons.Jump : CampaignButtons.None });
                ys.Add(host.Player.Position.Y - floor);
            }
            Summarize("MphRead", ys.Where((_, i) => i % 2 == 1).ToList());
            if (tracePath != null && File.Exists(tracePath))
            {
                List<float> vanilla = VanillaJump(tracePath);
                Summarize("vanilla", vanilla);
                Console.WriteLine("  tick  vanilla  MphRead  diff");
                List<float> ours = ys.Where((_, i) => i % 2 == 1).ToList();
                for (int t = 0; t < Math.Min(vanilla.Count, ours.Count) && t < 40; t++)
                {
                    Console.WriteLine($"  {t,4}  {vanilla[t],7:F3}  {ours[t],7:F3}  {ours[t] - vanilla[t],6:F3}");
                }
            }
        }

        // heights above the floor per vanilla tick, from the first tick that leaves the floor after the A press
        private static List<float> VanillaJump(string path)
        {
            string[] lines = File.ReadAllLines(path);
            string[] head = lines[0].Split(',');
            int fi = Array.IndexOf(head, "f"), yi = Array.IndexOf(head, "py");
            var rows = lines.Skip(1).Select(l => l.Split(','))
                .Select(c => (F: Int32.Parse(c[fi], _inv), Y: Single.Parse(c[yi], _inv))).ToList();
            // probe_buttons.lua: A at frame 330 (8 frames), game ticks on even frames
            float floor = rows.First(r => r.F == 328).Y;
            return rows.Where(r => r.F >= 330 && r.F % 2 == 0 && r.F < 330 + 140).Select(r => r.Y - floor).ToList();
        }

        // probe_buttons.lua frames 60-99: Up held from the ship's hull, she walks off its edge and falls to the floor.
        // Same start (vanilla position at frame 58, facing her walking direction), same 40 frames of Up.
        private static void Walk(string tracePath)
        {
            Dictionary<int, string[]> v = ReadTrace(tracePath, out Dictionary<string, int> col);
            Vector3 VPos(int f) => new Vector3(F(v[f][col["px"]]), F(v[f][col["py"]]), F(v[f][col["pz"]]));
            Vector3 VSpeed(int f) => new Vector3(F(v[f][col["vx"]]), F(v[f][col["vy"]]), F(v[f][col["vz"]]));
            Vector3 start = VPos(58);
            Vector3 dir = VSpeed(66).Xz.Normalized() is var d ? new Vector3(d.X, 0, d.Y) : default;
            using CampaignHost host = StartLanding();
            Console.WriteLine($"  MphRead after the landing sequence: {Fmt(host.Player.Position)} (vanilla landing.State: (-2.954, 7.212, 0.127)), "
                + $"flags1 {host.Player.Flags1}, flags2 {host.Player.Flags2}");
            host.PlacePlayer(start, dir);
            int settle = Environment.GetEnvironmentVariable("ACC_SETTLE") is string st ? Int32.Parse(st) : 1;
            for (int i = 0; i < settle; i++)
            {
                host.Step(default);
            }
            Console.WriteLine($"  after placing: flags1 {host.Player.Flags1}, flags2 {host.Player.Flags2}");
            Console.WriteLine($"  start {Fmt(start)} facing {Fmt(dir)}; MphRead after placing: {Fmt(host.Player.Position)}");
            Console.WriteLine("  frame   vanilla pos                  MphRead pos                  dpos   | vanilla speed            MphRead speed");
            // MphRead frame k here plays vanilla frame 59 + k (Up on vanilla frames 60-99)
            for (int k = 1; k <= 72; k++)
            {
                int f = 59 + k;
                host.Step(new CampaignInput { Move = new System.Numerics.Vector2(0, f >= 60 && f <= 99 ? 1 : 0) });
                if (host.DialogPaused || k == 1)
                {
                    Console.WriteLine($"  f{f}: dialog paused {host.DialogPaused} ({host.Dialog}), flags1 {host.Player.Flags1}");
                }
                if (f <= 66 || (f >= 99 && f <= 104))
                {
                    Console.WriteLine($"    frame {f}: MphRead speed {Fmt(host.Player.Speed)} |h| {host.Player.Speed.Xz.Length:F4}");
                }
                if (f % 2 == 0 && v.ContainsKey(f))
                {
                    Vector3 p = host.Player.Position;
                    Console.WriteLine($"  {f,5}   {Fmt(VPos(f))}  {Fmt(p)}  {(p - VPos(f)).Length,6:F3} | {Fmt(VSpeed(f))}  {Fmt(host.Player.Speed)}");
                }
            }
        }

        // probe_roll.lua frames 478-600: the morph ball at rest against the wall on the landing site's flat floor
        // (x -25.35, facing -X), Down (roll backward, +X) on vanilla frames 480-519, then coasting; flat until x ~ -17.8.
        // Vanilla tick at frame 480 + 2k <-> MphRead after 2k + 2 frames of the same input.
        private static void Roll(string tracePath)
        {
            Dictionary<int, string[]> v = ReadTrace(tracePath, out Dictionary<string, int> col);
            Vector3 VPos(int f) => new Vector3(F(v[f][col["px"]]), F(v[f][col["py"]]), F(v[f][col["pz"]]));
            float VH(int f) => new Vector2(F(v[f][col["vx"]]), F(v[f][col["vz"]])).Length;
            Vector3 start = VPos(478);
            using CampaignHost host = StartLanding();
            for (int f = 0; f < 60; f++)
            {
                host.Step(new CampaignInput { Buttons = f < 4 ? CampaignButtons.Morph : CampaignButtons.None });
            }
            host.PlacePlayer(start, new Vector3(-1, 0, 0));
            for (int f = 0; f < 60; f++)
            {
                host.Step(default);
            }
            Console.WriteLine($"  alt form {host.Player.IsAltForm}; vanilla start {Fmt(start)}, MphRead {Fmt(host.Player.Position)} "
                + $"speed {host.Player.Speed.Xz.Length:F4}");
            Vector3 mStart = host.Player.Position;
            Console.WriteLine("  frame   vanilla x-x0  MphRead x-x0   diff  | vanilla |h|  MphRead |h|");
            int vanillaTop = -1, oursTop = -1;
            for (int k = 0; k < 60; k++)
            {
                int f = 480 + 2 * k;
                for (int i = 0; i < 2; i++)
                {
                    var input = new CampaignInput { Move = new System.Numerics.Vector2(0, f < 520 ? -1 : 0) };
                    host.Step(input);
                    for (int d = 0; d < 600 && host.DialogPaused; d++)
                    {
                        // the landing site's one-time message box (vanilla showed it on the first roll, frame ~232)
                        host.Step(new CampaignInput { Move = input.Move, DialogButton = d % 10 == 0 ? HostDialogButton.Advance : HostDialogButton.None });
                    }
                }
                if (!v.ContainsKey(f))
                {
                    break;
                }
                float dv = VPos(f).X - start.X, dm = host.Player.Position.X - mStart.X;
                float hv = VH(f), hm = host.Player.Speed.Xz.Length;
                if (vanillaTop < 0 && hv >= 0.308f) { vanillaTop = k; }
                if (oursTop < 0 && hm >= 0.308f) { oursTop = k; }
                if (k < 26 || k % 4 == 0)
                {
                    Console.WriteLine($"  {f,5}   {dv,10:F4}  {dm,12:F4}  {dm - dv,7:F4} | {hv,10:F4}  {hm,11:F4}");
                }
            }
            Console.WriteLine($"  top speed reached: vanilla tick {vanillaTop}, MphRead tick {oursTop}");
        }

        // probe_slope.lua frames 478-740: Samus at rest on the landing site's flat floor (x -19.25, facing -X), Down (walk
        // backward, +X) from vanilla frame 480: flat floor to x ~ -17.2, then the ~19 deg slope up to x ~ -7.2 (a short flat
        // step at x ~ -11.6), then the gunship. Vanilla tick at frame 480 + 2k <-> MphRead after 2k + 2 frames of Down.
        // ramp26 = probe_ramp26.lua: placed at (-11, 0.5, -19.8) facing -X, Down 480-599: 1.5 u of floor, the 26.6 deg
        // ramp (x -9.5 .. -3.5, y 0 .. 3, normal Y 0.894 = PlayerCollision's scaled push-out), then the flat top.
        // ramp45 = probe_ramp45.lua: placed on the y 13 ledge at (-2.5, 13.6, -23.75) facing -X, Up (forward, -X) 480-639:
        // 2 u of ledge, the 45 deg ramp (x -4.5 .. -6.5, y 13 .. 15), then the top. Progress is measured along -X there.
        private static void Slope(string tracePath, bool ramp26, bool ramp45 = false)
        {
            Dictionary<int, string[]> v = ReadTrace(tracePath, out Dictionary<string, int> col);
            Vector3 VPos(int f) => new Vector3(F(v[f][col["px"]]), F(v[f][col["py"]]), F(v[f][col["pz"]]));
            Vector3 start = VPos(478);
            Vector3 walk = ramp26 || ramp45 ? VPos(494) - VPos(486) : VPos(500) - VPos(486);
            int downEnd = ramp45 ? 640 : ramp26 ? 600 : 740;
            float sign = ramp45 ? -1 : 1; // progress along +X (backward walks) or -X (ramp45's forward walk)
            if (ramp45)
            {
                walk = -walk;
            }
            var facing = -new Vector3(walk.X, 0, walk.Z).Normalized();
            using CampaignHost host = StartLanding();
            host.PlacePlayer(start + new Vector3(0, 0.1f, 0), facing);
            for (int f = 0; f < 60; f++)
            {
                host.Step(default);
            }
            Vector3 mStart = host.Player.Position;
            Console.WriteLine($"  vanilla start {Fmt(start)}, MphRead {Fmt(mStart)} facing {Fmt(facing)}, flags1 {host.Player.Flags1}");
            Console.WriteLine("  frame   vanilla x-x0   y      MphRead x-x0   y      dx     dy   | per tick: vanilla h   MphRead h");
            var vx = new List<(float X, float Y)>();
            var mx = new List<(float X, float Y)>();
            Vector3 vPrev = start, mPrev = mStart;
            for (int k = 0; k < (ramp45 ? 90 : ramp26 ? 75 : 130); k++)
            {
                int f = 480 + 2 * k;
                for (int i = 0; i < 2; i++)
                {
                    var input = new CampaignInput { Move = new System.Numerics.Vector2(0, f < downEnd ? -sign : 0) };
                    host.Step(input);
                    for (int d = 0; d < 600 && host.DialogPaused; d++)
                    {
                        // the landing site's one-time message box (vanilla already showed it on the walk down)
                        host.Step(new CampaignInput { Move = input.Move, DialogButton = d % 10 == 0 ? HostDialogButton.Advance : HostDialogButton.None });
                    }
                }
                if (!v.ContainsKey(f))
                {
                    break;
                }
                Vector3 vp = VPos(f), mp = host.Player.Position;
                vx.Add((vp.X * sign, vp.Y));
                mx.Add((mp.X * sign, mp.Y));
                float hv = (vp - vPrev).Xz.Length, hm = (mp - mPrev).Xz.Length;
                vPrev = vp;
                mPrev = mp;
                if (k < 12 || k % 4 == 0)
                {
                    Console.WriteLine($"  {f,5}   {vp.X - start.X,9:F3} {vp.Y,6:F3}   {mp.X - mStart.X,9:F3} {mp.Y,6:F3}  {mp.X - mStart.X - (vp.X - start.X),6:F3} {mp.Y - vp.Y,6:F3} |  {hv,8:F4}  {hm,10:F4}");
                }
            }
            // average horizontal speed over a stretch of x, per vanilla tick
            void Stretch(string what, float x0, float x1)
            {
                static float Ticks(List<(float X, float Y)> t, float x)
                {
                    int i = t.FindIndex(p => p.X >= x);
                    if (i <= 0) { return Single.NaN; }
                    return i - 1 + (x - t[i - 1].X) / (t[i].X - t[i - 1].X);
                }
                float tv = Ticks(vx, x1) - Ticks(vx, x0), tm = Ticks(mx, x1) - Ticks(mx, x0);
                Console.WriteLine($"  {what,-28} x {x0} .. {x1}: vanilla {tv,6:F2} ticks ({(x1 - x0) / tv:F4} u/tick), "
                    + $"MphRead {tm,6:F2} ticks ({(x1 - x0) / tm:F4} u/tick) = {tv / tm * 100:F1}% of vanilla's speed");
            }
            if (ramp45)
            {
                Stretch("ledge (-x)", 2.7f, 4.2f);
                Stretch("45 deg ramp (-x)", 4.6f, 5.9f);
                return;
            }
            if (ramp26)
            {
                Stretch("flat floor", -10.7f, -9.7f);
                Stretch("26.6 deg ramp", -9.2f, -4.0f);
                Stretch("flat top", -1.9f, -1.0f);
                return;
            }
            Stretch("flat floor", -18.9f, -17.4f);
            Stretch("slope (lower)", -16.8f, -12.3f);
            Stretch("slope (upper)", -10.2f, -7.8f);
            Stretch("slope incl. the step", -16.8f, -7.8f);
        }

        private static void Steep(string which, string[] args)
        {
            IEnumerable<RoomMetadata> rooms = which == "all"
                ? Metadata.RoomMetadata.Values.Where(r => !r.FirstHunt && !r.Hybrid && r.Name.StartsWith("UNIT"))
                : new[] { Metadata.RoomMetadata[which] };
            foreach (RoomMetadata room in rooms)
            {
                if (Collision.GetCollision(room).Info is not MphCollisionInfo info)
                {
                    continue;
                }
                var faces = new List<(float Area, float Deg, Vector3 Centre, Vector3 Normal, float Walkable, List<Vector3> Verts)>();
                float steep = 0, mild = 0;
                foreach (CollisionData data in info.Data)
                {
                    Vector4 plane = info.Planes[data.PlaneIndex];
                    if (data.IgnorePlayers || plane.Y <= 0.5f)
                    {
                        continue;
                    }
                    Vector3 p0 = info.Points[info.PointIndices[data.PointStartIndex]];
                    Vector3 centre = p0;
                    float area = 0;
                    for (int j = 1; j < data.PointIndexCount - 1; j++)
                    {
                        Vector3 p1 = info.Points[info.PointIndices[data.PointStartIndex + j]];
                        Vector3 p2 = info.Points[info.PointIndices[data.PointStartIndex + j + 1]];
                        area += Vector3.Cross(p1 - p0, p2 - p0).Length / 2;
                        centre += p1;
                    }
                    centre = (centre + info.Points[info.PointIndices[data.PointStartIndex + data.PointIndexCount - 1]]) / data.PointIndexCount;
                    float deg = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(plane.Y, -1, 1)));
                    if (args.Length >= 8 && args[3] == "box")
                    {
                        // steep <ROOM> box x0 x1 z0 z1: every walkable face whose centre is in the box
                        float[] b = args[4..8].Select(a => Single.Parse(a, _inv)).ToArray();
                        if (centre.X >= b[0] && centre.X <= b[1] && centre.Z >= b[2] && centre.Z <= b[3])
                        {
                            Console.WriteLine($"    {area,6:F2} u2 {deg,5:F1} deg n {Fmt(plane.Xyz)}: " + String.Join(" ", Enumerable.Range(0, data.PointIndexCount)
                                .Select(j => Fmt(info.Points[info.PointIndices[data.PointStartIndex + j]]))));
                        }
                        continue;
                    }
                    if (plane.Y < 0.9f)
                    {
                        steep += area;
                        faces.Add((area, deg, centre, plane.Xyz, data.Slipperiness, Enumerable.Range(0, data.PointIndexCount)
                            .Select(j => info.Points[info.PointIndices[data.PointStartIndex + j]]).ToList()));
                    }
                    else if (plane.Y < 0.999f)
                    {
                        mild += area;
                    }
                }
                if (which == "all")
                {
                    if (steep >= 4)
                    {
                        Console.WriteLine($"  {room.Name,-12} {room.InGameName,-28} steep {steep,7:F1} u2 ({faces.Count} faces, biggest "
                            + $"{faces.Max(f => f.Area),5:F1} u2), 1-26 deg {mild,7:F1} u2");
                    }
                    continue;
                }
                Console.WriteLine($"  {room.Name} ({room.InGameName}): steep floors {steep:F1} u2 in {faces.Count} faces; 1-26 deg floors {mild:F1} u2");
                foreach (var f in faces.OrderByDescending(f => f.Area).Take(25))
                {
                    Console.WriteLine($"    {f.Area,6:F2} u2  {f.Deg,5:F1} deg  centre {Fmt(f.Centre)}  normal {Fmt(f.Normal)}  slip {f.Walkable}");
                    if (args.Length >= 4 && args[3] == "verts")
                    {
                        Console.WriteLine($"      {String.Join(" ", f.Verts.Select(Fmt))}");
                    }
                }
            }
        }

        private static float F(string s) => Single.Parse(s, _inv);

        private static Dictionary<int, string[]> ReadTrace(string path, out Dictionary<string, int> columns)
        {
            string[] lines = File.ReadAllLines(path);
            string[] head = lines[0].Split(',');
            columns = head.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i);
            int fi = columns["f"];
            return lines.Skip(1).Select(l => l.Split(',')).ToDictionary(c => Int32.Parse(c[fi], _inv), c => c);
        }

        private static void Summarize(string who, List<float> perTick)
        {
            int start = perTick.FindIndex(y => y > 0.01f);
            int apexTick = perTick.IndexOf(perTick.Max());
            int land = start < 0 ? -1 : perTick.FindIndex(start + 1, y => y <= 0.02f);
            Console.WriteLine($"  {who,-8}: apex {perTick.Max():F3} at tick {apexTick}, air time {(land < 0 ? "?" : (land - start).ToString())} ticks");
        }

        private static string Fmt(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
    }
}
