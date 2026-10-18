using System;
using System.Collections.Generic;
using System.Globalization;
using MphRead;
using MphRecomp.Config;
using MphRecomp.Multiplayer;

// The controls menu's zoom rows and TRY CONTROLS (owner 2026-10-16; Core Input/ZoomAim.cs), in three parts for CONTROLS'
// sections (RecompMenus.Controls.cs): TryRows (practice), ZoomSensRows (aim), ZoomLookRows (zoom).
//   - try controls: a pop-up of the hunter icons with the practice bots' IDLE / ACTIVE switch under a divider (C21/C22,
//     owner 2026-10-17; MultiplayerMenu.TryPicker.cs), then a practice run in Alinos Gateway (App SoloArena.cs
//     ConfigureTry: no intro, no FIRE to spawn, no replay -- C23), from the menus only (not over a game: the gunship's
//     OPTIONS or the pause menu); closing the pop-up, or one Start press in the run, comes back to this list
//   - imperialist sensitivity: the controller's 1-15 (like SENSITIVITY) or the mouse's (its own steps); greyed while paired,
//     showing the aim's value
//   - auto imp sensitivity (C18, was "pair with aim"; C3: a checkbox with a lock; ticked, a box round the aim sensitivity
//     and this, the imperialist's locked): ON (default) = the Imperialist's zoomed aim is the aim sensitivity, slowed by the
//     game's own zoom factor; OFF = IMPERIALIST SENSITIVITY on its own (it starts where the aim sensitivity is when it's
//     switched off)
//   Each row's hint is the one for the option it's on (C17; the texts: ControlHints.cs)
//   - zoom feel (C19) was removed 2026-10-17 (owner: no lag on zoom): its SMOOTH drew the zoom eased, ~50 ms behind
//   - zoom step: OFF (vanilla: one press in, the zoom pulls in onto targets by itself) or ON (owner 2026-10-17: press for
//     the first zoom, again for vanilla's target zoom when one is ready (else out), again to come out; zoom in / zoom out
//     step while zoomed; no auto zoom)
//   - zoom reticle (ON by default, greyed while zoom step is off): the reticle faint in the first zoom, solid once the
//     target zoom is ready (MphRead PlayerZoomReady.cs)
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        // TRY CONTROLS (none over a game); its bots' switch is in its pop-up (C22)
        private List<RowModel> TryRows()
        {
            var rows = new List<RowModel>();
            if (_controlsFromHost || _inPause || InNetMatch) return rows;
            rows.Add(new RowModel
            {
                Label = "try\ncontrols",
                Description = ControlHints.For("trycontrols"),
                Value = () => "PLAY",
                Step = _ => { if (_stepMenu != null) OpenTryPicker(_stepMenu); },
                Action = true
            });
            return rows;
        }

        // imperialist sensitivity + pair with aim (C3: under the aim sensitivity, which the box takes in with it)
        private List<RowModel> ZoomSensRows(bool keys)
        {
            var inv = CultureInfo.InvariantCulture;
            RecompSettings d = DefaultSettings;
            var rows = new List<RowModel>();
            rows.Add(new RowModel
            {
                Label = "imperialist\nsensitivity",
                Description = ControlHints.For("impsensitivity", keys), // (greyed while paired: never focused then)
                Value = () => keys
                    ? MouseScale.Text(_settings.ZoomSensPaired ? _settings.MouseSens : _settings.ZoomMouseSens)
                    : Math.Clamp(_settings.ZoomSensPaired ? _settings.GameSensitivity : _settings.ZoomSensitivity, 1, 15).ToString(inv),
                Step = s =>
                {
                    if (keys) _settings.ZoomMouseSens = MouseScale.Step(_settings.ZoomMouseSens, s);
                    else _settings.ZoomSensitivity = Math.Clamp(_settings.ZoomSensitivity + s, 1, 15);
                },
                Disabled = () => _settings.ZoomSensPaired,
                Reset = () =>
                {
                    if (keys) _settings.ZoomMouseSens = d.ZoomMouseSens;
                    else _settings.ZoomSensitivity = d.ZoomSensitivity;
                },
                AtDefault = () => keys ? _settings.ZoomMouseSens == d.ZoomMouseSens : _settings.ZoomSensitivity == d.ZoomSensitivity
            });
            rows.Add(new RowModel
            {
                Label = "auto imp\nsensitivity",
                Describe = () => ControlHints.For(_settings.ZoomSensPaired ? "autoimp.on" : "autoimp.off"),
                Value = () => _settings.ZoomSensPaired ? "ON" : "OFF",
                Check = () => _settings.ZoomSensPaired,
                Step = _ =>
                {
                    _settings.ZoomSensPaired = !_settings.ZoomSensPaired;
                    if (!_settings.ZoomSensPaired)
                    {
                        // unpaired: it starts where it was, then goes its own way
                        _settings.ZoomSensitivity = Math.Clamp(_settings.GameSensitivity, 1, 15);
                        _settings.ZoomMouseSens = MouseScale.Clamp(_settings.MouseSens);
                    }
                }
            });
            return rows;
        }

        // zoom step + its reticle cue
        private List<RowModel> ZoomLookRows(bool keys)
        {
            var rows = new List<RowModel>();
            rows.Add(new RowModel
            {
                Label = "zoom\nstep",
                Describe = () => _settings.ZoomStep ? ControlHints.For("zoomstep.on", keys) : ControlHints.For("zoomstep.off"),
                Value = () => _settings.ZoomStep ? "ON" : "OFF",
                Step = _ => _settings.ZoomStep = !_settings.ZoomStep
            });
            rows.Add(new RowModel
            {
                Label = "reticle\nindicator", // (was ZOOM RETICLE, owner 2026-10-17)
                Describe = () => ControlHints.For(_settings.ZoomReadyIcon ? "zoomicon.on" : "zoomicon.off"),
                Value = () => _settings.ZoomReadyIcon ? "ON" : "OFF",
                Disabled = () => !_settings.ZoomStep,
                Step = _ => _settings.ZoomReadyIcon = !_settings.ZoomReadyIcon
            });
            return rows;
        }

        // ---- TRY CONTROLS' pop-up ----

        // TRY CONTROLS' press: the hunter icons + the bots' switch over CONTROLS (MultiplayerMenu.TryPicker.cs); a pick runs
        private void OpenTryPicker(MenuEngine menu)
        {
            Multiplayer?.OpenTryPicker(menu, () => _settings.TryActiveBots, active =>
            {
                _settings.TryActiveBots = active;
                _settings.Save(_settingsPath);
            }, StartTryControls);
        }

        // TRY CONTROLS: the picked hunter (and look) alone in Alinos Gateway with three practice bots
        private void StartTryControls((Hunter Hunter, string? Skin, string? Character) pick)
        {
            if (StartOnlineMatch == null) return;
            Hunter hunter = pick.Hunter;
            var settings = new MatchSettings
            {
                Mode = GameMode.Battle,
                Arena = MatchArenas.Find("Alinos Gateway")?.Name ?? "UNIT1 ALINOS LANDFALL",
            };
            settings.Players.Add(new MatchPlayer { Hunter = hunter, Skin = pick.Skin, Character = pick.Character });
            Hunter[] targets = { Hunter.Kanden, Hunter.Spire, Hunter.Noxus };
            foreach (Hunter t in targets)
            {
                settings.Players.Add(new MatchPlayer { Hunter = t == hunter ? Hunter.Trace : t, Bot = true, Recolor = 1, BotLevel = 1 });
            }
            if (_ctl != null) MultiplayerMenu.PendingReturn = _ctl.Page; // back to CONTROLS after the run
            StartOnlineMatch(MatchSetupText.Write(settings) + ";" + (_settings.TryActiveBots ? MatchSetupText.TryActivePart : MatchSetupText.TryPart));
        }
    }
}
