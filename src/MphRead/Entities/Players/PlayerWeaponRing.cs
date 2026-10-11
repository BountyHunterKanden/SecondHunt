using System;
using MphRead.Hud;

namespace MphRead.Entities
{
    // recomp: the weapon ring (owner queue #9, 2026-10-03; built 2026-10-09). The DS lays the six weapons along one curved
    // line in the top-right corner, and the stick's six directions didn't match where each one sat. With the ring on, the
    // same six slots (box + icon, the ROM's own sprites) sit on a full circle in the middle of the screen, each in the
    // direction the right stick points to pick it (MphRecomp CampaignHost.WheelSector: clockwise from straight up, Volt
    // Driver, Battlehammer, Imperialist, Judicator, Magmaul, Shock Coil). Draw-only: the pick is still MphRead's own
    // (UpdateWeaponSelect from the host's pointer), so play and replays don't change.
    // With the Pirate Blaster on (WeaponSlotCount 7, owner 2026-10-09) there are seven ~51-degree slots, turned so the
    // blaster and the Volt Driver straddle straight up and the Judicator sits straight down (PlayerPirateBlaster.cs).
    public partial class PlayerEntity
    {
        // set by the app while its sim step runs (CampaignRenderer.SetPlayerHooks); off = MphRead's own corner arc
        public static bool HostWeaponRing { get; set; }

        private const float _weaponRingRadius = 60; // DS pixels from the screen's middle to each slot's middle

        private void DrawWeaponRing()
        {
            _scene.HostHudDialog(true); // a centred 4:3 box: DS pixels stay square, so the ring is round on a wide screen
            int slots = WeaponSlotCount;
            for (int i = 0; i < slots; i++)
            {
                float angle = slots == 7 ? (i + 0.5f) * 2 * MathF.PI / 7 : i * MathF.PI / 3;
                float x = 128 + _weaponRingRadius * MathF.Sin(angle);
                float y = 96 - _weaponRingRadius * MathF.Cos(angle);
                DrawRingSlot(_selectBoxInsts[i], x, y);
                DrawRingSlot(_weaponSelectInsts[i], x, y);
            }
            DrawRingOmega();
            _scene.HostHudDialog(false);
        }

        // recomp (T4 N15, owner 2026-10-10: "How does Omega cannet get selected again"): while the Omega Cannon sits in the
        // third slot (vanilla keeps it there while you use the Power Beam or missiles, and its touch icon brings it back)
        // the ring shows it in its middle; letting go of the ring with no other slot picked equips it (the app's
        // WeaponRingInput.CentrePick, sent as the Omega Cannon's own key). Lit while no slot is picked. Draw only
        private HudObjectInstance? _ringOmegaIcon;
        private HudObjectInstance? _ringOmegaBox;

        public bool HostRingOmegaShown => HostThirdSlotWeapon == BeamType.OmegaCannon && _availableWeapons[BeamType.OmegaCannon];

        private void DrawRingOmega()
        {
            if (!HostRingOmegaShown || !SetUpRingOmega())
            {
                return;
            }
            bool lit = _hudPreviousWeaponSelection < 0 && CurrentWeapon != BeamType.OmegaCannon;
            _ringOmegaBox!.SetIndex(lit ? 2 : 1, _scene);
            DrawRingSlot(_ringOmegaBox, 128, 96);
            DrawRingSlot(_ringOmegaIcon!, 128, 96);
        }

