using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using MphRead.Export;
using MphRead.Formats.Sound;

namespace MphRead
{
    internal static class Program
    {
        public static Version Version => AppInfo.Version;
        private static readonly Version _minExtractVersion = new Version(0, 19, 0, 0);

        private static void Main(string[] args)
        {
            ConsoleSetup.Run();
            if (args.Length >= 1 && args[0] == "-savetest") { SaveTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-configtest") { ConfigTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-simtest") { SimTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-modtest") { ModTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-inputtest") { InputTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-gametest") { GameTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-vfstest") { VfsTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-modmgrtest") { ModManagerTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-modapitest") { ModApiTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-exampletest") { ExampleModTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-roomloadtest") { RoomLoadTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-collisiontest") { CollisionTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-combattest") { CombatTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-mdl0test") { MdlColorTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-mdlrecon") { MdlRecon.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-mdltev") { MdlRecon.DumpTev(args); return; }
            if (args.Length >= 1 && args[0] == "-gxtest") { GxTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-gxevaltest") { GxEvalTest.Run(args); return; }
            if (args.Length >= 2 && args[0] == "-gxdump") { GxDump.Run(args); return; }
            if (args.Length >= 2 && args[0] == "-hdlook") { HdLook.Run(args); return; }
            if (args.Length >= 2 && args[0] == "-hdanim") { HdAnim.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-retropose") { RetroBodyPose.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-shieldtest") { ShieldWallTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-wallclingtest") { WallClingTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-wallclingprobe") { WallClingProbe.Run(args); return; } // T5 N10
            if (args.Length >= 1 && args[0] == "-trooperfixtest") { TrooperFixesTest.Run(args); return; } // T5
            if (args.Length >= 1 && args[0] == "-touchtest") { TouchTest.Run(args); return; } // T11
            if (args.Length >= 1 && args[0] == "-dronetest") { DroneTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-lookalikes") { LookAlikeTest.Run(args); return; } // D5 look-alikes (Look-alikes thread)
            if (args.Length >= 1 && args[0] == "-blasterpickup") { BlasterPickupTool.Run(args); return; } // Pirate Blaster pickup art
            if (args.Length >= 1 && args[0] == "-standinghittest") { StandingBodyHitTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-bodytest") { BodyTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-weavelcling") { WeavelClingTool.Run(args); return; } // Pirate Blaster + Trooper abilities online [7d1e5b]
            if (args.Length >= 1 && args[0] == "-seatabilitiestest") { SeatAbilitiesTest.Run(); return; } // Pirate Blaster + Trooper abilities online (cont.) [22406f]
            if (args.Length >= 1 && args[0] == "-pirateblastertest") { PirateBlasterTest.Run(args); return; } // Pirate Blaster + Trooper abilities online [e5e980]
            if (args.Length >= 1 && args[0] == "-placetest") { PlacementTest.Run(args); return; } // Pickup placement editor [e5e356]
            if (args.Length >= 1 && args[0] == "-noderefsnaptest") { NodeRefSnapTest.Run(args); return; } // joiner room part after a snap [cb9d6a]
            if (args.Length >= 1 && args[0] == "-darksamustest") { DarkSamusTest.Run(args); return; }   // Dark Samus's native bodies (D14 hold)
            if (args.Length >= 1 && args[0] == "-armcannontest") { ArmCannonTest.Run(args); return; }   // first-person arm cannons cut from the native bodies
            if (args.Length >= 1 && args[0] == "-dronefxtest") { DroneFxTest.Run(args); return; }   // the Scout Drone's Prime 1 shot effects (BodyFx + RetroParticles)
            if (args.Length >= 2 && args[0] == "-movieframes") { HdMovieFrames.Run(args); return; }
            if (args.Length >= 2 && args[0] == "-hdrepose") { HdRepose.Run(args); return; }
            if (args.Length >= 2 && args[0] == "-riglook") { RigLook.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-animprobe") { AnimProbe.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-animtest") { AnimTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-hdpose") { HdPoseProbe.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-hdrig") { HdRigTool.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-hdjoints") { HdRigTool.Joints(args); return; }
            if (args.Length >= 1 && args[0] == "-dsdump") { HdRigTool.DsDump(args); return; }
            if (args.Length >= 1 && args[0] == "-rigstudio") { RigStudio.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-posestudio") { PoseStudio.Run(args); return; }
            if (args.Length >= 2 && args[0] == "-hdball") { HdBallLook.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-dsview") { DsView.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-rigscan") { RigScan.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignmap") { CampaignMap.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignsim") { CampaignSim.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignsweep") { CampaignSim.Sweep(args); return; }
            if (args.Length >= 1 && args[0] == "-campaigndraw") { CampaignSim.Draw(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignui") { CampaignSim.Ui(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignscan") { CampaignScanTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignencounters") { CampaignSim.Encounters(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignsuit") { CampaignSim.Suit(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignportal") { CampaignSim.Portal(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignlock") { CampaignSim.LockReplay(args); return; }
            if (args.Length >= 1 && args[0] == "-fxscan") { CampaignSim.FxScan(args); return; }
            if (args.Length >= 1 && args[0] == "-fxpool") { CampaignSim.FxPool(args); return; }
            if (args.Length >= 1 && args[0] == "-bossfx") { CampaignSim.BossFx(args); return; }
            if (args.Length >= 1 && args[0] == "-roomentry") { CampaignSim.RoomEntry(args); return; }
            if (args.Length >= 1 && args[0] == "-fxobjects") { CampaignSim.FxObjects(args); return; }
            if (args.Length >= 1 && args[0] == "-escapemusic") { CampaignSim.EscapeMusic(args); return; }
            if (args.Length >= 1 && args[0] == "-bossskip") { CampaignSim.BossSkipRun(args); return; }
            if (args.Length >= 1 && args[0] == "-endingtest") { CampaignSim.EndingTest(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignspawn") { CampaignSim.SpawnTiming(args); return; }
            if (args.Length >= 1 && args[0] == "-landingcam") { CampaignSim.LandingCam(args); return; }
            if (args.Length >= 1 && args[0] == "-suitspec") { CampaignSuitSpec.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-landingperf") { CampaignSim.LandingPerf(args); return; }
            if (args.Length >= 1 && args[0] == "-suitrow") { CampaignSim.SuitRow(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignhud") { CampaignSim.Hud(args); return; }
            if (args.Length >= 1 && args[0] == "-huddialog") { CampaignSim.HudDialogs(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignship") { CampaignShipTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-gyrotest") { GyroTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-viewtest") { ViewTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-accuracy") { AccuracyTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-matchsim") { MatchSim.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-matchsweep") { MatchSim.Sweep(args); return; }
            if (args.Length >= 1 && args[0] == "-matcharenas") { MatchSim.Arenas(args); return; }
            if (args.Length >= 1 && args[0] == "-p8scoreboard") { P8ScoreboardTool.Run(args); return; } // Netcode P8: 5-8 row scoreboard pictures
            if (args.Length >= 1 && args[0] == "-introbanner") { IntroBannerTool.Run(args); return; } // F4: Echoes intro banner picture
            if (args.Length >= 1 && args[0] == "-blastersfxlevels") { BlasterSfxLevels.Run(args); return; } // F4: blaster hit sound level
            if (args.Length >= 1 && args[0] == "-replaytest") { ReplayTest.Test(args); return; }
            if (args.Length >= 1 && args[0] == "-replayplay") { ReplayTest.PlayFile(args); return; }
            if (args.Length >= 1 && args[0] == "-replaynetdump") { ReplayNetDump.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-replaymock") { ReplayMockTool.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-replaysweep") { ReplayTest.Sweep(args); return; }
            if (args.Length >= 1 && args[0] == "-drawinvariance") { ReplayTest.DrawInvariance(args); return; }
            if (args.Length >= 1 && args[0] == "-replaygolden") { ReplayTest.Golden(args); return; }
            if (args.Length >= 1 && args[0] == "-detmathtest") { DetMathTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-replayverify") { ReplayTest.Verify(args); return; }
            if (args.Length >= 1 && args[0] == "-replaynettest") { ReplayTest.NetTest(args); return; }
            if (args.Length >= 1 && args[0] == "-replaysnaptest") { ReplayTest.SnapTest(args); return; } // T9 seek checkpoints
            if (args.Length >= 1 && args[0] == "-echoesarena") { EchoesArenaTool.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-netloop") { MatchNetLoop.Host(args); return; }
            if (args.Length >= 1 && args[0] == "-netclient") { MatchNetLoop.Client(args); return; }
            if (args.Length >= 1 && args[0] == "-lantest") { MatchNetLoop.LanTest(args); return; }
            if (args.Length >= 1 && args[0] == "-lanpausetest") { LanPauseTest.Run(); return; } // (host pause keeps a LAN match, 2026-10-11)
            if (args.Length >= 1 && args[0] == "-lanagaintest") { LanAgainTest.Run(); return; } // (host + join twice in one process, release 0.2.0)
            if (args.Length >= 1 && args[0] == "-lanjoin") { MatchNetLoop.LanJoin(args); return; }
            if (args.Length >= 1 && args[0] == "-onlinetest") { OnlineMatchTool.Test(args); return; }
            if (args.Length >= 1 && args[0] == "-netlobbytest") { NetLobbyTest.Run(args); return; } // Online play menus: lobby protocol 4, no ROM
            // ONLINE PLAY's menus (Online play menus thread, OnlineMenuTool.cs): sample renders, the live join test, a PC host
            if (args.Length >= 1 && args[0] == "-onlinemenu") { OnlineMenuTool.Preview(args); return; }
            if (args.Length >= 1 && args[0] == "-footertaptest") { FooterTapTool.Test(args); return; } // Options footer links [e051ce]
            if (args.Length >= 1 && args[0] == "-chattest") { ChatTool.Test(args); return; } // (Social O7: ChatTool.cs)
            if (args.Length >= 1 && args[0] == "-onlinemenutest") { OnlineMenuTool.Test(args); return; }
            if (args.Length >= 1 && args[0] == "-onlinehostleaveagain") { OnlineHostLeaveTool.Test(args); return; } // (release 0.2.0 F1)
            if (args.Length >= 1 && args[0] == "-onlinehost") { OnlineMenuTool.Host(args); return; }
            if (args.Length >= 1 && args[0] == "-onlinecreatetest") { OnlineMenuTool.CreateTest(args); return; }
            if (args.Length >= 1 && args[0] == "-migratemenutest") { MigrateMenuTool.Test(args); return; }
            if (args.Length >= 1 && args[0] == "-onlinefriendstest") { OnlineMenuTool.FriendsTest(args); return; }
            if (args.Length >= 1 && args[0] == "-onlinerecordtest") { OnlineRecordTool.Test(args); return; } // (T3: head to head on the server)
            if (args.Length >= 1 && args[0] == "-onlinejoin") { OnlineMatchTool.Join(args); return; }
            if (args.Length >= 1 && args[0] == "-turntest") { TurnTestServer.Test(); return; } // O5 TURN client vs a local TURN server
            if (args.Length >= 1 && args[0] == "-voicetest") { VoiceTest.Run(args); return; } // O8 voice chat: codec, jitter, policy, PTT
            // FIND GAME (Social track O6, FindGameTool.cs): sample renders, four devices through the queue, the menus' search
            if (args.Length >= 1 && args[0] == "-findgamemenu") { FindGameTool.Preview(args); return; }
            if (args.Length >= 1 && args[0] == "-findgametest") { FindGameTool.Test(args); return; }
            if (args.Length >= 1 && args[0] == "-findgamejoin") { FindGameTool.Join(args); return; }
            if (args.Length >= 1 && args[0] == "-findgamemenutest") { FindGameTool.MenuTest(args); return; }
            if (args.Length >= 3 && args[0] == "-campaigndoorprobe") { CampaignDoorProbe.Run(args); return; }
            if (args.Length >= 2 && args[0] == "-campaignmsgscan") { CampaignDoorProbe.MessageScan(args); return; }
            if (args.Length >= 3 && args[0] == "-campaigndoorcheck") { CampaignDoorProbe.DoorCheck(args); return; }
            if (args.Length >= 1 && args[0] == "-movietest") { MovieTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-audiolevels") { AudioLevels.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-guntest") { GunTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-importcheck") { ImportCheck.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-importedcontenttest") { Environment.Exit(ImportedContentTest.Run(args)); }   // ship-safe gate (docs/SHIP_SAFE_AUDIT_2026-10-10.md)
            if (args.Length >= 1 && args[0] == "-recipecheck") { Environment.Exit(RecipeCheck.Run(args)); }   // recipe lint (board S16)
            if (args.Length >= 1 && args[0] == "-importeditions") { Environment.Exit(ImportEditions.Run(args)); }   // a game from another disc (Import/Editions.cs)
            if (args.Length >= 2 && args[0] == "-feprobe") { FrontendProbe.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-fedump") { FrontendTool.Dump(args); return; }
            if (args.Length >= 2 && args[0] == "-ferender") { FrontendTool.Render(args); return; }
            if (args.Length >= 2 && args[0] == "-fecanvas") { FrontendTool.Canvas(args); return; }
            if (args.Length >= 1 && args[0] == "-feflow") { FrontendTool.Flow(args); return; }
            if (args.Length >= 1 && args[0] == "-felayouts") { FrontendTool.Layouts(); return; }
            if (args.Length >= 1 && args[0] == "-mpmenu") { MultiplayerMenuTool.Run(args); return; } // Vanilla multiplayer menu
            if (args.Length >= 1 && args[0] == "-extrarules") { ExtraRulesTest.Run(args); return; } // Extra pickups + Extra characters rules
            if (args.Length >= 1 && args[0] == "-pickertest") { PickerTest.Run(args); return; } // Character + skin picker (D9)
            if (args.Length >= 2 && args[0] == "-fesaves") { FrontendTool.Saves(args); return; }
            if (args.Length >= 1 && args[0] == "-feship") { FrontendTool.Ship(args); return; }
            if (args.Length >= 1 && args[0] == "-feshipweapon") { ShipWeaponTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-fecockpitjets") { CockpitJetsTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-fegrooves") { FrontendGrooves.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-femenusamus") { MenuSamusTool.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-febrief") { FrontendTool.Brief(args); return; }
            if (args.Length >= 1 && args[0] == "-feplanet") { FrontendTool.Planet(args); return; }
            if (args.Length >= 1 && args[0] == "-feredraw") { FrontendTool.Redraw(args); return; }
            if (args.Length >= 1 && args[0] == "-fecockpit") { FrontendTool.Cockpit(args); return; }
            if (args.Length >= 1 && args[0] == "-femap") { MapTool.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-femaptrack") { CampaignSim.MapTrack(args); return; }
            if (args.Length >= 2 && args[0] == "-sndtrace") { SoundTrace.Run(args); return; }
            if (args.Length >= 2 && args[0] == "-sndpages") { SoundTrace.Pages(args); return; }
            if (args.Length >= 1 && args[0] == "-sndecho") { SoundTrace.Echo(args); return; }
            if (CheckSetup(args))
            {
                return;
            }
            IReadOnlyList<Argument> arguments = ParseArguments(args);
            if (arguments.Count == 0)
            {
                //using var renderer = new RenderWindow();
                //renderer.AddRoom("MP3 PROVING GROUND");
                //renderer.AddModel("Crate01");
                //renderer.Run();
                Menu.ShowMenuPrompts();
            }
            else if (arguments.Any(a => a.Name == "setup"))
            {
                foreach (string path in Directory.EnumerateFiles(Paths.Combine(Paths.FileSystem, "archives")))
                {
                    Read.ExtractArchive(Path.GetFileNameWithoutExtension(path));
                }
            }
            else if (TryGetString(arguments, "export", "e", out string? exportValue))
            {
                if (exportValue.ToLower() == "layer2d")
                {
                    Images.ExportHudLayers();
                }
                else if (exportValue.ToLower() == "object2d")
                {
                    Images.ExportHudObjects();
                }
                else if (exportValue.ToLower() == "sfx")
                {
                    SoundRead.ExportSamples();
                }
                else if (exportValue.ToLower() == "wfs")
                {
                    SoundRead.ExportWfsSamples();
                }
                else if (exportValue.ToLower() == "strm")
                {
                    SoundRead.ExportStreams();
                }
                else if (exportValue.ToLower() == "fhsfx")
                {
                    SoundRead.ExportAllFh();
                }
                else if (exportValue.ToLower() == "movie")
                {
                    TryGetArgument(arguments, "export", "e", out Argument? exportArgument);
                    if (exportArgument!.Value.ValueTwo != null)
                    {
                        Formats.VxDecoder.Instance1.Export(exportArgument!.Value.ValueTwo).GetAwaiter().GetResult();
                    }
                    else
                    {
                        Formats.VxDecoder.Instance1.ExportAll().GetAwaiter().GetResult();
                    }
                }
                else
                {
                    bool firstHunt = arguments.Any(a => a.Name == "fh");
                    Read.ReadAndExport(exportValue, firstHunt);
                }
            }
            else if (TryGetString(arguments, "extract", "x", out string? extractValue))
            {
                Read.ExtractArchive(extractValue);
            }
            else
            {
                var rooms = new List<string>();
                var models = new List<(string, int)>();
                GameMode mode = GameMode.None;
                int playerCount = 0;
                BossFlags bossFlags = BossFlags.None;
                int nodeLayerMask = 0;
                int entityLayerId = -1;
                if (TryGetInt(arguments, "room", "r", out int roomId))
                {
                    RoomMetadata? meta = Metadata.GetRoomById(roomId);
                    if (meta == null)
                    {
                        Exit();
                    }
                    rooms.Add(meta.Name);
                }
                else if (TryGetString(arguments, "room", "r", out string? roomName))
                {
                    rooms.Add(roomName);
                }
                if (TryGetInt(arguments, "mode", "g", out int modeValue))
                {
                    mode = (GameMode)modeValue;
                }
                if (TryGetInt(arguments, "players", "p", out int playerValue))
                {
                    playerCount = playerValue;
                }
                if (TryGetInt(arguments, "boss", "b", out int bossValue))
                {
                    bossFlags = (BossFlags)bossValue;
                }
                if (TryGetInt(arguments, "node", "n", out int nodeValue))
                {
                    nodeLayerMask = nodeValue;
                }
                if (TryGetInt(arguments, "entity", "l", out int entityValue))
                {
                    entityLayerId = entityValue;
                }
                foreach ((string, int) pair in GetPairs(arguments, "model", "m"))
                {
                    models.Add(pair);
                }
                if (rooms.Count > 1 || (rooms.Count == 0 && models.Count == 0))
                {
                    Exit();
                }
                using var renderer = new RenderWindow();
                foreach (string room in rooms)
                {
                    renderer.AddRoom(room, mode, playerCount, bossFlags, nodeLayerMask, entityLayerId);
                }
                bool firstHunt = arguments.Any(a => a.Name == "fh");
                foreach ((string model, int recolor) in models)
                {
                    renderer.AddModel(model, recolor, firstHunt);
                }
                renderer.Run();
            }
        }

        private static bool CheckSetup(string[] args)
        {
            if (File.Exists("paths.txt") && !CheckVersion())
            {
                Console.WriteLine("Your paths.txt file is not compatible with this version of MphRead and needs to be recreated.");
                Console.WriteLine("It is recommended that you delete the file as well as any extracted game files, " +
                    "then perform setup again.");
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                return true;
            }
            if (args.Length == 1 && !args[0].StartsWith('-') && File.Exists(args[0]))
            {
                Extract.Setup(args[0]);
                return true;
            }
            if (!File.Exists("paths.txt"))
            {
                Console.WriteLine("Could not find the paths.txt file.");
                Console.WriteLine("You may need to perform first-time setup by dragging a ROM onto the MphRead executable.");
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                return true;
            }
            Paths.UpdatePaths();
            Paths.ChooseMphPath();
            Paths.ChooseFhPath();
            return false;
        }

        private static bool CheckVersion()
        {
            string text = File.ReadAllText("paths.txt").Split('\n')[0].Trim();
            if (Version.TryParse(text, out Version? extractVersion))
            {
                return extractVersion >= _minExtractVersion;
            }
            return false;
        }

        private readonly struct Argument
        {
            public readonly string Name;
            public readonly string? ValueOne;
            public readonly string? ValueTwo;

            public Argument(string name, string? valueOne, string? valueTwo = null)
            {
                Name = name;
                ValueOne = valueOne;
                ValueTwo = valueTwo;
            }
        }

        private static IEnumerable<(string, int)> GetPairs(IEnumerable<Argument> arguments, string fullName, string shortName)
        {
            foreach (Argument argument in arguments.Where(a => a.Name == fullName || a.Name == shortName))
            {
                if (argument.ValueOne != null)
                {
                    Int32.TryParse(argument.ValueTwo, out int valueTwo);
                    yield return (argument.ValueOne, valueTwo);
                }
            }
        }

        private static bool TryGetArgument(IEnumerable<Argument> arguments, string fullName, string shortName,
            [NotNullWhen(true)] out Argument? argument)
        {
            IEnumerable<Argument> matches = arguments.Where(a => a.Name == fullName || a.Name == shortName);
            if (matches.Any())
            {
                argument = matches.First();
                return true;
            }
            argument = null;
            return false;
        }

        private static bool TryGetString(IEnumerable<Argument> arguments, string fullName, string shortName,
            [NotNullWhen(true)] out string? value)
        {
            if (TryGetArgument(arguments, fullName, shortName, out Argument? argument) && argument.Value.ValueOne != null)
            {
                value = argument.Value.ValueOne;
                return true;
            }
            value = null;
            return false;
        }

        private static bool TryGetInt(IEnumerable<Argument> arguments, string fullName, string shortName,
            out int value)
        {
            if (TryGetString(arguments, fullName, shortName, out string? stringValue))
            {
                if (Int32.TryParse(stringValue, out int intValue))
                {
                    value = intValue;
                    return true;
                }
            }
            value = 0;
            return false;
        }

        private static IReadOnlyList<Argument> ParseArguments(string[] args)
        {
            var arguments = new List<Argument>();
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        arg = arg[1..];
                        if (i == args.Length - 1)
                        {
                            arguments.Add(new Argument(arg, null));
                        }
                        else
                        {
                            string valueOne = args[i + 1];
                            if (valueOne.StartsWith('-'))
                            {
                                arguments.Add(new Argument(arg, null));
                            }
                            else
                            {
                                string? valueTwo = null;
                                if (i < args.Length - 2 && !args[i + 2].StartsWith('-'))
                                {
                                    valueTwo = args[i + 2];
                                    i++;
                                }
                                arguments.Add(new Argument(arg, valueOne, valueTwo));
                                i++;
                            }
                        }
                    }
                }
            }
            return arguments;
        }

        [DoesNotReturn]
        private static void Exit()
        {
            Nop();
            Console.WriteLine("MphRead usage:");
            Console.WriteLine("    -room <room_name -or- room_id>");
            Console.WriteLine("    -model <model_name> [recolor_index]");
            Console.WriteLine("At most one room may be specified. Any number of models may be specified.");
            Console.WriteLine("To load First Hunt models, include -fh in the argument list.");
            Console.WriteLine("Available room options: -mode, -players, -boss, -node, -entity");
            Console.WriteLine("- or -");
            Console.WriteLine("    -extract <archive_path>");
            Console.WriteLine("If the target archive is LZ10-compressed, it will be decompressed.");
            Console.WriteLine("- or -");
            Console.WriteLine("    -export <target_name>");
            Console.WriteLine("The export target may be a model or room name.");
            Environment.Exit(1);
        }

        private static void Nop() { }
    }
}
