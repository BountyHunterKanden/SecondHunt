using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -kandenhit [room] [hunter=X]: a story hunter fight's damage path. Starts the room on its first-visit layer, releases
    // its first hunter spawner, stands the player next to the hunter and logs what TakeDamage sees (health, the spawner's
    // threshold, the AI's no-damage flag, spawn invulnerability) around a few 40-damage hits from the player.
    internal static partial class CampaignSim
    {
        public static void KandenHit(string[] args)
        {
            args = TakeHunterArg(args);
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            int roomId = args.Skip(1).Where(a => a.Length > 0 && Char.IsDigit(a[0])).Select(Int32.Parse).FirstOrDefault(50);
            RoomMetadata meta = Metadata.GetRoomById(roomId)!;
            FieldInfo invuln = typeof(PlayerEntity).GetField("_spawnInvulnTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            FieldInfo dmgInvuln = typeof(PlayerEntity).GetField("_damageInvulnTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            RoomEntity.TransitionFailure = null;
            Cheats.NoRandomEncounters = true;
            using CampaignHost host = CampaignHost.Start(meta.Name, hunter: _hunter, arriving: false, collectDrawItems: args.Contains("hud"), setupSave: s =>
            {
                GiveAll(s, 0);
                s.BossFlags = FlagsForPlanet(meta.Id, 0);
                s.DefeatedHunters = 0;
                if (args.Contains("used"))
                {
                    s.SetRoomState(roomId, 16, state: 1);
                }
            });
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            void Run(int frames)
            {
                for (int i = 0; i < frames && !host.Ended; i++)
                {
                    host.Step(input);
                    if (host.Player.Health is > 0 and < 400)
                    {
                        host.Player.Health = 799;
                    }
                }
            }
            PlayerEntity? bot = Enumerable.Range(1, Math.Max(0, PlayerEntity.PlayerCount - 1)).Select(i => PlayerEntity.Players[i])
                .FirstOrDefault(p => p.IsBot && p.EnemySpawner != null);
            Console.WriteLine($"{meta.InGameName} as {host.Player.Hunter}: bot {bot?.Hunter}");
            if (args.Contains("sfx")) { foreach (Hunter h in new[] { Hunter.Kanden, Hunter.Trace }) Console.WriteLine($"sfx {h}: " + String.Join(" ", Enumerable.Range(0, 17).Select(i => $"{(HunterSfx)i}={Metadata.HunterSfx[(int)h, i]}"))); return; }
            if (bot == null)
            {
                return;
            }
            foreach (EntityBase e in host.Scene.Entities)
            {
                if (e is CamSeqEntity cs)
                {
                    Console.WriteLine($"camseq #{cs.Id} {cs.Name}: end msg {cs.Data.EndMessage} -> {cs.Data.EndMessageTargetId}");
                    foreach (CameraSequenceKeyframe k in cs.Sequence.Keyframes.Where(k => k.MessageId != 0))
                    {
                        Console.WriteLine($"   key msg {(Message)k.MessageId} param {k.MessageParam} -> type {k.MessageTargetType} id {k.MessageTargetId} = {k.MessageTarget?.Type} {(k.MessageTarget as PlayerEntity)?.Hunter}");
                    }
                }
            }
            foreach (EntityBase e in host.Scene.Entities)
            {
                object? data = e.GetType().GetProperty("Data")?.GetValue(e);
                if (data == null)
                {
                    continue;
                }
                var refs = new System.Collections.Generic.List<string>();
                void Walk(object o, string pre, int depth)
                {
                    foreach (FieldInfo fi in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        object? v = fi.GetValue(o);
                        if (v == null) continue;
                        if (fi.FieldType.IsValueType && !fi.FieldType.IsPrimitive && !fi.FieldType.IsEnum && depth < 3 && fi.FieldType.Namespace == "MphRead")
                        {
                            Walk(v, pre + fi.Name + ".", depth + 1);
                        }
                        else if ((fi.Name.Contains("Id") || fi.Name.Contains("Message")) && v.ToString() != "0" && v.ToString() != "None")
                        {
                            refs.Add($"{pre}{fi.Name}={v}");
                        }
                    }
                }
                Walk(data, "", 0);
                if (refs.Any(r => r.EndsWith("=26") || r.EndsWith("=3") || r.EndsWith("=25") || r.EndsWith("=42") || r.EndsWith("=21") || r.EndsWith("=22"))  || e is CamSeqEntity || e.Id == 25 || e.Id == 3)
                {
                    Console.WriteLine($"ent #{e.Id} {e.Type}: {String.Join(" ", refs)}");
                }
            }
            foreach (EntityBase e in host.Scene.Entities)
            {
                if (e is DoorEntity || e is PlayerSpawnEntity)
                {
                    Console.WriteLine($"layout {e.Type} #{e.Id} at {e.Position} facing {e.FacingVector}");
                }
            }
            foreach (EntityBase e in host.Scene.Entities)
            {
                if (e is TriggerVolumeEntity tv && (tv.Id == 25))
                {
                    TriggerVolumeEntityData d = tv.Data;
                    Console.WriteLine($"trigger #{tv.Id}: subtype {d.Subtype} flags {d.TriggerFlags} active {tv.Active} (data {d.Active}) deact {d.DeactivateAfterUse} at {tv.Position} "
                        + $"parent {d.ParentId} {d.ParentMessage} child {d.ChildId} {d.ChildMessage} state {GameState.StorySave.GetRoomState(host.Scene.RoomId, tv.Id)}");
                }
            }
            Console.WriteLine($"PlayerCount {PlayerEntity.PlayerCount}; slots " + String.Join(",", Enumerable.Range(0, PlayerEntity.PlayerCount).Select(i => $"{i}:{PlayerEntity.Players[i].Hunter}{(PlayerEntity.Players[i].IsBot ? "(bot)" : "")}")));
            EnemySpawnFields09 f = bot.EnemySpawner!.Data.Fields.S09;
            Console.WriteLine($"spawner #{bot.EnemySpawner.Id}: encounter {f.EncounterType}, health {f.HunterHealth}/{f.HunterHealthMax}, threshold {f.HunterHealthThreshold}, weapon {f.HunterWeapon}");
            string State() => $"hp {bot.Health} at {bot.Position.X:0.0},{bot.Position.Y:0.0},{bot.Position.Z:0.0}; flags1 {bot.AiData.Flags1}, "
                + $"threshold {bot.AiData.HealthThreshold}, flags2 {bot.AiData.Flags2}, spawn invuln {invuln.GetValue(bot)}, dmg invuln {dmgInvuln.GetValue(bot)}; "
                + $"player {host.Player.Position.X:0.0},{host.Player.Position.Y:0.0},{host.Player.Position.Z:0.0} hp {host.Player.Health}";
            EnemySpawnEntity sp = bot.EnemySpawner;
            Console.WriteLine($"spawner flags {sp.Flags} activeDist {sp.Data.ActiveDistance.FloatValue} pos {sp.Position} state {GameState.StorySave.GetRoomState(host.Scene.RoomId, sp.Id)}");
            if (args.Contains("walk"))
            {
                Vector3 from = host.Player.Position;
                Vector3 to = sp.Position + sp.FacingVector * 6;
                for (int t = 0; t <= 40; t++)
                {
                    Vector3 at = Vector3.Lerp(from, to, t / 40f);
                    host.PlacePlayer(at + Vector3.UnitY * 0.5f, (to - from).Normalized());
                    Run(10);
                    Console.WriteLine($"  walk {t}: player {host.Player.Position}, camseq {CameraSequence.Current?.Name}, bot hp {bot.Health} flags2 {bot.AiData.Flags2} spawner {sp.Flags}");
                }
            }
            if (args.Contains("trig"))
            {
                Run(30);
                string? blockArg = args.FirstOrDefault(a => a.StartsWith("block="));
                if (blockArg != null && host.Scene.TryGetEntity(Int32.Parse(blockArg[6..]), out EntityBase? blocker))
                {
                    host.Scene.SendMessage(Message.Activate, blocker, blocker, 0, 0);
                    Run(5);
                    Console.WriteLine($"blocker #{blocker.Id}: current {CameraSequence.Current?.Name} flags {CameraSequence.Current?.Flags}");
                }
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e is TriggerVolumeEntity tv && tv.Id == 25)
                    {
                        CollisionVolume v = tv.Volume;
                        Vector3 c = v.Type == VolumeType.Box ? v.BoxPosition + (v.BoxVector1 * v.BoxDot1 + v.BoxVector2 * v.BoxDot2 + v.BoxVector3 * v.BoxDot3) / 2
                            : v.Type == VolumeType.Cylinder ? v.CylinderPosition + v.CylinderVector * v.CylinderDot / 2 : v.SpherePosition;
                        Console.WriteLine($"trigger 25 vol {v.Type} center {c} testpoint {v.TestPoint(c)}");
                        host.PlacePlayer(c, -Vector3.UnitZ);
                    }
                }
                for (int t = 0; t < (args.Contains("long") ? 90 : 30); t++)
                {
                    Run(10);
                    if (args.Contains("cut") && t == 5)
                    {
                        Console.WriteLine($"  cut camseq {CameraSequence.Current?.Name} kf {(CameraSequence.Current == null ? -1 : typeof(CameraSequence).GetField("_keyframeIndex", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(CameraSequence.Current))}");
                        CamSeqEntity.CancelCurrent();
                    }
                    Console.WriteLine($"  trig f{host.Frame}: player {host.Player.Position}, camseq {CameraSequence.Current?.Name} kf {(CameraSequence.Current == null ? -1 : typeof(CameraSequence).GetField("_keyframeIndex", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(CameraSequence.Current))}, bot hp {bot.Health} flags2 {bot.AiData.Flags2} {State()}");
                }
            }
            void Shoot()
            {
                for (int t = 0; t < 600 && !host.Ended && bot.Health > 0; t++)
                {
                    Vector3 face = new Vector3(bot.FacingVector.X, 0, bot.FacingVector.Z).Normalized();
                    Vector3 at = bot.Position + face * (args.Contains("close") ? 3 : 5) + Vector3.UnitY * 0.3f;
                    Vector3 look = (bot.Position + Vector3.UnitY * 0.5f - at).Normalized();
                    if (t % (args.Contains("close") ? 2 : 20) == 0)
                    {
                        host.PlacePlayer(at, look);
                    }
                    input.Buttons = (t % 8) < 4 ? CampaignButtons.Shoot : CampaignButtons.None;
                    int before = bot.Health;
                    host.Step(input);
                    if (args.Contains("beams") && t % 10 == 0)
                    {
                        Console.WriteLine($"   f{host.Frame} beams {host.Player.EquipInfo.Beams.Count(b => b.Lifespan > 0)} camseq {CameraSequence.Current?.Name} pf1 {host.Player.Flags1} pf2 {host.Player.Flags2} inputLock {host.Scene.CameraMode} lastTarget {(typeof(PlayerEntity).GetField("_lastTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host.Player) as EntityBase)?.Type} hp {bot.Health} invuln {invuln.GetValue(bot)}/{dmgInvuln.GetValue(bot)} ai2 {bot.AiData.Flags2} teams {GameState.Teams} ff {GameState.FriendlyFire} ti {bot.TeamIndex}/{host.Player.TeamIndex}");
                        foreach (BeamProjectileEntity b in host.Player.EquipInfo.Beams.Where(b => b.Lifespan > 0)) { Console.WriteLine($"      beam {b.Beam} pos {b.Position} dir {b.Direction} age {b.Age:0.00} bot {bot.Position} alt {bot.IsAltForm} minY {Fixed.ToFloat(bot.Values.MinPickupHeight)} maxY {Fixed.ToFloat(bot.Values.MaxPickupHeight)} r {bot.Volume.SphereRadius} segs {String.Join(" ", bot.KandenSegPos.Take(4))} sph {bot.Volume.SpherePosition}"); }
                    }
                    if (t % 60 == 30 && args.Contains("poke"))
                    {
                        int b2 = bot.Health;
                        bot.TakeDamage(1, DamageFlags.None, null, host.Player);
                        Console.WriteLine($"  poke: {b2} -> {bot.Health}");
                    }
                    if (bot.Health != before || t % 60 == 0)
                    {
                        var pth = new System.Text.StringBuilder();
                        bot.AiData.GetOuptut(pth);
                        Console.WriteLine("   path " + pth.ToString().Split((char)10)[0]);
                        int live = host.Player.EquipInfo.Beams.Count(b => b.Lifespan > 0);
                        Console.WriteLine($"   live beams {live} weapon {host.Player.EquipInfo.Weapon?.Beam}");
                        Console.WriteLine($"  shoot f{host.Frame}: {before} -> {State()}");
                        if (args.Contains("hud"))
                        {
                            var sig = new System.Text.StringBuilder();
                            for (int n = 0; n < host.Scene.HudItemCount; n++) { var it = host.Scene.GetHudItem(n); if (it.Top > 0.6f) sig.Append($"{it.SpriteIndex}/{it.BindingId}@{it.Left:0.00} "); }
                            Console.WriteLine($"   hud items {host.Scene.HudItemCount} lastTarget {(typeof(PlayerEntity).GetField("_lastTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host.Player) as EntityBase)?.Type} top: {sig}");
                        }
                    }
                }
                input.Buttons = CampaignButtons.None;
                Run(5);
                Console.WriteLine($"after: spawner {sp.Flags} state {GameState.StorySave.GetRoomState(roomId, sp.Id)} defeated {GameState.StorySave.DefeatedHunters} encounters {GameState.EncounterState[1]} bot hp {bot.Health} lf {bot.LoadFlags}");
            }
            if (args.Contains("hits"))
            {
                for (int i = 0; i < 12; i++)
                {
                    int b3 = bot.Health;
                    bot.TakeDamage(40, DamageFlags.None, null, host.Player);
                    Run(20);
                    Console.WriteLine($"  hit {i}: {b3} -> {bot.Health} ai2 {bot.AiData.Flags2} owed {bot.CamSeqAiOwed}");
                }
            }
            if (args.Contains("bar"))
            {
                FieldInfo lt = typeof(PlayerEntity).GetField("_lastTarget", BindingFlags.NonPublic | BindingFlags.Instance)!;
                string prev = "";
                for (int i = 0; i < 10; i++)
                {
                    lt.SetValue(host.Player, bot); typeof(PlayerEntity).GetField("_timeSinceHitTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(host.Player, (ushort)0);
                    int b3 = bot.Health;
                    if (i > 0) bot.TakeDamage(70, DamageFlags.None, null, host.Player);
                    Run(40);
                    var items = new System.Collections.Generic.List<string>();
                    for (int n = 0; n < host.Scene.HudItemCount; n++) { var it = host.Scene.GetHudItem(n); items.Add($"{it.SpriteIndex}/{it.BindingId}@{it.Left:0.000},{it.Top:0.000}"); }
                    string sig = String.Join(" ", items);
                    Console.WriteLine($"  bar {i}: {b3} -> {bot.Health}, lastTarget {(lt.GetValue(host.Player) as EntityBase)?.Type}, camseq {CameraSequence.Current?.Name}, items {items.Count}, changed: {String.Join(" ", items.Except(prev.Split(' ')))}");
                    prev = sig;
                }
            }
            if (args.Contains("shoot"))
            {
                Shoot();
            }
            host.Scene.SendMessage(Message.Activate, sp, sp, 0, 0);
            for (int t = 0; t < 600 && bot.Health == 0; t += 30)
            {
                if (t == 60)
                {
                    host.PlacePlayer(sp.Position + sp.FacingVector * 3 + sp.UpVector * 0.5f, -sp.FacingVector);
                }
                Run(30);
            }
            Console.WriteLine($"spawned: {State()}");
            string? camArg = args.FirstOrDefault(a => a.StartsWith("cam="));
            if (camArg != null && host.Scene.TryGetEntity(Int32.Parse(camArg[4..]), out EntityBase? camEnt))
            {
                Run(30);
                host.Scene.SendMessage(Message.Activate, camEnt, camEnt, 0, 0);
                for (int t = 0; t < 120; t++)
                {
                    Run(10);
                    if (t % 6 == 0)
                    {
                        Console.WriteLine($"  cam f{host.Frame}: camseq {CameraSequence.Current?.Name} {State()}");
                    }
                    if (t >= 40 && args.Contains("fire"))
                    {
                        Shoot();
                        break;
                    }
                    if (t >= 40 && t % 6 == 3)
                    {
                        int b2 = bot.Health;
                        bot.TakeDamage(40, DamageFlags.None, null, host.Player);
                        Console.WriteLine($"  cam poke: {b2} -> {bot.Health}");
                    }
                }
            }
            var tree = new System.Text.StringBuilder();
            void Dump(AiPersonalityData1 n, string ind)
            {
                tree.AppendLine($"{ind}{n.Label} f24={n.Func24Id} init=[{String.Join(",", n.Data3a)}] proc=[{String.Join(",", n.Data3b)}]");
                foreach (AiPersonalityData2 d in n.Data2)
                {
                    tree.AppendLine($"{ind}  -> sel {d.Data1SelectIndex} w {d.Weight} f3 {d.Func3Id}({d.Parameters.Param1},{d.Parameters.Param2}) and "
                        + String.Join(" & ", d.Data4.Select(c => $"f3 {c.Func3Id}({c.Parameters.Param1},{c.Parameters.Param2})")));
                }
                foreach (AiPersonalityData1 c in n.Data1)
                {
                    Dump(c, ind + "    ");
                }
            }
            Dump(bot.AiData.Personality, "");
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kanden_ai_tree.txt"), tree.ToString());
            var path = new System.Text.StringBuilder();
            bot.AiData.GetOuptut(path);
            Console.WriteLine($"ai path: {path}");
            int dist = args.Skip(1).Where(a => a.StartsWith("dist=")).Select(a => Int32.Parse(a[5..])).FirstOrDefault(6);
            if (dist > 0)
            {
                Vector3 toward = bot.Position - sp.Position;
                Vector3 face = new Vector3(bot.FacingVector.X, 0, bot.FacingVector.Z).Normalized();
                host.PlacePlayer(bot.Position + face * dist + Vector3.UnitY * 0.5f, -face);
            }
            for (int t = 0; t < 20; t++)
            {
                Run(30);
                Console.WriteLine($"  f{host.Frame}: {State()}");
            }
            string mode = args.Skip(1).FirstOrDefault(a => a.StartsWith("start=")) ?? "start=none";
            if (mode == "start=msg" || mode == "start=msgodd")
            {
                if ((host.Scene.FrameCount % 2 == 0) == (mode == "start=msgodd"))
                {
                    Run(1);
                }
                Console.WriteLine($"send SetCamSeqAi on frame {host.Scene.FrameCount}");
                host.Scene.SendMessage(Message.SetCamSeqAi, null!, bot, 0, 0);
            }
            else if (mode == "start=camseq")
            {
                CamSeqEntity? cs = null;
                foreach (EntityBase e in host.Scene.Entities)
                {
                    cs ??= e as CamSeqEntity;
                }
                Debug.Assert(cs != null);
                host.Scene.SendMessage(Message.Activate, cs, cs, 0, 0);
                Console.WriteLine("activated camseq");
            }
            for (int t = 0; t < 10; t++)
            {
                Run(30);
                Console.WriteLine($"  f{host.Frame}: {State()}");
            }
            for (int t = 0; t < 12 && !host.Ended; t++)
            {
                int before = bot.Health;
                bot.TakeDamage(40, DamageFlags.None, null, host.Player);
                Console.WriteLine($"  hit {t}: {before} -> {bot.Health}; {State()}");
                Run(30);
            }
            Run(300);
            Console.WriteLine($"after: {State()}; ended {host.Ended}");
        }
    }
}
