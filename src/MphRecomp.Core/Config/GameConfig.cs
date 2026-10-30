using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// mph-recomp data-driven config / tuning layer. Holds the game's tunable CONSTANTS as data
// (not per-user save state -- that's MphRecomp.Save). Defaults reproduce the current hardcoded
// renderer behaviour, so wiring this in changes nothing until a value is overridden. Supports
// layered overrides (defaults <- shipped config file <- mod/user overrides) via deep JSON merge,
// which is how the moddability "data defs" tier works. Pure data + logic -> headlessly testable.
namespace MphRecomp.Config
{
    public readonly record struct Vec3(float X, float Y, float Z);

    public sealed class GameConfig
    {
        public CameraConfig Camera { get; set; } = new();
        public MovementConfig Movement { get; set; } = new();
        public LightingConfig Lighting { get; set; } = new();
    }

    public sealed class CameraConfig
    {
        public float FovDegrees { get; set; } = 62f;
        public float LookSpeed { get; set; } = 2.2f;   // radians/sec at full stick
        public float NearFactor { get; set; } = 0.01f; // near = max(0.1, playRadius*NearFactor)
        public float FarFactor { get; set; } = 2.5f;   // far  = renderRadius*FarFactor + FarBase
        public float FarBase { get; set; } = 100f;
    }

    public sealed class MovementConfig
    {
        public float WalkSpeedFactor { get; set; } = 0.5f;    // * playRadius
        public float FreeFlySpeedFactor { get; set; } = 0.75f;
        public float GravityFactor { get; set; } = 2.0f;
        public float EyeHeightFactor { get; set; } = 0.06f;
        public float WallSkinFactor { get; set; } = 0.03f;
    }

    public sealed class LightingConfig
    {
        public float AmbientBase { get; set; } = 0.55f;
        public float DiffuseScale { get; set; } = 0.6f;
        public float FillFactor { get; set; } = 0.4f; // secondary light contribution
        public Vec3 Light1Dir { get; set; } = new(0.4f, 0.85f, 0.5f);
        public Vec3 Light2Dir { get; set; } = new(-0.5f, 0.35f, -0.6f);
    }

    public static class ConfigManager
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static GameConfig Default() => new();

        public static void Save(GameConfig config, string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
        }

        // Load a (possibly partial) config file layered over defaults: any key the file omits
        // keeps its default. Missing or corrupt file -> pure defaults (never throws). Values are
        // clamped to sane ranges after merge.
        public static GameConfig Load(string path)
        {
            if (!File.Exists(path)) return Validate(Default());
            try { return Validate(MergeOver(Default(), File.ReadAllText(path))); }
            catch (Exception) { return Validate(Default()); }
        }

        // Apply an override document (JSON text) on top of a base config -- the moddability path:
        // each mod/user layer only needs to specify the keys it changes.
        public static GameConfig ApplyOverrides(GameConfig baseConfig, string overrideJson)
            => Validate(MergeOver(baseConfig, overrideJson));

        // Deep-merge `overrideJson` over `baseConfig`: object keys recurse, scalars/arrays replace.
        private static GameConfig MergeOver(GameConfig baseConfig, string overrideJson)
        {
            JsonNode baseNode = JsonSerializer.SerializeToNode(baseConfig, Options)!;
            JsonNode? overNode = JsonNode.Parse(overrideJson);
            if (overNode is JsonObject overObj && baseNode is JsonObject baseObj)
            {
                DeepMerge(baseObj, overObj);
            }
            return baseNode.Deserialize<GameConfig>(Options) ?? Default();
        }

        private static void DeepMerge(JsonObject target, JsonObject source)
        {
            foreach (var kv in source)
            {
                if (kv.Value is JsonObject srcChild && target[kv.Key] is JsonObject tgtChild)
                {
                    DeepMerge(tgtChild, srcChild);
                }
                else
                {
                    target[kv.Key] = kv.Value?.DeepClone();
                }
            }
        }

        // Clamp values into ranges that keep the renderer/movement well-behaved.
        public static GameConfig Validate(GameConfig c)
        {
            c.Camera.FovDegrees = Clamp(c.Camera.FovDegrees, 30f, 110f);
            c.Camera.LookSpeed = Clamp(c.Camera.LookSpeed, 0.1f, 20f);
            c.Camera.NearFactor = Clamp(c.Camera.NearFactor, 0.0001f, 1f);
            c.Camera.FarFactor = Clamp(c.Camera.FarFactor, 1f, 100f);
            c.Camera.FarBase = Clamp(c.Camera.FarBase, 0f, 10000f);
            c.Movement.WalkSpeedFactor = Clamp(c.Movement.WalkSpeedFactor, 0f, 100f);
            c.Movement.FreeFlySpeedFactor = Clamp(c.Movement.FreeFlySpeedFactor, 0f, 100f);
            c.Movement.GravityFactor = Clamp(c.Movement.GravityFactor, 0f, 100f);
            c.Movement.EyeHeightFactor = Clamp(c.Movement.EyeHeightFactor, 0f, 1f);
            c.Movement.WallSkinFactor = Clamp(c.Movement.WallSkinFactor, 0f, 1f);
            c.Lighting.AmbientBase = Clamp(c.Lighting.AmbientBase, 0f, 1f);
            c.Lighting.DiffuseScale = Clamp(c.Lighting.DiffuseScale, 0f, 4f);
            c.Lighting.FillFactor = Clamp(c.Lighting.FillFactor, 0f, 4f);
            return c;
        }

        private static float Clamp(float v, float lo, float hi)
            => float.IsFinite(v) ? Math.Min(hi, Math.Max(lo, v)) : lo;
    }
}
