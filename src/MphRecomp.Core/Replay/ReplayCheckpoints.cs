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
        public const int DefaultEvery = 60; // 1 s of play (B4: a seek re-runs at most this far, ~1.9 ms a step on the Odin; was
        // 5 s before checkpoints shared their unchanged objects -- an 8-player 7-minute match is now ~150 MB at 1 s, was
        // 139 MB at 5 s; a longer one thins to 2 s)
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

        // (thread B keyframes) the step-0 base two copies of a match share: taken right after the match starts, once the
        // statics earlier matches in this process left behind are put back as a fresh process has them
        public static SimSnapshot.KeyframeBase KeyframeBase(CampaignHost host)
        {
            SetUpOnce();
            ResetProcessHistory();
            var kb = new SimSnapshot.KeyframeBase(SimSnapshot.Take(0, Roots(host)));
            return kb;
        }

        // statics that still hold the last match's objects but are always set before they're read: cleared, so the match is
        // the same and the walk no longer reaches the old match through them
        static void ResetProcessHistory()
        {
            // the ricochet's scratch equip: its pool is set from the hitting beam's equip before each spawn
            var ricochet = (EquipInfo)typeof(MphRead.Entities.BeamProjectileEntity).GetField("_ricochetEquip",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            ricochet.Beams = null!;
            ricochet.Weapon = null!; // (and its weapon, set with it)
            // (PlacedPickups._placed and any other static list: their spare room)
            SimSnapshot.TrimStaticLists();
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
            // (thread B keyframes) the Prime map's 3D pixels: draw-only, repainted after a load
            SimSnapshot.KeyframeSkipsContents.Add(typeof(MphRead.Entities.PlayerEntity).GetField("_mapRoom3D",
                BindingFlags.NonPublic | BindingFlags.Instance)!);
            // the weapon tables (readonly metadata the walk doesn't read): the same objects in every copy
            SimSnapshot.KeyframeLeafPools.Add(() => new[]
            {
                Weapons.Weapons1P, Weapons.WeaponsMP, Weapons.EnemyWeapons, Weapons.BossWeapons, Weapons.GoreaWeapons,
                Weapons.PlatformWeapons, Weapons.Ricochets, PirateBlaster.All, Paralyzer.All, HyperBeam.All

            }.SelectMany(l => l));
            // a model loaded mid-match: the loading copy's own, from its file cache (or loaded there by the same name)
            var models = (Dictionary<string, Model>)typeof(Read).GetField("_modelCache", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
            var fhModels = (Dictionary<string, Model>)typeof(Read).GetField("_fhModelCache", BindingFlags.NonPublic
                | BindingFlags.Static)!.GetValue(null)!;
            SimSnapshot.KeyframeResolvers[typeof(Model)] = (o =>
            {
                var m = (Model)o;
                Dictionary<string, Model> cache = m.FirstHunt ? fhModels : models;
                return cache.TryGetValue(m.Name, out Model? c) && c == m ? (m.FirstHunt ? "fh:" : "") + m.Name : null;
            }, key =>
            {
                bool fh = key.StartsWith("fh:", StringComparison.Ordinal);
                string name = fh ? key[3..] : key;
                if ((fh ? fhModels : models).TryGetValue(name, out Model? c)) return c;
                try
                {
                    return Read.GetModelInstance(name, fh).Model;
                }
                catch (Exception) when (!fh)
                {
                    return Read.GetRoomModelInstance(name).Model;
                }
            });
        }

        // movies (decoder + frames) only draw
        static readonly string[] MphReadExcludedNamespaces = { "MphRead.Formats.Vx" };

        // the loaded files' data that never changes after loading (a model's display lists, texels, palettes, raw nodes,
        // the metadata tables) and what only the renderer uses (RenderItem: rebuilt every frame)
        static IEnumerable<Type> MphReadExcludedTypes() => new[]
        {
            typeof(RenderInstruction), typeof(TextureData), typeof(PaletteData), typeof(RawNode), typeof(RenderItem),
            typeof(StringTableEntry), typeof(RoomMetadata), typeof(WeaponInfo), typeof(MphRead.Formats.VxDecoder), typeof(MphRead.Text.Font),
            // a model's recolors: loaded data, and the app adds look-alike sets to them (Read.HostExtraRecolors) that a
            // headless prepare doesn't (thread B keyframes: the viewer and the prepare walked differently)
            typeof(Recolor)
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
                // the HUD sprite table (interned glyph / meter pixels): the app's GPU copies are keyed by its indices, so a
                // restore must not shrink it -- the next new sprite would take an index whose texture is another sprite's
                // (letters and wrong pieces in the HUD after a seek)
                (typeof(Scene), "_hudSprites"), (typeof(Scene), "_hudSpriteIndex"), (typeof(Scene), "_hudSpriteRefs"),
                (typeof(Scene), "_textureCount"), (typeof(Scene), "_shaderProgramId"), (typeof(Scene), "_rttShaderProgramId"),
                (typeof(Scene), "_shiftShaderProgramId"), (typeof(Scene), "_frameBuffer"), (typeof(Scene), "_screenTexture"),
                (typeof(Scene), "_renderBuffer"),
                (typeof(Model), "_nextId"), (typeof(Read), "_modelCache"), (typeof(Read), "_fhModelCache"),
                // (thread B keyframes) caches and scratch pools that keep whatever earlier matches in this process left in
                // them: not match state, and they made two copies of one match walk differently. The pools' items are
                // reset before each use (CollisionDetection clears its candidates per query, _seenData per check); a
                // meter or a cached file still reachable from the match is walked from there.
                (typeof(MphRead.Formats.AiPersonality), "_data1Cache"), (typeof(MphRead.Formats.AiPersonality), "_data2Cache"),
                (typeof(MphRead.Formats.AiPersonality), "_data3Cache"), (typeof(MphRead.Formats.AiPersonality), "_data4Cache"),
                (typeof(MphRead.Formats.AiPersonality), "_data5Cache"),
                // (a cached subtree's parent / label: rewritten by whichever personality used it last; debug output only)
                (typeof(MphRead.Formats.AiPersonalityData1), "Parent"), (typeof(MphRead.Formats.AiPersonalityData1), "Label"),
                (typeof(MphRead.Formats.CollisionDetection), "_activeItems"), (typeof(MphRead.Formats.CollisionDetection), "_inactiveItems"),
                (typeof(MphRead.Formats.CollisionDetection), "_tempItems"), (typeof(MphRead.Formats.CollisionDetection), "_seenData"),
                (typeof(MphRead.Formats.Collision.Collision), "_cache"), (typeof(MphRead.Formats.Collision.Collision), "_fhCache"),
                (typeof(MphRead.Hud.HudElements), "AmmoBars"), (typeof(MphRead.Hud.HudElements), "MainHealthbars"),
                (typeof(MphRead.Hud.HudElements), "SubHealthbars"),
                (typeof(MphRead.Text.Strings), "_cache"), (typeof(FirstHuntHud), "_cache"),
                // (an Echoes arena's intro view, cached by wall-clock time; the menu thumbnails' refresh queue)
                (typeof(MphRecomp.Arenas.EchoesArena), "_views"),
                (typeof(MphRecomp.Arenas.ArenaThumbnail), "_refreshQueued"), (typeof(MphRecomp.Arenas.ArenaThumbnail), "_refresh"),
                // (set again from the host every frame before the sim runs; at step 0 it's whatever the last match left)
                (typeof(MphRead.Entities.PlayerEntity), "HostLagShift"),
                // (thread B keyframes) the app's hooks and the caches it fills from the menus: a headless prepare has none
                // of them, so the viewer's walk differed and every prepared keyframe was refused; not match state
                (typeof(MphRead.Entities.BeamProjectileEntity), "HostSpawned"), (typeof(MphRead.Entities.RoomEntity), "HostTransitionTrace"),
                (typeof(Read), "HostExtraRecolors"), (typeof(Scene), "HostEffectPoolWarning"), (typeof(Scene), "HostEntityInitTrace"),
                (typeof(Music), "RoomHost"), (typeof(MusicPlayer), "Host"),
                // the music's audio output: the viewer opens a sound device when music plays, the prepare never does
                (typeof(MusicPlayer), "_audioEngine"), (typeof(MusicPlayer), "_playbackDevice"), (typeof(MusicPlayer), "_provider"),
                (typeof(MusicPlayer), "_stream"), (typeof(MusicPlayer), "_player"), (typeof(MphRecomp.Arenas.ArenaMusic), "_host"),
                (typeof(MphRecomp.Multiplayer.HunterColours), "ModelHunter"), (typeof(MphRecomp.Multiplayer.HunterColours), "_start"),
                (typeof(MphRecomp.Multiplayer.LookAlikes), "_built"),
                // (10-28) the look-alike sets the viewer registers before a Trooper / Dark Samus match (MatchSkins ->
                // LookAlikes.Register) and its on switches: the prepare registers none, so a Trooper-skin match refused
                // its keyframes (one more object), and a seek would have put back the prepare's empty sets and On=false
                (typeof(MphRecomp.Multiplayer.LookAlikes), "_sets"), (typeof(MphRecomp.Multiplayer.LookAlikes), "_registered"),
                (typeof(MphRecomp.Multiplayer.LookAlikes), "ModelLooks"), (typeof(MphRecomp.Multiplayer.LookAlikes), "On"),
                (typeof(MphRecomp.Multiplayer.LookAlikes), "Force"),
                // (10-26) left set by a match just played in this process (the Prime map's painters, the arena list's
                // census cache): a replay opened right after its match refused its live-prepared keyframes
                (typeof(MphRead.Entities.PlayerEntity), "HostPrimeMap"), (typeof(MphRead.Entities.PlayerEntity), "HostMapPieceSeen"),
                (typeof(MphRead.Entities.PlayerEntity), "HostPrimeMapWarm"), (typeof(MphRead.Entities.PlayerEntity), "HostPrimeMarker"),
                (typeof(MphRecomp.Multiplayer.MatchArenas), "_census"),
                // (the Echoes arenas' crate spots, filled in whatever order the arenas registered; read-only during a
                // match) and the owned-arenas cache (refreshed by wall-clock time)
                (typeof(MphRecomp.Arenas.EchoesCrates), "_rooms"),
            // the rest of the arenas' per-room registration tables (same rule: written only when an arena registers,
            // in registration order, never by a match)
            (typeof(MphRecomp.Arenas.EchoesBreakables), "_rooms"), (typeof(MphRecomp.Arenas.EchoesTurret), "_rooms"),
            (typeof(MphRecomp.Arenas.EchoesStatics), "_rooms"), (typeof(MphRecomp.Arenas.EchoesMovement), "_water"),
            (typeof(MphRecomp.Arenas.ArenaNav), "_links"), (typeof(MphRecomp.Arenas.ArenaNav), "_versions"),
                (typeof(MphRecomp.Multiplayer.ExtraArenas), "s_owned"), (typeof(MphRecomp.Multiplayer.ExtraArenas), "s_ownedAt"),
                // (10-27) the viewer's display switches, not match state: the headless prepare runs with drawing off,
                // and a prepared keyframe put CollectDrawItems=false back, so nothing drew after a seek (black screen);
                // the watched seat, HUD, reticle, helmet and framing options had the same problem
                (typeof(Scene), "CollectDrawItems"), (typeof(Scene), "Headless"),
                // (10-27) a mesh's draw list: the viewer's GPU list (or host mesh slot), the prepare's is 0 -- put back, the
                // arena drew with the wrong lists after a seek (the owner's "severely glitched" picture)
                (typeof(Mesh), "ListId"),
                // (10-27) the view's size: the prepare's 256x192 put back squashed the 3D view and the HUD to 4:3
                (typeof(Scene), "Size"),
                // (10-27) texture binding ids handed out by this process's renderer: the prepare's ids are other textures
                // in the viewer (the HUD drew and repainted into another sprite's texture after a seek); the pixels are
                // painted again from the restored frame (PlayerEntity.HostAfterRestore)
                (typeof(MphRead.Hud.HudObjectInstance), "BindingId"), (typeof(MphRead.Hud.LayerInfo), "BindingId"),
                (typeof(MphRead.Hud.LayerInfo), "MaskId"),
                (typeof(MphRead.Entities.PlayerEntity), "_trailBindingId1"), (typeof(MphRead.Entities.PlayerEntity), "_trailBindingId2"),
                (typeof(MphRead.Entities.PlayerEntity), "_doubleDmgBindingId"), (typeof(MphRead.Entities.PlayerEntity), "_iceLayerBindingId"),
                (typeof(MphRead.Entities.PlayerEntity), "_helmetBindingId"), (typeof(MphRead.Entities.PlayerEntity), "_helmetDropBindingId"),
                (typeof(MphRead.Entities.PlayerEntity), "_visorBindingId"), (typeof(MphRead.Entities.PlayerEntity), "_scanBindingId"),
                (typeof(MphRead.Entities.PlayerEntity), "s_hostHudFields"),
                (typeof(Scene), "HostFovScale"), (typeof(Scene), "HostFramingAspect"),
                (typeof(MphRead.Entities.PlayerEntity), "HostHideMainPlayer"), (typeof(MphRead.Entities.PlayerEntity), "HostHideSeat"),
                (typeof(MphRead.Entities.PlayerEntity), "HostFirstPersonSeat"), (typeof(MphRead.Entities.PlayerEntity), "HostFirstPersonTurn"),
                (typeof(MphRead.Entities.PlayerEntity), "HostMainSpectated"), (typeof(MphRead.Entities.PlayerEntity), "HostHideReticle"),
                (typeof(MphRead.Entities.PlayerEntity), "HostHideScore"), (typeof(MphRead.Entities.PlayerEntity), "HostNoHelmet"),
                (typeof(MphRead.Entities.PlayerEntity), "HostScanVisorOpacity"), (typeof(MphRead.Entities.PlayerEntity), "HostHudSeat"),
                (typeof(MphRead.Entities.PlayerEntity), "HostScoreboardHeld"), (typeof(MphRead.Entities.PlayerEntity), "HostNoViewer"),
                (typeof(MphRead.Entities.PlayerEntity), "HostOutsideHead"), (typeof(MphRead.Entities.BeamProjectileEntity), "HostDrawFade")
            };
            // every static Host* hook (Action / Func): the app wires them, a headless prepare doesn't, and they hold app
            // objects, never match state -- named by rule so the next hook added doesn't break the walk again
            foreach (Type t in SafeTypes(typeof(MphRead.Entities.PlayerEntity).Assembly).Concat(SafeTypes(typeof(ReplayCheckpoints).Assembly)))
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    string n = f.Name.StartsWith('<') ? f.Name[1..] : f.Name;
                    if (n.StartsWith("Host", StringComparison.Ordinal) && typeof(Delegate).IsAssignableFrom(f.FieldType)) yield return f;
                }
            }
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
