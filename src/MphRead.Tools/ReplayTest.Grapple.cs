using System;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Replay;

namespace MphRead
{
    // -replaygrapple <file> [seat]: plays a replay and prints the seat's grapple through it -- every state change, and each
    // scan press with every point as the pick / the beam's lock see it (PlayerEntity.HostGrappleWhy)
    internal static partial class ReplayTest
    {
        public static void GrappleTrace(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: -replaygrapple <file> [seat] [echoes disc: its points, when the PC arena predates G0]");
                return;
            }
            int seat = args.Length > 2 ? Int32.Parse(args[2]) : -1;
            if (!Setup())
            {
                return;
            }
            ReplayFile file = ReplayFile.Load(args[1]);
            if (seat < 0)
            {
                seat = file.Settings.LocalSeat;
            }
            if (args.Length > 3)
            {
                EchoesArena.Register(Paths.FileSystem);
                EchoesArena.Def def = EchoesArena.Find(file.Settings.Arena) ?? throw new InvalidOperationException($"not an Echoes arena: {file.Settings.Arena}");
                EchoesGrapple.SetRoomSpots(def.Id, GrappleDumpTool.DiscSpots(def, args[3]).ToArray());
            }
            Console.WriteLine($"{file.Settings.Mode} in {file.Settings.Arena}, {file.Steps.Count} steps, seat {seat}");
            PlayerEntity.GrappleState last = PlayerEntity.GrappleState.None;
            bool scanPrev = false;
            _beforeStep = host =>
            {
                PlayerEntity p = PlayerEntity.Players[seat];
                string t = $"f{host.Frame,6} ({host.Frame / 60f,6:0.00}s)";
                if (p.Grapple != last)
                {
                    Console.WriteLine($"{t} {last} -> {p.Grapple} pos {p.Position} tip {p.GrappleTip}{p.HostGrappleWhy()}");
                    last = p.Grapple;
                }
                bool scan = p.Controls.Scan.IsDown;
                if (scan && !scanPrev)
                {
                    Console.WriteLine($"{t} SCAN press, state {p.Grapple}, pos {p.Position}, facing {p.FacingVector}{p.HostGrappleWhy()}");
                }
                scanPrev = scan;
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
