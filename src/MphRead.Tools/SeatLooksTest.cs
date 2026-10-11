using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MphRead.Entities;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

namespace MphRead
{
    // Seat characters + skins in network matches (docs/CHARACTERS_AND_SKINS.md "Network matches"), for -lantest and
    // -onlinetest looks=1: the host's own seat wears a Samus suit, a joiner plays the Trooper in a trooper skin, and the
    // host's last bot is a Trooper in another skin (bots' skins are the host's, D4). Every device must end up with the same
    // character + skin per seat (LOOKS), every Trooper seat must have the Trooper's abilities and no other seat (ABILITIES n,
    // n = the Trooper seats in LOOKS: on in network matches since LAN protocol 9 / online 8, MatchSettings.Abilities),
    // and a JOIN from the previous protocol must get a REFUSE it can read (OLDJOIN refused).
    internal static class SeatLooksTest
    {
        public const string HostSkin = "MP1VariaSuit";
        public const string JoinerSkin = "MP1WaveTrooper";
        public const string BotSkin = "MP1PlasmaTrooper";
        public static Hunter JoinerHunter => Characters.Trooper.Base;
        public static string JoinerCharacter => Characters.Trooper.Id;

        // the host's template: seat 0 in a suit (when it's Samus), the last bot the Trooper
        public static void Dress(MatchSettings template)
        {
            if (template.Players[0].Hunter == Hunter.Samus)
            {
                template.Players[0].Skin = HostSkin;
            }
            MatchPlayer? bot = template.Players.LastOrDefault(p => p.Bot);
            if (bot != null)
            {
                bot.Hunter = Characters.Trooper.Base;
                bot.Character = Characters.Trooper.Id;
                bot.Skin = BotSkin;
            }
        }

        // "LOOKS Samus//MP1VariaSuit|Weavel/trooper/MP1WaveTrooper|..." (seat order)
        public static string Looks(MatchSettings s) => "LOOKS " + string.Join("|", s.Players.Select(p => $"{p.Hunter}/{p.Character}/{p.Skin}"));

        // after StartMatch: how many seats have the Trooper's Wall Cling, Scout Drone or Shield Wall on
        public static string Abilities(int seats) => "ABILITIES " + Enumerable.Range(0, seats).Count(i =>
            PlayerEntity.Players[i].HostWallCling || PlayerEntity.Players[i].HostScoutDrone || PlayerEntity.Players[i].HostShieldWall);

        // the ABILITIES line every device must show for these LOOKS: one per Trooper seat
        public static string AbilitiesExpected(string looks) => "ABILITIES " + looks["LOOKS ".Length..].Split('|')
            .Count(seat => seat.Split('/') is { Length: >= 2 } f && string.Equals(f[1], JoinerCharacter, StringComparison.OrdinalIgnoreCase));

        // an app on the previous LAN protocol (6) asks a LAN host for a seat
        public static string OldLanJoin(IPEndPoint host, string romKey)
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            socket.Client.ReceiveTimeout = 3000;
            byte[] join = OldJoin("MPHR", 6, w =>
            {
                w.Write(romKey);
                w.Write((byte)Hunter.Kanden);
                w.Write("old app");
                w.Write(MphRecomp.Replay.ReplayFile.CurrentSimVersion);
                w.Write((sbyte)-1);
            });
            socket.Send(join, join.Length, host);
            try
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                return "OLDJOIN " + Refusal(socket.Receive(ref from), "MPHR", 6);
            }
            catch (SocketException)
            {
                return "OLDJOIN no answer";
            }
        }

        // an app on the previous online protocol (1) asks the host (relay slot 0) for a seat
        public static string OldOnlineJoin(IDatagramTransport relay, string romKey)
        {
            string? answer = null;
            var got = new ManualResetEventSlim();
            DatagramHandler handler = (from, data) =>
            {
                if (from == 0 && !got.IsSet)
                {
                    answer = Refusal(data.ToArray(), "MPHO", 1);
                    got.Set();
                }
            };
            relay.Received += handler;
            try
            {
                byte[] join = OldJoin("MPHO", 1, w =>
                {
                    w.Write(romKey);
                    w.Write(MphRecomp.Replay.ReplayFile.CurrentSimVersion);
                    w.Write((byte)Hunter.Kanden);
                    w.Write("old app");
                    w.Write((sbyte)-1);
                });
                for (int i = 0; i < 5 && !got.IsSet; i++)
                {
                    relay.Send(0, join);
                    got.Wait(600);
                }
            }
            finally
            {
                relay.Received -= handler;
            }
            return "OLDJOIN " + (answer ?? "no answer");
        }

        private static byte[] OldJoin(string magic, byte protocol, Action<BinaryWriter> body)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((byte)2); // JOIN
            w.Write(Encoding.ASCII.GetBytes(magic));
            w.Write(protocol);
            body(w);
            return ms.ToArray();
        }

        // "refused: <reason>" when it's a REFUSE in the old app's own protocol (what that app reads), else what it was
        private static string Refusal(byte[] data, string magic, byte protocol)
        {
            try
            {
                var r = new BinaryReader(new MemoryStream(data));
                byte type = r.ReadByte();
                string m = Encoding.ASCII.GetString(r.ReadBytes(4));
                byte p = r.ReadByte();
                if (type != 4 || m != magic || p != protocol)
                {
                    return $"unreadable (type {type}, {m}, protocol {p})";
                }
                return "refused: " + r.ReadString();
            }
            catch (EndOfStreamException)
            {
                return $"short packet ({data.Length} bytes)";
            }
        }

        // the match setup line (MatchSetupText, local matches) keeps every seat's character + skin
        public static string? SetupLineProblem(MatchSettings s)
        {
            string line = MphRecomp.Frontend.MatchSetupText.Write(s);
            string back = Looks(MphRecomp.Frontend.MatchSetupText.Read(line));
            return back == Looks(s) ? null : $"setup line {line} reads back as {back}";
        }

        // the host's verdict on one device's lines
        public static string? Problem(string who, string hostLooks, string[] lines, bool oldJoin)
        {
            string? looks = lines.FirstOrDefault(l => l.StartsWith("LOOKS "));
            string? abilities = lines.FirstOrDefault(l => l.StartsWith("ABILITIES "));
            string? old = lines.FirstOrDefault(l => l.StartsWith("OLDJOIN "));
            if (looks != hostLooks) return $"{who} {looks ?? "no LOOKS line"} vs host {hostLooks}";
            if (abilities != AbilitiesExpected(hostLooks))
            {
                return $"{who} {abilities ?? "no ABILITIES line"} (expected {AbilitiesExpected(hostLooks)}: abilities on for each Trooper seat)";
            }
            if (oldJoin && (old == null || !old.StartsWith("OLDJOIN refused: "))) return $"{who} {old ?? "no OLDJOIN line"}";
            return null;
        }
    }
}
