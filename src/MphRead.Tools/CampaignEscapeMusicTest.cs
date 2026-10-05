using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Sound;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -escapemusic [room=UNIT4_RM3] [triggerVolume=40] [escapeSeconds=150]: the owner's Arcterra report (2026-10-01): in
    // the escape, the music for the Trace fight in Sic Transit didn't start at the right time. Loads the room on its
    // Escape layer with the escape running (as after the octolith pickup), walks Samus into the volume that starts the
    // hunter's intro camseq, and logs every music call (LoggingMusicHost's [music] lines) next to the frame, the camseq,
    // Music's current/resume ids and encounter bits, and when each bot spawns and dies. Then kills the Guardian first and
    // the hunter second, so both encounter-music endings show up.
    internal static partial class CampaignSim
    {
        private sealed class EscapeSfxLog : SfxInstanceBase
        {
            public Func<string> Where { get; set; } = () => "";

            public override int PlaySample(int id, SoundSource? source, bool? loop, bool noUpdate,
                float recency, bool sourceOnly, bool cancellable)
            {
                string name = id < 0x4000 ? ((SfxId)id).ToString() : $"0x{id:X}";
                if (name.Contains("TRACE") || name.Contains("GUARD") || name.Contains("SNIPER") || name.Contains("DIE")
                    || name.Contains("DEATH"))
                {
                    Console.WriteLine($"  {Where()} sfx {id} {name}");
                }
                return -1;
            }
        }

        public static void EscapeMusic(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT4_RM3";
            int tvId = args.Length >= 3 ? Int32.Parse(args[2]) : 40;
            int escapeSeconds = args.Length >= 4 ? Int32.Parse(args[3]) : 150;
            (RoomMetadata? meta, _) = Metadata.GetRoomByName(room);
            if (meta == null)
            {
                Console.WriteLine($"no room {room}");
                return;
            }
            MusicPlayer.Host = new LoggingMusicHost();
            using CampaignHost host = CampaignHost.Start(room, arriving: false,
                setupSave: s => s.BossFlags = FlagsForPlanet(meta.Id, layer: 1)); // 1 = Escape
            Scene scene = host.Scene;
            int frame = 0;
            System.Reflection.FieldInfo current = typeof(Music).GetField("_currentMusicId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            string last = "";
            var alive = new Dictionary<PlayerEntity, bool>();
            void Watch()
            {
                string state = $"music {current.GetValue(null)}, resume {Music.MusicToResume}, encounter bits 0x{Music.MusicEncounterSuspension:X}, "
                    + $"camseq {CameraSequence.Current?.SequenceId.ToString() ?? "-"}, escape {(GameState.EscapeTimer < 0 ? "off" : $"{GameState.EscapeTimer:0.0} s")}";
                string key = state[..state.IndexOf(", escape", StringComparison.Ordinal)];
                if (key != last)
                {
                    Console.WriteLine($"  f{frame,5} {state}");
                    last = key;
                }
                for (int i = 1; i < PlayerEntity.MaxPlayers; i++)
                {
                    PlayerEntity p = PlayerEntity.Players[i];
                    if (!p.IsBot)
                    {
                        continue;
                    }
                    bool up = p.Health > 0;
                    if (!alive.TryGetValue(p, out bool was) || was != up)
                    {
                        Console.WriteLine($"  f{frame,5} bot {p.Hunter} (spawner #{p.EnemySpawner?.Id}) {(up ? $"OUT, hp {p.Health}" : "down/not spawned")}");
                        alive[p] = up;
                    }
                }
            }
            var sfx = new EscapeSfxLog { Where = () => $"f{frame,5}" };
            var hp = new Dictionary<PlayerEntity, int>();
            void Step(CampaignInput input = default)
            {
                if (Sfx.Instance != sfx)
                {
                    Sfx.SetHost(sfx);
                }
                host.Step(input);
                frame++;
                Watch();
                for (int i = 1; i < PlayerEntity.MaxPlayers; i++)
                {
                    PlayerEntity p = PlayerEntity.Players[i];
                    if (p.IsBot && (!hp.TryGetValue(p, out int was) || was != p.Health))
                    {
                        Console.WriteLine($"  f{frame,5} {p.Hunter} hp {p.Health} at {Fmt(p.Position)}");
                        hp[p] = p.Health;
                    }
                }
            }
            for (int i = 0; i < 3000 && (i < 60 || CameraSequence.Current != null); i++)
            {
                Step();
            }
            // the octolith pickup's escape start (GameState.UpdateEscapeState via an EscapeUpdate1 message)
            typeof(GameState).GetMethod("UpdateEscapeState", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)!.Invoke(null, [escapeSeconds * 30, (int)EscapeState.Escape]);
            Console.WriteLine($"  f{frame,5} escape started ({escapeSeconds} s)");
            for (int i = 0; i < 60; i++)
            {
                Step();
            }
            TriggerVolumeEntity? tv = null;
            foreach (EntityBase e in scene.Entities)
            {
                if (e is TriggerVolumeEntity t && t.Id == tvId)
                {
                    tv = t;
                }
            }
            if (tv == null)
            {
                Console.WriteLine($"  no trigger volume #{tvId}");
                return;
            }
            Console.WriteLine($"  f{frame,5} Samus into trigger volume #{tvId} at {Fmt(tv.Position)}");
            // held at the volume's center until it fires (dropped from there she can fall out of a thin volume)
            object tvVolume = typeof(TriggerVolumeEntity).GetField("_volume", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)!.GetValue(tv)!;
            var center = (Vector3)tvVolume.GetType().GetMethod("GetCenter")!.Invoke(tvVolume, null)!;
            Console.WriteLine($"    volume center {Fmt(center)}");
            for (int i = 0; i < 60 && CameraSequence.Current == null; i++)
            {
                host.PlacePlayer(center, -Vector3.UnitZ);
                Step();
                if (i < 3)
                {
                    object vol = typeof(TriggerVolumeEntity).GetField("_volume", System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance)!.GetValue(tv)!;
                    bool inside = (bool)vol.GetType().GetMethod("TestPoint")!.Invoke(vol, [host.Player.Position])!;
                    Console.WriteLine($"    tv active {tv.Active}, Samus at {Fmt(host.Player.Position)} inside {inside}, spawned {host.Player.LoadFlags}");
                }
            }
            for (int i = 0; i < 1200; i++)
            {
                Step();
            }
            List<PlayerEntity> bots = Enumerable.Range(1, PlayerEntity.MaxPlayers - 1).Select(i => PlayerEntity.Players[i])
                .Where(p => p.IsBot).ToList();
            foreach (Hunter which in new[] { Hunter.Guardian, Hunter.Trace, Hunter.Noxus, Hunter.Sylux, Hunter.Kanden,
                Hunter.Spire, Hunter.Weavel })
            {
                foreach (PlayerEntity b in bots.Where(b => b.Hunter == which))
                {
                    Console.WriteLine($"  f{frame,5} killing {b.Hunter} (hp {b.Health})");
                    for (int t = 0; t < 200 && b.Health > 0; t++)
                    {
                        b.TakeDamage(40, DamageFlags.None, null, host.Player);
                        for (int k = 0; k < 10; k++)
                        {
                            Step();
                        }
                        if (host.Player.Health is > 0 and < 400)
                        {
                            host.Player.Health = 799;
                        }
                    }
                    for (int i = 0; i < 300; i++)
                    {
                        Step();
                    }
                }
            }
            Console.WriteLine($"  f{frame,5} end");
        }
    }
}
