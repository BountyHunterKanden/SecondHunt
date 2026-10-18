using System;
using System.IO;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;

namespace MphRead
{
    // -dialogtouch [room=UNIT2_LAND]: the campaign's dialogs answered by a tap on the buttons they draw (owner 2026-10-14:
    // "pop up notifications in campaign that have an onscreen ok button ... touchable/clickable"). For each view size it
    // opens a message, an energy tank pickup and a YES / NO question headless, taps the screen pixel at the centre of the
    // drawn OK / YES / NO (PlayerDialog.HostDialogButtonAtView, what CampaignRenderer.DialogTouch calls), and checks the
    // dialog answers as that button; a tap off the buttons must miss. Pictures of the 1920x1080 dialogs with their tap
    // points go to extract_out/campaign/dialogtouch_*.png (the tap pixel is printed: look that it sits on the button).
    internal static partial class CampaignSim
    {
        public static void DialogTouch(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT2_LAND";
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign"));
            Directory.CreateDirectory(outDir);
            int pass = 0, fail = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {what}");
                if (ok) pass++; else fail++;
            }
            foreach ((int w, int h) in new[] { (1920, 1080), (2400, 1080), (1280, 960) })
            {
                Console.WriteLine($"view {w}x{h}");
                using CampaignHost host = CampaignHost.Start(room, collectDrawItems: true, viewWidth: w, viewHeight: h);
                for (int landing = 0; landing < 3000 && (landing < 300 || CameraSequence.Current != null); landing++) host.Step(default);
                for (int i = 0; i < 60; i++) host.Step(default);
                PlayerEntity p = host.Player;
                float box = MathF.Min(w, h * 4 / 3f);
                // a DS pixel of the dialog's layout -> the view pixel it is drawn at
                (float X, float Y) Screen(float dsX, float dsY) => ((w - box) / 2 + dsX / 256 * box, dsY / 192 * h);
                bool Tap(float dsX, float dsY, HostDialogButton expect, string name)
                {
                    (float x, float y) = Screen(dsX, dsY);
                    HostDialogButton hit = PlayerEntity.HostDialogButtonAtView(x, y, w, h, host.Dialog == DialogType.YesNo);
                    Check(hit == expect, $"{name}: tap at ({x:0},{y:0}) -> {hit} (want {expect})");
                    host.Step(new CampaignInput { DialogButton = hit });
                    for (int i = 0; i < 5; i++) host.Step(default);
                    return hit == expect;
                }
                void Open(string name, Action open)
                {
                    open();
                    // the box opens, the text types out, then OK / YES / NO appear
                    for (int i = 0; i < 300; i++) host.Step(default);
                    if (w == 1920)
                    {
                        CampaignSim.RenderHud(host.Scene, w, h, Path.Combine(outDir, $"dialogtouch_{name}_{w}x{h}.png"));
                    }
                }
                void Close()
                {
                    for (int i = 0; i < 600 && host.DialogPaused; i++)
                    {
                        host.Step(new CampaignInput { DialogButton = i % 10 == 0 ? (host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance) : HostDialogButton.None });
                    }
                    for (int i = 0; i < 30; i++) host.Step(default);
                }

                // a message (maybe several pages: the right arrow until OK is live)
                Open("message", () => p.ShowDialog(DialogType.Okay, messageId: 43));
                Check(host.DialogPaused, "message open");
                Check(PlayerEntity.HostDialogButtonAtView(2, 2, w, h, yesNo: false) == HostDialogButton.None, "message: a tap in the corner misses");
                for (int page = 0; page < 6 && host.DialogPaused; page++)
                {
                    Tap(128, 182, HostDialogButton.Okay, $"message OK (try {page + 1})");
                    if (host.DialogPaused) Tap(185, 181, HostDialogButton.Right, "message next page arrow");
                }
                Check(!host.DialogPaused && p.DialogConfirmState == ConfirmState.Okay, "message closed by its OK");
                Close();

                // a pickup (Event)
                Open("energytank", () => p.ShowDialog(DialogType.Event, messageId: 4, param1: (int)EventType.EnergyTank));
                Check(host.DialogPaused, "energy tank open");
                Tap(128, 182, HostDialogButton.Okay, "energy tank OK");
                Check(!host.DialogPaused, "energy tank closed by its OK");
                Close();

                // YES / NO: NO, then YES
                Open("yesno", () => p.ShowDialog(DialogType.YesNo, messageId: 1));
                Check(host.DialogPaused && host.Dialog == DialogType.YesNo, "question open");
                Tap(160, 182, HostDialogButton.No, "question NO");
                Check(host.Dialog == DialogType.None && p.DialogConfirmState == ConfirmState.No, "question closed, answered NO"); // (vanilla stays paused until the prompt's owner reads the answer)
                Close();
                Open("yesno2", () => p.ShowDialog(DialogType.YesNo, messageId: 1));
                Tap(96, 182, HostDialogButton.Yes, "question YES");
                Check(host.Dialog == DialogType.None && p.DialogConfirmState == ConfirmState.Yes, "question closed, answered YES");
                Close();
            }
            Console.WriteLine($"dialogtouch: {pass} PASS, {fail} FAIL");
        }
    }
}