        private bool SetUpRingOmega()
        {
            if (_ringOmegaIcon != null)
            {
                return true;
            }
            HudObject select = HudInfo.GetHudObject(_hudObjects.WeaponSelect);
            HudObject box = HudInfo.GetHudObject(_hudObjects.SelectBox);
            int size = select.Width * select.Height;
            int frame = PirateBlaster.BaseIndex(BeamType.OmegaCannon);
            var icon = new HudObjectInstance(select.Width, select.Height);
            if (size > 0 && select.CharacterData.Count >= (frame + 1) * size)
            {
                icon.SetCharacterData(select.CharacterData, frame, _scene); // the weapon menu's own Omega frame
                icon.SetPaletteData(select.PaletteData, _scene);
            }
            else
            {
                // (no Omega frame in the menu's sprite: the top screen's weapon icon, which has one)
                HudObject top = HudInfo.GetHudObject(_hudObjects.WeaponIcon);
                if (top.AnimParams.Count <= frame)
                {
                    return false;
                }
                icon = new HudObjectInstance(top.Width, top.Height);
                icon.SetCharacterData(top.CharacterData, top.AnimParams[frame].ImageIndex, _scene);
                icon.SetPaletteData(top.PaletteData, _scene);
            }
            icon.Enabled = true;
            icon.Alpha = 0.722f;
            var boxInst = new HudObjectInstance(box.Width, box.Height);
            boxInst.SetCharacterData(box.CharacterData, _scene);
            boxInst.SetPaletteData(HudInfo.GetHudObject(_hudObjects.SelectIcon).PaletteData, _scene);
            boxInst.Enabled = true;
            _ringOmegaIcon = icon;
            _ringOmegaBox = boxInst;
            return true;
        }

        // the corner arc with seven slots (WeaponSlotCount 7, ring off): the DS's six slot spots (PlayerHud's positions,
        // top-left corners around the radial's anchor 224, 38) as angle + distance, spread evenly from the first to the
        // last over seven slots, the distance read off the vanilla six at each angle
        private static readonly (float Angle, float Radius)[] _arcSpots = MakeArcSpots();

        private static (float, float)[] MakeArcSpots()
        {
            (float X, float Y)[] vanilla = { (201, 156), (161, 152), (122, 142), (90, 109), (81, 70), (77, 32) };
            var polar = new (float A, float R)[6];
            for (int i = 0; i < 6; i++)
            {
                float dx = 224 - vanilla[i].X;
                float dy = vanilla[i].Y - 38;
                polar[i] = (MathF.Atan2(dx, dy), MathF.Sqrt(dx * dx + dy * dy));
            }
            var spots = new (float, float)[7];
            for (int i = 0; i < 7; i++)
            {
                float a = polar[0].A + (polar[5].A - polar[0].A) * i / 6;
                int k = 0;
                while (k < 4 && polar[k + 1].A < a)
                {
                    k++;
                }
                float t = Math.Clamp((a - polar[k].A) / (polar[k + 1].A - polar[k].A), 0, 1);
                spots[i] = (a, polar[k].R + (polar[k + 1].R - polar[k].R) * t);
            }
            return spots;
        }

        private void DrawWeaponArc7()
        {
            for (int i = 0; i < 7; i++)
            {
                (float a, float r) = _arcSpots[i];
                float x = 224 - r * MathF.Sin(a);
                float y = 38 + r * MathF.Cos(a);
                DrawArcSlot(_selectBoxInsts[i], x, y);
                DrawArcSlot(_weaponSelectInsts[i], x, y);
            }
        }

        private void DrawArcSlot(HudObjectInstance inst, float x, float y)
        {
            float oldX = inst.PositionX;
            float oldY = inst.PositionY;
            inst.PositionX = x / 256f;
            inst.PositionY = y / 192f;
            _scene.DrawHudObject(inst, mode: 1);
            inst.PositionX = oldX;
            inst.PositionY = oldY;
        }

        // one sprite centred on (x, y) in DS pixels, then back where MphRead keeps it (its corner arc)
        private void DrawRingSlot(HudObjectInstance inst, float x, float y)
        {
            float oldX = inst.PositionX;
            float oldY = inst.PositionY;
            inst.PositionX = (x - inst.Width / 2f) / 256f;
            inst.PositionY = (y - inst.Height / 2f) / 192f;
            _scene.DrawHudObject(inst, mode: 1);
            inst.PositionX = oldX;
            inst.PositionY = oldY;
        }
    }
}
