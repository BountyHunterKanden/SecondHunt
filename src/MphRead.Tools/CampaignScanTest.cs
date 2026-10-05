using System;
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
            if (!ToolPaths.UseDesktopExtraction() || args.Length < 3)
            {
                Console.WriteLine("usage: -campaignscan <room> <entityId> [layer] [logged] | <room> list | names <scanId>...");
                return;
            }
            string room = args[1];
            bool list = args[2] == "list";
            int entityId = list ? -1 : Int32.Parse(args[2]);
            int layer = args.Length >= 4 && Int32.TryParse(args[3], out int l) ? l : 0;
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
                    host.Step(new CampaignInput { DialogButton = k % 10 == 0 ? HostDialogButton.Advance : HostDialogButton.None });
            }
            // spots around and below the target (items often float over a ledge): keep the first where she stands
            // within scan range (< 20) with a clear camera -> target line
            foreach (float rise in new[] { 0f, -2f, -4f, -6f, 2f })
            {
                foreach (float dist in new[] { 3f, 5f, 8f, 12f })
                {
                    for (int a = 0; a < 12 && !clear; a++)
                    {
                        float ang = a * MathF.PI / 6;
                        var dir = new Vector3(MathF.Cos(ang), 0, MathF.Sin(ang));
                        host.PlacePlayer(at + dir * dist + new Vector3(0, rise, 0), -dir);
                        for (int i = 0; i < 30; i++) host.Step(default);
                        CloseDialogs();
                        for (int i = 0; i < 40; i++) host.Step(new CampaignInput { AimDelta = AimAt(p, at) });
                        CollisionResult discard = default;
                        clear = p.Health > 0 && (p.CameraInfo.Position - at).Length < 20
                            && !CollisionDetection.CheckBetweenPoints(p.CameraInfo.Position, at, TestFlags.Scan, host.Scene, ref discard);
                    }
                    if (clear) break;
                }
                if (clear) break;
            }
            CloseDialogs();
            Console.WriteLine($"  stood at {Fmt(p.Position)}, camera {Fmt(p.CameraInfo.Position)}; clear line of sight {clear}");
            for (int i = 0; i < 20; i++) host.Step(new CampaignInput { Buttons = i < 2 ? CampaignButtons.ScanVisor : CampaignButtons.None });
            CloseDialogs();
            if (!p.ScanVisor) for (int i = 0; i < 20; i++) host.Step(new CampaignInput { Buttons = i < 2 ? CampaignButtons.ScanVisor : CampaignButtons.None });
            Console.WriteLine($"  scan visor {p.ScanVisor}");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            object? Field(string name) => typeof(PlayerEntity).GetField(name, flags)?.GetValue(p);
            static object? Member(object? o, string name) => o == null ? null
                : (object?)o.GetType().GetField(name)?.GetValue(o) ?? o.GetType().GetProperty(name)?.GetValue(o);
            int f;
            for (f = 0; f < 600 && !host.DialogPaused; f++)
            {
                host.Step(new CampaignInput { Buttons = CampaignButtons.Scan, AimDelta = AimAt(p, at) });
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
