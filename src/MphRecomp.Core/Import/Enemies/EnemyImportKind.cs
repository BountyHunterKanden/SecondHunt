using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MphRecomp.Import.Bodies;
using MphRecomp.Import.Effects;
using MphRecomp.Import.Hd;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Enemies
{
    // The "enemies" item kind (docs/ENEMY_IMPORT.md K1/E1): recipes/enemies/<Name>.json -> <root>/enemies/<Name>/, one
    // Retro enemy read from the user's own disc at import time, nothing of it in the repo:
    //   body/        the model, skin and clips (BodyImporter, the "bodies" kind's export)
    //   body/fx/     its clips' particle effects and sounds in BodyFx's fx.cfg shape (Echoes: the ANCS's inline event sets,
    //                EchoesEvents; each particle's bone id named through the CINF), plus "part" lines for the death effect
    //   enemy.cfg    the reference placement's numbers: every leaf property of the script instance as "p <id path> <hex>"
    //                (ids only: the property names never ship), and type / area / instance / fsm lines
    //   state.fsm2   the AI state machine the instance names (its Patterned StateMachine2), raw; Core Campaign/Retro runs it
    // Recipe (HdRecipe's game/discId/paks/ancs/char, plus):
    //   "type": "SPTR", "area": "<MREA id>", "instance": "<script instance id>",
    //   "sfx": { "lookup": "<ATBL in AudioGrp.pak>", "group": ["<AGSC>", ...] }
    public sealed class EnemyImportKind : IImportKind
    {
        public string Kind => "enemies";

        public const string ConfigFile = "enemy.cfg", FsmFile = "state.fsm2", BodyDir = "body";

        // Patterned (CPatterned's property struct) and the two ids in it the export follows
        public const uint Patterned = 0xB3774750, StateMachine2 = 0xC1C7E255, GibParticles = 0x684F00C9;

        public IEnumerable<(string Path, string Sha1)> Sources(ImportRecipe recipe)
        {
            foreach (HdRecipe.PakRef p in HdRecipe.From(recipe).Paks) yield return (p.Name, p.Sha1);
        }

        public void Run(ImportContext ctx, ImportRecipe recipe, string outDir, Action<float, string> progress, CancellationToken ct)
        {
            HdRecipe h = HdRecipe.From(recipe);
            if (h.Game != "mp2") throw new NotSupportedException($"{recipe.Id}: enemies from game '{h.Game}' are not importable yet");
            Pak[] paks = h.Paks.Select(p => ctx.Pak(p.Name)).ToArray();
            Export(id => Res(paks, h, id), recipe, outDir, progress, ct);
        }

        static byte[] Res(Pak[] paks, HdRecipe h, uint id)
        {
            foreach (Pak p in paks)
            {
                if (p.Contains(id)) return p.Get(id);
            }
            throw new KeyNotFoundException($"{id:x8} is in none of {String.Join(", ", h.Paks.Select(p => p.Name))}");
        }

        public static void Export(Func<uint, byte[]> res, ImportRecipe recipe, string outDir, Action<float, string>? progress, CancellationToken ct)
        {
            HdRecipe h = HdRecipe.From(recipe);
            Directory.CreateDirectory(outDir);
            // the script instance first: a wrong area or id fails before the long body export
            uint area = recipe.Hex("area"), instance = recipe.Hex("instance");
            string type = recipe.Str("type");
            Mrea.ScriptObject obj = Mrea.Read(res(area)).ReadScripts().FirstOrDefault(o => o.Id == instance && o.Type == type)
                ?? throw new InvalidDataException($"{recipe.Id}: no {type} {instance:x8} in area {area:x8}");
            Mrea.Property pat = obj.Root.Find(Patterned) ?? throw new InvalidDataException($"{recipe.Id}: {instance:x8} has no Patterned struct");
            uint fsm = pat.Find(StateMachine2)?.U32 ?? throw new InvalidDataException($"{recipe.Id}: {instance:x8} names no state machine");
            uint gib = pat.Find(GibParticles)?.U32 ?? UInt32.MaxValue;
            byte[] fsmRaw = res(fsm);
            Fsm.Load(fsmRaw);   // it parses (throws otherwise)
            File.WriteAllBytes(Path.Combine(outDir, FsmFile), fsmRaw);
            var cfg = new StringBuilder();
            cfg.Append("# the enemy's own numbers, read from the disc (Import/Enemies/EnemyImportKind.cs)\n");
            cfg.Append(Inv($"type {type}\narea {area:x8}\ninstance {instance:x8}\nfsm {fsm:x8}\n"));
            if (gib != UInt32.MaxValue && gib != 0) cfg.Append(Inv($"gib {gib:x8}\n"));
            Leaves(obj.Root, "", cfg);
            File.WriteAllText(Path.Combine(outDir, ConfigFile), cfg.ToString());
            progress?.Invoke(0.1f, "body");
            string body = Path.Combine(outDir, BodyDir);
            BodyImporter.Export(res, h, body, progress == null ? null : (f, s) => progress(0.1f + 0.7f * f, s), ct);
            progress?.Invoke(0.85f, "effects");
            ExportFx(res, recipe, h, gib, body);
            progress?.Invoke(1, "done");
        }

        static void Leaves(Mrea.Property p, string path, StringBuilder cfg)
        {
            foreach (Mrea.Property c in p.Children ?? new())
            {
                string at = path.Length == 0 ? $"{c.Id:x8}" : $"{path}/{c.Id:x8}";
                if (c.Children != null) Leaves(c, at, cfg);
                else cfg.Append($"p {at} {Convert.ToHexString(c.Data).ToLowerInvariant()}\n");
            }
        }

        // Echoes' clip effects in BodyFx's fx.cfg shape (BodyFx.Config.Load reads it back)
        static void ExportFx(Func<uint, byte[]> res, ImportRecipe recipe, HdRecipe h, uint gib, string bodyDir)
        {
            string dir = Path.Combine(bodyDir, "fx");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            byte[] ancsRaw = res(h.Ancs);
            Ancs a = Ancs.Parse(ancsRaw);
            Ancs.AnimSet set = a.Set ?? throw new InvalidDataException($"{h.Ancs:x8}: no animation set");
            if (set.EventsAt < 0) throw new InvalidDataException($"{h.Ancs:x8}: no inline event sets (not an Echoes ANCS?)");
            EchoesEvents ev = EchoesEvents.Read(ancsRaw, set.EventsAt);
            Cinf cinf = Cinf.Load(res(Ancs.FindCharacter(ancsRaw, h.Char).Cinf));
            string BoneName(uint id) => cinf.Bones.TryGetValue(id, out Cinf.Bone? b) && !String.IsNullOrEmpty(b.Name) ? b.Name! : $"bone{id}";
            var events = new List<BodyFx.Event>();
            var sounds = new List<BodyFx.Sound>();
            foreach ((string clip, Ancs.MetaAnim meta) in set.Anims)
            {
                if (ev.For(meta) is not EchoesEvents.Set s) continue;
                foreach (EchoesEvents.Particle p in s.Particles)
                {
                    events.Add(new BodyFx.Event(clip, p.Base.Time, (int)p.Frames, BoneName(p.Bone), p.Part, (int)p.Mode, p.Scale));
                }
                foreach (EchoesEvents.Sound p in s.Sounds) sounds.Add(new BodyFx.Sound(clip, p.Base.Time, p.Id, p.Base.Name.Replace(' ', '_')));
            }
            var extra = new List<uint>();
            if (gib != UInt32.MaxValue && gib != 0) extra.Add(gib);
            // the PARTs (and their child systems) and their textures
            var done = new HashSet<uint>();
            var textures = new SortedSet<uint>();
            var todo = new Queue<uint>(events.Select(e => e.Part).Concat(extra));
            while (todo.Count > 0)
            {
                uint id = todo.Dequeue();
                if (!done.Add(id)) continue;
                byte[] b = res(id);
                // an electric effect (ELSM, Grenchler's beam) is kept beside the particles for its own drawing, not read
                if (b.Length >= 4 && b[0] == 'E' && b[1] == 'L' && b[2] == 'S' && b[3] == 'M')
                {
                    File.WriteAllBytes(Path.Combine(dir, $"{id:x8}.elsm"), b);
                    continue;
                }
                Part part = Part.Load(b, echoes: true);
                File.WriteAllBytes(Path.Combine(dir, $"{id:x8}.part"), b);
                foreach ((string t, uint dep) in part.Dependencies())
                {
                    if (t == "PART") todo.Enqueue(dep);
                    else if (t == "TXTR") textures.Add(dep);
                    else if (t == "SWHC" && !File.Exists(Path.Combine(dir, $"{dep:x8}.swhc")))
                    {
                        byte[] w = res(dep);
                        if (Part.Swoosh.Load(w).Texture is uint wt and not 0) textures.Add(wt);
                        File.WriteAllBytes(Path.Combine(dir, $"{dep:x8}.swhc"), w);
                    }
                }
            }
            foreach (uint t in textures)
            {
                byte[]? rgba = Txtr.Decode(res(t), out uint fmt, out int w, out int hh);
                if (rgba == null) throw new InvalidDataException($"texture {t:x8}: format {fmt} is not decoded");
                PngOut.Write(Path.Combine(dir, $"{t:x8}.png"), rgba, w, hh);
            }
            var cfg = new StringBuilder();
            cfg.Append("# the enemy's clip effects (Import/Enemies/EnemyImportKind.cs, Echoes inline events)\n");
            foreach (BodyFx.Event e in events) cfg.Append(Inv($"event {e.Clip} {e.Time:R} {e.Frames} {e.Bone} {e.Part:x8} {e.Mode} {e.Scale:R}\n"));
            foreach (BodyFx.Sound s in sounds) cfg.Append(Inv($"sound {s.Clip} {s.Time:R} {s.Id} {s.Name}\n"));
            foreach (uint id in extra) cfg.Append($"part {id:x8}\n");
            if (recipe.Root.TryGetProperty("sfx", out JsonElement sfx))
            {
                short[] lookup = MusyX.Lookup(res(recipe.HexOf(sfx.GetProperty("lookup"))));
                JsonElement g = sfx.GetProperty("group");
                Agsc[] groups = (g.ValueKind == JsonValueKind.Array ? g.EnumerateArray() : new[] { g }.AsEnumerable())
                    .Select(x => Agsc.Parse(res(recipe.HexOf(x)))).ToArray();
                foreach (uint id in new SortedSet<uint>(sounds.Select(x => x.Id)))
                {
                    int m = MusyX.Translate(lookup, id);
                    if (m < 0) continue;
                    Agsc.Rendered? r = groups.Select(x => x.Render((ushort)m)).FirstOrDefault(x => x != null && x.Pcm.Length > 0);
                    if (r == null) continue;
                    MusyX.WriteWav(Path.Combine(dir, BodyFx.SfxFile(id)), r.Pcm, r.Rate);
                    cfg.Append(Inv($"sfx {id} {m}\n"));
                }
            }
            File.WriteAllText(Path.Combine(dir, "fx.cfg"), cfg.ToString());
        }

        static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    }

    static class RecipeHex
    {
        public static uint HexOf(this ImportRecipe _, JsonElement e) => UInt32.Parse(e.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
