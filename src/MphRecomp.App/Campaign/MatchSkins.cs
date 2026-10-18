using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Multiplayer skins (docs/CHARACTERS_AND_SKINS.md): every seat whose MatchPlayer.Skin is imported on this device wears it
// -- the local player, bots and remote players alike (Samus's suits on her rig, the troopers on Weavel's). One CampaignSuit
// per distinct skin, shared by every seat wearing it, loaded on a worker thread while the arena loads and given its GL
// objects before the first frame (as the campaign's suit: StartGearLoad / FinishGearLoad); a skin that isn't there, fails,
// or is still loading leaves that seat to MphRead's own DS model. Drawing only: the match is the same with or without them.
// A skin with a native Retro body imported (bodies/<skin>/: Import/Bodies/BodyImporter, D22 -- the Trooper on his own
// Prime 1 pirate rig) draws that instead of the HD suit's body: its own skeleton and clips (CampaignRetroBody); the HD
// suit keeps the morph ball.
//   --es skins <seat:skin,...>  (dev builds, local matches) e.g. "0:MP1VariaSuit,1:MP1WaveTrooper": that seat wears the
//                               skin, its hunter switched to the skin's rig (a trooper skin's seat plays the Trooper:
//                               Weavel + the Shield Wall); "1:trooper" = the Trooper in Weavel's own DS model (no skin)
internal sealed partial class CampaignRenderer
{
    // per MphRead player slot (= the match seat): the skin it wears, and the loads by skin id
    readonly string?[] _seatSkin = new string?[16]; // (more than MphRead's MaxPlayers will ever be)
    readonly Dictionary<string, Task<CampaignSuit?>> _skinLoads = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CampaignSuit> _skins = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Task<CampaignRetroBody?>> _bodyLoads = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CampaignRetroBody> _bodies = new(StringComparer.OrdinalIgnoreCase);

