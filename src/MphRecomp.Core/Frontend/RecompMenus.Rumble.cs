using System.Linq;

namespace MphRecomp.Frontend
{
    // OPTIONS > RUMBLE PAK (board queue T1): the game's own row, live when the host has a motor (MphRead.Rumble.Output:
    // a controller's or the phone's on Android, the SDL pad on PC), greyed as the game shows it without a pak otherwise
    // (FrontendSession.OnPageEntered). The row's arrows send call 48 on page 21; vanilla's handler (ov0 0x212dd0c)
    // flips the option bit (0x020e8318+0x10 bit 7) and, turned on, gives the preview buzz (100, 10). Off by default, as
    // a fresh DS save. Saved in RecompSettings.Rumble; MphRead.Rumble.Enabled follows it from the first page on.
    public sealed partial class RecompMenus
    {
        private const int RumbleArrowsCall = 48;
        private int _rumbleValueString = -2; // the row's value text ("rumb", which the front end fills with OFF), -1 = none

        private bool RumbleCall(MenuEngine menu, int call)
        {
            if (call != RumbleArrowsCall || menu.Page?.Index != OptionsPage || MphRead.Rumble.Output == null) return false;
            _settings.Rumble = !_settings.Rumble;
            SaveSettings();
            MphRead.Rumble.Enabled = _settings.Rumble;
            if (_settings.Rumble)
            {
                MphRead.Rumble.Start(100, 10); // ov0 0x212dd0c: the preview buzz
            }
            else
            {
                MphRead.Rumble.Stop();
            }
            ShowRumbleValue(menu);
            return true;
        }

        private void EnterRumble(MenuEngine menu, int page)
        {
            MphRead.Rumble.Enabled = _settings.Rumble && MphRead.Rumble.Output != null;
            if (page == OptionsPage) ShowRumbleValue(menu);
        }

        private void ShowRumbleValue(MenuEngine menu)
        {
            if (_rumbleValueString == -2)
            {
                // the value text at the ROM's place, or where AddOptionsButtons moved the rumble column
                float dx = OptionsColumns[1] - RomRumbleX;
                MenuItem? value = menu.Page!.Items.FirstOrDefault(it => TextAt(it, 106 + dx, 55))
                    ?? menu.Page.Items.FirstOrDefault(it => TextAt(it, 106, 55));
                _rumbleValueString = value?.States[0].Text is MenuTextStyle t ? t.StringId : -1;
            }
            if (_rumbleValueString < 0) return;
            _strings.Fill(_rumbleValueString, RomString(_settings.Rumble && MphRead.Rumble.Output != null ? "ON" : "OFF"));
        }

        // the ROM's own string for a word (FrontendSession fills "rumb" with its OFF the same way), else the word
        private string RomString(string text)
        {
            for (int i = 0; i < _strings.RomCount; i++)
            {
                if (_strings[i] == text) return _strings[i];
            }
            return text;
        }
    }
}
