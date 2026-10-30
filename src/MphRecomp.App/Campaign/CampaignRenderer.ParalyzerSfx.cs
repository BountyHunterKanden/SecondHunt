using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRead.Sound;
using MphRecomp.Import;
using MphRecomp.Import.BrawlFx;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The Paralyzer's sounds in Brawl's (owner 2026-10-15: "can we get real brawl sounds for charging, firing, and impact?"),
// from the user's own Brawl disc (recipes/brawl/ParalyzerSounds.json -> brawl/ParalyzerSounds: sounds/<name>.wav and
// sounds.txt), each at Brawl's own volume (its sound entry's 0-127 byte, already in the right range for MPH's: -14 to -18
// dBFS RMS against MPH's beam sounds' -15 to -19):
//   a shot          snd_se_szerosuit_smash_L01 (her Paralyzer shot) at the cannon, once per shot MphRead fires
//   the charge      snd_se_szerosuit_012, looped while the charge glow shows (ParalyzerFx's hold condition)
//   a charged shot  snd_se_szerosuit_013
//   a hit           snd_se_common_Electric_hit_m wherever MphRead plays a beam's hit sound (walls, players, doors, ...);
//                   a charged hit (the stun) snd_se_common_Electric_hit_ll, the long crackle
// Brawl's own stun sound (DamageElec) has volume 0 in its archive, never heard, so the stun has the charged hit's.
// MphRead's stand-in Power Beam shot, charge and hit sounds aren't played while these are (PlayerEntity
// .HostOwnParalyzerSfx / HitSfx). Sound only; without the import (or with the look off) MphRead's sounds stay.
internal sealed partial class CampaignRenderer
{
    const string ParalyzerSfxItem = "ParalyzerSounds";
    const string ParaShotSound = "snd_se_szerosuit_smash_L01";
    const string ParaChargeSound = "snd_se_szerosuit_012";
    const string ParaChargedShotSound = "snd_se_szerosuit_013";
    const string ParaHitSound = "snd_se_common_Electric_hit_m";
    const string ParaChargedHitSound = "snd_se_common_Electric_hit_ll";
    static readonly string[] ParaSoundsUsed = { ParaShotSound, ParaChargeSound, ParaChargedShotSound, ParaHitSound, ParaChargedHitSound };

    sealed class ParaSound
    {
        public short[] Pcm = Array.Empty<short>();
        public int Rate;
        public float Volume;
    }

    sealed class ParaSfxSeat
    {
        public int Heard = -1;
        public object? Hum;
        public readonly SoundSource Sound = new();
    }

    readonly Dictionary<string, ParaSound> _paraSounds = new(StringComparer.Ordinal);
    readonly Dictionary<PlayerEntity, ParaSfxSeat> _paraSfxSeats = new();
    readonly SoundSource _paraSound = new();
    readonly List<(Vector3 At, bool Charged)> _paraHitSounds = new();
    Action<BeamProjectileEntity>? _paraHitSfx, _paraPrevHitSfx;

    bool ParalyzerSfxOn => _sfx != null && _paraSounds.Count == ParaSoundsUsed.Length;

    // ParalyzerFxReady, once the look has loaded: brawl/ParalyzerSounds, all five or none
    void LoadParalyzerSounds(string root)
    {
        _paraSounds.Clear();
        if (!ImportedContent.Usable(root, BrawlFxImportKind.ImportKind, ParalyzerSfxItem))
        {
            Log.Info("MPHAudio", $"Paralyzer: no Brawl sounds, MPH's kept (no usable {BrawlFxImportKind.ImportKind}/{ParalyzerSfxItem})");
            return;
        }
        string dir = Path.Combine(root, BrawlFxImportKind.ImportKind, ParalyzerSfxItem);
        try
        {
            var volume = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(Path.Combine(dir, "sounds.txt")))
            {
                string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 3 && int.TryParse(t[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) volume[t[0]] = v;
            }
            foreach (string name in ParaSoundsUsed)
            {
                if (MusyX.ReadWav(Path.Combine(dir, "sounds", name + ".wav"), out int rate) is not short[] pcm || pcm.Length == 0
                    || !volume.TryGetValue(name, out int vol))
                {
                    throw new InvalidDataException($"{name} missing");
                }
                _paraSounds[name] = new ParaSound { Pcm = pcm, Rate = rate, Volume = vol / 127f };
            }
            Log.Info("MPHAudio", $"Paralyzer: Brawl sounds: {_paraSounds.Count}");
        }
        catch (Exception ex)
        {
            Log.Warn("MPHAudio", $"Paralyzer: the Brawl sounds failed to load, MPH's kept: {ex.Message}");
            _paraSounds.Clear();
        }
    }

