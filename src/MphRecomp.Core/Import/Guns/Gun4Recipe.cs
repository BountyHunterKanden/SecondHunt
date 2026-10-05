using System;
using System.Collections.Generic;
using System.IO;
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
    //   roles       [[MPH gun state (GunViewmodel's names), Beyond animation], ...] in gun.bin's order
    // TODO (with the Beyond pak/SMDL readers): "modelSha1", the model resource's hash -- its raw bytes are not on the PC
    // yet (only the Model Dumper's glTF of it), so it cannot be authored now.
    public sealed class Gun4Recipe
    {
        public string Name = "", Game = "", Pak = "", Chpr = "", ChprSha1 = "", Model = "", ModelName = "", Character = "";
        public string Meshes = "auto";
        public string Idle = "powerBasePosition";
        public readonly List<(string Role, string Anim)> Roles = new();

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
            if (g.ChprSha1.Length != 40) throw new InvalidDataException($"{r.Id}: chprSha1 must be a SHA-1");
            _ = g.IdleAnim;
            return g;
        }
    }
}
