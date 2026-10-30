using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;

// The menus' HD Samus backdrop (owner 2026-10-03): the Samus behind the logo and menus (sourceimages/bg main1 = helmet
// on the top screen, main2 = chest on the touch screen) redrawn from the user's own imported HD suit -- Echoes' Varia
// first, else whichever Prime suit they imported -- straight on like the DS picture: the model as Retro built it (its own
// T-pose; owner 2026-10-03: the DS Idle on her rig twisted her torso and stretched the neck), a solid visor with no face
// behind it, seen from the front through a camera framed on the DS picture's visor and chest lights. Rendered once on the device (MphRead.Android/MenuSamusRender.cs, the same GX
// program as the campaign's HD suits) and cached as a PNG next to the user's data; nothing of it ships. The recipe
// (recipes/frontend/MenuSamus/MenuSamus.json) holds only ids and numbers: the suit order, the pose, the camera, and per
// suit the material names of the face (hidden) and the visor pane (drawn opaque).
namespace MphRecomp.Frontend
{
    public sealed class MenuSamus
    {
        // hd/<suit> folders to try, in order (the first one with a model and a rig cache wins)
        public string[] Suits { get; private init; } = Array.Empty<string>();
        // "pose": "built" = the model as built (no animation, straight on); else Samus's DS animation (PlayerAnimation
        // index) and its frame on her rig
        public bool AsBuilt { get; private init; }
        public int Clip { get; private init; } = 8;
        public int Frame { get; private init; }
        // the camera in the rig's space (the DS model's own: Samus faces -Z, feet on y = 0)
        public Vector3 Eye { get; private init; }
        public Vector3 Target { get; private init; }
        public float FovY { get; private init; } = 30;
        // per suit: the face behind the visor (not drawn) and the visor pane (drawn opaque, so it reads as the DS visor)
        readonly Dictionary<string, (string[] Hide, string[] Opaque)> _visor = new();
        // per suit: its own camera where the body sits differently (Prime 4's suits: taller, the helmet higher)
        readonly Dictionary<string, (Vector3 Eye, Vector3 Target)> _camera = new();
        // per suit: its stance in the still, as offsets per bone of her rig (rig units), each corner moved by its own bone
        // weights (owner 2026-10-13: Prime 1's suits "stand" like Echoes' -- head up out of the chest plate, pads down)
        readonly Dictionary<string, Dictionary<string, Vector3>> _stand = new();
        // the picture: two DS screens stacked, 256 x 384
        public const float Aspect = 256f / 384f;
        // a short hash of the recipe text: a changed recipe renders again
        public string Hash { get; private init; } = "";

        public Matrix4 View => Matrix4.LookAt(Eye, Target, Vector3.UnitY);

        public Vector3 EyeFor(string suit) => _camera.TryGetValue(suit, out var c) ? c.Eye : Eye;

        public Vector3 TargetFor(string suit) => _camera.TryGetValue(suit, out var c) ? c.Target : Target;

        public Matrix4 ViewFor(string suit) => Matrix4.LookAt(EyeFor(suit), TargetFor(suit), Vector3.UnitY);

        public Matrix4 Projection => Matrix4.CreatePerspectiveFieldOfView(FovY * MathF.PI / 180f, Aspect, 0.05f, 100f);

        public static MenuSamus Load()
        {
            // recipes/frontend/MenuSamus/MenuSamus.json (one folder deeper than the import recipes, which the importer's
            // RecipeStore reads as kind/name.json); the resource name may carry backslashes
            Assembly asm = typeof(MenuSamus).Assembly;
            string res = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == "recipes/frontend/MenuSamus/MenuSamus.json")
                ?? throw new FileNotFoundException("recipes/frontend/MenuSamus/MenuSamus.json is not embedded");
            using Stream st = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(st);
            return Parse(reader.ReadToEnd());
        }

