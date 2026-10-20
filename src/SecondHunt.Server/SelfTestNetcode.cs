using System;
using System.Collections.Generic;

namespace SecondHunt.Server
{
    // 0.3.6 netcode switches (HubServed.cs ServedConfig): the server's --served-* values become each served match host's
    // arguments (SecondHunt.MatchHost Program.Netcode.cs); none given = none passed (an older match host knows none of them)
    public static partial class SelfTest
    {
        private static void NetcodeTests()
        {
            string? Arg(string[] a, string name)
            {
                int i = Array.IndexOf(a, name);
                return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
            }
            ServedConfig none = ServedConfig.FromArgs(Array.Empty<string>(), Arg);
            Check(none.NetcodeArgs().Count == 0, "netcode: no --served-* values -> no match host arguments (its defaults)");
            ServedConfig back = ServedConfig.FromArgs(new[] { "--served-snapshot-hz", "30", "--served-client-position", "off",
                "--served-position-limit", "2.5" }, Arg);
            List<string> args = back.NetcodeArgs();
            Check(String.Join(" ", args) == "--snapshot-hz 30 --client-position off --position-limit 2.5",
                $"netcode: 30 Hz, client positions off, limit 2.5 -> the match host's arguments ({String.Join(" ", args)})");
            ServedConfig fr = ServedConfig.FromArgs(new[] { "--served-fruity-pin", "off", "--served-catchup-cap", "0" }, Arg);
            Check(String.Join(" ", fr.NetcodeArgs()) == "--fruity-pin off --catchup-cap 0", "netcode: Fruity switches -> the match host's arguments");
            ServedConfig on = ServedConfig.FromArgs(new[] { "--served-client-position", "on" }, Arg);
            Check(String.Join(" ", on.NetcodeArgs()) == "--client-position on", "netcode: only what was set is passed");
            bool refused = false;
            try
            {
                ServedConfig.FromArgs(new[] { "--served-snapshot-hz", "45" }, Arg);
            }
            catch (ArgumentException)
            {
                refused = true;
            }
            Check(refused, "netcode: --served-snapshot-hz 45 is refused (60, 30 or 20)");
        }
    }
}
