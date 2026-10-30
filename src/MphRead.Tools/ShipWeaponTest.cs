using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Frontend;

namespace MphRead
{
    // -feshipweapon: the ship's WEAPON SELECT end to end (owner queue #8a). Drags a weapon into the slot on the ship
    // page by touch (as on the Odin), starts the landing room on foot from that story (what EXIT SHIP does after a
    // loadout change), then checks the player's third weapon slot and that the affinity button (D-pad Left on the
    // Odin) equips it. Vanilla (BizHawk, rev 1, 2026-10-03): the pick only fills the third slot of the bottom-screen
    // weapon bar; Samus leaves the ship holding the Power Beam.
    internal static class ShipWeaponTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            int pass = 0, fail = 0;
            void Check(string what, bool ok)
            {
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {what}");
                if (ok) pass++;
                else fail++;
            }
            // -feshipweapon [save.json]: a campaign save (its story + planet) instead of a fresh story on Celestial Archives
            StorySave story;
            string room = "UNIT2_LAND";
            if (args.Length >= 2)
            {
                CampaignSaveFile file = CampaignSaves.FromJson(System.IO.File.ReadAllText(args[1]));
                story = file.Story;
                room = ShipNavigation.Get(file.Planet).LandingRoom;
                Console.WriteLine($"  save {args[1]}: {file.Planet} ({room}), slots {string.Join(",", story.WeaponSlots)}");
            }
            else
            {
                story = new StorySave();
                story.Weapons |= (ushort)(1 << (int)BeamType.VoltDriver | 1 << (int)BeamType.Battlehammer | 1 << (int)BeamType.Judicator);
                story.Ammo[0] = story.AmmoMax[0] = 400;
                // a return to a landing room already seen (as from the ship): no first-visit camera sequence
                story.SetVisitedRoom(Metadata.GetRoomByName(room).Item1!.Id);
            }
            var log = new List<string>();
            var menu = new ShipMenu(Paths.FileSystem, new UiTextureCache())
            {
                Log = m => log.Add(m),
                SaveGame = () => true,
                Request = r => log.Add($"request {r}"),
                Story = story
            };
            bool changed = false;
            menu.WeaponsChanged = () => changed = true;
            menu.Open("CELESTIAL ARCHIVES");
            const int W = 1280, H = 720;
            for (int i = 0; i < 300 && !menu.RedrawReady; i++)
            {
                menu.Build(W, H);
                Thread.Sleep(20);
            }
            void Frames(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    menu.Tick();
                    menu.Build(W, H);
                }
            }
            Frames(30);
            (float X, float Y) tab = menu.ControlAt("WEAPON") ?? (0, 0);
            menu.Touch(tab.X, tab.Y);
            Frames(90);
            // Judicator = slot 3 of the icon row (13 + 25k, 142, 24x24); the big slot (44, 86, 48x32) is tested at touch + 8
            (float X, float Y)? from = menu.CardToCanvas(13 + 25 * 3 + 12, 154);
            (float X, float Y)? to = menu.CardToCanvas(44 + 24 - 8, 86 + 16 - 8);
            Check($"weapon page open ({from != null})", from != null && to != null);
            if (from is { } a && to is { } b)
            {
                menu.Touch(a.X, a.Y);
                for (int i = 1; i <= 20; i++)
                {
                    menu.TouchMove(a.X + (b.X - a.X) * i / 20, a.Y + (b.Y - a.Y) * i / 20);
                    Frames(1);
                }
                menu.TouchUp(b.X, b.Y);
                Frames(5);
            }
            Check($"drag into the slot -> WeaponSlots[2] = {(BeamType)story.WeaponSlots[2]}, WeaponsChanged {changed}",
                story.WeaponSlots[2] == (int)BeamType.Judicator && changed);
            foreach (string m in log) Console.WriteLine($"    menu: {m}");

            using CampaignHost host = CampaignHost.Start(room, save: CampaignSaves.Clone(story), arriving: false);
            for (int i = 0; i < 120; i++) host.Step(default);
            PlayerEntity p = host.Player;
            var slots = (BeamType[])typeof(PlayerEntity).GetField("_weaponSlots", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;
            Check($"on foot: holding {p.CurrentWeapon} (vanilla: PowerBeam)", p.CurrentWeapon == BeamType.PowerBeam);
            Check($"on foot: slots {slots[0]}, {slots[1]}, {slots[2]} (third = the pick)", slots[2] == BeamType.Judicator);
            object? F(string n) => typeof(PlayerEntity).GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(p)
                ?? typeof(PlayerEntity).GetProperty(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(p);
            for (int t = 0; t < 4 && p.CurrentWeapon != BeamType.Judicator; t++)
            {
                Console.WriteLine($"    try {t} at frame {host.Frame}: camseq {CameraSequence.Current?.SequenceId} {CameraSequence.Current?.Name} block {CameraSequence.Current?.BlockInput}, gun anim {F("GunAnimation")}, "
                    + $"flags1 {F("Flags1")}, available {F("_availableWeapons")}, ammo {string.Join("/", (int[])F("_ammo")!)}");
                host.Step(new CampaignInput { Buttons = CampaignButtons.AffinitySlot });
                host.Step(new CampaignInput { Buttons = CampaignButtons.AffinitySlot });
                for (int i = 0; i < 60; i++) host.Step(default);
                Console.WriteLine($"      -> {p.CurrentWeapon}");
                for (int i = 0; i < 240; i++) host.Step(default);
            }
            Check($"affinity button equips it: {p.CurrentWeapon}", p.CurrentWeapon == BeamType.Judicator);
            Console.WriteLine($"  {pass} passed, {fail} failed");
        }
    }
}
