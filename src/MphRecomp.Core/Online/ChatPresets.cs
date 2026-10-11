using System;

namespace MphRecomp.Online
{
    // The quick messages (docs/ONLINE_PLAY.md section 8, Social O7): chat that needs no typing and no filter. A message on the
    // wire is an index into All, so every language and every build shows its own words, and the list may only grow at the end.
    public static class ChatPresets
    {
        public static readonly string[] All =
        {
            "GG", "Nice shot!", "Well played!", "Rematch?", "One more!", "Ready!", "Wait for me", "Thanks!",
            "Sorry!", "Pick an arena", "Good luck!", "Hello!", "Follow me", "Watch out!", "Oops", "I'm back",
            "Need a break", "Help!",
        };

        // the eight on the in-match wheel (indexes into All), clockwise from the top
        public static readonly int[] Wheel = { 0, 1, 3, 4, 11, 13, 7, 8 };

        public static bool Valid(int index) => index >= 0 && index < All.Length;

        public static string Text(int index) => Valid(index) ? All[index] : "";
    }
}
