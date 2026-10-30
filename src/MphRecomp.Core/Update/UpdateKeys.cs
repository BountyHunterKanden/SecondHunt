using System.Collections.Generic;

// The public keys the update check trusts (SubjectPublicKeyInfo DER, base64). Release: the owner's update key; its
// private half never leaves the owner's machine (docs/RELEASE.md, "Update file"). Test: a throwaway key for dev builds
// and the release tool's tests, never trusted by a public build.
namespace MphRecomp.Update
{
    public static class UpdateKeys
    {
        // key id 790d77d81c6d21dc (owner's update key, made 2026-10-06)
        public const string Release = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEQMIvOlY8du9jrHoWZVDrFsgT042K8ESigWoJRUG3R/v8Y4dnc1PRUFmIapC9crEDH5FsYsC+l0Z5Simy+eB5PA==";

        // key id 0997abc9791dbd72
        public const string Test = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXWdYMDetRRcNTOjXC+P/D35kQUx6jPK4VvELsO7bUtY3Wyof0LXU0ll0oZl8x+N4Qc8t6EaNot/YFG6dajhDPQ==";

        public static IReadOnlyList<string> Trusted(bool publicBuild) => publicBuild ? new[] { Release } : new[] { Release, Test };
    }
}
