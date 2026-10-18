using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead;
using OpenTK.Mathematics;

// The PRIME map's split tools (owner 2026-10-14): D-pad up cycles the door-bounded corridor / chamber splits (CorridorMap)
// ON -> ALL (every split shown, as if entered) -> OFF (whole rooms, as the game draws them); L3 or R3 opens a report on
// the selected area: the split is right, shouldn't be split, needs a split, has the wrong shape, or the wrong name (a
// room near it, or a name typed in). Reports go to map_reports.json in the player's files, for us to fix the splits from;
// a new name shows on the map at once.
namespace MphRecomp.Frontend
{
    public sealed partial class PrimeMap
    {
        // where the reports are kept (null = nowhere: the menu still opens, nothing is saved)
        public string? ReportPath { get; set; }
        // a line of text typed by the player (Android: a box with the soft keyboard): hint, then the text or null when
        // cancelled -- may be called back on another thread. Null = no keyboard (the menu offers no typed name)
        public Action<string, Action<string?>>? TextRequested { get; set; }

        private sealed class Menu
        {
            public string Title = "";
            public readonly List<(string Label, Action Pick)> Rows = new();
            public int Cursor;
            public Menu? Back;
        }

        private sealed class Report
        {
            public string Time { get; set; } = "";
            public string Verdict { get; set; } = "";
            public string? Name { get; set; }
            public string Room { get; set; } = "";
            public string RoomName { get; set; } = "";
            public string? Piece { get; set; }
            public string Shown { get; set; } = "";
            public string Splits { get; set; } = "";
            public string SamusRoom { get; set; } = "";
            public float[] SamusPos { get; set; } = Array.Empty<float>();
            public float[] AreaCentre { get; set; } = Array.Empty<float>();
        }

        private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

        private Menu? _menu;
        private readonly List<(float X0, float Y0, float X1, float Y1)> _menuRects = new();
        private string _toast = "";
        private float _toastTime;
        private float _menuRepeat;
        private bool _menuHeld, _typing;
        private volatile bool _typedDone;
        private volatile string? _typed;
        private Shown? _reportOn;
        // names the reports gave (room|piece -> name), shown in the title
        private Dictionary<string, string>? _renamed;

        private static string SplitsState => CorridorMap.Off ? "OFF" : CorridorMap.All ? "ALL" : "ON";

        private static string RenameKey(Shown s) => s.Part.Name.ToUpperInvariant() + "|" + (s.PieceInfo?.Name ?? "");

        private string? RenamedTitle(Shown s)
        {
            if (_renamed == null)
            {
                _renamed = new();
                foreach (Report r in ReadReports().Where(r => r.Verdict == "name" && !String.IsNullOrWhiteSpace(r.Name)))
                {
                    _renamed[r.Room.ToUpperInvariant() + "|" + (r.Piece ?? "")] = r.Name!;
                }
            }
            return _renamed.TryGetValue(RenameKey(s), out string? name) ? name : null;
        }

        // D-pad up: ON -> ALL -> OFF -> ON; the map rebuilt on the same area
        private void CycleSplits()
        {
            (bool off, bool all) = CorridorMap.Off ? (false, false) : CorridorMap.All ? (true, false) : (false, true);
            CorridorMap.SetView(off, all);
            AreaMapModel.Part? part = _selected?.Part;
            string? piece = _selected?.PieceInfo?.Name;
            LoadPair(_pair);
            _selected = _shown.FirstOrDefault(s => s.Part == part && s.PieceInfo?.Name == piece)
                ?? _shown.FirstOrDefault(s => s.Part == part && s.Piece < 0) ?? _playerRoom;
            _forced = _selected; // kept (as a tap keeps it) while the view slides onto its new middle: the look ray would pick a neighbour
            SetTitle();
            Toast(off ? "SPLITS OFF: WHOLE ROOMS" : all ? "SPLITS: ALL SHOWN" : "SPLITS ON: SHOWN ONCE ENTERED");
        }

        private void Toast(string text)
        {
            _toast = text;
            _toastTime = 2.5f;
        }

