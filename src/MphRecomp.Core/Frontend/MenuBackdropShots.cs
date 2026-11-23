using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTK.Mathematics;

namespace MphRecomp.Frontend
{
    // The menus' arena backdrop's camera per arena (LOOK > MENUS ARENA BACKGROUND, owner 2026-11-20: "a fixed shot from a
    // chosen position"). The owner takes them in the dev launcher's MENU BACKGROUND SHOTS (owner 11-20: "a dev entry for
    // me to go in every map and free cam fly and press a button", CampaignRenderer.MenuShotPick.cs), saved to
    // <data>/menu_shots.json; those win over the built-in Shots below. An arena with neither is seen from one of its
    // player spawns, a little behind and above it, looking along it.
    // A picked shot can carry a depth of field (owner 2026-11-21: "a depth of field setting in the menu background shot
    // picker ... only if that depth of field will apply when the actual map is displayed live on menu"): "focus", the
    // distance in world units that stays sharp, "blur", 0 (none) to MaxBlur, and "range" (owner 11-21 "yes add range":
    // "the area that can be in focus is really narrow"), how much nearer and farther than focus stays sharp too, 0 (only
    // focus; a file from before has none) to MaxRange. The backdrop draws it live (CampaignRenderer.Dof.cs), so the
    // menus' blur behind the pages has it too.
    public static class MenuBackdropShots
    {
        public const float Fov = 60;
        public const float SpawnEyeUp = 1.4f, SpawnEyeBack = 1.5f, SpawnLookDown = 0.12f;
        public const float MinFocus = 0.5f, MaxFocus = 300, DefaultFocus = 12, MaxBlur = 10, MaxRange = 50;

        // room name -> eye, target (world units), vertical FOV in degrees
        static readonly Dictionary<string, (Vector3 Eye, Vector3 Target, float Fov)> Shots = new(StringComparer.OrdinalIgnoreCase)
        {
        };

        // the shots taken with the dev picker: <data>/menu_shots.json, {"<room>": {"eye": [x, y, z], "target": [x, y, z], "fov": f}}
        public const string FileName = "menu_shots.json";
        static Dictionary<string, (Vector3 Eye, Vector3 Target, float Fov)> _picked = new(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, (float Focus, float Blur, float Range)> _pickedDof = new(StringComparer.OrdinalIgnoreCase);

        public static bool HasShot(string arena) => _picked.ContainsKey(arena) || Shots.ContainsKey(arena);
        public static int PickedCount => _picked.Count;

        // (re)reads <dir>/menu_shots.json; a missing or broken file is no picks
        public static void LoadPicked(string? dir)
        {
            var picked = new Dictionary<string, (Vector3, Vector3, float)>(StringComparer.OrdinalIgnoreCase);
            var dof = new Dictionary<string, (float, float, float)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(dir ?? "", FileName);
                if (dir != null && File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject all)
                {
                    foreach (var kv in all)
                    {
                        if (kv.Value is not JsonObject o || Vec(o["eye"]) is not Vector3 eye || Vec(o["target"]) is not Vector3 target) continue;
                        float fov = o["fov"] is JsonValue f && f.TryGetValue(out float fv) ? fv : Fov;
                        float blur = o["blur"] is JsonValue b && b.TryGetValue(out float bv) ? Math.Clamp(bv, 0, MaxBlur) : 0;
                        float focus = o["focus"] is JsonValue fo && fo.TryGetValue(out float fcv) ? Math.Clamp(fcv, MinFocus, MaxFocus) : DefaultFocus;
                        float range = o["range"] is JsonValue ra && ra.TryGetValue(out float rv) ? Math.Clamp(rv, 0, MaxRange) : 0;
                        if (blur > 0) dof[kv.Key] = (focus, blur, range); // (a file from before has none)
                        picked[kv.Key] = (eye, target, fov);
                    }
                }
            }
            catch (Exception)
            {
            }
            _picked = picked;
            _pickedDof = dof;
        }

        // a picked shot's depth of field; Blur 0 = none (no shot, a spawn's view, or none set)
        public static (float Focus, float Blur, float Range) DofFor(string arena) =>
            _pickedDof.TryGetValue(arena, out var d) ? d : (DefaultFocus, 0, 0);

        // the same for the camera a backdrop was given (For): the picked shot it is, else none
        public static (float Focus, float Blur, float Range) DofFor((Vector3 Eye, Vector3 Target, float Fov) shot)
        {
            foreach (var kv in _picked)
            {
                if (kv.Value == shot) return DofFor(kv.Key);
            }
            return (DefaultFocus, 0, 0);
        }

        static Vector3? Vec(JsonNode? n) => n is JsonArray { Count: 3 } a
            && a[0] is JsonValue x && x.TryGetValue(out float fx) && a[1] is JsonValue y && y.TryGetValue(out float fy)
            && a[2] is JsonValue z && z.TryGetValue(out float fz) ? new Vector3(fx, fy, fz) : null;

        // the dev picker's SHOT: this arena's camera into <dir>/menu_shots.json (replacing its last one); the file's count
        public static int SavePicked(string dir, string arena, Vector3 eye, Vector3 target, float fov, float focus = DefaultFocus,
            float blur = 0, float range = 0)
        {
            LoadPicked(dir);
            _picked[arena] = (eye, target, fov);
            if (blur > 0) _pickedDof[arena] = (Math.Clamp(focus, MinFocus, MaxFocus), Math.Min(blur, MaxBlur), Math.Clamp(range, 0, MaxRange));
            else _pickedDof.Remove(arena);
            var all = new JsonObject();
            foreach (var kv in _picked)
            {
                var o = new JsonObject
                {
                    ["eye"] = new JsonArray(kv.Value.Eye.X, kv.Value.Eye.Y, kv.Value.Eye.Z),
                    ["target"] = new JsonArray(kv.Value.Target.X, kv.Value.Target.Y, kv.Value.Target.Z),
                    ["fov"] = kv.Value.Fov,
                };
                if (_pickedDof.TryGetValue(kv.Key, out var d))
                {
                    o["focus"] = d.Focus;
                    o["blur"] = d.Blur;
                    if (d.Range > 0) o["range"] = d.Range;
                }
                all[kv.Key] = o;
            }
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, FileName);
            File.WriteAllText(path + ".tmp", all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
            return _picked.Count;
        }

        // spawn >= 0: that spawn's view even if the arena has a shot (the preview sheet)
        public static (Vector3 Eye, Vector3 Target, float Fov) For(string arena, IReadOnlyList<(Vector3 Position, Vector3 Facing)> spawns, Random rng,
            int spawn = -1)
        {
            if (spawn < 0 && _picked.TryGetValue(arena, out var picked)) return picked;
            if (spawn < 0 && Shots.TryGetValue(arena, out var shot)) return shot;
            if (spawns.Count == 0) return (new Vector3(0, 5, 10), Vector3.Zero, Fov);
            return FromSpawn(spawns[spawn >= 0 ? spawn % spawns.Count : rng.Next(spawns.Count)]);
        }

        public static (Vector3 Eye, Vector3 Target, float Fov) FromSpawn((Vector3 Position, Vector3 Facing) spawn)
        {
            Vector3 flat = new(spawn.Facing.X, 0, spawn.Facing.Z);
            flat = flat.LengthSquared > 1e-6f ? flat.Normalized() : Vector3.UnitZ;
            Vector3 eye = spawn.Position + Vector3.UnitY * SpawnEyeUp - flat * SpawnEyeBack;
            Vector3 target = eye + flat * 10 - Vector3.UnitY * (10 * SpawnLookDown);
            return (eye, target, Fov);
        }
    }
}
