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
            if (args.Length >= 1 && args[0] == "-campaignspawn") { CampaignSim.SpawnTiming(args); return; }
            if (args.Length >= 1 && args[0] == "-landingcam") { CampaignSim.LandingCam(args); return; }
            if (args.Length >= 1 && args[0] == "-suitspec") { CampaignSuitSpec.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-landingperf") { CampaignSim.LandingPerf(args); return; }
            if (args.Length >= 1 && args[0] == "-suitrow") { CampaignSim.SuitRow(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignhud") { CampaignSim.Hud(args); return; }
            if (args.Length >= 1 && args[0] == "-campaignship") { CampaignShipTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-gyrotest") { GyroTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-viewtest") { ViewTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-accuracy") { AccuracyTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-matchsim") { MatchSim.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-matchsweep") { MatchSim.Sweep(args); return; }
            if (args.Length >= 1 && args[0] == "-matcharenas") { MatchSim.Arenas(args); return; }
            if (args.Length >= 1 && args[0] == "-echoesarena") { EchoesArenaTool.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-netloop") { MatchNetLoop.Host(args); return; }
            if (args.Length >= 1 && args[0] == "-netclient") { MatchNetLoop.Client(args); return; }
            if (args.Length >= 1 && args[0] == "-lantest") { MatchNetLoop.LanTest(args); return; }
            if (args.Length >= 1 && args[0] == "-lanjoin") { MatchNetLoop.LanJoin(args); return; }
            if (args.Length >= 3 && args[0] == "-campaigndoorprobe") { CampaignDoorProbe.Run(args); return; }
            if (args.Length >= 2 && args[0] == "-campaignmsgscan") { CampaignDoorProbe.MessageScan(args); return; }
            if (args.Length >= 3 && args[0] == "-campaigndoorcheck") { CampaignDoorProbe.DoorCheck(args); return; }
            if (args.Length >= 1 && args[0] == "-movietest") { MovieTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-audiolevels") { AudioLevels.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-guntest") { GunTest.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-importcheck") { ImportCheck.Run(args); return; }
            if (args.Length >= 2 && args[0] == "-feprobe") { FrontendProbe.Run(args); return; }
            if (args.Length >= 1 && args[0] == "-fedump") { FrontendTool.Dump(args); return; }
            if (args.Length >= 2 && args[0] == "-ferender") { FrontendTool.Render(args); return; }
            if (args.Length >= 2 && args[0] == "-fecanvas") { FrontendTool.Canvas(args); return; }
            if (args.Length >= 1 && args[0] == "-feflow") { FrontendTool.Flow(args); return; }
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
