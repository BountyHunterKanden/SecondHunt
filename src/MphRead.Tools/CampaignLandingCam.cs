using System;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -landingcam [room=UNIT2_LAND] [frames=1500] [from-to]: owner queue #37 (frame dips ~13 s into the Celestial Archives landing,
    // GPU 13-25 ms a frame with MP4 Legacy). Steps a first landing and prints, per stretch of frames, the camera sequence,
    // whether the main biped is drawn (HostMainBipedAlpha), and where the camera is against Samus: "inside" = the test
    // CampaignActivity.UpdateBodyCut uses (flat < 1.0, below her top + 0.5, above her feet - 0.6), which today only cuts
    // with no camera sequence running.
    internal static partial class CampaignSim
    {
        public static void LandingCam(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT2_LAND";
            int frames = args.Length >= 3 ? Int32.Parse(args[2]) : 1500;
            int detailFrom = -1, detailTo = -1;
            if (args.Length >= 4)
            {
                string[] range = args[3].Split('-');
                detailFrom = Int32.Parse(range[0]);
                detailTo = Int32.Parse(range[1]);
            }
            using CampaignHost host = CampaignHost.Start(room, setupSave: s => GiveAll(s, 0), collectDrawItems: true,
                viewWidth: 1920, viewHeight: 1080);
            Scene scene = host.Scene;
            string last = "";
            int start = 0;
            float nearest = float.MaxValue;
            void Flush(int end)
            {
                if (last != "")
                {
                    string near = nearest < float.MaxValue ? $", nearest flat {nearest:0.00}" : "";
                    Console.WriteLine($"  frames {start,4}-{end,4} ({(end - start + 1) / 60f,5:0.00} s): {last}{near}");
                }
            }
            for (int f = 0; f < frames && !host.Ended; f++)
            {
                host.Step(default);
                PlayerEntity p = host.Player;
                Vector3 cam = scene.CameraPosition, pos = p.Position;
                float dx = cam.X - pos.X, dz = cam.Z - pos.Z;
                float flat = MathF.Sqrt(dx * dx + dz * dz);
                float top = pos.Y + Fixed.ToFloat(p.Values.MaxPickupHeight) + 0.5f;
                bool spawned = p.LoadFlags.TestFlag(LoadFlags.Spawned);
                bool drawn = PlayerEntity.HostMainBipedAlpha > 0;
                bool inside = spawned && flat < 1.0f && cam.Y < top && cam.Y > pos.Y - 0.6f;
                CameraSequence? seq = CameraSequence.Current;
                string state = $"camseq {(seq == null ? "none" : $"{seq.SequenceId} {seq.Name}")}, "
                    + $"{(spawned ? "spawned" : "not spawned")}, body {(drawn ? "drawn" : "not drawn")}, camera {(inside ? "INSIDE" : "outside")}"
                    + (inside && drawn ? (seq == null ? " -> cut today" : " -> NOT cut today (camseq)") : "");
                if (state != last)
                {
                    Flush(f - 1);
                    last = state;
                    start = f;
                    nearest = float.MaxValue;
                }
                if (spawned)
                {
                    nearest = MathF.Min(nearest, flat);
                }
                // [from-to]: her chest (feet + 0.6) in the camera's view space, every other frame
                if (detailFrom <= f && f <= detailTo && f % 2 == 0)
                {
                    Vector4 v = new Vector4(pos.X, pos.Y + 0.6f, pos.Z, 1) * scene.ViewMatrix;
                    Console.WriteLine($"    f{f}: flat {flat:0.00}, camera {cam.Y - pos.Y:+0.00;-0.00} over her feet, chest {-v.Z:+0.00;-0.00} ahead, "
                        + $"{v.X:+0.00;-0.00} right, {v.Y:+0.00;-0.00} up, alpha {PlayerEntity.HostMainBipedAlpha:0.00}");
                }
            }
            Flush(frames - 1);
        }
    }
}
