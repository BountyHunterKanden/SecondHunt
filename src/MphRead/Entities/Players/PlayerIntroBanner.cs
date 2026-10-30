using System.Collections.Generic;
using MphRead.Formats;
using MphRead.Text;

namespace MphRead.Entities
{
    // recomp: a line in the middle of the screen while a match's intro shows the mode's rules (owner 2026-10-10, release
    // test note N8: in Echoes arenas "Space Jump is active!", and on Pipeline "Space Jump and Gravity Boost are active!",
    // centred and slightly larger than the rules text). The host sets the text as the room loads (Core
    // Arenas/EchoesMovement); MphRead's own font and colours, drawn 1.25x (HudObjectInstance.Scale) in the dialogs'
    // centred 4:3 box so the letters stay square on a wide screen, fading in over half a second and wrapped at word gaps
    // to fit. Draw-only: play and replays don't change.
    // Echoes arenas have no intro camera sequence (no rules screen; release 0.2.0 F4: "entirely missing" on the Odin), so
    // there the line shows over play for the match's first seconds instead, above the reticle, fading out at the end.
    public partial class PlayerEntity
    {
        // the intro's middle line, null = none (set on every room load)
        public static string? HostIntroBanner { get; set; }

        private const float _introBannerScale = 1.25f;
        private const float _introBannerWidth = 232; // DS pixels a line may take
        private const float _introBannerHold = 4; // seconds over play with no intro, then a half-second fade

        // a room with no intro sequence: the line over play while the match's first seconds last
        private void DrawIntroBannerInPlay()
        {
            if (CameraSequence.Current == null && GameState.Multiplayer && _scene.ElapsedTime < _introBannerHold + 0.5f)
            {
                DrawIntroBanner(inPlay: true);
            }
        }

        private void DrawIntroBanner(bool inPlay = false)
        {
            string? text = HostIntroBanner;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            Font font = SetUpFont(text[0], set: true);
            float Width(string s)
            {
                float w = 0;
                foreach (char ch in s)
                {
                    int index = ch - font.MinCharacter;
                    if (index >= 0 && index < font.Widths.Count) w += font.Widths[index];
                }
                return w * _introBannerScale;
            }
            // wrap at spaces
            var lines = new List<string>();
            string line = "";
            foreach (string word in text.Split(' '))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Width(next) > _introBannerWidth)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = next;
                }
            }
            lines.Add(line);
            float lineHeight = 12 * _introBannerScale;
            float y = (inPlay ? 64 : 96) - lines.Count * lineHeight / 2;
            var color = new ColorRgba(0x7FDE); // the rules' header colour
            _scene.HostHudDialog(true);
            float alpha = System.Math.Clamp(_scene.ElapsedTime / 0.5f, 0, 1);
            if (inPlay)
            {
                alpha = System.Math.Min(alpha, System.Math.Clamp((_introBannerHold + 0.5f - _scene.ElapsedTime) / 0.5f, 0, 1));
            }
            _textInst.Alpha = alpha;
            _textInst.Scale = _introBannerScale;
            foreach (string l in lines)
            {
                float x = 128 - Width(l) / 2;
                foreach (char ch in l)
                {
                    int index = ch - font.MinCharacter;
                    if (index < 0 || index >= font.Widths.Count)
                    {
                        continue;
                    }
                    if (ch != ' ')
                    {
                        _textInst.PositionX = x / 256f;
                        _textInst.PositionY = (y + font.Offsets[index] * _introBannerScale) / 192f;
                        _textInst.SetData(index, color, _scene);
                        _scene.DrawHudObject(_textInst, mode: 2);
                    }
                    x += font.Widths[index] * _introBannerScale;
                }
                y += lineHeight;
            }
            _textInst.Scale = 1;
            _textInst.Alpha = 1;
            _scene.HostHudDialog(false);
        }
    }
}
