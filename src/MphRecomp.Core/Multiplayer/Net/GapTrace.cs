using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using MphRead;
using MphRead.Entities;

namespace MphRecomp.Multiplayer.Net
{
    // The served-match gap test's recorder (docs/FRUITY_PORT.md Step 2a; PC only): with GAPTRACE=<folder> set, each process
    // of a PC served match (the match host, each joiner) writes every step's player positions to <folder>/<role>.txt, stamped
    // with the machine's steady clock (Stopwatch: one clock for every process on the PC). OnlineMatchTool's GapCheck then
    // asks: is where the server has a seat, and where the other device draws it, a place that seat's own device really had
    // it a moment before? Unset (every device, the real server) = nothing written.
    public sealed class GapTrace : IDisposable
    {
        private readonly StreamWriter _out;
        private readonly StringBuilder _line = new();

        public static string? Folder => Environment.GetEnvironmentVariable("GAPTRACE") is { Length: > 0 } dir ? dir : null;

        private GapTrace(string path)
        {
            _out = new StreamWriter(path, append: false, Encoding.ASCII) { AutoFlush = false };
        }

        // role: "host" or "j<index>"; null when the test isn't on
        public static GapTrace? Open(string role)
        {
            if (Folder is not string dir) return null;
            Directory.CreateDirectory(dir);
            return new GapTrace(Path.Combine(dir, role + ".txt"));
        }

        public static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        // one step: every active seat; kind O = this device's own player, D = drawn here (another device's), S = the server's
        public void Step(long frame, int local)
        {
            double now = NowMs;
            for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                if (!p.LoadFlags.TestFlag(LoadFlags.Active)) continue;
                char kind = local < 0 ? 'S' : i == local ? 'O' : 'D';
                bool alive = p.Health > 0 && p.LoadFlags.TestFlag(LoadFlags.Spawned);
                _line.Clear();
                _line.Append(now.ToString("0.000", CultureInfo.InvariantCulture)).Append(' ').Append(frame).Append(' ')
                    .Append(kind).Append(' ').Append(i).Append(' ')
                    .Append(p.Position.X.ToString("0.0000", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(p.Position.Y.ToString("0.0000", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(p.Position.Z.ToString("0.0000", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(alive ? '1' : '0').Append(' ').Append(p.IsBot ? '1' : '0');
                _out.WriteLine(_line);
            }
        }

        public void Dispose() => _out.Dispose();
    }
}