    public void ConfigureSkins(LaunchArgs args)
    {
        MatchSettings? settings = _matchSettings;
        if (settings == null) return;
        // the 5th-8th seat with the same hunter: four more colours on MPH's models as they load (HunterColours.cs)
        HunterColours.Install();
        // no-import look-alikes (D5, MphRecomp.Core LookAlikes.cs): their palette sets go on MPH's models as they load, worn
        // wherever a seat's skin isn't drawn here (owner OK 2026-10-09). Dev: --es lookalikes 1 = even where the skin is
        // imported (pictures next to the real looks), 0 = off
        LookAlikes.Force = !MphRecomp.Config.RecompSettings.PublicBuild && args.Get("lookalikes") == "1";
        bool looksOn = _looksOn = LookAlikes.On = MphRecomp.Config.RecompSettings.PublicBuild || args.Get("lookalikes") != "0";
        string? dev = args.Get("skins");
        CampaignRetroBody.LogClips = !MphRecomp.Config.RecompSettings.PublicBuild && args.Get("bodylog") == "1";
        // dev: --es zspose bind = Zero Suit held in one clip for every move (her bind pose = T-pose; or any clip name), no arm hold
        if (!MphRecomp.Config.RecompSettings.PublicBuild) MphRecomp.Campaign.ZeroSuitBody.Pose = args.Get("zspose");
        // dev: --es zsaim arm = Brawl Zero Suit (bodies/SSBBZeroSuit) takes her hits on her gun arm only, not her upper body
        if (!MphRecomp.Config.RecompSettings.PublicBuild) MphRecomp.Campaign.BrawlZeroSuitBody.Aim = args.Get("zsaim") == "arm" ? "arm" : "upper";
        // dev: --es zsupright <deg> = Zero Suit's chest tilt back on Weavel's rig (CampaignSuit.Straighten; 0 = Weavel's own)
        if (!MphRecomp.Config.RecompSettings.PublicBuild && float.TryParse(args.Get("zsupright"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float zsUp)) CampaignSuit.ZeroSuitUprightDeg = zsUp;
        if (!string.IsNullOrEmpty(dev) && !MphRecomp.Config.RecompSettings.PublicBuild && _netSession == null && !settings.IsClient)
        {
            foreach (string part in dev.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = part.Split(':');
                if (f.Length != 2 || !int.TryParse(f[0], out int seat) || seat < 0 || seat >= settings.Players.Count) continue;
                MatchPlayer p = settings.Players[seat];
                if (Characters.IsOriginal(f[1]))
                {
                    p.Skin = null;
                    continue;
                }
                if (f[1].Equals(Characters.Trooper.Id, StringComparison.OrdinalIgnoreCase))
                {
                    p.Skin = null;
                    if (!ReplayPlaying)
                    {
                        p.Hunter = Characters.Trooper.Base;
                        p.Character = Characters.Trooper.Id;
                    }
                }
                else
                {
                    p.Skin = f[1];
                    if (!ReplayPlaying)
                    {
                        // (a replay keeps its seats as recorded: hunters and characters change play)
                        p.Hunter = Characters.HunterFor(f[1], CampaignSuit.RigHunter(f[1])); // (Zero Suit: Samus on Weavel's rig)
                        p.Character = Characters.IdFor(p.Hunter, p.Skin);
                    }
                }
                if (seat == settings.LocalSeat) _hunter = p.Hunter;
            }
        }
        for (int i = 0; i < _seatSkin.Length; i++)
        {
            _seatSkin[i] = i < settings.Players.Count && !LookAlikes.Force ? Characters.Normalize(settings.Players[i].Hunter, settings.Players[i].Skin) : null;
            _seatKind[i] = i < settings.Players.Count ? LookAlikes.For(settings.Players[i]) : LookAlikes.Look.None;
            _seatLook[i] = looksOn ? _seatKind[i] : LookAlikes.Look.None;
            _seatVariant[i] = i < settings.Players.Count ? LookAlikes.VariantFor(settings, i) : LookAlikes.Variant.First;
            _seatName[i] = looksOn && i < settings.Players.Count ? LookAlikes.CharacterName(settings.Players[i]) : null;
            if (_seatLook[i] != LookAlikes.Look.None) Log.Info("MPHCampaign", $"skins: seat {i} look-alike {_seatLook[i]} {_seatVariant[i]} (where its skin isn't drawn)");
            if (_seatSkin[i] != null) Log.Info("MPHCampaign", $"skins: seat {i} ({settings.Players[i].Hunter}) wears {_seatSkin[i]}");
            _seatShieldRecolor[i] = i < settings.Players.Count ? ShieldRecolor(settings.Players[i].Skin) : 0;
            if (i < settings.Players.Count && Characters.HasWallCling(settings.Players[i]))
            {
                Log.Info("MPHCampaign", $"skins: seat {i} plays the Trooper (Wall Cling + Scout Drone{(Characters.AbilitiesFor(settings) ? "" : ": off in network matches")})");
            }
        }
        // dev pictures: --es lookvariant "<seat>:<0-9>,..." draws that seat as the nth of its look / a team (First, Second,
        // Third, Fourth, OrangeTeam, GreenTeam, Fifth-Eighth; a hunter without a look too) in any mode
        string? devVariant = MphRecomp.Config.RecompSettings.PublicBuild ? null : args.Get("lookvariant");
        foreach (string part in (devVariant ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = part.Split(':');
            if (f.Length == 2 && int.TryParse(f[0], out int seat) && int.TryParse(f[1], out int v) && seat >= 0 && seat < _seatVariant.Length)
            {
                _seatVariant[seat] = LookAlikes.VariantOf(v);
            }
        }
        // the looks' palette sets in the variants this match's seats wear, once a match has a seat that may wear one
        // (vanilla-only matches load none)
        var wanted = new List<(LookAlikes.Look, LookAlikes.Variant)>();
        for (int i = 0; i < _seatLook.Length; i++)
        {
            if (_seatLook[i] != LookAlikes.Look.None) wanted.Add((_seatLook[i], _seatVariant[i]));
        }
        if (wanted.Count > 0) LookAlikes.Register(wanted);
        ConfigureDrones(settings); // the Scout Drone's own body (MatchDrones.cs)
        ConfigureArmCannon(args); // the main player's first-person arm cannon (MatchArmCannon.cs)
    }

    // per seat: the look-alike MphRead's own model wears where the seat's skin isn't drawn here, in its variant (MPH's
    // palette slot: the nth seat with that look, or its team: LookAlikes.Variant); a real (imported) body wears the same
    // variant as a tint (SeatTint). _seatKind = the seat's character look whether or not look-alikes are on
    bool _looksOn; // the look-alikes and the Scout Drone's attack fallbacks (CampaignRenderer.DroneFallback.cs)
    readonly LookAlikes.Look[] _seatLook = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Repeat(LookAlikes.Look.None, 16));
    readonly LookAlikes.Look[] _seatKind = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Repeat(LookAlikes.Look.None, 16));
    readonly LookAlikes.Variant[] _seatVariant = new LookAlikes.Variant[16];