    void PlayParalyzerSound(string name, SoundSource at)
    {
        if (_sfx != null && _paraSounds.TryGetValue(name, out ParaSound? s)) _sfx.PlayPcm(s.Pcm, s.Rate, s.Volume, at);
    }

    // TickParalyzerFx, every 60 Hz frame while the look is on
    void TickParalyzerSfx()
    {
        if (!ParalyzerSfxOn) return;
        // our hit sounds first, the rest to whoever set HostHitSfx (the blaster)
        _paraHitSfx ??= OnParalyzerHitSfx;
        if (BeamProjectileEntity.HostHitSfx != _paraHitSfx)
        {
            _paraPrevHitSfx = BeamProjectileEntity.HostHitSfx;
            BeamProjectileEntity.HostHitSfx = _paraHitSfx;
        }
        foreach ((Vector3 at, bool charged) in _paraHitSounds)
        {
            _paraSound.Update(at, BlasterHitSfxRange);
            PlayParalyzerSound(charged ? ParaChargedHitSound : ParaHitSound, _paraSound);
        }
        _paraHitSounds.Clear();
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null) continue;
            if (!_paraSfxSeats.TryGetValue(p, out ParaSfxSeat? seat)) _paraSfxSeats[p] = seat = new ParaSfxSeat();
            bool self = p.IsMainPlayer && !p.IsAltForm && !PlayerEntity.HostMainSoundsPlaced;
            seat.Sound.Update(BlasterTip(p) ?? p.Position, self ? -1 : DroneSfxRange);
            // each shot MphRead fired (PlayerSound counts them), at the cannon
            int shots = p.HostParalyzerShots;
            if (seat.Heard < 0 || shots < seat.Heard) seat.Heard = shots;
            if (shots != seat.Heard)
            {
                seat.Heard = shots;
                PlayParalyzerSound(p.HostParalyzerLastCharged ? ParaChargedShotSound : ParaShotSound, seat.Sound);
            }
            // the charge hum while the glow shows
            bool charging = p.Health > 0 && !p.IsAltForm && p.CurrentWeapon == BeamType.Paralyzer
                && p.EquipInfo.ChargeLevel >= p.EquipInfo.Weapon.MinCharge * 2;
            ParaSound hum = _paraSounds[ParaChargeSound];
            if (charging && seat.Hum == null) seat.Hum = _sfx!.PlayPcmLoop(hum.Pcm, hum.Rate, hum.Volume, seat.Sound);
            else if (charging) _sfx!.MovePcm(seat.Hum, hum.Volume, seat.Sound);
            else if (seat.Hum != null)
            {
                _sfx!.StopPcm(seat.Hum);
                seat.Hum = null;
            }
        }
    }

    // BeamProjectileEntity.HostHitSfx: a Paralyzer shot's hit sound, played on the next tick; the rest to the previous handler
    void OnParalyzerHitSfx(BeamProjectileEntity beam)
    {
        if (!beam.HostParalyzerLook)
        {
            _paraPrevHitSfx?.Invoke(beam);
            return;
        }
        if (_paraHitSounds.Count < 64) _paraHitSounds.Add((beam.Position, beam.Flags.TestFlag(BeamFlags.Charged)));
    }

    // ResetParalyzerFx (a replay restarting its match)
    void ResetParalyzerSfx()
    {
        foreach (ParaSfxSeat seat in _paraSfxSeats.Values) _sfx?.StopPcm(seat.Hum);
        _paraSfxSeats.Clear();
        _paraHitSounds.Clear();
    }
}
