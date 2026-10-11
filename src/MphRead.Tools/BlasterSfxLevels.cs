using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Formats;
using MphRead.Entities;
using MphRead.Formats.Sound;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // -blastersfxlevels [fx=<bodies/MP1PowerTrooper dir>]: peak/RMS (dBFS, after the game's own volumes) of MPH's beam
    // shot/hit sounds next to the Prime pirate shot (548) and hit (547) the app plays for the Pirate Blaster
    // (release 0.2.0 F4 item 4: the blaster's hit sound too quiet), plus MPH's sound ranges 1-2
    internal static class BlasterSfxLevels
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string fx = Path.Combine(Path.GetTempPath(), "mph-bodytest", "bodies", "MP1PowerTrooper", "fx");
            foreach (string arg in args[1..])
            {
                if (arg.StartsWith("fx=")) fx = Path.Combine(arg[3..], "fx");
            }
            // the hit-sound hook (BeamProjectileEntity.HostHitSfx) with the app's flags on: every seat holds the blaster, the
            // bots fire it at each other and at the main player; each hit sound counted next to the hit effects reported
            PlayerEntity.HostOwnPirateBlasterLook = true;
            PlayerEntity.HostOwnPirateBlasterHitSfx = true;
            int sounds = 0, onMain = 0, impacts = 0;
            BeamProjectileEntity.HostHitSfx = b =>
            {
                if (!b.HostBlasterLook) return;
                sounds++;
                if ((b.Position - PlayerEntity.Players[0].Position).Length < 2.5f) onMain++;
            };
            BeamProjectileEntity.HostImpact = (b, c, expired) => { if (b.HostBlasterLook && !expired) impacts++; };
            using (CampaignHost host = CampaignHost.StartMatch(MatchSettings.Quick(GameMode.Battle, "MP3 PROVING GROUND", Hunter.Samus, 3, 2)))
            {
                for (int f = 0; f < 60 * 60; f++)
                {
                    foreach (PlayerEntity p in PlayerEntity.Players)
                    {
                        if (p != null && p.Health > 0 && p.CurrentWeapon != BeamType.PirateBlaster) p.HostPickUpWeapon(ItemType.PirateBlaster);
                    }
                    host.Step(new CampaignInput { SelectWeapon = BeamType.None });
                }
            }
            BeamProjectileEntity.HostHitSfx = null;
            BeamProjectileEntity.HostImpact = null;
            PlayerEntity.HostOwnPirateBlasterLook = false;
            PlayerEntity.HostOwnPirateBlasterHitSfx = false;
            bool ok = sounds > 0 && onMain > 0 && sounds >= impacts;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} 60 s of bots with the blaster: {sounds} hit sounds ({onMain} at the main player), {impacts} hit effects");
            IReadOnlyList<SoundSample> samples = SoundRead.ReadSoundSamples();
            SoundTable table = SoundRead.ReadSoundTables();
            IReadOnlyList<SfxScriptFile> scripts = SoundRead.ReadSfxScriptFiles();
            IReadOnlyList<DgnFile> dgns = SoundRead.ReadDgnFiles();
            IReadOnlyList<Sound3dEntry> ranges = SoundRead.ReadSound3dList();
            for (int i = 0; i < ranges.Count && i < 4; i++)
            {
                Console.WriteLine($"  range {i}: full volume to {ranges[i].FalloffDistance / 4096f:0.0}, silent at {ranges[i].MaxDistance / 4096f:0.0}");
            }
            short[] Pcm(int id)
            {
                SoundSample s = samples[id];
                byte[] raw = s.WaveData.Value;
                if (s.Format == WaveFormat.PCM8) return raw.Select(b => (short)((b - 128) * 256)).ToArray();
                var pcm = new short[raw.Length / 2];
                Buffer.BlockCopy(raw, 0, pcm, 0, pcm.Length * 2);
                return pcm;
            }
            void Print(string name, short[] pcm, float gain)
            {
                if (pcm.Length == 0) { Console.WriteLine($"  {name}: empty"); return; }
                double peak = pcm.Max(x => Math.Abs((int)x)) * gain / 32768.0;
                double rms = Math.Sqrt(pcm.Average(x => (double)x * x)) * gain / 32768.0;
                Console.WriteLine($"  {name}: peak {20 * Math.Log10(peak):0.0} dBFS, rms {20 * Math.Log10(rms):0.0} dBFS (gain {gain:0.00})");
            }
            void Mph(string name, int id)
            {
                if (id == -1) { Console.WriteLine($"  {name}: none"); return; }
                int index = id & 0x3FFF;
                if ((id & 0x8000) != 0)
                {
                    DgnFile d = dgns[index];
                    foreach (DgnFileEntry e in d.Entries.Take(3)) Print($"{name} DGN {d.Name} sample {e.SfxId}", Pcm((int)e.SfxId), 1);
                }
                else if ((id & 0x4000) != 0)
                {
                    SfxScriptFile s = scripts[index];
                    foreach (SfxScriptEntry e in s.Entries.Where(e => (e.SfxData & 0x8000) == 0).Take(3))
                    {
                        Print($"{name} script {s.Name} sample {e.SfxData & 0x3FFF}", Pcm(e.SfxData & 0x3FFF), e.Volume);
                    }
                }
                else
                {
                    Print($"{name} sample {index}", Pcm(index), table.Entries[index].InitialVolume / 127f);
                }
            }
            foreach (BeamType beam in new[] { BeamType.PowerBeam, BeamType.Imperialist })
            {
                int b = (int)beam;
                Mph($"MPH {beam} shot", Metadata.BeamSfx[b, (int)BeamSfx.Shot]);
                Mph($"MPH {beam} hit", Metadata.BeamSfx[b, (int)BeamSfx.Hit]);
            }
            foreach (uint id in new uint[] { 548, 547 })
            {
                string wav = Path.Combine(fx, $"sfx_{id}.wav");
                if (!File.Exists(wav)) { Console.WriteLine($"  Prime {id}: no {wav}"); continue; }
                short[] pcm = MphRecomp.Import.Retro.MusyX.ReadWav(wav, out int rate) ?? Array.Empty<short>();
                Print($"Prime {id} ({(id == 548 ? "shot" : "hit")}, {rate} Hz)", pcm, 1);
            }
        }
    }
}
