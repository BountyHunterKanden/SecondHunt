using System.Collections.Generic;

namespace MphRead
{
    // recomp (owner 2026-11-15, wave 2: the custom HUD editor): which recorded HUD items belong to which HUD element
    // (energy, ammo, the reticle...), so a host can move, size, fade or hide each one (MphRecomp.App/Hud). PlayerHud
    // tags its draws with HostHudTag; a tag runs until the next one or a null. Only records item ranges: nothing in
    // the simulation reads them, and a host that never sets HostHudElementsOn records none.
    public partial class Scene
    {
        public struct HostHudElementRange
        {
            public int Start; // first HUD item (GetHudItem)
            public int End;   // one past the last
            public string Id;
        }

        public static bool HostHudElementsOn { get; set; }

        private readonly List<HostHudElementRange> _hudElementRanges = new();
        private string? _hudElementOpen;
        private int _hudElementStart;

        public int HudElementRangeCount => _hudElementRanges.Count;
        public HostHudElementRange GetHudElementRange(int index) => _hudElementRanges[index];

        // RecordHud's start: a new step's HUD
        private void HostHudElementsReset()
        {
            _hudElementRanges.Clear();
            _hudElementOpen = null;
        }

        // the items recorded from here on belong to element `id` (null: to none)
        public void HostHudTag(string? id)
        {
            if (!HostHudElementsOn)
            {
                return;
            }
            if (_hudElementOpen != null && _hudItemCount > _hudElementStart)
            {
                _hudElementRanges.Add(new HostHudElementRange { Start = _hudElementStart, End = _hudItemCount, Id = _hudElementOpen });
            }
            _hudElementOpen = id;
            _hudElementStart = _hudItemCount;
        }
    }
}
