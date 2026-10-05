using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;

using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // Headless campaign tests: MphRead's OWN adventure simulation hosted by MphRecomp.Campaign.CampaignHost (no GL,
    // no audio, host-driven input) -- the same way the Android app hosts it. Saves use slot 0: nothing touches disk.
    //
    //   -campaignsim [room=UNIT2_LAND] [doorId=9] [weapon=auto|power|missile|...] [blocked]
    //       one door, verbose: stand Samus in front of it, fire, walk through, check the arrival.
    //   -campaignsweep rooms    load every story room on every entity layer it uses and run 300 frames
    //   -campaignsweep doors [roomId|a-b]  walk through every connector door (on the first layer it exists on) with every
    //                           weapon unlocked, and check she arrives at the door the entity data pairs it with;
    //                           doors locked until an event are expected to hold. Results also go to
    //                           extract_out/campaign/{room,door}_sweep.txt
    internal static partial class CampaignSim
    {
        private static readonly string[] _layerNames = { "FirstVisit", "Escape", "Cleared", "Layer3" };

        // boss flags putting every area (incl. the Oubliette) on the given entity layer: 2 bits per area
        private static BossFlags FlagsForLayer(int layer) => (BossFlags)(layer * 0x15555);

        private static bool IsLandingRoom(int roomId) => roomId is 27 or 45 or 65 or 77 or 89;

        // "hunter=Kanden" anywhere in the arguments plays the tests as that hunter
        private static Hunter _hunter = Hunter.Samus;

        private static string[] TakeHunterArg(string[] args)
        {
            foreach (string arg in args)
            {
                if (arg.StartsWith("hunter=", StringComparison.OrdinalIgnoreCase))
                {
                    _hunter = Enum.Parse<Hunter>(arg[7..], ignoreCase: true);
                }
            }
            return args.Where(a => !a.StartsWith("hunter=", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        public static void Run(string[] args)
        {
            args = TakeHunterArg(args);
            if (Environment.GetEnvironmentVariable("MPHAUDIOLOG") is "1" or "2")
            {
                MusicPlayer.Host = new LoggingMusicHost();
            }
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT2_LAND";
            int doorId = args.Length >= 3 ? Int32.Parse(args[2]) : 9;
            string weapon = args.Length >= 4 ? args[3].ToLowerInvariant() : "auto";
            bool blocked = args.Length >= 5 && args[4] == "blocked";
            RoomMetadata meta = Metadata.RoomMetadata[room];
            DoorCase? c = DoorCases(meta).FirstOrDefault(d => d.DoorId == doorId);
            if (c == null)
            {
                Console.WriteLine($"  no connector door #{doorId} in {room}");
                return;
            }
            if (weapon != "auto")
            {
                c.Weapon = weapon == "power" ? BeamType.PowerBeam : Enum.Parse<BeamType>(weapon, ignoreCase: true);
            }
            c.ExpectBlocked |= blocked;
            DoorResult r = RunDoor(c, verbose: true);
            Console.WriteLine($"  {(r.Ok ? "PASS" : "FAIL")}: {r.Summary}");
        }

        public static void Sweep(string[] args)
        {
            args = TakeHunterArg(args);
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            Cheats.NoRandomEncounters = true;
            string mode = args.Length >= 2 ? args[1] : "doors";
            // optional room id or range "a-b"
            int fromRoom = 27, toRoom = 92;
            if (args.Length >= 3)
            {
                string[] parts = args[2].Split('-');
                fromRoom = Int32.Parse(parts[0]);
                toRoom = parts.Length > 1 ? Int32.Parse(parts[1]) : fromRoom;
            }
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign"));
            Directory.CreateDirectory(outDir);
            var lines = new List<string>();
            void Log(string line)
            {
                Console.WriteLine(line);
                lines.Add(line);
            }
            var total = Stopwatch.StartNew();
            int ok = 0, bad = 0;
            var outcomes = new Dictionary<Outcome, int>();
            if (mode == "rooms")
            {
                for (int id = 27; id <= 92; id++)
                {
                    RoomMetadata meta = Metadata.GetRoomById(id)!;
                    ushort mask = 0;
                    foreach (Entity e in Read.GetEntities(meta.EntityPath!, -1, meta.FirstHunt))
                    {
                        mask |= e.LayerMask;
                    }
                    for (int layer = 0; layer < 4; layer++)
                    {
                        if ((mask & (1 << layer)) == 0 || mask == 0xFFFF && layer == 3)
                        {
                            continue;
                        }
                        string label = $"{id} {meta.Name} ({meta.InGameName}) [{_layerNames[layer]}]";
                        try
                        {
                            var sw = Stopwatch.StartNew();
                            using CampaignHost host = CampaignHost.Start(meta.Name, setupSave: s => GiveAll(s, layer), hunter: _hunter);
                            long loadMs = sw.ElapsedMilliseconds;
                            for (int i = 0; i < 300 && !host.Ended; i++)
                            {
                                host.Step(default);
                            }
                            bool sane = host.RoomId == id && Single.IsFinite(host.Player.Position.X);
                            Log($"  {(sane ? "ok  " : "BAD ")} {label}: load {loadMs} ms, pos {Fmt(host.Player.Position)}, health {host.Player.Health}"
                                + (host.Ended ? " (scene ended)" : ""));
                            if (sane) ok++; else bad++;
                        }
                        catch (Exception ex)
                        {
                            bad++;
                            Log($"  FAIL {label}: {ex.GetType().Name}: {ex.Message}");
                            Log($"       {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
                        }
                    }
                }
            }
            else
            {
                for (int id = 27; id <= 92; id++)
                {
                    if (id < fromRoom || id > toRoom)
                    {
                        continue;
                    }
                    foreach (DoorCase c in DoorCases(Metadata.GetRoomById(id)!))
                    {
                        DoorResult r;
                        try
                        {
                            r = RunDoor(c, verbose: false);
                        }
                        catch (Exception ex)
                        {
                            r = new DoorResult { Summary = $"{ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}" };
                        }
                        if (r.Ok) ok++; else bad++;
                        outcomes[r.Outcome] = outcomes.GetValueOrDefault(r.Outcome) + 1;
                        Log($"  {(r.Ok ? "ok  " : "FAIL")} {c.Label}: {r.Summary}");
                    }
                }
            }
            Log($"  {ok} ok, {bad} not ok, {total.Elapsed.TotalSeconds:0} s"
                + (outcomes.Count > 0 ? $" ({String.Join(", ", outcomes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))})" : ""));
            File.WriteAllLines(Path.Combine(outDir, $"{(mode == "rooms" ? "room" : "door")}_sweep.txt"), lines);
        }

        // -campaigndraw [room=UNIT2_LAND]: host with draw-list collection at 1920x1080, check every mesh item maps to
        // a registered host mesh, and measure the per-frame cost of logic + draw list
        public static void Draw(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT2_LAND";
            using CampaignHost host = CampaignHost.Start(room, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
            Scene scene = host.Scene;
            var sw = new Stopwatch();
            int frames = 0;
            int badIds = 0, maxItems = 0, texturedItems = 0, badTextures = 0;
            var byType = new Dictionary<RenderItemType, int>();
            var meshesSeen = new HashSet<int>();
            var input = new CampaignInput();
            for (int i = 0; i < 1500 && !host.Ended; i++)
            {
                // after the landing sequence: walk forward and turn slowly so the view sweeps the room
                if (i >= 900)
                {
                    input.Move = new System.Numerics.Vector2(0, 1);
                    input.AimDelta = new System.Numerics.Vector2(4, 0);
                }
                sw.Start();
                host.Step(input);
                sw.Stop();
                frames++;
                int count = scene.OpaqueItems.Count + scene.DecalItems.Count + scene.TranslucentItems.Count;
                maxItems = Math.Max(maxItems, count);
                foreach (IReadOnlyList<RenderItem> list in new[] { scene.OpaqueItems, scene.DecalItems, scene.TranslucentItems })
                {
                    foreach (RenderItem item in list)
                    {
                        byType[item.Type] = byType.GetValueOrDefault(item.Type) + 1;
                        if (item.HasTexture)
                        {
                            texturedItems++;
                            if (!scene.HostTextures.TryGetValue(item.TextureBindingId, out Scene.HostTexture? tex)
                                || tex.Pixels.Length != tex.Width * tex.Height || tex.Width == 0)
                            {
                                badTextures++;
                            }
                        }
                        if (item.Type == RenderItemType.Mesh)
                        {
                            if (item.ListId < 1 || item.ListId > scene.HostMeshes.Count)
                            {
                                badIds++;
                            }
                            else
                            {
                                meshesSeen.Add(item.ListId);
                            }
                        }
                    }
                }
            }
            Console.WriteLine($"  {room}: {frames} frames, {sw.Elapsed.TotalMilliseconds / frames:0.000} ms/frame (logic + draw list)");
            Console.WriteLine($"  host meshes registered: {scene.HostMeshes.Count}, distinct drawn: {meshesSeen.Count}, max items/frame: {maxItems}");
            Console.WriteLine($"  items by type (all frames): {String.Join(", ", byType.Select(kv => $"{kv.Key} {kv.Value}"))}");
            Console.WriteLine($"  {(badIds == 0 ? "PASS" : "FAIL")}: mesh items with an unregistered list id: {badIds}");
            Console.WriteLine($"  {(badTextures == 0 ? "PASS" : "FAIL")}: textured items {texturedItems}, missing/bad host textures: {badTextures} "
                + $"(registry {scene.HostTextures.Count})");
            // decode every drawn mesh the way the Android backend will
            int tris = 0, badVerts = 0, maxMtx = 0, empty = 0;
            foreach (int id in meshesSeen)
            {
                (Model model, Mesh mesh, bool isRoom) = scene.HostMeshes[id - 1];
                float[] v = MphRecomp.Render.DsDisplayList.Decode(model, mesh, isRoom);
                int n = v.Length / MphRecomp.Render.DsDisplayList.Stride;
                if (n == 0)
                {
                    empty++;
                }
                tris += n / 3;
                for (int k = 0; k < v.Length; k++)
                {
                    if (!Single.IsFinite(v[k]))
                    {
                        badVerts++;
                    }
                    if (k % MphRecomp.Render.DsDisplayList.Stride == 12)
                    {
                        maxMtx = Math.Max(maxMtx, (int)v[k]);
                    }
                }
            }
            Console.WriteLine($"  {(badVerts == 0 && maxMtx < 31 ? "PASS" : "FAIL")}: decoded {meshesSeen.Count} meshes -> {tris} triangles, "
                + $"{empty} empty, non-finite values {badVerts}, max matrix index {maxMtx}");
            var models = scene.HostMeshes.Where((m, i) => meshesSeen.Contains(i + 1)).Select(m => m.Model.Name).Distinct().OrderBy(n => n);
            Console.WriteLine($"  models drawn: {String.Join(", ", models)}");
        }

        // -campaignui: the touch-screen-only interactions through the host -- the scan visor tutorial prompt (OK),
        // scanning the Celestial Gateway console to completion (scan dialog OK -> logbook), the weapon wheel, and
        // direct weapon keys not handing out weapons you don't have
        public static void Ui(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            int pass = 0, fail = 0;
            void Check(string name, bool ok)
            {
                if (ok) pass++; else fail++;
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            }
            using (CampaignHost host = CampaignHost.Start("UNIT2_LAND"))
            {
                for (int i = 0; i < 900; i++) host.Step(default);
                PlayerEntity p = host.Player;
                // free weapon cheat off: Volt Driver isn't owned yet
                for (int i = 0; i < 10; i++) host.Step(new CampaignInput { SelectWeapon = i < 2 ? BeamType.VoltDriver : BeamType.None });
                Check($"direct key doesn't grant an unowned weapon (holding {p.CurrentWeapon})", p.CurrentWeapon == BeamType.PowerBeam);
                // 1) walking into the first-visit trigger shows the SCAN VISOR prompt as an OK dialog and pauses
                host.Scene.TryGetEntity(24, out EntityBase? trigEnt);
                var trig = (TriggerVolumeEntity)trigEnt!;
                Vector3 trigCenter = trig.Volume.GetCenter();
                host.PlacePlayer(trigCenter, -Vector3.UnitX);
                int f = 0;
                for (; f < 120 && !host.DialogPaused; f++)
                {
                    host.Step(default);
                    if (f % 20 == 0)
                    {
                        Console.WriteLine($"    trigger #24 {trig.Volume.Type} centre {Fmt(trigCenter)}, active {trig.Active}; Samus {Fmt(p.Position)} "
                            + $"inside {trig.Volume.TestPoint(p.Position)}, dialog {host.Dialog}, paused {host.DialogPaused}");
                    }
                }
                Check($"tutorial prompt pauses the game as an OK dialog ({host.Dialog}, frame {f})", host.DialogPaused && host.Dialog == DialogType.Okay);
                // the confirm button appears after the text has typed out; press OK until it closes
                for (f = 0; f < 600 && host.DialogPaused; f++) host.Step(new CampaignInput { DialogButton = f % 10 == 0 ? HostDialogButton.Okay : HostDialogButton.None });
                Check($"OK closes it and unpauses (after {f} frames)", !host.DialogPaused);
                // 2) scan the console (Generic_Console, scan id 280)
                var console = new Vector3(-5.34f, 3.12f, 4.76f);
                // stand on the console's own level with a clear line of sight (the game drops a scan target the
                // moment anything blocks camera -> target, TestFlags.Scan)
                bool clear = false;
                foreach (Vector3 dir in new[] { -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ })
                {
                    foreach (float dist in new[] { 3f, 4.5f })
                    {
                        host.PlacePlayer(console + dir * dist, -dir);
                        for (int i = 0; i < 30; i++) host.Step(default);
                        for (int i = 0; i < 60; i++) host.Step(new CampaignInput { AimDelta = AimAt(p, console) });
                        CollisionResult discard = default;
                        clear = p.Health > 0 && MathF.Abs(p.Position.Y - console.Y) < 3
                            && !CollisionDetection.CheckBetweenPoints(p.CameraInfo.Position, console, TestFlags.Scan, host.Scene, ref discard);
                        if (clear)
                        {
                            break;
                        }
                    }
                    if (clear)
                    {
                        break;
                    }
                }
                Console.WriteLine($"    stood at {Fmt(p.Position)} (strict line-of-sight check: {clear})");
                for (int i = 0; i < 20; i++) host.Step(new CampaignInput { Buttons = i < 2 ? CampaignButtons.ScanVisor : CampaignButtons.None });
                Check("scan visor on", p.ScanVisor);
                host.Scene.TryGetEntity(13, out EntityBase? consoleEnt);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                object? Field(string name) => typeof(PlayerEntity).GetField(name, flags)?.GetValue(p);
                static object? Member(object? o, string name) => o == null ? null
                    : (object?)o.GetType().GetField(name)?.GetValue(o) ?? o.GetType().GetProperty(name)?.GetValue(o);
                for (f = 0; f < 900 && !host.DialogPaused; f++)
                {
                    host.Step(new CampaignInput { Buttons = CampaignButtons.Scan, AimDelta = AimAt(p, console) });
                    if (f % 150 == 0)
                    {
                        object? cur = Field("_curScanTarget");
                        object? curEnt = Member(cur, "Entity");
                        object? curDist = Member(cur, "Distance");
                        Console.WriteLine($"    f{f}: Samus {Fmt(p.Position)} cam {Fmt(p.CameraInfo.Position)} facing {Fmt(p.CameraInfo.Facing)}; "
                            + $"console visible {consoleEnt!.ScanVisible()} scanId {consoleEnt.GetScanId()}; targets {Field("_scanTargetCount")}, "
                            + $"current {curEnt} dist {curDist}, scanning {Field("_scanning")} timer {Field("_scanningTimer")}/{Field("_scanningTime")}");
                        var list = (System.Collections.IList?)Field("_scanTargets");
                        object? t0 = list?[0];
                        if (t0 != null)
                        {
                            float sx = (float)Member(t0, "ScreenX")!;
                            float sy = (float)Member(t0, "ScreenY")!;
                            Console.WriteLine($"      target0 {Member(t0, "Entity")} screen DS px ({sx * 256:0}, {sy * 192:0}), "
                                + $"dist {Member(t0, "Distance")}; scene size {host.Scene.Size}");
                        }
                    }
                }
                Check($"holding scan completes it -> scan dialog ({host.Dialog}, frame {f})", host.DialogPaused && host.Dialog == DialogType.Scan);
                var scanned = (EntityBase?)Field("_scanningEntity");
                int scannedId = scanned?.GetScanId() ?? -1;
                Console.WriteLine($"    scanned {scanned?.Type}#{scanned?.Id} (scan id {scannedId})");
                bool logged = scannedId > 0 && GameState.StorySave.CheckLogbook(scannedId);
                for (f = 0; f < 600 && host.DialogPaused; f++) host.Step(new CampaignInput { DialogButton = f % 10 == 0 ? HostDialogButton.Advance : HostDialogButton.None });
                Check($"Advance pages through and closes the scan result (after {f} frames)", !host.DialogPaused);
                Check($"scan id {scannedId} logged in the logbook after OK (before OK: {logged})", scannedId > 0 && GameState.StorySave.CheckLogbook(scannedId));
            }
            using (CampaignHost host = CampaignHost.Start("UNIT2_LAND", setupSave: s => GiveAll(s, 0)))
            {
                // the landing sequence blocks input until its camera sequence ends
                int landing = 0;
                for (; landing < 3000 && (landing < 300 || CameraSequence.Current != null); landing++) host.Step(default);
                Console.WriteLine($"    landing sequence over after {landing} frames");
                PlayerEntity p = host.Player;
                // 3) weapon wheel: hold the menu, point right (-> Imperialist), release
                for (int i = 0; i < 30; i++) host.Step(new CampaignInput { Buttons = CampaignButtons.WeaponMenu, WeaponWheel = new System.Numerics.Vector2(1, 0) });
                Check($"wheel open while held ({host.WeaponMenuOpen}), selection {p.WeaponSelection}", host.WeaponMenuOpen && p.WeaponSelection == BeamType.Imperialist);
                for (int i = 0; i < 30; i++) host.Step(default);
                Check($"release equips it ({p.CurrentWeapon})", p.CurrentWeapon == BeamType.Imperialist);
                for (int i = 0; i < 30; i++) host.Step(new CampaignInput { Buttons = CampaignButtons.WeaponMenu, WeaponWheel = new System.Numerics.Vector2(0, 1) });
                for (int i = 0; i < 30; i++) host.Step(default);
                Check($"up -> Volt Driver ({p.CurrentWeapon})", p.CurrentWeapon == BeamType.VoltDriver);
            }
            Console.WriteLine($"  {pass} passed, {fail} failed");
        }

        // -campaignhud [room=UNIT2_LAND]: record MphRead's HUD headless and software-render it to PNGs
        // (extract_out/campaign/hud_*.png) -- the same quads/textures the Android HUD pass draws
        public static void Hud(string[] args)
        {
            args = TakeHunterArg(args);
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT2_LAND";
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign"));
            using CampaignHost host = CampaignHost.Start(room, hunter: _hunter, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
            int landing = 0;
            for (; landing < 3000 && (landing < 300 || CameraSequence.Current != null); landing++) host.Step(default);
            for (int i = 0; i < 60; i++) host.Step(default);
            void Shot(string name)
            {
                Scene scene = host.Scene;
                Console.WriteLine($"  {name}: {scene.HudItemCount} HUD items, {scene.HudSprites.Count} sprites interned, mask {scene.HudMaskBindingId}");
                string file = Path.Combine(outDir, $"hud_{name}.png");
                RenderHud(scene, 1920, 1080, file);
                Console.WriteLine($"    wrote {file}");
            }
            Shot("normal");
            for (int i = 0; i < 40; i++) host.Step(new CampaignInput { Buttons = i < 2 ? CampaignButtons.ScanVisor : CampaignButtons.None });
            Shot("scanvisor");
        }

        private static void RenderHud(Scene scene, int w, int h, string file)
        {
            var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h,
                new SixLabors.ImageSharp.PixelFormats.Rgba32(40, 44, 52, 255));
            for (int n = 0; n < scene.HudItemCount; n++)
            {
                Scene.HostHudItem item = scene.GetHudItem(n);
                ColorRgba[] pixels;
                int tw, th;
                if (item.SpriteIndex >= 0)
                {
                    Scene.HostHudSprite sprite = scene.HudSprites[item.SpriteIndex];
                    (pixels, tw, th) = (sprite.Pixels, sprite.Width, sprite.Height);
                }
                else if (scene.HostTextures.TryGetValue(item.BindingId, out Scene.HostTexture? tex))
                {
                    (pixels, tw, th) = (tex.Pixels, tex.Width, tex.Height);
                }
                else
                {
                    continue;
                }
                // NDC -> pixels; texcoord (0,0) sits at (Left, Top) even when the quad is flipped
                float x0 = (item.Left + 1) / 2 * w, x1 = (item.Right + 1) / 2 * w;
                float y0 = (1 - item.Top) / 2 * h, y1 = (1 - item.Bottom) / 2 * h;
                int px0 = (int)MathF.Floor(MathF.Max(0, MathF.Min(x0, x1))), px1 = (int)MathF.Ceiling(MathF.Min(w, MathF.Max(x0, x1)));
                int py0 = (int)MathF.Floor(MathF.Max(0, MathF.Min(y0, y1))), py1 = (int)MathF.Ceiling(MathF.Min(h, MathF.Max(y0, y1)));
                for (int py = py0; py < py1; py++)
                {
                    float v = (py + 0.5f - y0) / (y1 - y0);
                    int ty = Math.Clamp((int)(v * th), 0, th - 1);
                    for (int px = px0; px < px1; px++)
                    {
                        float u = (px + 0.5f - x0) / (x1 - x0);
                        int tx = Math.Clamp((int)(u * tw), 0, tw - 1);
                        ColorRgba c = pixels[ty * tw + tx];
                        float a = c.Alpha / 255f * item.Alpha;
                        if (a <= 0)
                        {
                            continue;
                        }
                        var dst = img[px, py];
                        img[px, py] = new SixLabors.ImageSharp.PixelFormats.Rgba32(
                            (byte)(c.Red * a + dst.R * (1 - a)), (byte)(c.Green * a + dst.G * (1 - a)), (byte)(c.Blue * a + dst.B * (1 - a)), 255);
                    }
                }
            }
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, file);
        }

        // aim delta (game mouse units) turning the camera toward a point: +X turns right, +Y looks down, 4 units = 1 degree
        private static System.Numerics.Vector2 AimAt(PlayerEntity p, Vector3 target)
        {
            Vector3 dir = (target - p.CameraInfo.Position).Normalized();
            Vector3 facing = p.CameraInfo.Facing.Normalized();
            Vector3 right = Vector3.Cross(facing, Vector3.UnitY).Normalized();
            var flat = new Vector3(facing.X, 0, facing.Z).Normalized();
            float yaw = MathF.Atan2(Vector3.Dot(dir, right), Vector3.Dot(new Vector3(dir.X, 0, dir.Z), flat)) * 180 / MathF.PI;
            float pitch = (MathF.Asin(Math.Clamp(dir.Y, -1, 1)) - MathF.Asin(Math.Clamp(facing.Y, -1, 1))) * 180 / MathF.PI;
            return new System.Numerics.Vector2(Math.Clamp(yaw * 2, -40, 40), Math.Clamp(-pitch * 2, -40, 40));
        }

        private static void GiveAll(StorySave s, int layer)
        {
            s.BossFlags = FlagsForLayer(layer);
            s.Weapons = 0xFF;
            s.Health = s.HealthMax = 799;
            s.Ammo[0] = s.AmmoMax[0] = 4000;
            s.Ammo[1] = s.AmmoMax[1] = 950;
        }

        private sealed class DoorCase
        {
            public RoomMetadata Room = null!;
            public int DoorId;
            public int Layer;
            public int TargetRoom = -1;
            public int TargetLayer;
            public int ExpectedArrival = -1;
            public BeamType Weapon = BeamType.PowerBeam;
            public bool ExpectBlocked;
            public bool Morph;
            public string Label = "";
        }

        private enum Outcome { Arrived, Held, Assisted, Skipped, Fail }

        private sealed class DoorResult
        {
            public Outcome Outcome = Outcome.Fail;
            public bool Ok => Outcome != Outcome.Fail;
            public string Summary = "";
        }

        // every connector-owning door of a room, on the first layer it exists on (everyLayer: once per story layer it
        // exists on), with its expected arrival door
        private static IEnumerable<DoorCase> DoorCases(RoomMetadata meta, bool everyLayer = false)
        {
            foreach (Entity e in Read.GetEntities(meta.EntityPath!, -1, meta.FirstHunt))
            {
                if (e is not Entity<DoorEntityData> door || door.Data.ConnectorId == 255)
                {
                    continue;
                }
                IEnumerable<int> layers = Enumerable.Range(0, door.LayerMask == 0xFFFF ? 3 : 4).Where(l => (door.LayerMask & (1 << l)) != 0);
                foreach (int layer in everyLayer ? layers : layers.Take(1))
                {
                    yield return DoorCaseOn(meta, door, layer);
                }
            }
        }

        private static DoorCase DoorCaseOn(RoomMetadata meta, Entity<DoorEntityData> door, int layer)
        {
            DoorEntityData d = door.Data;
            string file = d.EntityFilename.MarshalString();
            RoomMetadata? target = Metadata.RoomList.FirstOrDefault(r => r.EntityFilename != null
                && r.EntityFilename.StartsWith(file[..Math.Min(15, file.Length)], StringComparison.InvariantCultureIgnoreCase));
            var c = new DoorCase
            {
                Room = meta, DoorId = door.EntityId, Layer = layer, TargetRoom = target?.Id ?? -1,
                Morph = d.DoorType == DoorType.MorphBall
            };
            bool locked = d.Locked != 0;
            c.ExpectBlocked = locked && d.PaletteId >= 8;
            if (locked && d.PaletteId < 8)
            {
                c.Weapon = (BeamType)d.PaletteId;
            }
            // the next room loads on the loader's TargetLayerId, else on its area's boss-flag layer (the same
            // layer here, since the test puts every area on one layer); pairing ids repeat across layers
            int targetLayer = c.TargetLayer = d.TargetLayerId != 255 ? d.TargetLayerId : layer;
            if (target?.EntityPath != null)
            {
                foreach (Entity t in Read.GetEntities(target.EntityPath, targetLayer, target.FirstHunt))
                {
                    if (t is Entity<DoorEntityData> a && a.Data.OutConnectorId == d.OutLoaderId)
                    {
                        c.ExpectedArrival = a.EntityId;
                        break;
                    }
                }
            }
            string gate = locked ? (d.PaletteId < 8 ? c.Weapon.ToString() : d.PaletteId == 8 ? "bomb" : "event") : "open";
            c.Label = $"{meta.Id} {meta.InGameName} door#{c.DoorId} [{_layerNames[layer]}] -> {c.TargetRoom} "
                + $"{target?.InGameName} ({gate}{(c.Morph ? ", morph" : "")})";
            return c;
        }

        // beforeWalk runs once the start room has settled; onArrival runs in the target room after a correct arrival
        // and can fail the case (both used by -campaignencounters)
        private static DoorResult RunDoor(DoorCase c, bool verbose, Action<CampaignHost>? beforeWalk = null,
            Func<CampaignHost, (bool Ok, string Summary)>? onArrival = null)
        {
            var result = new DoorResult();
            RoomEntity.TransitionFailure = null;
            using CampaignHost host = CampaignHost.Start(c.Room.Name, setupSave: s => GiveAll(s, c.Layer), hunter: _hunter);
            int settle = IsLandingRoom(c.Room.Id) ? 900 : 300;
            for (int i = 0; i < settle && !host.Ended; i++)
            {
                host.Step(new CampaignInput
                {
                    DialogButton = !host.DialogPaused || i % 10 != 0 ? HostDialogButton.None
                        : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance
                });
            }
            DoorEntity? door = host.FindDoor(c.DoorId);
            if (door == null)
            {
                result.Summary = "door not present after load";
                return result;
            }
            int startRoom = host.RoomId;
            beforeWalk?.Invoke(host);
            // doors the room locks at load (boss rooms until the boss dies, encounter/scripted doors) are event gates
            bool runtimeEventLock = door.Flags.TestFlag(DoorFlags.Locked) && door.Data.PaletteId >= 8;
            if (runtimeEventLock && !c.ExpectBlocked)
            {
                c.ExpectBlocked = true;
                c.Label += " [locked at load]";
            }
            Vector3 facing = door.FacingVector;
            // stand where there is floor in front of the door: the game's own door-arrival spot (2 units out) first,
            // then other distances -- some doors sit on ledges over drops, where a fixed offset is mid-air
            float standAt = -1;
            foreach (float dist in new[] { 2f, 2.5f, 3f, 1.5f, 3.5f, 4f, 5f, 6f })
            {
                host.PlacePlayer(door.Position + facing * dist + door.UpVector * 0.5f, -facing);
                for (int i = 0; i < 20; i++)
                {
                    host.Step(default);
                }
                float drop = Vector3.Dot(door.Position - host.Player.Position, door.UpVector);
                if (host.Player.Health > 0 && drop < 1.0f && drop > -1.5f)
                {
                    standAt = dist;
                    break;
                }
            }
            if (standAt < 0)
            {
                result.Summary = "no floor in front of the door (test placement), skipped";
                result.Outcome = Outcome.Skipped;
                return result;
            }
            var input = new CampaignInput { SelectWeapon = c.Weapon };
            for (int i = 0; i < 30; i++)
            {
                input.Buttons = c.Morph && i < 2 ? CampaignButtons.Morph : CampaignButtons.None;
                host.Step(input);
                input.SelectWeapon = BeamType.None;
            }
            // the morph takes ~50 frames; let it finish before driving the ball
            for (int i = 0; i < 60 && c.Morph && !host.Player.IsAltForm; i++)
            {
                host.Step(default);
            }
            if (verbose)
            {
                Console.WriteLine($"  {c.Label}\n  door at {Fmt(door.Position)} facing {Fmt(facing)}, flags {door.Flags}, "
                    + $"holding {host.Player.CurrentWeapon}{(host.Player.IsAltForm ? " (morph ball)" : "")}, stood {standAt} u out, "
                    + $"player flags2 {host.Player.Flags2}");
            }
            bool sawTransition = false, everOpen = false, lockedOnApproach = false, farDoorLocked = false;
            bool Drive(int frames)
            {
                for (int i = 0; i < frames && !host.Ended; i++)
                {
                    // the door in the way: this one until she is past its plane (facing points into the room), then the
                    // loader door at the far end of the corridor, which starts the room load once opened and reached
                    bool pastDoor = Vector3.Dot(host.Player.Position - door.Position, facing) < -0.5f;
                    DoorEntity target = pastDoor && door.LoaderDoor != null ? door.LoaderDoor : door;
                    bool open = target.Flags.TestFlag(DoorFlags.ShotOpen) || target.Flags.TestFlag(DoorFlags.Open);
                    float near = (host.Player.Position - target.Position).Length;
                    bool fire = !open && i % 30 < 2;
                    bool walk = open || !host.Player.IsAltForm || near > 1.5f; // a ball rolls up to a door (it stops ~0.9 out), then bombs
                    input.Move = new System.Numerics.Vector2(0, walk ? 1 : 0);
                    // biped shoots the door; the ball bombs it once it has rolled up to it
                    input.Buttons = !fire ? CampaignButtons.None
                        : !host.Player.IsAltForm ? CampaignButtons.Shoot
                        : near <= 1.5f ? CampaignButtons.AltAttack : CampaignButtons.None;
                    // prompts and pickups pause the game: page through / confirm, and decline yes/no questions
                    // ("enter your gunship?") so the walk carries on
                    input.DialogButton = !host.DialogPaused || i % 10 != 0 ? HostDialogButton.None
                        : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance;
                    host.Step(input);
                    input.DialogButton = HostDialogButton.None;
                    sawTransition |= GameState.TransitionState != TransitionState.None;
                    everOpen |= door.Flags.TestFlag(DoorFlags.ShotOpen) || door.Flags.TestFlag(DoorFlags.Open);
                    lockedOnApproach |= door.Flags.TestFlag(DoorFlags.Locked) && door.Data.PaletteId >= 8;
                    // LockConnectors (High Ground / Elder Passage) locks the far loader doors, not the one you shoot
                    farDoorLocked |= door.LoaderDoor?.Flags.TestFlag(DoorFlags.Locked) == true;
                    if (verbose && i % 120 == 0)
                    {
                        Console.WriteLine($"    f{i,4}: room {host.RoomId}, transition {GameState.TransitionState}, "
                            + $"pos {Fmt(host.Player.Position)}, door {door.Flags}, far door {door.LoaderDoor?.Flags}, "
                            + $"alt {host.Player.IsAltForm}");
                    }
                    if (host.RoomId != startRoom && GameState.TransitionState == TransitionState.None)
                    {
                        return true;
                    }
                    if (RoomEntity.TransitionFailure != null)
                    {
                        return false; // the background room load threw: the game would wait at this door forever
                    }
                }
                return false;
            }
            bool arrived = Drive(c.ExpectBlocked ? 600 : 1500);
            bool assisted = false;
            if (!arrived && !c.ExpectBlocked && everOpen && !lockedOnApproach && !farDoorLocked && door.LoaderDoor != null
                && host.RoomId == startRoom && GameState.TransitionState == TransitionState.None)
            {
                // the gate opened but the straight-line driver got stuck on the way (turns, steps, jumps): stand in the
                // corridor in front of the far door and let the game do the rest -- swap and arrival are still real
                DoorEntity loader = door.LoaderDoor;
                Vector3 toDoor = (door.Position - loader.Position).Normalized();
                host.PlacePlayer(loader.Position + toDoor * 2 + loader.UpVector * 0.5f, -toDoor);
                assisted = arrived = Drive(900);
            }
            for (int i = 0; i < 30 && !host.Ended && RoomEntity.TransitionFailure == null; i++)
            {
                host.Step(default);
            }
            if (RoomEntity.TransitionFailure is Exception loadFail)
            {
                result.Summary = $"room load failed: {loadFail.GetType().Name}: {loadFail.Message} @ "
                    + loadFail.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
                return result;
            }
            if (c.ExpectBlocked || !arrived && (lockedOnApproach || farDoorLocked))
            {
                bool held = !sawTransition && host.RoomId == startRoom;
                result.Outcome = held ? Outcome.Held : Outcome.Fail;
                string why = c.ExpectBlocked ? "locked until its event" : lockedOnApproach ? "an event locked it on approach"
                    : "far door locked by LockConnectors";
                result.Summary = held ? $"held ({why}), as expected" : $"OPENED without its event -> room {host.RoomId}";
                return result;
            }
            if (host.RoomId != c.TargetRoom)
            {
                result.Summary = $"no arrival (room {host.RoomId}, transition seen: {sawTransition}, gate opened: {everOpen}, door {door.Flags}, "
                    + $"pos {Fmt(host.Player.Position)}, health {host.Player.Health})";
                return result;
            }
            DoorEntity? nearest = null;
            float best = Single.MaxValue;
            foreach (DoorEntity d in host.Scene.GetDoorEntities())
            {
                float dist = (d.Position - host.Player.Position).Length;
                if (d.Id >= 0 && dist < best)
                {
                    best = dist;
                    nearest = d;
                }
            }
            bool visited = GameState.StorySave.CheckVisitedRoom(c.TargetRoom);
            bool right = nearest?.Id == c.ExpectedArrival && best < 25 && visited;
            result.Outcome = !right ? Outcome.Fail : assisted ? Outcome.Assisted : Outcome.Arrived;
            result.Summary = $"arrived at door#{nearest?.Id} ({best:0.0} u; expected #{c.ExpectedArrival}), visited {visited}"
                + (assisted ? " [assisted: placed in the corridor after the gate opened]" : "");
            if (right && onArrival != null)
            {
                (bool ok, string summary) = onArrival(host);
                result.Summary += "; " + summary;
                if (!ok)
                {
                    result.Outcome = Outcome.Fail;
                }
            }
            return result;
        }

        private static string Fmt(Vector3 v) => $"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})";
    }
}