        // L3 / R3: the report menu on the selected area
        private void OpenReport()
        {
            if (_selected == null) return;
            _reportOn = _selected;
            bool piece = _selected.PieceInfo != null;
            var menu = new Menu { Title = "REPORT: " + _title };
            menu.Rows.Add(("SPLIT IS RIGHT", () => Save("right")));
            if (piece) menu.Rows.Add(("SHOULDN'T BE SPLIT OFF", () => Save("merge")));
            else menu.Rows.Add(("NEEDS A SPLIT", () => Save("needs split")));
            if (piece) menu.Rows.Add(("WRONG SHAPE", () => Save("wrong shape")));
            menu.Rows.Add(("WRONG NAME...", () => _menu = NamesMenu(menu)));
            menu.Rows.Add(("CANCEL", () => _menu = null));
            _menu = menu;
        }

        // a name for it: type one, or a room near it (this planet's rooms, nearest first)
        private Menu NamesMenu(Menu back)
        {
            var menu = new Menu { Title = "CALL IT", Back = back };
            if (TextRequested != null) menu.Rows.Add(("TYPE A NAME...", StartTyping));
            Vector3 at = _reportOn!.Centre;
            var near = new List<(float Dist, string Name)>();
            foreach ((AreaMapModel? model, Vector3 offset) in new[] { (_modelA, Vector3.Zero), (_modelB, _pair >= 0 ? AreaMapModel.HalfOffsets[_pair + 1] : Vector3.Zero) })
            {
                if (model == null) continue;
                foreach (AreaMapModel.Part part in model.Parts)
                {
                    if (part.IsConnector || part.RoomId < 0) continue;
                    RoomMetadata? meta = Metadata.GetRoomById(part.RoomId, noThrow: true);
                    string? name = meta?.InGameName ?? meta?.Name;
                    if (name == null) continue;
                    float d = (Vector3.Clamp(at, part.Min + offset, part.Max + offset) - at).Length;
                    near.Add((d, name.ToUpperInvariant()));
                }
            }
            foreach (string name in near.OrderBy(n => n.Dist).Select(n => n.Name).Distinct().Take(8))
            {
                menu.Rows.Add((name, () => Save("name", name)));
            }
            menu.Rows.Add(("BACK", () => _menu = back));
            return menu;
        }

        private void StartTyping()
        {
            _typing = true;
            _typedDone = false;
            _typed = null;
            TextRequested!("A name for " + _title.ToLowerInvariant(), text =>
            {
                _typed = text;
                _typedDone = true;
            });
        }

        // the menu's 60 Hz input: up / down (D-pad, or the left stick held: repeats), A picks, B back, L3 / R3 closes
        private void MenuTick(Input input)
        {
            if (_typing)
            {
                if (!_typedDone) return;
                _typing = false;
                string? text = _typed?.Trim();
                if (!String.IsNullOrEmpty(text)) Save("name", text.ToUpperInvariant());
                return;
            }
            Menu menu = _menu!;
            if (input.Report)
            {
                _menu = null;
                return;
            }
            if (input.Close)
            {
                _menu = menu.Back;
                return;
            }
            int step = input.Up ? -1 : input.Down ? 1 : 0;
            float y = Dead(input.MoveY);
            if (MathF.Abs(y) > 0.5f)
            {
                _menuRepeat -= 1 / 60f;
                if (_menuRepeat <= 0 && step == 0)
                {
                    step = y > 0 ? -1 : 1;
                    _menuRepeat = _menuHeld ? 0.12f : 0.35f;
                    _menuHeld = true;
                }
            }
            else
            {
                _menuRepeat = 0;
                _menuHeld = false;
            }
            if (step != 0) menu.Cursor = (menu.Cursor + step + menu.Rows.Count) % menu.Rows.Count;
            if (input.World) menu.Rows[menu.Cursor].Pick();
        }

