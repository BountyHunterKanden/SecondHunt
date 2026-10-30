using System;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Text;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -campaignscan <room> <entityId> [layer]: can Samus scan this entity? Stands her in clear view of it (the game drops
    // a scan target the moment anything blocks camera -> target), turns the scan visor on, holds scan while aiming at it,
    // and reports what the scan code sees: the entity's scan id and scan visibility, the target list, the current
    // target, the scan timer, then whether the scan dialog opened and the logbook took the entry.
    internal static class CampaignScanTest
    {
        public static void Run(string[] args)
        {
            if (ToolPaths.UseDesktopExtraction() && args.Length >= 2 && args[1] == "names")
            {
                // -campaignscan names <scanId>...: logbook entries (e.g. the bits set in a pulled save)
                foreach (string s in args[2..])
                {
                    int sid = Int32.Parse(s);
                    StringTableEntry? e = Strings.GetScanEntry(sid);
                    Console.WriteLine($"  {sid,4}: [{e?.Category} cat {Strings.GetScanEntryCategory(sid)}] {e?.Value1}");
                }
                return;
            }
            if (ToolPaths.UseDesktopExtraction() && args.Length >= 2 && args[1] == "logbook")
            {
                // -campaignscan logbook: every ScanLog entry that counts toward the scan percentage (L/B/O)
                foreach (StringTableEntry e in Strings.ReadStringTable(StringTables.ScanLog))
                {
                    if (e.Category == 'L' || e.Category == 'B' || e.Category == 'O')
                    {
                        Console.WriteLine($"LOG {e.Id} {e.Category} cat {Strings.GetScanEntryCategory(Int32.Parse(e.Id[1..]))} {e.Value1}");
                    }
                }
                return;
            }
            if (ToolPaths.UseDesktopExtraction() && args.Length >= 2 && args[1] == "aitree")
            {
                // -campaignscan aitree: each campaign hunter's AI behaviour tree (aiPersonalityData) per encounter type,
                // with the behaviour ids whose code presses the morph button marked (ids from PlayerAi.cs setup/Funcs2)
                var bf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                var offsets = (System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyList<int>>)
                    typeof(MphRead.Formats.AiPersonality).GetField("_encounterAiOffsets", bf)!.GetValue(null)!;
                System.Reflection.MethodInfo load = typeof(MphRead.Formats.AiPersonality).GetMethod("LoadData", bf)!;
                var morphIds = new System.Collections.Generic.HashSet<int> { 1, 45, 47, 49, 3, 12, 15, 31, 32, 35, 36, 38, 40, 41, 44, 48, 85, 120 };
                string[] enc = { "enc0", "enc1", "enc3", "enc4" };
                for (int e = 0; e < offsets.Count; e++)
                {
                    for (int h = 1; h <= 6; h++)
                    {
                        var root = (MphRead.Formats.AiPersonalityData1)load.Invoke(null, new object[] { offsets[e][h] })!;
                        var ids = new System.Collections.Generic.SortedSet<int>();
                        var seenNodes = new System.Collections.Generic.HashSet<MphRead.Formats.AiPersonalityData1>();
                        var stack = new System.Collections.Generic.Stack<MphRead.Formats.AiPersonalityData1>();
                        stack.Push(root);
                        while (stack.Count > 0)
                        {
                            MphRead.Formats.AiPersonalityData1 n = stack.Pop();
                            if (!seenNodes.Add(n)) continue;
                            ids.Add(n.Func24Id);
                            foreach (MphRead.Formats.AiPersonalityData1 c in n.Data1) stack.Push(c);
                        }
                        Console.WriteLine($"AITREE {enc[e]} {(Hunter)h} @{offsets[e][h]}: morph [{String.Join(",", ids.Where(morphIds.Contains))}] all [{String.Join(",", ids)}]");
                    }
                }
                return;
            }
            if (!ToolPaths.UseDesktopExtraction() || args.Length < 3)
            {
                Console.WriteLine("usage: -campaignscan <room> <entityId> [layer] [logged] | <room> list | names <scanId>...");
                return;
            }
            string room = args[1];
            bool list = args[2] == "list";
            int entityId = list || args[2] == "all" || args[2] == "fields" || args[2] == "spawns" || args[2] == "hunt" || args[2] == "lock" ? -1 : Int32.Parse(args[2]);
            int layer = args[2] == "hunt" || args[2] == "lock" ? (args.Length >= 5 ? Int32.Parse(args[4]) : 0) : args.Length >= 4 && Int32.TryParse(args[3], out int l) ? l : 0;
            bool logged = Array.IndexOf(args, "logged") > 0; // the entry is already in the logbook (e.g. a key picked up earlier)
            using CampaignHost host = CampaignHost.Start(room, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080, setupSave: s =>
            {
                s.BossFlags = (BossFlags)(layer * 0x15555);
                s.Weapons = 0xFF;
                s.Health = s.HealthMax = 799;
            });
            PlayerEntity p = host.Player;
            for (int i = 0; i < 1000 && !host.Ended; i++)
            {
                host.Step(default); // landing / intro camera sequences hold input
            }
            if (list)
            {
                // everything with a scan id, plus item spawns (an item's scan id lives on the instance it spawns)
                foreach (EntityBase e in host.Scene.Entities)
                {
                    e.GetPosition(out Vector3 ep);
                    string extra = e is ItemSpawnEntity sp ? $" spawns {sp.Data.ItemType} enabled {sp.Data.Enabled} active {sp.Active} item #{sp.Item?.Id}"
                        : e is ItemInstanceEntity ii ? $" {ii.ItemType} parent {ii.ParentId}" : "";
                    if (e.GetScanId() > 0 || e is ItemSpawnEntity || e is ItemInstanceEntity)
                        Console.WriteLine($"  {e.Type}#{e.Id} at {Fmt(ep)} scan {e.GetScanId()} ({Strings.GetScanEntry(e.GetScanId())?.Value1}) visible {e.ScanVisible()}{extra}");
                }
                return;
            }
            if (args[2] == "fields")
            {
                // every entity's room-data fields (numbers and enums), for tracing who sends a message to whom
                var fflags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
                foreach (EntityBase e in host.Scene.Entities)
                {
                    object? data = null;
                    for (Type? t = e.GetType(); t != null && data == null; t = t.BaseType)
                        data = t.GetField("_data", fflags)?.GetValue(e);
                    if (e is CamSeqEntity cse)
                    {
                        data = cse.Data;
                        var ks = new System.Text.StringBuilder();
                        foreach (CameraSequenceKeyframe k in cse.Sequence.Keyframes)
                        {
                            if (k.MessageId != 0) ks.Append($" key:{(Message)k.MessageId}->{k.MessageTarget?.Id}");
                        }
                        Console.WriteLine($"  FLD {room} L{layer} CamSeqKeys#{e.Id} {cse.Name}{ks}");
                    }
                    if (data == null) continue;
                    var sb = new System.Text.StringBuilder();
                    foreach (var fi in data.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        Type ft = fi.FieldType;
                        if (ft.IsPrimitive || ft.IsEnum) sb.Append($" {fi.Name}={fi.GetValue(data)}");
                    }
                    Console.WriteLine($"  FLD {room} L{layer} {e.Type}#{e.Id}{sb}");
                }
                return;
            }
            if (args[2] == "lock")
            {
                // -campaignscan <room> lock <id> [layer]: the entity's scan id before and after it gets a Lock message
                host.Scene.TryGetEntity(Int32.Parse(args[3]), out EntityBase? le);
                Console.WriteLine($"  LOCK {room} L{layer} {le!.Type}#{le.Id} before: scan {le.GetScanId()} active {le.Active} vis {le.ScanVisible()}");
                host.Scene.SendMessage(Message.Lock, p, le, 0, 0);
                for (int i = 0; i < 10; i++) host.Step(default);
                Console.WriteLine($"  LOCK {room} L{layer} {le.Type}#{le.Id} after:  scan {le.GetScanId()} active {le.Active} vis {le.ScanVisible()}");
                return;
            }
            if (args[2] == "hunt")
            {
                // -campaignscan <room> hunt <spawnerId> [layer]: switch a hunter spawner on, stand Samus (kept alive) near
                // it and count the frames each enemy hunter spends in alt form (its alt-form logbook entry needs that)
                int spId = Int32.Parse(args[3]);
                int frames = args.Length >= 6 ? Int32.Parse(args[5]) : 60 * 300;
                host.Scene.TryGetEntity(spId, out EntityBase? spe);
                host.Scene.SendMessage(Message.Activate, p, spe, 0, 0);
                spe!.GetPosition(out Vector3 spos);
                var alt = new int[PlayerEntity.MaxPlayers];
                var seen = new int[PlayerEntity.MaxPlayers];
                // AI trace (reflection): how many frames each hunter spent with each behaviour id (Func24Id) in its tree
                var funcs = new System.Collections.Generic.SortedDictionary<int, int>[PlayerEntity.MaxPlayers];
                for (int k = 0; k < funcs.Length; k++) funcs[k] = new System.Collections.Generic.SortedDictionary<int, int>();
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                System.Reflection.FieldInfo? treeField = typeof(PlayerEntity.PlayerAiData).GetField("_executionTree", bf);
                System.Reflection.MethodInfo? switchForms = typeof(PlayerEntity).GetMethod("TrySwitchForms", bf);
                string samusMode = Environment.GetEnvironmentVariable("HUNTSAMUS") ?? "";
                for (int i = 0; i < frames && !host.Ended; i++)
                {
                    if (i % 600 == 0)
                    {
                        CollisionResult fl = default;
                        if (CollisionDetection.CheckBetweenPoints(spos + new Vector3(0, 1, 0), spos - new Vector3(0, 10, 0), TestFlags.None, host.Scene, ref fl))
                            host.PlacePlayer(fl.Position + new Vector3(0, 0.05f, 0), Vector3.UnitZ);
                    }
                    p.Health = 799;
                    if (samusMode == "morph" && i % 900 == 450)
                        switchForms?.Invoke(p, new object[] { true }); // Samus rolls up / stands up every 15 s
                    host.Step(default);
                    bool hit = Environment.GetEnvironmentVariable("HUNTHIT") == "1" && i % 240 == 120;
                    for (int k = 1; k < PlayerEntity.MaxPlayers; k++)
                    {
                        PlayerEntity o = PlayerEntity.Players[k];
                        if (hit && o.LoadFlags.TestFlag(LoadFlags.Active) && o.Health > 10)
                            o.TakeDamage(Math.Min(8, o.Health - 10), DamageFlags.None, null, p); // a Samus hit, kept short of a kill
                        if (o.LoadFlags.TestFlag(LoadFlags.Active) && o.Health > 0)
                        {
                            seen[k]++;
                            if (o.IsAltForm) alt[k]++;
                            if (treeField?.GetValue(o.AiData) is Array tree)
                            {
                                foreach (object? c in tree)
                                {
                                    int fid = c == null ? 0 : (int)c.GetType().GetProperty("Func24Id")!.GetValue(c)!;
                                    if (fid != 0) funcs[k][fid] = (funcs[k].TryGetValue(fid, out int fn) ? fn : 0) + 1;
                                }
                            }
                        }
                    }
                }
                for (int k = 1; k < PlayerEntity.MaxPlayers; k++)
                {
                    if (seen[k] > 0)
                        Console.WriteLine($"  HUNT {room} L{layer} spawner #{spId}: {PlayerEntity.Players[k].Hunter} alive {seen[k]} frames, alt form {alt[k]} frames, scan now {PlayerEntity.Players[k].GetScanId()}");
                    if (seen[k] > 0)
                        Console.WriteLine($"  HUNTAI {PlayerEntity.Players[k].Hunter}: " + String.Join(" ", funcs[k].Select(f => $"{f.Key}x{f.Value}")));
                }
                Console.WriteLine($"  HUNT done (ended {host.Ended})");
                return;
            }
            if (args[2] == "spawns")
            {
                // every enemy spawner: hunters print their fixed id / chance; others spawn one enemy and print its scan id
                var spawners = new System.Collections.Generic.List<EnemySpawnEntity>();
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e is EnemySpawnEntity es) spawners.Add(es);
                }
                foreach (EnemySpawnEntity sp in spawners)
                {
                    EntityBase e = sp;
                    EnemySpawnEntityData d = sp.Data;
                    if (d.EnemyType == EnemyType.Hunter)
                    {
                        Console.WriteLine($"  SPN {room} L{layer} #{e.Id} Hunter id {d.Fields.S09.HunterId} enc {d.Fields.S09.EncounterType} chance {d.Fields.S09.HunterChance} active {d.Active != 0}");
                        continue;
                    }
                    string res;
                    try
                    {
                        EnemyInstanceEntity? en = EnemySpawnEntity.SpawnEnemy(sp, d.EnemyType, sp.NodeRef, host.Scene);
                        if (en == null) res = "null";
                        else
                        {
                            host.Scene.AddEntity(en);
                            res = $"scan {en.GetScanId()}";
                        }
                    }
                    catch (Exception ex)
                    {
                        res = $"error {ex.GetType().Name}";
                    }
                    Console.WriteLine($"  SPN {room} L{layer} #{e.Id} {d.EnemyType} {res} active {d.Active != 0}");
                }
                return;
            }
            if (args[2] == "all")
            {
                // every entity: runtime scan id next to the scan ids its room data carries (objects: ScanId; platforms:
                // ScanData1 awake / ScanData2 asleep), for spotting things whose data says scannable but the game says 0
                var dflags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
                foreach (EntityBase e in host.Scene.Entities)
                {
                    object? data = null;
                    for (Type? t = e.GetType(); t != null && data == null; t = t.BaseType)
                        data = t.GetField("_data", dflags)?.GetValue(e);
                    string ds = "";
                    if (data != null)
                    {
                        foreach (string fn in new[] { "ScanId", "ScanData1", "ScanData2", "ModelId", "ScanMessage", "ScanMsgTarget" })
                        {
                            object? v = data.GetType().GetField(fn)?.GetValue(data);
                            if (v != null) ds += $" {fn}={v}";
                        }
                    }
                    object? st = e.GetType().GetField("_state", dflags)?.GetValue(e);
                    string extra = e is EnemyInstanceEntity ei ? $" enemy {ei.EnemyType}" : e is ItemInstanceEntity it ? $" item {it.ItemType}" : "";
                    e.GetPosition(out Vector3 ep);
                    Console.WriteLine($"  ENT {room} L{layer} {e.Type}#{e.Id} scan {e.GetScanId()} active {e.Active} vis {e.ScanVisible()}{ds}{(st != null ? $" state={st}" : "")}{extra} at {Fmt(ep)}");
                }
                return;
            }
            if (!host.Scene.TryGetEntity(entityId, out EntityBase? target))
            {
                Console.WriteLine($"  no entity #{entityId} in {room}");
                return;
            }
            if (target is ItemSpawnEntity idle && idle.Item == null)
            {
                // a spawner the room enables later (artifact keys appear after a fight or a switch): enable it as that event would
                host.Scene.SendMessage(Message.Activate, idle, idle, 0, 0);
                for (int i = 0; i < 30 && idle.Item == null; i++) host.Step(default);
                Console.WriteLine($"  {room}: activated item spawn #{entityId}: item {(idle.Item == null ? "none" : idle.Item.ItemType.ToString())}");
            }
            if (target is ItemSpawnEntity spawn && spawn.Item != null)
            {
                Console.WriteLine($"  {room}: item spawn #{entityId} -> its {spawn.Item.ItemType} instance #{spawn.Item.Id}");
                target = spawn.Item;
            }
            if (logged && target.GetScanId() > 0) GameState.StorySave.UpdateLogbook(target.GetScanId());
            target.GetPosition(out Vector3 at);
            Console.WriteLine($"  {room}: {target.Type}#{entityId} at {Fmt(at)}, scan id {target.GetScanId()}, scan-visible {target.ScanVisible()}");
            bool clear = false;
            void CloseDialogs()
            {
                for (int k = 0; k < 600 && host.DialogPaused; k++)
                    host.Step(new CampaignInput { DialogButton = k % 10 != 0 ? HostDialogButton.None
                        : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance }); // a prompt the spot set off (ship, save): decline it
            }
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            object? Field(string name) => typeof(PlayerEntity).GetField(name, flags)?.GetValue(p);
            static object? Member(object? o, string name) => o == null ? null
                : (object?)o.GetType().GetField(name)?.GetValue(o) ?? o.GetType().GetProperty(name)?.GetValue(o);
            void VisorOn()
            {
                // the visor is biped only: a spot in a ball tunnel (morph ball doors) leaves her in the ball
                for (int t = 0; t < 3 && (p.IsAltForm || p.IsMorphing); t++)
                {
                    for (int i = 0; i < 40; i++)
                    {
                        host.Step(new CampaignInput { Buttons = i < 2 ? CampaignButtons.Morph : CampaignButtons.None });
                        if (Environment.GetEnvironmentVariable("SCANDBG") == "3" && i < 6) Console.WriteLine($"      unmorph {t}.{i}: alt {p.IsAltForm} morphing {p.IsMorphing} unmorphing {p.IsUnmorphing} f1 {p.Flags1} f2 {p.Flags2} camseq {CameraSequence.Current?.Name} fb {CameraSequence.Current?.ForceBiped} fa {CameraSequence.Current?.ForceAlt} mcam {p.MorphCamera?.Id}");
                    }
                }
                for (int t = 0; t < 2 && !p.ScanVisor; t++)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        host.Step(new CampaignInput { Buttons = i < 2 ? CampaignButtons.ScanVisor : CampaignButtons.None });
                        if (Environment.GetEnvironmentVariable("SCANDBG") == "2") Console.WriteLine($"      visor step {i}: {p.ScanVisor} dlg {host.DialogPaused} alt {p.IsAltForm} sp {GameState.SinglePlayer}");
                    }
                    CloseDialogs();
                }
            }
            // spots around and below the target (items often float over a ledge): keep the first where the scan visor
            // picks this entity as its current target within scan start range (< 12, PlayerScan) with a clear
            // camera -> target line
            if (Environment.GetEnvironmentVariable("SCANDBG") != null) Console.WriteLine($"    start: pos {Fmt(p.Position)} alt {p.IsAltForm} mcam {p.MorphCamera?.Id}");
            string BallZones()
            {
                var zs = new System.Collections.Generic.List<string>();
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e is AreaVolumeEntity av && av.Data.InsideMessage == Message.PreventFormSwitch
                        && typeof(AreaVolumeEntity).GetField("_volume", flags)?.GetValue(av) is CollisionVolume vol)
                    {
                        zs.Add($"#{av.Id}{(av.Active ? "" : "(off)")}{(vol.TestPoint(p.Position) ? "*" : "")}{(vol.TestPoint(at) ? "@door" : "")}");
                    }
                }
                return String.Join(" ", zs);
            }
            if (Environment.GetEnvironmentVariable("SCANDBG") != null) Console.WriteLine($"    ball-only zones (* = her spot, @door = target inside): {BallZones()}");
            VisorOn();
            foreach (float rise in new[] { 0f, -2f, -4f, -6f, 2f })
            {
                foreach (float dist in new[] { 3f, 5f, 8f, 10f })
                {
                    for (int a = 0; a < 12 && !clear; a++)
                    {
                        float ang = a * MathF.PI / 6;
                        var dir = new Vector3(MathF.Cos(ang), 0, MathF.Sin(ang));
                        // stand her on a floor under the spot (a spot over a pit drops her out of the room, and the
                        // death's game-over prompt ends the run)
                        Vector3 spot = at + dir * dist + new Vector3(0, rise + 2, 0);
                        CollisionResult floor = default;
                        if (!CollisionDetection.CheckBetweenPoints(spot, spot - new Vector3(0, 10, 0), TestFlags.None, host.Scene, ref floor)
                            || floor.Plane.Y < 0.7f)
                        {
                            continue;
                        }
                        host.PlacePlayer(floor.Position + new Vector3(0, 0.05f, 0), -dir);
                        for (int i = 0; i < 30; i++) host.Step(default);
                        CloseDialogs();
                        if (host.Ended || p.Health <= 0)
                        {
                            Console.WriteLine($"  harness: run ended at spot {Fmt(p.Position)} (health {p.Health}, ended {host.Ended})");
                            Console.WriteLine("  RESULT: FAIL: harness run ended");
                            return;
                        }
                        Vector3 settled = p.Position;
                        for (int i = 0; i < 40; i++) host.Step(new CampaignInput { AimDelta = AimAt(p, at) });
                        CollisionResult discard = default;
                        // the target must also be drawn from there (room-node culling: a spot below the floor or past a
                        // wall puts the camera in another node) and no prompt may be up
                        VisorOn();
                        // standing, not falling (the scan takes a second, a spot in mid-air carries her out of range)
                        clear = p.Health > 0 && (p.Position - settled).Length < 0.25f && (p.CameraInfo.Position - at).Length < 12
                            && target.ScanVisible() && !host.DialogPaused
                            && Member(Field("_curScanTarget"), "Entity") == target
                            && !CollisionDetection.CheckBetweenPoints(p.CameraInfo.Position, at, TestFlags.Scan, host.Scene, ref discard);
                        if (Environment.GetEnvironmentVariable("SCANDBG") != null)
                        {
                            Console.WriteLine($"    try rise {rise} dist {dist} ang {a}: pos {Fmt(p.Position)} moved {(p.Position - settled).Length:0.00} "
                                + $"camdist {(p.CameraInfo.Position - at).Length:0.0} vis {target.ScanVisible()} dlg {host.DialogPaused} ended {host.Ended} alt {p.IsAltForm} nounm {p.Flags1.HasFlag(PlayerFlags1.NoUnmorph)} mcam {p.MorphCamera?.Id} visor {p.ScanVisor} sfx {MphRead.Sound.Sfx.Instance != null} "
                                + $"cur {(Member(Field("_curScanTarget"), "Entity") as EntityBase)?.Type}#{(Member(Field("_curScanTarget"), "Entity") as EntityBase)?.Id}");
                        }
                    }
                    if (clear) break;
                }
                if (clear) break;
            }
            CloseDialogs();
            Console.WriteLine($"  stood at {Fmt(p.Position)}, camera {Fmt(p.CameraInfo.Position)}; clear line of sight {clear}");
            VisorOn();
            Console.WriteLine($"  scan visor {p.ScanVisor}");
            int f;
            Vector3 hold = p.Position;
            for (f = 0; f < 600 && !host.DialogPaused; f++)
            {
                host.Step(new CampaignInput { Buttons = CampaignButtons.Scan, AimDelta = AimAt(p, at) });
                if (clear && (p.Position - hold).Length > 0.3f && p.Health > 0 && !host.Ended)
                {
                    // an enemy's shots knock her off the spot mid-scan: put her back (the test is the target, not the fight)
                    Vector3 face = (at - hold).WithY(0);
                    host.PlacePlayer(hold, face.LengthSquared > 0.001f ? Vector3.Normalize(face) : Vector3.UnitZ);
                }
                if (Environment.GetEnvironmentVariable("SCANDBG") != null && f < 80 && f % 8 == 0) Console.WriteLine($"      f{f} pos {Fmt(p.Position)} hp {p.Health} alt {p.IsAltForm} spd {p.Speed}");
                if (f % 100 == 0)
                {
                    object? cur = Field("_curScanTarget");
                    Console.WriteLine($"    f{f}: scan id {target.GetScanId()} visible {target.ScanVisible()} active {target.Active}; targets {Field("_scanTargetCount")}, "
                        + $"current {Member(cur, "Entity")} dist {Member(cur, "Distance")}, scanning {Field("_scanning")} timer {Field("_scanningTimer")}/{Field("_scanningTime")}");
                }
            }
            // the frames around the dialog: does it open, and what closes it?
            for (int k = 0; k < 8; k++)
            {
                Console.WriteLine($"    +{k}: paused {host.DialogPaused} dialog {host.Dialog} confirm {Field("_showDialogConfirm")} complete {Field("_scanComplete")} "
                    + $"scanning {Field("_scanning")} entity {(Field("_scanningEntity") as EntityBase)?.Type}");
                if (k < 7) host.Step(new CampaignInput { Buttons = CampaignButtons.Scan, AimDelta = AimAt(p, at) });
            }
            var scanned = (EntityBase?)Field("_scanningEntity");
            int scannedId = scanned?.GetScanId() ?? -1;
            Console.WriteLine($"  scan dialog {(host.DialogPaused && host.Dialog == DialogType.Scan ? "OPENED" : "did NOT open")} (frame {f}, paused {host.DialogPaused}, dialog {host.Dialog}); scanned {scanned?.Type}#{scanned?.Id} (scan id {scannedId})");
            for (f = 0; f < 600 && host.DialogPaused; f++) host.Step(new CampaignInput { DialogButton = f % 10 == 0 ? HostDialogButton.Advance : HostDialogButton.None });
            int id = target.GetScanId() > 0 ? target.GetScanId() : scannedId;
            Console.WriteLine($"  RESULT: {(id > 0 && GameState.StorySave.CheckLogbook(id) ? "PASS" : "FAIL")}: logbook has scan id {id}: {(id > 0 && GameState.StorySave.CheckLogbook(id))}");
        }

        static string Fmt(Vector3 v) => $"({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})";

        // aim delta (game mouse units) turning the camera toward a point (as CampaignSim.AimAt)
        static System.Numerics.Vector2 AimAt(PlayerEntity p, Vector3 target)
        {
            Vector3 dir = (target - p.CameraInfo.Position).Normalized();
            Vector3 facing = p.CameraInfo.Facing.Normalized();
            Vector3 right = Vector3.Cross(facing, Vector3.UnitY).Normalized();
            var flat = new Vector3(facing.X, 0, facing.Z).Normalized();
            float yaw = MathF.Atan2(Vector3.Dot(dir, right), Vector3.Dot(new Vector3(dir.X, 0, dir.Z), flat)) * 180 / MathF.PI;
            float pitch = (MathF.Asin(Math.Clamp(dir.Y, -1, 1)) - MathF.Asin(Math.Clamp(facing.Y, -1, 1))) * 180 / MathF.PI;
            return new System.Numerics.Vector2(Math.Clamp(yaw * 2, -40, 40), Math.Clamp(-pitch * 2, -40, 40));
        }
    }
}