        public static MenuSamus Parse(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            static Vector3 V(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(json)) h = (h ^ b) * 16777619;
            var recipe = new MenuSamus
            {
                Suits = r.GetProperty("suits").EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s != "").ToArray(),
                Clip = r.GetProperty("clip").GetInt32(),
                Frame = r.GetProperty("frame").GetInt32(),
                Eye = V(r.GetProperty("eye")),
                Target = V(r.GetProperty("target")),
                FovY = r.GetProperty("fovY").GetSingle(),
                AsBuilt = r.TryGetProperty("pose", out JsonElement pose) && pose.GetString() == "built",
                Hash = h.ToString("x8")
            };
            static string[] Names(JsonElement e, string key) => e.TryGetProperty(key, out JsonElement a)
                ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToArray() : Array.Empty<string>();
            if (r.TryGetProperty("visor", out JsonElement visor))
            {
                foreach (JsonProperty p in visor.EnumerateObject()) recipe._visor[p.Name] = (Names(p.Value, "hide"), Names(p.Value, "opaque"));
            }
            if (r.TryGetProperty("camera", out JsonElement camera))
            {
                foreach (JsonProperty p in camera.EnumerateObject()) recipe._camera[p.Name] = (V(p.Value.GetProperty("eye")), V(p.Value.GetProperty("target")));
            }
            if (r.TryGetProperty("stand", out JsonElement stand))
            {
                foreach (JsonProperty p in stand.EnumerateObject())
                {
                    if (p.Name.StartsWith('_')) continue; // a note
                    recipe._stand[p.Name] = p.Value.EnumerateObject().ToDictionary(b => b.Name, b => V(b.Value));
                }
            }
            return recipe;
        }

        public string[] HideFor(string suit) => _visor.TryGetValue(suit, out var v) ? v.Hide : Array.Empty<string>();

        public string[] OpaqueFor(string suit) => _visor.TryGetValue(suit, out var v) ? v.Opaque : Array.Empty<string>();

        public IReadOnlyDictionary<string, Vector3>? StandFor(string suit) => _stand.TryGetValue(suit, out var s) ? s : null;

        // a stance's offset per bone of a skeleton (`names` = its bones in order), null when it moves nothing there
        public static Vector3[]? StandOffsets(IReadOnlyDictionary<string, Vector3>? stand, IReadOnlyList<string> names)
        {
            if (stand == null || stand.Count == 0) return null;
            var off = new Vector3[names.Count];
            bool any = false;
            for (int i = 0; i < names.Count; i++)
            {
                if (stand.TryGetValue(names[i], out Vector3 o)) { off[i] = o; any = true; }
            }
            return any ? off : null;
        }

        // a corner moved by its bones' offsets, each by its weight (4 bones + 4 weights a vertex, as TrophyRig holds them)
        public static Vector3 Stand(Vector3 p, Vector3[] off, int[] bones, float[] weights, int vertex)
        {
            for (int k = 0; k < 4; k++)
            {
                int b = bones[vertex * 4 + k];
                if (b < off.Length) p += off[b] * weights[vertex * 4 + k];
            }
            return p;
        }

        // the first suit of the recipe's list the user has: its model (.dae) and a rig cache (.mphrig) in hd/<suit>
        public string? PickSuit(string hdRoot) => Suits.FirstOrDefault(s => Has(hdRoot, s));

        // RECOMP SETTINGS "menu art"'s pick (RecompSettings.MenuArtSuit): that suit when the user has it (or a DS body:
        // StandinPictures.MenuArtOf, drawn by StandinPictures.MenuArt rather than the HD suit code); "shuffle" = any of
        // Samus's imported suits but `avoid` (the last one shown, so each start differs); else the recipe's order
        public string? PickSuit(string hdRoot, string? want, string? avoid, Random? random = null)
        {
            if (want == Config.RecompSettings.MenuArtShuffle)
            {
                List<string> all = Multiplayer.Characters.Skins(Multiplayer.Characters.Samus, hdRoot)
                    .Where(s => !Multiplayer.Characters.IsOriginal(s) && Has(hdRoot, s)).ToList();
                List<string> fresh = all.Where(s => !s.Equals(avoid, StringComparison.OrdinalIgnoreCase)).ToList();
                List<string> from = fresh.Count > 0 ? fresh : all;
                if (from.Count > 0) return from[(random ?? Random.Shared).Next(from.Count)];
            }
            else if (!string.IsNullOrEmpty(want) && (Has(hdRoot, want) || StandinPictures.MenuArtOf(want) != null)) return want;
            return PickSuit(hdRoot);
        }

        // the user has hd/<suit>: its model (.dae), a rig cache (.mphrig), and a complete import
        public static bool Has(string hdRoot, string suit)
        {
            string dir = Path.Combine(hdRoot, suit);
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.dae").Any() && Directory.EnumerateFiles(dir, "*.mphrig").Any()
                && MphRecomp.Import.ImportedContent.Usable(hdRoot, suit);
        }

        public string CacheName(string suit, int width, int height) => $"menu_samus_{suit}_{Hash}_{width}x{height}.png";
    }
}
