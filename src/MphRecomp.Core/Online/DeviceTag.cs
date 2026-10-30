using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MphRecomp.Online
{
    // A scrambled ID of this device, sent with the login (HelloMsg.Device) so a ban also holds after the player clears the
    // app's data or reinstalls (a new key on a banned device is refused). Only a SHA-256 of the system's own ID leaves the
    // device, and the server keeps it only for a banned profile (docs/PRIVACY_NOTICE.md).
    //   Android: Settings.Secure.ANDROID_ID (per app signing key; survives reinstalls, changes on a factory reset), set by
    //            the shell (MphApp.OnCreate) because Core can't reach a Context.
    //   Windows: HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid (made when Windows was installed).
    //   Linux:   /etc/machine-id.
    // A determined player can still change these (root, a registry edit, a modified build): this stops the easy way round.
    public static class DeviceTag
    {
        private static string? _current;
        private static bool _looked;

        // "" when there's no ID (the server then checks the key only)
        public static string Current
        {
            get
            {
                if (_current == null && !_looked)
                {
                    _looked = true;
                    _current = Hash(ReadSystemId());
                }
                return _current ?? "";
            }
        }

        // the Android shell hands over ANDROID_ID at start-up
        public static void SetRawId(string? raw)
        {
            _looked = true;
            _current = Hash(raw);
        }

        public static string? Hash(string? raw)
        {
            raw = raw?.Trim().ToLowerInvariant();
            if (String.IsNullOrEmpty(raw) || raw.Trim('0', '-').Length == 0)
            {
                return null; // no ID, or an all-zero placeholder every such device shares
            }
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("SecondHunt device v1|" + raw));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string? ReadSystemId()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                        Microsoft.Win32.RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                    return key?.GetValue("MachineGuid") as string;
                }
                if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
                {
                    return File.Exists("/etc/machine-id") ? File.ReadAllText("/etc/machine-id") : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
            return null;
        }
    }
}