    // a Trooper / Dark Samus seat's team or duplicate tint for its real body (zero: none, or another hunter's skin)
    Vector4 SeatTint(PlayerEntity p)
    {
        int i = p.SlotIndex;
        if (i < 0 || i >= _seatKind.Length || _seatKind[i] == LookAlikes.Look.None) return Vector4.Zero;
        return LookAlikes.BodyTint(_seatKind[i] == LookAlikes.Look.DarkSamus, _seatVariant[i]) is { } t ? new Vector4(t.R, t.G, t.B, t.W) : Vector4.Zero;
    }
    // ... and a same-look copy's lights colour (the two-tone copies 2-8, GxShader uTint2)
    Vector4 SeatTint2(PlayerEntity p)
    {
        int i = p.SlotIndex;
        if (i < 0 || i >= _seatKind.Length || _seatKind[i] == LookAlikes.Look.None) return Vector4.Zero;
        var l = LookAlikes.BodyLight(_seatKind[i] == LookAlikes.Look.DarkSamus, _seatVariant[i]);
        return new Vector4(l.R, l.G, l.B, 0);
    }
    readonly string?[] _seatName = new string?[16]; // D7: the picked character's name (target health bar, RESULTS)

    // the Trooper's barrier in his skin's colour: the ForceField model's palettes (Metadata.DoorPalettes: door type -> palette)
    readonly int[] _seatShieldRecolor = new int[16];

    static int ShieldRecolor(string? skin) => skin?.ToLowerInvariant() switch
    {
        "mp1powertrooper" => Metadata.DoorPalettes[1], // Volt Driver's yellow
        "mp1icetrooper" => Metadata.DoorPalettes[5], // Judicator's ice blue
        "mp1wavetrooper" => Metadata.DoorPalettes[7], // Shock Coil's
        "mp1plasmatrooper" => Metadata.DoorPalettes[6], // Magmaul's red
        _ => Metadata.DoorPalettes[0], // Power Beam's
    };

