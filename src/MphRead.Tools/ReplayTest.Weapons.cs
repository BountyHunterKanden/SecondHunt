using System;
using MphRead.Entities;
using MphRecomp.Replay;

namespace MphRead
{
    // -replayweapons <file> [seat]: plays a replay and prints each change of one seat's weapon state: current weapon, the
    // Omega Cannon held, the third slot, the ring showing it, FIRE held (the Shooting flag), the gun animation and alt form
    // (Discord report 11-12: an Omega Cannon pickup that didn't switch to it)
    internal static partial class ReplayTest
    {
        private static int _detailFrom = -1;
        private static int _detailTo = -1;
        private static bool _spawnerData;
        private static string _lastSpawner = "";
        private const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        public static void WeaponTrace(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: -replayweapons <file> [seat [from to]] [raise] [onetaker]");
                return;
            }
            if (!Setup())
            {
                return;
            }
            ReplayFile file = ReplayFile.Load(args[1]);
            int seat = args.Length > 2 ? Int32.Parse(args[2]) : Math.Max(0, file.Settings.LocalSeat);
            if (Array.IndexOf(args, "raise") > 0) // raise: play it with MatchSettings.PickupEquipRaise on (it parts from the file after such a pickup)
            {
                file.Settings.PickupEquipRaise = true;
                args = Array.FindAll(args, a => a != "raise");
            }
            if (Array.IndexOf(args, "onetaker") > 0) // onetaker: play it with MatchSettings.PickupOneTaker on (a format 93 file, 11-13)
            {
                file.Settings.PickupOneTaker = true;
                args = Array.FindAll(args, a => a != "onetaker");
            }
            _detailFrom = args.Length > 4 ? Int32.Parse(args[3]) : -1; // [from to]: every frame's gun state between them
            _detailTo = args.Length > 4 ? Int32.Parse(args[4]) : -1;
            Console.WriteLine($"{file.Settings.Mode} in {file.Settings.Arena}, {file.Steps.Count} steps, seat {seat}");
            // a network host's recording: the seat's weapon inputs as they came in (FIRE, a picked weapon, next/prev, the
            // wheel button, the affinity slot), each change with its step
            const MphRecomp.Campaign.CampaignButtons WeaponButtons = MphRecomp.Campaign.CampaignButtons.Shoot
                | MphRecomp.Campaign.CampaignButtons.NextWeapon | MphRecomp.Campaign.CampaignButtons.PrevWeapon
                | MphRecomp.Campaign.CampaignButtons.WeaponMenu | MphRecomp.Campaign.CampaignButtons.AffinitySlot
                | MphRecomp.Campaign.CampaignButtons.Morph;
            string lastInput = "";
            foreach (ReplayNetEvent e in file.NetEvents)
            {
                if (e.Seat != seat || e.Kind != ReplayNetKind.Input || e.Input == null) continue;
                MphRecomp.Multiplayer.Net.NetInput input = MphRecomp.Multiplayer.Net.NetInput.ReadRecord(e.Input);
                string text = $"buttons={input.Buttons & WeaponButtons} select={input.SelectWeapon}";
                if (text != lastInput)
                {
                    Console.WriteLine($"in {e.Step,6} ({e.Step / 60f,6:0.0}s) {text}");
                    lastInput = text;
                }
            }
            string last = "";
            string before = "";
            _beforeStep = host =>
            {
                PlayerEntity p = PlayerEntity.Players[seat];
                bool omega = p.AvailableWeapons[BeamType.OmegaCannon];
                string state = $"weapon={p.CurrentWeapon} omega={(omega ? 1 : 0)} slot3={p.HostThirdSlotWeapon} ring={(p.HostRingOmegaShown ? 1 : 0)}"
                    + $" hp={p.Health} alt={(p.IsAltForm ? 1 : 0)} morphing={(p.IsMorphing ? 1 : 0)}";
                string extra = $" shooting={(p.Flags2.TestFlag(PlayerFlags2.Shooting) ? 1 : 0)} gun={p.GunAnimation}";
                if (_detailFrom <= host.Frame && host.Frame <= _detailTo)
                {
                    object? t = typeof(PlayerEntity).GetField("_timeSinceInput", Flags)?.GetValue(p);
                    object? c = typeof(PlayerEntity).GetField("_camSwitchTimer", Flags)?.GetValue(p);
                    Console.WriteLine($"  f {host.Frame} gun={p.GunAnimation} idle={t} camSwitch={c} alt={(p.IsAltForm ? 1 : 0)} morphing={(p.IsMorphing ? 1 : 0)} weapon={p.CurrentWeapon}");
                }
                if (state != last)
                {
                    Console.WriteLine($"f {host.Frame,6} ({host.Frame / 60f,6:0.0}s) {state}{extra}   (step before:{before})");
                    last = state;
                }
                before = extra;
                // the Omega Cannon spawners: their data once, then each change of active / item out / cooldown at 0
                foreach (ItemSpawnEntity e in host.Scene.GetItemSpawnEntities())
                {
                    if (e.Data.ItemType != ItemType.OmegaCannon) continue;
                    if (!_spawnerData)
                    {
                        Console.WriteLine($"spawner {e.Id}: delay={e.Data.SpawnDelay} interval={e.Data.SpawnInterval} max={e.Data.MaxSpawnCount}"
                            + $" enabled={e.Data.Enabled} always={e.Data.AlwaysActive} parent={e.Data.ParentId} at {e.Position}");
                    }
                    (ushort cooldown, ushort count) = e.HostSpawnState;
                    string sp = $"active={(e.Active ? 1 : 0)} item={(e.Item != null ? 1 : 0)} ready={(cooldown == 0 ? 1 : 0)} count={count}";
                    if (sp != _lastSpawner)
                    {
                        // who is nearest the pad (and holds the Omega already) as the item goes
                        string near = "";
                        foreach (PlayerEntity q in PlayerEntity.Players)
                        {
                            if (q.LoadFlags.TestFlag(LoadFlags.Active))
                            {
                                float d = (q.Position - e.Position).Length;
                                if (d < 6) near += $" s{q.SlotIndex}:{d:0.0}{(q.AvailableWeapons[BeamType.OmegaCannon] ? "*" : "")}";
                            }
                        }
                        Console.WriteLine($"sp {host.Frame,6} ({host.Frame / 60f,6:0.0}s) {sp} cooldown={cooldown} near{near}");
                        _lastSpawner = sp;
                    }
                }
                _spawnerData = true;
            };
            try
            {
                Run r = Replay(file, View.NoDraw, types: false, reference: null, stopOnFail: false);
                Console.WriteLine(r.Verdict());
            }
            finally
            {
                _beforeStep = null;
            }
        }
    }
}
