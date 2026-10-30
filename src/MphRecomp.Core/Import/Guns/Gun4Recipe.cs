using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MphRecomp.Import.Guns
{
    // recipes/guns/<Suit>.json for Metroid Prime 4: Beyond's first-person arm (game "mp4"): gun_export4b.py's arguments
    // and constants as ids and names, and the hash of the character project it was authored against:
    //   game        "mp4"
    //   pak         the RomFS pak holding the CHPR and the models, as the dump names it ("universeRoom.pak")
    //   chpr        Samus's character project (GUID): skeleton + animations (Beyond/ChprCharacter)
    //   chprSha1    SHA-1 of the CHPR's decompressed bytes
    //   model       the first-person model (GUID; one of the CHPR's model nodes)    modelName  its name there ("default")
    //   character   the converted look's name (its materials are "<character>_mat<i>")
    //   meshes      "auto" (the first mesh list without its last mesh, the effect shell) or ranges "0-11,21"
    //   idle        the role baked as one frame (frame 0); the arm joints the other animations leave alone hold it
    //   roles       [[MPH gun state (GunViewmodel's names), Beyond animation], ...] in gun.bin's order; an animation "clip@a-b"
    //               plays frames a to b of the clip (a held deploy: the end of an opening clip, looped); "clip@b-a" (b > a)
    //               plays them backwards (an opening made from a closing clip); "a + b + c" joins pieces into one, and a
    //               trailing " x1.5" plays it at that speed
    //   paks        the RomFS paks the in-app import searches, in order, as RomFS paths (default: just "pak")
    //   skinChpr    the character project the model is skinned to, when not "chpr" (GUID)
    //   look        {"spec": 1.5, ...}: the static look conversion's constants (BeyondTrophy.Look), where not the defaults
    // A cannon cut from a third-person body (Sylux's Shock Coil: no first-person model of its own) names its joints:
    //   keep        the joint whose subtree is kept (a triangle by its main joint; default: Samus's rule, both arms' model
    //               without the left arm and the shoulders)
    //   animate     a regex: the joints the animations move; the rest hold the idle's first frame (the body clips move the
    //               whole body, the first-person arm only its cannon). Default: every joint an animation carries
    //   barrel / top   regexes: the joints whose triangles give the barrel axis / its up side (fps_gun, top_rail|top_panel)
    //   elbow / shoulder   the joints behind the barrel: its back end / which way it points (fps_gun_bind, fps_shoulder_r_bind)
    //   muzzle      a joint at the muzzle (its idle position, on the barrel axis); default: the front tip's centre
    //   up          [x, y, z]: the cannon's up side as a model direction in the idle pose, in place of the top joints
    //               (a body aiming its arm: the world's up)
    //   size        the gun's size as a multiple of Prime 1's (gun.cfg "scale"; default 1): a cut cannon sized to the hunter's
    //               own MPH gun (Sylux's SyluxGun is longer and stouter than SamusGun), its cut end pushed off screen
    //   aim         [yaw, pitch] degrees: the barrel turned about the muzzle after placing (the hunter's own gun's angle on
    //               screen; Sylux's comes in from the lower right)
    //   idleRest    true: the idle holds the moving ("animate") joints at the rest pose, the cannon as the third-person body
    //               wears it (the other roles still animate them)
    //   upDown      "hold": MPH's raise/lower state (unmorphing, the idle lowering) holds the gun's last pose instead of
    //               playing fromHolster (gun.cfg "updown hold"), for a gun whose switch-in clip is no raise
    //   glowTint    false: no "glow" line in gun.cfg -- the lights keep their own colour, not MPH's per-weapon one (a gun
    //               whose MPH model has no light material to read the colour from: SyluxGun)
    // TODO (with the Beyond pak/SMDL readers): "modelSha1", the model resource's hash -- its raw bytes are not on the PC
    // yet (only the Model Dumper's glTF of it), so it cannot be authored now.
    public sealed class Gun4Recipe
    {
        public string Name = "", Game = "", Pak = "", Chpr = "", ChprSha1 = "", Model = "", ModelName = "", Character = "";
        public string Meshes = "auto";
        public string Idle = "powerBasePosition";
        public readonly List<(string Role, string Anim)> Roles = new();
        public string[] Paks = Array.Empty<string>();
        public string SkinChpr = "";
        public readonly Dictionary<string, double> Look = new(StringComparer.Ordinal);
        public string Keep = "", Animate = "", Barrel = "^fps_gun", Top = "top_rail|top_panel", Elbow = "fps_gun_bind",
            Shoulder = "fps_shoulder_r_bind", MuzzleJoint = "";
        public double[]? Up;
        public double Size = 1;
        public double[]? Aim;
        public bool IdleRest, GlowTint = true, UpDownHold;

        public static bool Applies(ImportRecipe r) => r.Game == "mp4";

        public string IdleAnim
        {
            get
            {
                foreach ((string role, string anim) in Roles)
                {
                    if (role == Idle) return anim;
                }
                throw new InvalidDataException($"guns/{Name}: the idle role '{Idle}' is not in roles");
            }
        }

        public static Gun4Recipe From(ImportRecipe r)
        {
            var g = new Gun4Recipe
            {
                Name = r.Name, Game = r.Game, Pak = r.OptStr("pak") ?? "", Chpr = r.Str("chpr"), ChprSha1 = (r.OptStr("chprSha1") ?? "").ToLowerInvariant(),
                Model = r.Str("model"), ModelName = r.OptStr("modelName") ?? "", Character = r.OptStr("character") ?? "",
                Meshes = r.OptStr("meshes") ?? "auto", Idle = r.OptStr("idle") ?? "powerBasePosition",
            };
            if (!r.Root.TryGetProperty("roles", out JsonElement roles)) throw new InvalidDataException($"{r.Id}: no roles");
            foreach (JsonElement pair in roles.EnumerateArray())
            {
                if (pair.GetArrayLength() != 2) throw new InvalidDataException($"{r.Id}: roles entries are [role, animation]");
                g.Roles.Add((pair[0].GetString() ?? "", pair[1].GetString() ?? ""));
            }
            g.Paks = r.Root.TryGetProperty("paks", out JsonElement pk) ? pk.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : g.Pak.Length > 0 ? new[] { g.Pak } : Array.Empty<string>();
            g.SkinChpr = (r.OptStr("skinChpr") ?? "").ToLowerInvariant();
            g.Keep = r.OptStr("keep") ?? ""; g.Animate = r.OptStr("animate") ?? ""; g.MuzzleJoint = r.OptStr("muzzle") ?? "";
            g.Barrel = r.OptStr("barrel") ?? g.Barrel; g.Top = r.OptStr("top") ?? g.Top;
            g.Elbow = r.OptStr("elbow") ?? g.Elbow; g.Shoulder = r.OptStr("shoulder") ?? g.Shoulder;
            if (r.Root.TryGetProperty("up", out JsonElement up))
            {
                g.Up = up.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (g.Up.Length != 3) throw new InvalidDataException($"{r.Id}: \"up\" is [x, y, z]");
            }
            if (r.Root.TryGetProperty("size", out JsonElement sz)) g.Size = sz.GetDouble();
            if (r.Root.TryGetProperty("aim", out JsonElement am))
            {
                g.Aim = am.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (g.Aim.Length != 2) throw new InvalidDataException($"{r.Id}: \"aim\" is [yaw, pitch]");
            }
            if (r.Root.TryGetProperty("idleRest", out JsonElement ir)) g.IdleRest = ir.GetBoolean();
            if (r.Root.TryGetProperty("glowTint", out JsonElement gt)) g.GlowTint = gt.GetBoolean();
            if (r.Root.TryGetProperty("upDown", out JsonElement ud)) g.UpDownHold = ud.GetString() == "hold";
            if (r.Root.TryGetProperty("look", out JsonElement lk)) foreach (JsonProperty kv in lk.EnumerateObject()) g.Look[kv.Name] = kv.Value.GetDouble();
            if (g.ChprSha1.Length != 40) throw new InvalidDataException($"{r.Id}: chprSha1 must be a SHA-1");
            _ = g.IdleAnim;
            return g;
        }
    }
}
