using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -campaignsuit [room]: the MphRead hooks the campaign's HD suit and third-person view stand on, headless.
    //   PlayerEntity.HostShowMainBiped   the main player's biped is drawn (and posed) in first person
    //   PlayerEntity.HostOwnMainBiped    ... but its draw items are left out (the host draws the HD body)
    //   PlayerEntity.HostOwnMainAlt      the morph ball's draw items are left out (the host draws the HD ball)
    //   PlayerEntity.HostMainBipedAlpha  the main biped's alpha as drawn, 0 when it wasn't
    //   Scene.HostCameraOffset           view matrix / camera position offset, aim untouched
    // plus the collision ray the third-person camera pulls in with (CheckBetweenPoints' Distance = fraction).
    internal static partial class CampaignSim
    {
        public static void Suit(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT2_LAND";
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {what}");
                if (!ok) fails++;
            }
            try
            {
                using CampaignHost host = CampaignHost.Start(room, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                Scene scene = host.Scene;
                PlayerEntity p = host.Player;
                var input = new CampaignInput { SelectWeapon = BeamType.None };
                int ModelItems(string name) => new[] { scene.OpaqueItems, scene.DecalItems, scene.TranslucentItems }
                    .Sum(list => list.Count(i => i.Type == RenderItemType.Mesh && i.ListId >= 1 && i.ListId <= scene.HostMeshes.Count
                        && scene.HostMeshes[i.ListId - 1].Model.Name == name));

                // A. the landing: MphRead draws the biped itself during its camera sequence
                int seqFrames = 0, seqBiped = 0, seqItems = 0, fpBiped = 0;
                for (int i = 0, after = 0; i < 1800 && !host.Ended && after < 60; i++, after = CameraSequence.Current == null && i > 60 ? after + 1 : 0)
                {
                    host.Step(input);
                    if (CameraSequence.Current != null)
                    {
                        seqFrames++;
                        if (PlayerEntity.HostMainBipedAlpha > 0) seqBiped++;
                        if (ModelItems("Samus_lod0") > 0) seqItems++;
                    }
                    else if (PlayerEntity.HostMainBipedAlpha > 0)
                    {
                        fpBiped++;
                    }
                }
                Console.WriteLine($"  landing: {seqFrames} cam-seq frames, biped drawn on {seqBiped} (items on {seqItems}); first-person frames with the biped: {fpBiped}");
                Check(seqFrames == 0 || seqBiped == seqItems, "HostMainBipedAlpha > 0 exactly when MphRead drew Samus_lod0 in the camera sequence");

                // B. the morph ball: MphRead's own ball items, then left to the host
                input.Buttons = CampaignButtons.Morph; // held 4 steps: MphRead reads input on its 30 Hz process frames
                for (int i = 0; i < 4; i++) host.Step(input);
                input.Buttons = CampaignButtons.None;
                for (int i = 0; i < 60 && !host.Ended && !p.IsAltForm; i++)
                {
                    host.Step(input);
                    if (i % 15 == 0) Console.WriteLine($"    morph wait {i}: alt {p.IsAltForm}, flags1 {p.Flags1}, dialog {host.DialogPaused}, pos {Fmt(p.Position)}");
                }
                for (int i = 0; i < 30 && !host.Ended; i++) host.Step(input);
                int ballItems = ModelItems("SamusAlt_lod0");
                bool drawnTp = p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson);
                PlayerEntity.HostOwnMainAlt = true;
                host.Step(input);
                int ownedItems = ModelItems("SamusAlt_lod0");
                PlayerEntity.HostOwnMainAlt = false;
                Console.WriteLine($"  morph ball: alt form {p.IsAltForm}, SamusAlt_lod0 items {ballItems} -> host-owned {ownedItems}, DrawnThirdPerson {drawnTp}");
                Check(p.IsAltForm && ballItems > 0 && ownedItems == 0 && drawnTp, "HostOwnMainAlt leaves out MphRead's ball, DrawnThirdPerson still says it was drawn");
                input.Buttons = CampaignButtons.Morph; // back on foot for the rest
                for (int i = 0; i < 4; i++) host.Step(input);
                input.Buttons = CampaignButtons.None;
                for (int i = 0; i < 90 && !host.Ended; i++) host.Step(input);

                // C. third person: the biped drawn in first person, animated by walking
                PlayerEntity.HostShowMainBiped = true;
                int shown = 0, withItems = 0, steps = 0;
                var legClips = new HashSet<int>();
                var torsoClips = new HashSet<int>();
                float legFrameMoves = 0, lastLegFrame = -1;
                input.Move = new System.Numerics.Vector2(0, 1);
                for (int i = 0; i < 200 && !host.Ended; i++)
                {
                    input.AimDelta = new System.Numerics.Vector2(i < 100 ? 3 : -3, 0);
                    host.Step(input);
                    steps++;
                    if (PlayerEntity.HostMainBipedAlpha > 0) shown++;
                    if (ModelItems("Samus_lod0") > 0) withItems++;
                    legClips.Add(p.HostBipedModel1.AnimInfo.Index[0]);
                    torsoClips.Add(p.BipedModel2.AnimInfo.Index[0]);
                    float f = p.HostBipedModel1.AnimInfo.Frame[0];
                    if (lastLegFrame >= 0 && f != lastLegFrame) legFrameMoves++;
                    lastLegFrame = f;
                }
                Console.WriteLine($"  third person (walking): biped shown {shown}/{steps}, items {withItems}/{steps}, leg clips "
                    + $"[{String.Join(", ", legClips.Select(c => (PlayerAnimation)c))}], torso clips "
                    + $"[{String.Join(", ", torsoClips.Select(c => (PlayerAnimation)c))}], leg frame changed on {legFrameMoves} steps");
                Check(shown == steps && withItems == steps, "HostShowMainBiped: the main biped is drawn every first-person frame");
                // MphRead's clips advance on its 30 Hz process frames: every other 60 Hz step
                Check(legClips.Count > 1 && legFrameMoves >= steps / 2 - 2, "the biped's legs layer animates while walking (the HD body's pose source)");

                // D. the host owns the body: posed (alpha reported), no Samus_lod0 items
                PlayerEntity.HostOwnMainBiped = true;
                shown = withItems = steps = 0;
                for (int i = 0; i < 60 && !host.Ended; i++)
                {
                    host.Step(input);
                    steps++;
                    if (PlayerEntity.HostMainBipedAlpha > 0) shown++;
                    if (ModelItems("Samus_lod0") > 0) withItems++;
                }
                Console.WriteLine($"  host-owned body: alpha reported {shown}/{steps}, Samus_lod0 items on {withItems}");
                Check(shown == steps && withItems == 0, "HostOwnMainBiped: posed and reported, but no draw items");
                input.Move = default;
                input.AimDelta = default;
                PlayerEntity.HostOwnMainBiped = false;
                PlayerEntity.HostShowMainBiped = false;
                host.Step(input);
                Check(PlayerEntity.HostMainBipedAlpha == 0 && ModelItems("Samus_lod0") == 0, "hooks off: back to the first-person gun only");

                // E. the camera offset: view + camera position move, the player's own camera doesn't
                var offset = new Vector3(0.3f, 0.5f, -1.2f);
                scene.HostCameraOffset = offset;
                host.Step(input);
                Matrix4 expect = Matrix4.CreateTranslation(-offset) * p.CameraInfo.ViewMatrix;
                float viewErr = 0;
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) viewErr = Math.Max(viewErr, MathF.Abs(scene.ViewMatrix[r, c] - expect[r, c]));
                float posErr = (scene.CameraPosition - (p.CameraInfo.Position + offset)).Length;
                Console.WriteLine($"  camera offset {offset}: view error {viewErr:0.000000}, camera position error {posErr:0.000000}");
                Check(viewErr < 1e-4f && posErr < 1e-4f, "Scene.HostCameraOffset moves the drawn view and camera position");
                scene.HostCameraOffset = Vector3.Zero;
                host.Step(input);
                float back = 0;
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) back = Math.Max(back, MathF.Abs(scene.ViewMatrix[r, c] - p.CameraInfo.ViewMatrix[r, c]));
                Check(back < 1e-6f, "offset zero: the view is the player's camera again");

                // F. the collision ray the third-person camera uses: Distance is the fraction along the segment
                Matrix4 cam = p.CameraInfo.ViewMatrix.Inverted();
                Vector3 eye = cam.Row3.Xyz;
                int hits = 0, sane = 0;
                for (int k = 0; k < 8; k++)
                {
                    float a = k * MathF.PI / 4;
                    var dir = new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
                    CollisionResult res = default;
                    if (CollisionDetection.CheckBetweenPoints(eye, eye + dir * 60, TestFlags.Players, scene, ref res))
                    {
                        hits++;
                        Vector3 at = eye + dir * 60 * res.Distance;
                        if (res.Distance > 0 && res.Distance < 1 && (at - res.Position).Length < 0.5f) sane++;
                        Console.WriteLine($"    ray {k * 45,3} deg: hit at {res.Distance * 60:0.00} u, position {Fmt(res.Position)} vs fraction point {Fmt(at)}");
                    }
                }
                Check(hits > 0 && sane == hits, $"CheckBetweenPoints: {hits}/8 horizontal rays hit a wall within 60 u, Distance is the fraction along the ray");

            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                fails++;
            }
            finally
            {
                PlayerEntity.HostShowMainBiped = false;
                PlayerEntity.HostOwnMainBiped = false;
                PlayerEntity.HostOwnMainAlt = false;
            }
            Console.WriteLine(fails == 0 ? "  ALL PASS" : $"  {fails} FAILED");
        }
    }
}