    string? HdRoot => _gunsRoot != null ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_gunsRoot) ?? "", "hd") : null;
    string? BodiesRoot => _gunsRoot != null ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_gunsRoot) ?? "", "bodies") : null;

    // GL thread, with the room load (OnSurfaceChanged): each distinct skin on a worker (its hunter's model looked up here)
    void StartSkinLoads()
    {
        string? hdRoot = HdRoot;
        if (hdRoot == null) return;
        string? bodiesRoot = BodiesRoot;
        StartDroneLoads(bodiesRoot);
        StartLockjawLoads(bodiesRoot); // Beyond Sylux's Prime 4 Lockjaw (MatchLockjaw.cs)
        foreach (string? id in _seatSkin)
        {
            if (id != null && bodiesRoot != null && !_bodyLoads.ContainsKey(id) && !_bodies.ContainsKey(id) && CampaignRetroBody.Imported(bodiesRoot, id))
            {
                string bid = id;
                _bodyLoads[bid] = Task.Factory.StartNew(() => CampaignRetroBody.TryLoad(bodiesRoot, bid), TaskCreationOptions.LongRunning);
            }
            if (id == null || _skinLoads.ContainsKey(id) || _skins.ContainsKey(id)) continue;
            if (!Characters.Imported(hdRoot, id))
            {
                Log.Info("MPHCampaign", $"skins: {id} isn't imported here -- MphRead's own model for its seats");
                continue;
            }
            Model? model = null;
            try { model = CampaignSuit.HunterModel(CampaignSuit.RigHunter(id)); }
            catch (Exception ex) { Log.Warn("MPHCampaign", $"skins: {id}: hunter model: {ex.Message}"); }
            string skin = id;
            _skinLoads[skin] = Task.Factory.StartNew(() => CampaignSuit.TryLoad(hdRoot, skin, model), TaskCreationOptions.LongRunning);
        }
    }

    // after the room (OnSurfaceChanged): wait for the loads and make their GL objects, so the first frame shows them; a
    // load past 5 s goes in during play (UpdateSkins)
    void FinishSkinLoads()
    {
        if (_skinLoads.Count == 0 && _bodyLoads.Count == 0) return;
        long t0 = Stopwatch.GetTimestamp();
        foreach (var (id, task) in _bodyLoads)
        {
            double left = 5000 - Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (left <= 0 || !task.Wait(TimeSpan.FromMilliseconds(left))) break;
            CampaignRetroBody? body = task.IsCompletedSuccessfully ? task.Result : null;
            while (body != null && !body.Failed && !body.Warm(100) && Stopwatch.GetElapsedTime(t0).TotalMilliseconds < 5000)
            {
                System.Threading.Thread.Sleep(1);
            }
        }
        foreach (var (id, task) in _skinLoads)
        {
            double left = 5000 - Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (left <= 0 || !task.Wait(TimeSpan.FromMilliseconds(left))) break;
            CampaignSuit? suit = task.IsCompletedSuccessfully ? task.Result : null;
            while (suit != null && !suit.Warm(100) && Stopwatch.GetElapsedTime(t0).TotalMilliseconds < 5000)
            {
                System.Threading.Thread.Sleep(1);
            }
        }
        UpdateSkins();
        Log.Info("MPHCampaign", $"skins before the first frame: {_skins.Count} ready ({_bodies.Count} native bodies), {_skinLoads.Count + _bodyLoads.Count} still loading, "
            + $"{Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms");
    }

    // GL thread, every frame: GL steps for the skins in use and the ones still loading; a loaded one goes in once it draws
    // without a stall
    void UpdateSkins()
    {
        foreach (CampaignSuit s in _skins.Values) s.Warm(WarmBudgetMs);
        UpdateBodies();
        if (_skinLoads.Count == 0) return;
        List<string>? done = null;
        foreach (var (id, task) in _skinLoads)
        {
            if (!task.IsCompleted) continue;
            if (task.IsFaulted || task.Result == null)
            {
                if (task.IsFaulted) Log.Error("MPHCampaign", $"skins: {id} failed: {task.Exception?.GetBaseException().Message}");
                (done ??= new()).Add(id);
                continue;
            }
            if (!task.Result.Warm(WarmBudgetMs)) continue;
            _skins[id] = task.Result;
            (done ??= new()).Add(id);
            Log.Info("MPHCampaign", $"skins: {id} in place (body {task.Result.HasBody}, ball {task.Result.HasBall})");
        }
        if (done != null) foreach (string id in done) _skinLoads.Remove(id);
    }

    // the native bodies' loads, as the suits' below
    void UpdateBodies()
    {
        if (_bodyLoads.Count == 0) return;
        List<string>? done = null;
        foreach (var (id, task) in _bodyLoads)
        {
            if (!task.IsCompleted) continue;
            if (task.IsFaulted || task.Result == null || task.Result.Failed)
            {
                if (task.IsFaulted) Log.Error("MPHCampaign", $"skins: body {id} failed: {task.Exception?.GetBaseException().Message}");
                (done ??= new()).Add(id);
                continue;
            }
            if (!task.Result.Warm(WarmBudgetMs)) continue;
            _bodies[id] = task.Result;
            (done ??= new()).Add(id);
            Log.Info("MPHCampaign", $"skins: {id} in place as a native body");
        }
        if (done != null) foreach (string id in done) _bodyLoads.Remove(id);
    }

    // the seat's native body (its skin's bodies/ folder), or null
    CampaignRetroBody? BodyOf(PlayerEntity p)
    {
        int i = p.SlotIndex;
        if (i < 0 || i >= _seatSkin.Length || _seatSkin[i] is not string id) return null;
        if (p.IsMainPlayer && SuitShown) return null;
        return _bodies.TryGetValue(id, out CampaignRetroBody? b) && !b.Failed ? b : null;
    }

    CampaignSuit? SkinOf(PlayerEntity p)
    {
        int i = p.SlotIndex;
        if (i < 0 || i >= _seatSkin.Length || _seatSkin[i] is not string id) return null;
        // the campaign's own suit already draws the main player (a dev-launched Samus match with RECOMP SETTINGS' suit)
        if (p.IsMainPlayer && SuitShown) return null;
        return _skins.TryGetValue(id, out CampaignSuit? s) ? s : null;
    }

    // with SetPlayerHooks: MphRead leaves each skinned seat's body / ball to us while the step collects its draw items
    void SetSkinHooks(bool on)
    {
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null) continue;
            CampaignSuit? s = on ? SkinOf(p) : null;
            bool body = s?.HasBody == true || on && BodyOf(p) != null;
            if (p.SlotIndex >= 0 && p.SlotIndex < _seatShieldRecolor.Length) p.HostShieldRecolor = _seatShieldRecolor[p.SlotIndex];
            p.HostOwnBiped = body;
            // MphRead's own body / ball in the seat's look-alike colours (only where the skin's own aren't drawn)
            LookAlikes.Look look = s == null && !body && p.SlotIndex >= 0 && p.SlotIndex < _seatLook.Length ? _seatLook[p.SlotIndex] : LookAlikes.Look.None;
            LookAlikes.Variant variant = p.SlotIndex >= 0 && p.SlotIndex < _seatVariant.Length ? _seatVariant[p.SlotIndex] : LookAlikes.Variant.First;
            int phase = LookAlikes.ShimmerPhase(_frame, p.SlotIndex);
            p.HostLookRecolor = LookAlikes.RecolorFor(look, variant, Metadata.HunterModels[p.Hunter][0], phase);
            p.HostGunLookRecolor = LookAlikes.RecolorFor(look, variant, Metadata.HunterModels[p.Hunter][3], phase); // its first-person gun
            // Dark Samus's phazon glows ride her shoulder pads (CampaignRenderer.LookAlikeFx.cs)
            p.HostTrackNodes = look == LookAlikes.Look.DarkSamus && p.HostLookRecolor >= 0 ? PhazonNodes : null;
            p.HostDroneLookRecolor = p.SlotIndex >= 0 && p.SlotIndex < _seatLook.Length
                ? LookAlikes.RecolorFor(_seatLook[p.SlotIndex], variant, Metadata.EnemyModelNames[23]) : -1; // the Psycho Bit stand-in (drawn only without the drone's body)
            // the 5th-8th seat with the same hunter (no character look): HunterColours' sets on its body, alt, gun and bombs
            int copy = LookAlikes.NewCopy(variant);
            bool hunterCopy = copy >= 0 && p.SlotIndex >= 0 && p.SlotIndex < _seatKind.Length && _seatKind[p.SlotIndex] == LookAlikes.Look.None;
            if (hunterCopy)
            {
                p.HostLookRecolor = HunterColours.RecolorFor(Metadata.HunterModels[p.Hunter][0], copy);
                p.HostGunLookRecolor = HunterColours.RecolorFor(Metadata.HunterModels[p.Hunter][3], copy);
            }
            p.HostBombLookRecolor = hunterCopy && p.Hunter == Hunter.Kanden ? HunterColours.RecolorFor("KandenAlt_TailBomb", copy) : -1;
            p.HostSyluxTrailRecolor = hunterCopy && p.Hunter == Hunter.Sylux ? HunterColours.RecolorFor(HunterColours.TrailModel, copy) : -1;
            p.HostSyluxBombColor = hunterCopy && p.Hunter == Hunter.Sylux && HunterColours.SyluxBombColor(copy) is var sc
                ? new Vector3(sc.R, sc.G, sc.B) : null;
            p.HostCharacterName = p.SlotIndex >= 0 && p.SlotIndex < _seatName.Length ? _seatName[p.SlotIndex] : null;
            p.HostOwnAlt = s?.HasBall == true || on && (DroneOf(p) != null || LockjawOf(p) != null);
            p.HostTrailColor = on ? BodyOf(p)?.Clips.Trail : null;   // a native body's own morph ball trail colour
            // the Trooper's standing body while his Scout Drone flies (MphRead HalfturretEntity.HostBodyModel): his skin
            if (p.Halfturret is HalfturretEntity t) t.HostBodyByApp = p.HostScoutDrone && body;
        }
    }

    // after each sim step: the balls' roll
    void TickSkins()
    {
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p != null && SkinOf(p) is CampaignSuit s) s.Tick(p);
            if (p != null && BodyOf(p) is CampaignRetroBody b) b.Tick(p);
            if (p != null) TickDrone(p);
            if (p != null) TickLockjaw(p);
        }
        TickBlasterFx(); // the Pirate Blaster's shots in Prime 1's look (CampaignRenderer.BlasterFx.cs; its systems step in TickDroneFx)
        TickTurretFx(); // Shooting Gallery turret's Echoes effects (CampaignRenderer.TurretFx.cs; its systems step in TickDroneFx)
        TickDroneFx(); // the Scout Drone's Prime 1 effects (CampaignRenderer.DroneFx.cs)
        TickDroneFallback(); // ... or MPH's own where the import has none (CampaignRenderer.DroneFallback.cs)
        TickParalyzerFx(); // the Paralyzer in Brawl's look (CampaignRenderer.ParalyzerFx.cs; after the two above: HostImpact)
        TickHyperFx(); // Dark Samus's Hyper Ball in Corruption's look and sound (CampaignRenderer.HyperFx.cs)
    }

    void DrawSkins(Scene scene, SuitPass pass)
    {
        DrawArmCannon(scene, pass); // the main player's first-person arm cannon (MatchArmCannon.cs)
        DrawDroneFx(scene, pass); // the Scout Drone's Prime 1 effects (CampaignRenderer.DroneFx.cs)
        DrawBlasterFx(scene, pass); // the Pirate Blaster shots' swooshes (CampaignRenderer.BlasterFx.cs)
        DrawTurretFx(scene, pass); // the turret slot's Echoes arcs (CampaignRenderer.TurretFx.cs)
        DrawParalyzerFx(scene, pass); // the Paralyzer in Brawl's look (CampaignRenderer.ParalyzerFx.cs)
        DrawHyperFx(scene, pass); // Dark Samus's Hyper Ball in Corruption's look (CampaignRenderer.HyperFx.cs)
        DrawDroneFallback(scene, pass); // ... or the fallbacks without the import (CampaignRenderer.DroneFallback.cs)
        DrawLookAlikeFx(scene, pass); // Dark Samus look-alike's phazon glow (CampaignRenderer.LookAlikeFx.cs)
        DrawLockjawFx(scene, pass); // Beyond Sylux's Lockjaw orb (CampaignRenderer.LockjawFx.cs)
        if (_skins.Count == 0 && _bodies.Count == 0 || _host == null || scene != _host.Scene) return;
        bool drew = false;
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null) continue;
            CampaignSuit? s = SkinOf(p);
            CampaignRetroBody? nb = BodyOf(p), drone = DroneOf(p);
            // the seat's team / duplicate tint on its real looks while they draw it (shared by every seat wearing them)
            Vector4 tint = SeatTint(p), tint2 = SeatTint2(p);
            if (s != null) (s.SeatTint, s.SeatTint2) = (tint, tint2);
            if (nb != null) (nb.SeatTint, nb.SeatTint2) = (tint, tint2);
            if (drone != null) (drone.SeatTint, drone.SeatTint2) = (tint, tint2);
            try
            {
                if (s == null && nb == null)
                {
                    if (p.IsAltForm && DrawDrone(p, scene, pass)) drew = true; // a skinless Trooper's drone
                    continue;
                }
                bool hasBody = nb != null || s!.HasBody;
                if (p.HostScoutDrone && hasBody && p.Flags2.TestFlag(PlayerFlags2.Halfturret) && p.Halfturret is HalfturretEntity t
                    && t.Health > 0 && t.HostBodyModel is ModelInstance body)
                {
                    if (nb != null) nb.DrawStanding(t, p.Hunter, SmoothView(t, scene), scene.PerspectiveMatrix, pass, t.HostDamageFlash);
                    else s!.DrawStanding(body.AnimInfo, t.HostBodyPosition, t.FacingVector, SmoothView(t, scene), scene.PerspectiveMatrix, pass,
                        t.HostDamageFlash);
                    drew = true;
                }
                if (p.IsAltForm && (DrawDrone(p, scene, pass) || DrawLockjaw(p, scene, pass)))
                {
                    drew = true;
                    continue;
                }
                if (p.IsAltForm)
                {
                    if (s == null || !s.HasBall || !p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson)) continue;
                    s.DrawBall(p, SmoothView(p, scene), scene.PerspectiveMatrix, pass, p.HostDamageFlash);
                    drew = true;
                }
                // (and a replay watching this seat from its eyes, PlayerEntity.HostFirstPersonSeat: T9)
                else if (hasBody && p.HostBipedAlpha > 0
                    && !((p.IsMainPlayer || p.SlotIndex == PlayerEntity.HostFirstPersonSeat) && CameraInBody(scene, p)))
                {
                    if (nb != null && !(s != null && s.HasBody && nb.Clips.SuitLegs.Contains((PlayerAnimation)p.HostBipedModel1.AnimInfo.Index[0])))
                    {
                        nb.DrawBody(p, SmoothView(p, scene), scene.PerspectiveMatrix, pass, p.HostBipedAlpha, p.HostDamageFlash);
                    }
                    else s!.DrawBody(p, SmoothView(p, scene), scene.PerspectiveMatrix, pass, p.HostBipedAlpha, p.HostDamageFlash, SuitLift(p, nb));
                    drew = true;
                }
            }
            finally
            {
                if (s != null) (s.SeatTint, s.SeatTint2) = (Vector4.Zero, Vector4.Zero);
                if (nb != null) (nb.SeatTint, nb.SeatTint2) = (Vector4.Zero, Vector4.Zero);
                if (drone != null) (drone.SeatTint, drone.SeatTint2) = (Vector4.Zero, Vector4.Zero);
            }
        }
        if (!drew) return;
        // MphRead's own drawing state back, as DrawSuit leaves it
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDisable(CullFaceCap);
    }

    // a native body's HD-suit states (BodyClips.SuitLegs) keep its hover: whole while jumping, sinking to the floor
    // through the morph and rising from it through the unmorph, so the switch between the two bodies doesn't pop
    static float SuitLift(PlayerEntity p, CampaignRetroBody? nb)
    {
        if (nb == null || nb.Clips.Lift == 0) return 0;
        AnimationInfo a = p.HostBipedModel1.AnimInfo;
        float t = a.FrameCount[0] > 0 ? Math.Clamp((float)a.Frame[0] / a.FrameCount[0], 0, 1) : 1;
        return (PlayerAnimation)a.Index[0] switch
        {
            PlayerAnimation.Morph => nb.Clips.Lift * (1 - t),
            PlayerAnimation.Unmorph => nb.Clips.Lift * t,
            _ => nb.Clips.Lift
        };
    }

    // the camera in or against the local player's own body (the unmorph swing back into the eye): left out, as the
    // campaign suit's body cut (UpdateBodyCut) does for the same GPU reason
    static bool CameraInBody(Scene scene, PlayerEntity p)
    {
        if (CameraSequence.Current != null) return false;
        Vector3 cam = scene.CameraPosition, pos = p.Position;
        float dx = cam.X - pos.X, dz = cam.Z - pos.Z;
        float top = pos.Y + Fixed.ToFloat(p.Values.MaxPickupHeight) + BodyCutAboveTop;
        return MathF.Sqrt(dx * dx + dz * dz) < BodyCutRadius && cam.Y < top && cam.Y > pos.Y - BodyCutBelow;
    }

    void ResetSkinsGl()
    {
        foreach (CampaignSuit s in _skins.Values) s.ResetGl();
        foreach (CampaignRetroBody b in _bodies.Values) b.ResetGl();
        foreach (Task<CampaignRetroBody?> t in _bodyLoads.Values) if (t.IsCompletedSuccessfully) t.Result?.ResetGl();
        foreach (Task<CampaignSuit?> t in _skinLoads.Values) if (t.IsCompletedSuccessfully) t.Result?.ResetGl();
    }
}