        private void Save(string verdict, string? name = null)
        {
            Shown s = _reportOn!;
            _menu = null;
            RoomMetadata? meta = Metadata.GetRoomById(s.Part.RoomId, noThrow: true);
            Vector3 samus = _info.Position, c = s.Centre;
            var report = new Report
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Verdict = verdict,
                Name = name,
                Room = s.Part.Name,
                RoomName = meta?.InGameName ?? meta?.Name ?? s.Part.Name,
                Piece = s.PieceInfo?.Name,
                Shown = _title,
                Splits = SplitsState,
                SamusRoom = _info.Room,
                SamusPos = new[] { samus.X, samus.Y, samus.Z },
                AreaCentre = new[] { c.X, c.Y, c.Z }
            };
            bool saved = false;
            try
            {
                if (ReportPath != null)
                {
                    List<Report> all = ReadReports();
                    all.Add(report);
                    Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
                    File.WriteAllText(ReportPath, JsonSerializer.Serialize(all, ReportJson));
                    saved = true;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke("primemap: report not saved: " + ex.Message);
            }
            if (verdict == "name" && name != null)
            {
                RenamedTitle(s); // (loads the earlier names first)
                _renamed![RenameKey(s)] = name;
                SetTitle();
            }
            Log?.Invoke($"primemap: report {verdict}{(name != null ? " '" + name + "'" : "")} on {report.Room}/{report.Piece ?? "-"} ({(saved ? ReportPath : "not saved")})");
            Toast(saved ? "REPORT SAVED" : "REPORT NOT SAVED");
        }

        private List<Report> ReadReports()
        {
            try
            {
                if (ReportPath != null && File.Exists(ReportPath)) return JsonSerializer.Deserialize<List<Report>>(File.ReadAllText(ReportPath)) ?? new();
            }
            catch (Exception ex)
            {
                Log?.Invoke("primemap: reports unreadable: " + ex.Message);
            }
            return new();
        }

        // a tap on the menu: a row picks it, outside it closes
        private bool MenuTouch(float ux, float uy, int kind)
        {
            if (_menu == null && !_typing) return false;
            if (kind != 2 || _typing) return true;
            for (int i = 0; i < _menuRects.Count && _menu != null; i++)
            {
                if (Hit(_menuRects[i], ux, uy))
                {
                    _menu.Cursor = i;
                    _menu.Rows[i].Pick();
                    return true;
                }
            }
            _menu = null;
            return true;
        }

        // the menu over the map, the toast under the title
        private void DrawReport(float a)
        {
            if (_toastTime > 0) Text(_toast, _w / 2, Top + 9, 2, TextK, KeyColour, a * Math.Min(1, _toastTime * 2));
            _menuRects.Clear();
            if (_menu == null && !_typing) return;
            const float RowH = 8, Width = 150;
            int rows = _typing ? 1 : _menu!.Rows.Count;
            float h = 12 + rows * RowH + 4;
            float x0 = (_w - Width) / 2, y0 = (192 - h) / 2;
            Rect(-1, x0, y0, x0 + Width, y0 + h, 0, 0, 0, 0.82f * a);
            Rect(-1, x0, y0, x0 + Width, y0 + 0.6f, LabelColour.R, LabelColour.G, LabelColour.B, a);
            Rect(-1, x0, y0 + h - 0.6f, x0 + Width, y0 + h, LabelColour.R, LabelColour.G, LabelColour.B, a);
            if (_typing)
            {
                Text("TYPING A NAME...", _w / 2, y0 + 4, 2, TextK, LabelColour, a);
                return;
            }
            Text(_menu!.Title, _w / 2, y0 + 3, 2, TextK, LabelColour, a);
            for (int i = 0; i < _menu.Rows.Count; i++)
            {
                float y = y0 + 12 + i * RowH;
                bool on = i == _menu.Cursor;
                if (on) Rect(-1, x0 + 4, y - 1.5f, x0 + Width - 4, y + RowH - 2.5f, LabelColour.R, LabelColour.G, LabelColour.B, 0.25f * a);
                Text(_menu.Rows[i].Label, x0 + 10, y, 0, TextK, on ? KeyColour : LabelColour, a);
                _menuRects.Add((x0, y - 1.5f, x0 + Width, y + RowH - 2.5f));
            }
        }
    }
}
