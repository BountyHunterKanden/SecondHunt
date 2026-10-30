using System;
using System.IO;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;

namespace MphRead
{
    // -huddialog [room=UNIT2_LAND] [w] [h]: the campaign's dialogs (Reports R16: a message box, a weapon pickup, an energy
    // tank, a yes/no question, a scan result) recorded headless and software-rendered as -campaignhud does, at the given
    // canvas (default 1920x1080) -> extract_out/campaign/dialog_*.png
    internal static partial class CampaignSim
    {
        public static void HudDialogs(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT2_LAND";
            int w = args.Length >= 3 ? Int32.Parse(args[2]) : 1920, h = args.Length >= 4 ? Int32.Parse(args[3]) : 1080;
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign"));
            Directory.CreateDirectory(outDir);
            using CampaignHost host = CampaignHost.Start(room, collectDrawItems: true, viewWidth: w, viewHeight: h);
            int landing = 0;
            for (; landing < 3000 && (landing < 300 || CameraSequence.Current != null); landing++) host.Step(default);
            for (int i = 0; i < 60; i++) host.Step(default);
            void Dialog(string name, Action open)
            {
                open();
                // the box opens over ~16 frames, then the text types out
                for (int i = 0; i < 150; i++) host.Step(default);
                string file = Path.Combine(outDir, $"dialog_{name}_{w}x{h}.png");
                RenderHud(host.Scene, w, h, file);
                Console.WriteLine($"  {name}: {host.Dialog}, {host.Scene.HudItemCount} HUD items -> {file}");
                for (int i = 0; i < 600 && host.DialogPaused; i++)
                {
                    host.Step(new CampaignInput { DialogButton = i % 10 == 0 ? HostDialogButton.Advance : HostDialogButton.None });
                }
                for (int i = 0; i < 30; i++) host.Step(default);
            }
            PlayerEntity p = host.Player;
            Dialog("message", () => p.ShowDialog(DialogType.Okay, messageId: 43));
            Dialog("weapon", () => p.ShowDialog(DialogType.Event, messageId: 5, param1: (int)BeamType.Battlehammer,
                value1: Metadata.WeaponNamesUpper[(int)BeamType.Battlehammer], value2: ""));
            Dialog("energytank", () => p.ShowDialog(DialogType.Event, messageId: 4, param1: (int)EventType.EnergyTank));
            Dialog("yesno", () => p.ShowDialog(DialogType.YesNo, messageId: 1));
        }
    }
}
