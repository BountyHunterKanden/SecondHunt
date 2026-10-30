using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRead.Editor;
using MphRead.Entities;
using MphRead.Formats.Collision;
using MphRead.Utility;
using MphRecomp.Arenas;

namespace MphRecomp.FirstHunt
{
    // A First Hunt level's entities as it loads (docs/FIRST_HUNT_PORT.md). MphRead reads them as its First Hunt classes,
    // most of which only draw; the ones whose MPH descendants already behave like First Hunt's are swapped for those:
    //   - jump pads -> MphRead's JumpPadEntity with First Hunt's push (FirstHuntArena.KeepPushBit; step 1 measured the
    //     launches against the cart in BizHawk)
    //   - morph cameras -> MorphCameraEntity
    // The import writes them per level beside the cart's entity file (<level>_Ent.bin -> <level>_Ent.mph.bin, MPH format).
    public static class FirstHuntRoom
    {
        public const string MphSuffix = ".mph.bin";

        // at import: the swapped entities of one level's entity file (the cart's, First Hunt format), as an MPH entity file
        public static byte[] ConvertEntities(string fhEntityFile)
        {
            List<EntityEditorBase> all = Repack.FhEntitiesAsMph(fhEntityFile);
            var keep = new List<EntityEditorBase>();
            foreach (EntityEditorBase e in all)
            {
                if (e is JumpPadEntityEditor pad)
                {
                    pad.Unused28 |= FirstHuntArena.KeepPushBit;
                    keep.Add(pad);
                }
                else if (e is MorphCameraEntityEditor)
                {
                    keep.Add(e);
                }
            }
            return Repack.PackEntities(keep);
        }

        public static string MphEntityPath(string fhEntityFile) =>
            fhEntityFile.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ? fhEntityFile[..^4] + MphSuffix : fhEntityFile + MphSuffix;

        // the FIRST HUNT tab (owner 2026-10-21): Second Hunt's look for the swapped objects (MPH's pad models) instead of
        // First Hunt's own; FirstHuntGame.ApplyOptions
        public static bool ShObjects { get; set; }

        static bool _hooked;
        static RoomMetadata? _loading;

        // the First Hunt level about to load (CampaignHost.StartFirstHunt): its ids (0-9) are MPH rooms' too, so the room
        // load hook can't tell it by id; taken by that one load
        public static void Loading(RoomMetadata meta)
        {
            Hook();
            _loading = meta;
        }

        // swap them in as a First Hunt level loads, and step 1's First Hunt ball rules (PlayerEntity.HostFhBallRules: the
        // maze lids, the ball pads) and P1's player rules (PlayerEntity.HostFirstHunt: values, weapons, ammo, items) on
        // for it (off for any other room)
        static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(EchoesArena).TypeHandle);
            JumpPadEntity.HostKeepPushBit = FirstHuntArena.KeepPushBit;
            var next = SceneSetup.HostExtraEntities;
            SceneSetup.HostExtraEntities = (scene, room, entities) =>
            {
                if (next != null) entities = next(scene, room, entities);
                RoomMetadata? meta = _loading;
                _loading = null;
                PlayerEntity.HostFirstHunt = meta != null && meta.Id == room;
                if (meta == null || meta.Id != room)
                {
                    FirstHuntMission.Stop();
                    return entities;
                }
                PlayerEntity.HostFhBallRules = true;
                // the level's match rules (P2: training games' clocks, scores, ends; FirstHuntMission)
                FirstHuntMission.Start(meta.Name);
                // First Hunt's player values and weapons (P1; FirstHuntRules, from the cart's arm9: FirstHuntGame.Register)
                if (FirstHuntRules.Weapons != null) Weapons.Current = FirstHuntRules.Weapons;
                // MphRead's First Hunt room table has kill height 0, under some levels' floors (MP5's walkway at -0.1):
                // 5 below the lowest collision, as step 1's arenas (todo: First Hunt's own rule, if it has one)
                meta.KillHeight = KillHeight(meta);
                return meta.EntityPath != null ? Swap(scene, meta.EntityPath, entities) : entities;
            };
        }

        static readonly Dictionary<string, float> KillHeights = new();

        static float KillHeight(RoomMetadata meta)
        {
            if (KillHeights.TryGetValue(meta.Name, out float k)) return k;
            CollisionInstance col = Collision.GetCollision(meta);
            float min = float.MaxValue;
            foreach (OpenTK.Mathematics.Vector3 v in col.Info.Points) min = MathF.Min(min, v.Y);
            k = min == float.MaxValue ? meta.KillHeight : min - 5;
            KillHeights[meta.Name] = k;
            return k;
        }

        static IReadOnlyList<EntityBase> Swap(Scene scene, string entityPath, IReadOnlyList<EntityBase> entities)
        {
            string file = MphEntityPath(Path.Combine(Paths.FhFileSystem, entityPath.Replace('\\', Path.DirectorySeparatorChar)));
            if (!File.Exists(file)) return entities;
            var fhPads = entities.OfType<FhJumpPadEntity>().ToDictionary(p => p.Id);
            var list = entities.Where(e => e.Type != EntityType.FhJumpPad && e.Type != EntityType.FhMorphCamera).ToList();
            foreach (Entity e in Read.GetEntitiesFromPath(file, -1, firstHunt: false))
            {
                if (e is Entity<JumpPadEntityData> pad)
                {
                    var jumpPad = new JumpPadEntity(pad.Data, pad.NodeName, scene);
                    // First Hunt's own pad models unless the FIRST HUNT tab picks Second Hunt's objects
                    if (!ShObjects && fhPads.TryGetValue(jumpPad.Id, out FhJumpPadEntity? fh)) jumpPad.HostFirstHuntModels(fh.Ball);
                    list.Add(jumpPad);
                }
                else if (e is Entity<MorphCameraEntityData> cam) list.Add(new MorphCameraEntity(cam.Data, cam.NodeName, scene));
            }
            return list;
        }
    }
}
