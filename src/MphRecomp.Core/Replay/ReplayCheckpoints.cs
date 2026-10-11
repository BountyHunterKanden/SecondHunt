using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MphRead;
using MphRecomp.Campaign;

namespace MphRecomp.Replay
{
    // A replay's checkpoints (T9): a SimSnapshot every Every steps as the replay plays (each one the first time it gets
    // there), so seeking back restores the nearest one at or before the target instead of starting the match again.
    //   var cps = new ReplayCheckpoints();
    //   after each checked step: cps.AfterStep(player.Position, host);
    //   seek back to s: if (cps.Before(s) is SimSnapshot snap) { snap.Restore(); player.Rewind(snap.Step); } else restart
    // A restored match is checked every step like any other (exact or refused); one whose check fails turns checkpoints off
    // for the rest of the replay (Broken) and the caller restarts instead.
    public sealed class ReplayCheckpoints
    {
        public const int DefaultEvery = 5 * 60; // 5 s of play
        // past this many bytes every other checkpoint goes and they're taken half as often (a long match's memory stays
        // bounded; seeking back then re-runs up to twice as far)
        public const long DefaultBudget = 160L << 20;
        public int Every { get; private set; }
        public long Budget { get; set; } = DefaultBudget;
        public bool Broken { get; private set; }
        public int Count => _snaps.Count;
        public long Bytes => _snaps.Values.Sum(s => s.Bytes);
        public double LastTakeMs { get; private set; }
        readonly SortedList<int, SimSnapshot> _snaps = new();
        SimSnapshot? _last;

        public ReplayCheckpoints(int every = DefaultEvery)
        {
            Every = every;
            SetUpOnce();
        }

        // after a checked step: a checkpoint at each multiple of Every not taken yet (step 0 = before the first step)
        public void AfterStep(int position, CampaignHost host)
        {
            if (Broken || position % Every != 0 || _snaps.ContainsKey(position)) return;
            _last = SimSnapshot.Take(position, Roots(host), _last);
            LastTakeMs = _last.TakeMs;
            _snaps[position] = _last;
            if (Bytes > Budget) Thin();
        }

        void Thin()
        {
            Every *= 2;
            foreach (int at in _snaps.Keys.Where(k => k % Every != 0).ToList())
            {
                if (_snaps[at] != _last) _snaps.Remove(at); // (the newest stays: the next one shares its arrays)
            }
        }

        // the latest checkpoint at or before step (null: none)
        public SimSnapshot? Before(int step)
        {
            if (Broken) return null;
            SimSnapshot? best = null;
            foreach ((int at, SimSnapshot s) in _snaps)
            {
                if (at > step) break;
                best = s;
            }
            return best;
        }

        // a restored match didn't play exactly: no more checkpoints this replay
        public void Break()
        {
            Broken = true;
            _snaps.Clear();
            _last = null;
        }

        public static IEnumerable<object> Roots(CampaignHost host)
        {
            yield return host;
        }

        // ---- what a checkpoint holds ----

        // Core's namespaces that hold match state (the rest -- menus, online, imports, updates, replays themselves -- is
        // left alone); all of MphRead is, but for its loaded files and its renderer / audio objects
        static readonly string[] CoreMatchNamespaces =
        {
            "MphRecomp.Campaign", "MphRecomp.Arenas", "MphRecomp.Multiplayer", "MphRecomp.Multiplayer.Net", "MphRecomp.Sim",
            "MphRecomp.Game", "MphRecomp.World"
        };

        static bool _setUp;

        public static void SetUpOnce()
        {
            if (_setUp) return;
            _setUp = true;
            var core = typeof(CampaignHost).Assembly;
            var mphRead = typeof(Scene).Assembly;
            IEnumerable<string> coreOther = SafeTypes(core).Select(t => t.Namespace).Where(n => n != null
                && n.StartsWith("MphRecomp", StringComparison.Ordinal) && !CoreMatchNamespaces.Contains(n)).Distinct()!;
            SimSnapshot.SetUp(new[] { mphRead, core }, coreOther.Concat(MphReadExcludedNamespaces), MphReadExcludedTypes(),
                MphReadExcludedFields());
        }

        // movies (decoder + frames) only draw
        static readonly string[] MphReadExcludedNamespaces = { "MphRead.Formats.Vx" };

        // the loaded files' data that never changes after loading (a model's display lists, texels, palettes, raw nodes,
        // the metadata tables) and what only the renderer uses (RenderItem: rebuilt every frame)
        static IEnumerable<Type> MphReadExcludedTypes() => new[]
        {
            typeof(RenderInstruction), typeof(TextureData), typeof(PaletteData), typeof(RawNode), typeof(RenderItem),
            typeof(StringTableEntry), typeof(RoomMetadata), typeof(WeaponInfo), typeof(MphRead.Formats.VxDecoder), typeof(MphRead.Text.Font)
        };

        // single fields of a loaded file's data held next to state (an animation group's frame tables next to its
        // CurrentFrame; a HUD object's graphics next to its animation state), the movie's picture buffers, and the
        // renderer's bookkeeping, kept as it is NOW so ids already handed to the GPU are never handed out again (texture
        // binding ids, the host's baked meshes, model ids, the loaded-model caches)
        static IEnumerable<FieldInfo> MphReadExcludedFields()
        {
            (Type, string)[] fields =
            {
                (typeof(NodeAnimationGroup), "Scales"), (typeof(NodeAnimationGroup), "Rotations"),
                (typeof(NodeAnimationGroup), "Translations"),
                (typeof(TexcoordAnimationGroup), "Scales"), (typeof(TexcoordAnimationGroup), "Rotations"),
                (typeof(TexcoordAnimationGroup), "Translations"),
                (typeof(TextureAnimationGroup), "FrameIndices"), (typeof(TextureAnimationGroup), "TextureIds"),
                (typeof(TextureAnimationGroup), "PaletteIds"),
                (typeof(MaterialAnimationGroup), "Colors"),
                (typeof(MphRead.Hud.HudObjectInstance), "CharacterData"), (typeof(MphRead.Hud.HudObjectInstance), "PaletteData"),
                (typeof(MphRead.Hud.HudObjectInstance), "Texture"),
                (typeof(Scene), "_botImageBuffer"), (typeof(Scene), "_topImageBuffer"),
                (typeof(Scene), "_hostTextures"), (typeof(Scene), "_hostMeshes"), (typeof(Scene), "_texPalMap"),
                (typeof(Scene), "_textureCount"), (typeof(Scene), "_shaderProgramId"), (typeof(Scene), "_rttShaderProgramId"),
                (typeof(Scene), "_shiftShaderProgramId"), (typeof(Scene), "_frameBuffer"), (typeof(Scene), "_screenTexture"),
                (typeof(Scene), "_renderBuffer"),
                (typeof(Model), "_nextId"), (typeof(Read), "_modelCache"), (typeof(Read), "_fhModelCache")
            };
            foreach ((Type type, string name) in fields)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly;
                // (a get-only property's backing field)
                FieldInfo? f = type.GetField(name, flags) ?? type.GetField($"<{name}>k__BackingField", flags);
                if (f != null) yield return f;
            }
        }

        static IEnumerable<Type> SafeTypes(System.Reflection.Assembly a)
        {
            try
            {
                return a.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null)!;
            }
        }
    }
}
