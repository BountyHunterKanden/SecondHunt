using System;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Culling;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Dev launcher "Solo arena" (owner 2026-10-08): any character -- a hunter, the Trooper, Dark Samus -- alone in any
// multiplayer arena, in full control, seen in the third-person view. R3 steps the camera round: an ORBIT camera that
// circles the player (Scene.HostView: render only, the game's own camera, aim and logic stay where they were), the
// over-the-shoulder view (CampaignRenderer.UpdateThirdPerson), then FIRST PERSON (owner 2026-10-15: "a way to switch to
// first person in these testing modes"), and back to the orbit camera. A Battle with no bots and
// no time limit (alone, nobody reaches the point goal), not recorded as a replay. Dev builds only.
//   --es solo 1  (with --es match Battle --arena <name> --es hunter / gun / skins as a match)
//   --es orbit <degrees>  start in the orbit camera, that far round from behind the player (180 = in front; PC checks)
//   --es fp 1  start in first person
//   --es selfbody 1  the Trooper's drone shots hit his own standing body (MphRead PlayerTrooperDev.cs; T5, the body's
//                    hitbox check), each hit shown on the status line
//   --es infua 1  (dev, any match, solo or not) the player's Universal Ammo never runs out (Dark Samus's Hyper Ball
//                 held as long as you like, e.g. a PC capture next to a bot)
//   --es morphat <n>  (dev, any match) presses morph at sim step n, --es holdfrom <n> holds fire from step n on (in
//                     the ball: Dark Samus's Hyper Ball): a PC capture (the desktop script's keys don't reach gameplay)
//   --es pullbot <units>  (dev, with holdfrom) from then on every other player is put that far ahead of the player each
//                         step (inside the Hyper Ball's 5-unit reach: its lock-on in a capture); the player's health is
//                         kept full
//   --es hyperbend <x>  (dev) the Corruption Hyper Ball lightning's bend (Core RetroElectric.Bend)
//   --es targets 1  (with solo and --es bots <n>) the bots stay as practice targets: they press nothing (MphRead
//                   PlayerEntity.HostIdleBots), standing where they spawn, and their health is filled every step, so
//                   hits land but never kill (owner 10-12: Hyper Ball practice in Data Shrine)
//   --es lineup <who,...>  (with targets 1) the practice targets are these characters, standing in one row on open floor,
//                          the player put in front of it (owner 10-16: every hunter side by side to judge the Trooper's size).
//                          A hunter's name, trooper, or a skin id (MP2DarkSamus, MP3ZeroSuit); at most 7 (8 seats)
//   --es lineupgap <units>  the row's spacing (default 1.6)
//   --es turretbot 1  (with targets 1, Shooting Gallery) a practice target sits idle in the turret, put back in its seat
//                     whenever the turret is free again (owner 10-16: bomb the slot as each hunter)
//   --es turretview 1  (with turretbot) once the target is seated, the player is put by the bomb slot facing the turret
//                      (PC look captures of the occupied turret)
internal sealed partial class CampaignRenderer
{
    bool _solo, _soloOrbit, _soloFirstPerson, _prevSoloR3, _soloR3Combo, _soloInfUa, _soloTargets;
    int _devMorphAt, _devJumpAt, _devHoldFrom, _devSteps; // (--es jumpat <n>: jump pressed at step n, e.g. out of a seat)
    float _devPullBot;
    int _devSeatAt; // dev --es seat N: at step N the player is put in the arena's turret seat (Shooting Gallery)
    System.Numerics.Vector2 _devSeatMove; int _devSeatMoveFor; // dev --es seatmove x,y,steps: the move stick held once seated
    int _devGratesAt; // dev --es grates N: at step N every Echoes grate breaks (the break effect's captures)
    bool _devGrateCam; // dev --es gratecam 1 (with grates): 30 steps before, the player stands beside the first grate, facing it
    bool _devTurretBot; // dev --es turretbot 1: a practice target sits in the turret (TurretBotStep)
    bool _devTurretView, _devTurretViewed; // dev --es turretview 1: the player put by the slot once (TurretBotStep)
    Vector2? _devTurretViewFrom; // dev --es turretviewfrom x,z: put there instead (from the turret's pivot, floor height)
    bool _devTurretSlot, _devTurretSlotted; // dev --es turretslot 1 (with turretview): once morphed, the ball put in the slot
    float _devTurretViewAim; int _devTurretViewAimed; // dev --es turretviewaim y: the aim's pitch input held 30 steps after
    float _orbitYaw, _orbitPitch, _orbitDist;
    float _orbitZoom = 1; // D-pad up / down in the orbit camera: its distance, x the default
    float? _orbitStart; // --es orbit: the first orbit, in degrees from behind the player
    bool _orbitPlaced;

    const float OrbitSpeed = 2.6f; // radians a second at full stick
    const float OrbitPitchMin = -1.05f, OrbitPitchMax = 1.35f; // about 60 degrees below to 77 above the player's middle
    const float OrbitDistBody = 3.2f, OrbitDistBall = 2.4f, OrbitMargin = 0.3f;
    // (min: 1.15 units off, just outside the 1-unit cylinder where the body is not drawn, CampaignRenderer.BodyCutRadius /
    // MatchSkins.CameraInBody; rate: x e per second held)
    const float OrbitZoomMin = 0.36f, OrbitZoomMax = 2f, OrbitZoomRate = 0.8f;

    // the solo and TRY CONTROLS runs' statics back off: every match (ConfigureMatch), so a match after a try
    // run spawns, scores and moves its bots as usual
    static void ResetSoloStatics()
    {
        PlayerEntity.HostSelfBodyHits = false; // (static: only the solo run that asks for it)
        PlayerEntity.HostSpawnAtOnce = false; PlayerEntity.HostHideScore = false; // (static: TRY CONTROLS only)
        PlayerEntity.HostIdleBots = false;
    }

    // ConfigureMatch, before the settings are checked: one player, Battle, no time limit (the point goal stays: alone,
    // nobody scores)
    void ConfigureSolo(LaunchArgs args, MatchSettings settings)
    {
        ResetSoloStatics();
        _soloTargets = false;
        _devTurretBot = false;
        _lineupGap = 0;
        _soloInfUa = args.Get("infua") == "1" && !MphRecomp.Config.RecompSettings.PublicBuild;
        _devMorphAt = !MphRecomp.Config.RecompSettings.PublicBuild && int.TryParse(args.Get("morphat"), out int ma) ? ma : 0;
        _devJumpAt = !MphRecomp.Config.RecompSettings.PublicBuild && int.TryParse(args.Get("jumpat"), out int ja) ? ja : 0;
        _devHoldFrom = !MphRecomp.Config.RecompSettings.PublicBuild && int.TryParse(args.Get("holdfrom"), out int hf) ? hf : 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _devPullBot = !MphRecomp.Config.RecompSettings.PublicBuild
            && float.TryParse(args.Get("pullbot"), System.Globalization.NumberStyles.Float, inv, out float pb) ? pb : 0;
        _devSeatAt = !MphRecomp.Config.RecompSettings.PublicBuild && int.TryParse(args.Get("seat"), out int seatAt) ? seatAt : 0;
        _devGratesAt = !MphRecomp.Config.RecompSettings.PublicBuild && int.TryParse(args.Get("grates"), out int gratesAt) ? gratesAt : 0;
        _devGrateCam = _devGratesAt > 30 && args.Get("gratecam") == "1";
        _devSeatMoveFor = 0;
        if (!MphRecomp.Config.RecompSettings.PublicBuild && args.Get("seatmove")?.Split(',') is { Length: 3 } sm
            && float.TryParse(sm[0], System.Globalization.NumberStyles.Float, inv, out float smx)
            && float.TryParse(sm[1], System.Globalization.NumberStyles.Float, inv, out float smy) && int.TryParse(sm[2], out int smn))
        {
            _devSeatMove = new System.Numerics.Vector2(smx, smy);
            _devSeatMoveFor = smn;
        }
        if (!MphRecomp.Config.RecompSettings.PublicBuild
            && float.TryParse(args.Get("hyperbend"), System.Globalization.NumberStyles.Float, inv, out float bend))
        {
            MphRecomp.Campaign.RetroElectric.Bend = bend;
        }
        if (args.Get("try") is "1" or "2")
        {
            ConfigureTry(settings, active: args.Get("try") == "2");
            return;
        }
        if (args.Get("solo") != "1" || MphRecomp.Config.RecompSettings.PublicBuild) return;
        ConfigureZeroSuitDev(args); // --es paralyzer 1 (ZeroSuitGunTip.cs)
        _solo = true;
        PlayerEntity.HostSelfBodyHits = args.Get("selfbody") == "1";
        if (args.Get("spawnnow") == "1") PlayerEntity.HostSpawnAtOnce = true; // MAP MAKER play: no FIRE to spawn
        settings.Solo = true;
        settings.Mode = GameMode.Battle;
        // vanilla never plays the expanded Transfer Lock in Battle (MultiplayerMenu's arena table: Bounty, Capture,
        // Defender only), and its Battle layer has no low gravity out in space: the Bounty layer, with a goal nobody
        // reaches (owner 2026-10-10)
        if (settings.Arena == "AD1 TRANSFER LOCK BT")
        {
            settings.Mode = GameMode.Bounty;
            settings.PointGoal = 99;
        }
        _soloTargets = args.Get("targets") == "1";
        PlayerEntity.HostIdleBots = _soloTargets;
        if (_soloTargets) settings.BotStuckFix = false; // (as ConfigureTry: idle targets stay where they spawn)
        _devTurretBot = _soloTargets && args.Get("turretbot") == "1";
        _devTurretView = _devTurretBot && args.Get("turretview") == "1";
        _devTurretViewed = false;
        _devTurretSlot = _devTurretView && args.Get("turretslot") == "1";
        _devTurretSlotted = false;
        _devTurretViewAimed = 0;
        _devTurretViewAim = float.TryParse(args.Get("turretviewaim"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float va) ? va : 0;
        _devTurretViewFrom = args.Get("turretviewfrom")?.Split(',') is { Length: 2 } vf
            && float.TryParse(vf[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vfx)
            && float.TryParse(vf[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vfz)
            ? new Vector2(vfx, vfz) : null;
        for (int i = settings.Players.Count - 1; i >= 1; i--)
        {
            if (!_soloTargets || !settings.Players[i].Bot) settings.Players.RemoveAt(i); // practice targets stay
        }
        ConfigureLineup(args, settings);
        _ddTestLoaded = false;
        _ddTest = args.Get("ddtest") == "1"; // dev: every seat keeps double damage (custom vs vanilla look)
        settings.TimeLimitSeconds = -1; // MphRead: a negative match time never runs out (and the HUD leaves it off)
        if (float.TryParse(args.Get("orbit"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float deg))
        {
            _orbitStart = deg;
            _soloOrbit = true;
        }
        if (!MphRecomp.Config.RecompSettings.PublicBuild && float.TryParse(args.Get("orbitzoom"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float zoom))
        {
            _orbitZoom = Math.Clamp(zoom, OrbitZoomMin, OrbitZoomMax); // dev: the orbit's first distance (close-up captures)
        }
        _soloFirstPerson = args.Get("fp") == "1" && !_soloOrbit;
        SetThirdPerson(!_soloFirstPerson); // whatever RECOMP SETTINGS' camera says (ReloadAimFromSettings keeps it)
        ConfigurePlacer(args); // --es place 1: the pickup placement editor (PickupPlacer.cs)
        Log.Info("MPHMatch", $"solo run in {settings.Arena} as {settings.Players[0].Hunter}"
            + (_soloTargets ? $", {settings.Players.Count - 1} practice targets" : ""));
    }

    // TRY CONTROLS (OPTIONS > CONTROLS, owner 2026-10-16; public builds too): the player with practice bots -- --es try 1:
    // idle targets (health refilled, as --es targets 1), try 2: bots that move and fight back (owner 10-17) -- the camera as
    // RECOMP SETTINGS has it, every weapon but the Omega Cannon with ammo kept full (TryStep; owner 2026-10-16: a normal
    // spawn, not the Imperialist in hand), no time limit (active bots: a 99 point goal), not recorded (owner 10-17 C23: "this
    // shouldnt be a traditional match ... just a sort of test area": no LAWS OF BATTLE intro, no FIRE to spawn -- the
    // player spawns at once, PlayerEntity.HostSpawnAtOnce -- and a faint pulsing "press start to exit" at the HUD's foot,
    // C20). Every spawn is in Alinos Gateway's
    // sniping perch, just behind the Imperialist's pickup and facing over it (owner 10-17; TryPerchStep). One Start press
    // goes back to the controls (TryLeave; owner 10-17: B is a gameplay button, never a back-out). Not _solo: R3 and the
    // D-pad keep their own binds (no orbit camera).
    bool _tryControls, _tryLeaving, _tryPerched;
    (Vector3 At, Vector3 Facing, NodeRef Part)? _tryPerch;
    bool _tryPerchLooked;

    void ConfigureTry(MatchSettings settings, bool active)
    {
        _tryControls = true;
        PlayerEntity.HostSpawnAtOnce = true;
        PlayerEntity.HostHideScore = true; // (owner 10-17: no points counter)
        _soloTargets = !active;
        PlayerEntity.HostIdleBots = !active;
        // idle targets: no stuck fix (owner 10-29: bots in lava) -- they never reach a node, so its edge/drift nudges (direct
        // speed writes past HostIdleBots, PlayerAiUnstick.cs / PlayerAiEdgeJump.cs) walked them off their spawn rocks
        if (!active) settings.BotStuckFix = false;
        if (active) settings.PointGoal = 99; // as good as never (0 = no goal, but the HUD then shows "0 / 0")
        settings.Solo = true;
        settings.PlacedPickups = true; // the arena's placed pickups as in a match (the Pirate Blaster's; PlacedPickups.For)
        settings.Mode = GameMode.Battle;
        for (int i = settings.Players.Count - 1; i >= 1; i--)
        {
            if (!settings.Players[i].Bot) settings.Players.RemoveAt(i);
        }
        settings.TimeLimitSeconds = -1; // MphRead: a negative match time never runs out (and the HUD leaves it off)
        Log.Info("MPHMatch", $"try controls in {settings.Arena} as {settings.Players[0].Hunter}, {settings.Players.Count - 1} "
            + (active ? "active bots" : "idle targets"));
    }

    float _lineupGap; // dev --es lineup: the row's spacing, 0 = no row
    (Vector3 At, Vector3 Forward, NodeRef Part)? _lineupAnchor;
    readonly System.Collections.Generic.HashSet<PlayerEntity> _lineupPlaced = new();

    // ConfigureSolo, with --es targets 1: the bots become the --es lineup characters (as MatchSkins reads --es skins)
    void ConfigureLineup(LaunchArgs args, MatchSettings settings)
    {
        string? list = args.Get("lineup");
        if (!_soloTargets || string.IsNullOrEmpty(list)) return;
        settings.Players.RemoveRange(1, settings.Players.Count - 1);
        foreach (string who in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (settings.Players.Count >= MatchSettings.MaxSeats) break;
            var bot = new MatchPlayer { Bot = true, BotLevel = 1 };
            if (Enum.TryParse(who, ignoreCase: true, out Hunter h) && h <= Hunter.Weavel)
            {
                bot.Hunter = h;
            }
            else
            {
                // trooper = his first skin (MP1PowerTrooper): with no skin a seat draws his look-alike (Weavel in his colours)
                string skin = who.Equals(Characters.Trooper.Id, StringComparison.OrdinalIgnoreCase) ? Characters.Trooper.Known[0] : who;
                bot.Skin = skin;
                bot.Hunter = Characters.HunterFor(skin, CampaignSuit.RigHunter(skin)); // (Zero Suit: Samus on Weavel's rig)
                bot.Character = Characters.IdFor(bot.Hunter, bot.Skin);
            }
            settings.Players.Add(bot);
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _lineupGap = float.TryParse(args.Get("lineupgap"), System.Globalization.NumberStyles.Float, inv, out float gap) && gap > 0 ? gap : 1.6f;
        _lineupAnchor = null;
        _lineupPlaced.Clear();
    }

    bool _ddTest, _ddTestLoaded; // dev --es ddtest 1 (solo only)
    static readonly System.Reflection.FieldInfo? DdTimer = typeof(PlayerEntity).GetField("_doubleDmgTimer",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    // DevCaptureInput, every sim step with --es ddtest 1: tops up each live seat's double damage timer, so a custom
    // hunter's double damage look can be checked beside a vanilla one's (dev only: the solo launch is off in public builds)
    void DdTestStep()
    {
        if (!_ddTest || DdTimer == null || _host == null) return;
        if (!_ddTestLoaded) { _host.Scene.LoadEffect(244, persistent: true); _ddTestLoaded = true; } // doubleDamageGun, as a pickup room
        foreach (PlayerEntity b in PlayerEntity.Players)
        {
            if (b != null && b.Health > 0) DdTimer.SetValue(b, (ushort)900);
        }
    }

    const float LineupFront = 5; // the player's distance in front of the row's middle

    // BuildInput, every sim step: once the player has spawned, the row's place is found (FindLineup: open, flat floor) and
    // the player is put in front of it, facing it; each lineup bot, once it has spawned, is put in its place, turned to
    // face the player. Idle, they stay there
    void LineupStep(PlayerEntity p)
    {
        if (_lineupGap <= 0 || _host == null || p.Health <= 0) return;
        int n = 0;
        foreach (PlayerEntity b in PlayerEntity.Players)
        {
            if (b != null && b.IsBot) n++;
        }
        if (_lineupAnchor == null)
        {
            var f = new Vector3(p.FacingVector.X, 0, p.FacingVector.Z);
            f = f.LengthSquared > 1e-6f ? f.Normalized() : Vector3.UnitZ;
            (Vector3, Vector3, NodeRef)? found = FindLineup(_host.Scene, n, _lineupGap);
            _lineupAnchor = found ?? (p.Position + f * LineupFront, f, LineupWalk(_host.Scene, p.NodeRef, p.Position, p.Position + f * LineupFront));
            (Vector3 c, Vector3 fw, NodeRef part) = _lineupAnchor.Value;
            Vector3 me = c - fw * LineupFront;
            p.Teleport(me, fw, LineupWalk(_host.Scene, part, c, me));
            p.PrevPosition = me;
            Log.Info("MPHMatch", $"lineup: {(found == null ? "no open place found, in front of the spawn" : "open place")} at {c.X:0.00} {c.Y:0.00} {c.Z:0.00},"
                + $" part {part.PartIndex}; player part {p.NodeRef.PartIndex} (lookup {_host.Scene.GetNodeRefContaining(me).PartIndex})");
        }
        (Vector3 at, Vector3 fwd, NodeRef atPart) = _lineupAnchor.Value;
        Vector3 right = Vector3.Cross(fwd, Vector3.UnitY).Normalized();
        int i = 0;
        foreach (PlayerEntity b in PlayerEntity.Players)
        {
            if (b == null || !b.IsBot) continue;
            int slot = i++;
            if (b.Health <= 0 || _lineupPlaced.Contains(b)) continue;
            Vector3 pos = at + right * ((slot - (n - 1) / 2f) * _lineupGap);
            b.Teleport(pos, -fwd, LineupWalk(_host.Scene, atPart, at, pos));
            b.PrevPosition = pos; // (else the next step's swept collision runs from its spawn)
            _lineupPlaced.Add(b);
            Log.Info("MPHMatch", $"lineup: seat {b.SlotIndex} ({b.Hunter}) at {pos.X:0.00} {pos.Y:0.00} {pos.Z:0.00}, part {b.NodeRef.PartIndex}"
                + $" (lookup {_host.Scene.GetNodeRefContaining(pos).PartIndex})");
        }
    }

    // an open, flat place for a row of n with the player LineupFront in front of its middle: centres on a grid round each
    // player spawn point (in plain view of it), 8 headings each. It fits when every spot (the row with one spare place at each end, 1.5 units
    // before and behind it, and the lane to the player) has floor within 0.3 of the centre's, 2.4 units of headroom, and
    // nothing between neighbouring spots or between the player and each place. The roomiest grid first (centre 0, then 4,
    // then 8 units off a spawn)
    static (Vector3 Centre, Vector3 Forward, NodeRef Part)? FindLineup(Scene scene, int n, float gap)
    {
        float half = (n - 1) / 2f + 1;
        foreach (float ring in new[] { 0f, 4f, 8f })
        {
            foreach (PlayerSpawnEntity spawn in scene.GetPlayerSpawnEntities())
            {
                Vector3 s = spawn.Position;
                if (spawn.NodeRef.PartIndex == -1) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (ring == 0 && (dx != 0 || dz != 0) || ring > 0 && dx == 0 && dz == 0) continue;
                        Vector3 c0 = s + new Vector3(dx * ring, 0, dz * ring);
                        if (!LineupFloor(scene, c0, out float cy)) continue;
                        var c = new Vector3(c0.X, cy, c0.Z);
                        if (!LineupClear(scene, s.AddY(1), c.AddY(1))) continue;
                        NodeRef part = LineupWalk(scene, spawn.NodeRef, s, c);
                        for (int k = 0; k < 8; k++)
                        {
                            float a = k * MathF.PI / 4;
                            var fwd = new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
                            if (LineupFits(scene, c, fwd, half, gap)) return (c, fwd, part);
                        }
                    }
                }
            }
        }
        return null;
    }

    static bool LineupFits(Scene scene, Vector3 c, Vector3 fwd, float half, float gap)
    {
        Vector3 right = Vector3.Cross(fwd, Vector3.UnitY).Normalized();
        Vector3 me = c - fwd * LineupFront;
        Vector3? prevSpot = null;
        foreach (float depth in new[] { 0f, -1.5f, 1.5f })
        {
            prevSpot = null;
            for (float x = -half; x <= half + 0.01f; x += 1)
            {
                Vector3 spot = c + right * (x * gap) + fwd * depth;
                if (!LineupSpot(scene, spot, c.Y, prevSpot)) return false;
                if (depth == 0 && !LineupClear(scene, me.AddY(1), spot.AddY(1))) return false;
                prevSpot = spot;
            }
        }
        prevSpot = null;
        for (float d = 0; d <= LineupFront + 0.01f; d += 1.25f)
        {
            Vector3 spot = c - fwd * d;
            if (!LineupSpot(scene, spot, c.Y, prevSpot)) return false;
            prevSpot = spot;
        }
        return true;
    }

    static bool LineupSpot(Scene scene, Vector3 spot, float floorY, Vector3? prev)
    {
        if (!LineupFloor(scene, spot.WithY(floorY), out float y) || MathF.Abs(y - floorY) > 0.3f) return false;
        if (!LineupClear(scene, new Vector3(spot.X, floorY + 0.2f, spot.Z), new Vector3(spot.X, floorY + 2.6f, spot.Z))) return false;
        return prev is not Vector3 p || LineupClear(scene, p.WithY(floorY + 0.4f), spot.WithY(floorY + 0.4f))
            && LineupClear(scene, p.WithY(floorY + 1.2f), spot.WithY(floorY + 1.2f));
    }

    static bool LineupFloor(Scene scene, Vector3 at, out float y)
    {
        var res = new CollisionResult();
        if (CollisionDetection.CheckBetweenPoints(at.AddY(1.5f), at.AddY(-4), TestFlags.None, scene, ref res) && res.Plane.Y > 0.85f)
        {
            y = res.Position.Y;
            return true;
        }
        y = 0;
        return false;
    }

    // the room part at b, walked from a (in part `from`) through the portals on the way, half a unit at a time (a
    // position lookup alone picks the wrong part where parts overlap: the black world, PlayerNet.NetSnapNodeRef)
    static NodeRef LineupWalk(Scene scene, NodeRef from, Vector3 a, Vector3 b)
    {
        if (from.PartIndex == -1) return PlayerEntity.NetSpawnNodeRef(scene, b);
        int steps = Math.Max(1, (int)MathF.Ceiling((b - a).Length / 0.5f));
        Vector3 prev = a;
        for (int i = 1; i <= steps; i++)
        {
            Vector3 cur = Vector3.Lerp(a, b, i / (float)steps);
            from = scene.UpdateNodeRef(from, prev, cur);
            prev = cur;
        }
        return from;
    }

    static bool LineupClear(Scene scene, Vector3 a, Vector3 b)
    {
        var res = new CollisionResult();
        return !CollisionDetection.CheckBetweenPoints(a, b, TestFlags.None, scene, ref res);
    }

    // BuildInput, every sim step: every weapon and full ammo (endless: refilled each step)
    void TryStep(ref MphRecomp.Campaign.CampaignInput input, PlayerEntity p)
    {
        if (!_tryControls) return;
        p.HostGiveAllWeapons();
        TryPerchStep(p);
        // (C20) faint and pulsing, at the foot of the HUD: queued each step, shown for two
        float alpha = 0.32f + 0.18f * MathF.Sin(_tryHintTick++ * (MathF.Tau / 120));
        p.QueueHudMessage(128, TryHintY, MphRead.Hud.Align.Center, 200, 8, new ColorRgba(0x7FFF), alpha, 2 / 60f, 0, "press start to exit");
    }

    const float TryHintY = 180;
    int _tryHintTick;

    // the frame loop, on Start: TRY CONTROLS leaves at once (back to the controls, RecompMenus PendingReturn) instead of
    // opening the pause menu. False outside TRY CONTROLS
    bool TryLeave()
    {
        if (!_tryControls) return false;
        if (!_tryLeaving)
        {
            _tryLeaving = true;
            Log.Info("MPHMatch", "try controls: Start, back to the menu");
            ExitSink?.Invoke();
        }
        return true;
    }

    // every spawn (the first and each respawn): the player is put in the perch, behind the Imperialist's pickup
    void TryPerchStep(PlayerEntity p)
    {
        if (_host == null) return;
        if (p.Health <= 0)
        {
            _tryPerched = false; // dead: the next life starts in the perch too
            return;
        }
        if (_tryPerched) return;
        _tryPerched = true;
        if (!_tryPerchLooked)
        {
            _tryPerchLooked = true;
            _tryPerch = FindTryPerch(_host.Scene);
        }
        if (_tryPerch is not { } perch) return;
        p.Teleport(perch.At, perch.Facing, perch.Part);
        p.PrevPosition = perch.At; // (else the next step's swept collision runs from the spawn point)
    }

    const float PerchBehind = 2.6f; // how far behind the pickup the player stands (it shows just in front, low in the view)

    // the Imperialist's pickup and a standing place just behind it, on the side away from the arena's middle (the average
    // of its spawn points and pickups), facing over the pickup: floor within half a unit of the pickup's, headroom, and a
    // clear line to the pickup. Straight back first, then turned up to 75 degrees either way, then closer in
    static (Vector3 At, Vector3 Facing, NodeRef Part)? FindTryPerch(Scene scene)
    {
        ItemSpawnEntity? imp = null;
        Vector3 sum = Vector3.Zero;
        int n = 0;
        foreach (EntityBase e in scene.Entities)
        {
            if (e is ItemSpawnEntity item)
            {
                sum += item.Position;
                n++;
                if (item.Data.ItemType == ItemType.Imperialist) imp = item;
            }
            else if (e is PlayerSpawnEntity spawn)
            {
                sum += spawn.Position;
                n++;
            }
        }
        if (imp == null || n == 0)
        {
            Log.Info("MPHMatch", "try controls: no Imperialist pickup here, the normal spawn");
            return null;
        }
        Vector3 at = imp.Position;
        var out0 = new Vector3(sum.X / n - at.X, 0, sum.Z / n - at.Z);
        out0 = out0.LengthSquared > 1e-6f ? out0.Normalized() : Vector3.UnitZ;
        foreach (float dist in new[] { PerchBehind, 1.8f, 1.3f, 0.9f })
        {
            foreach (float deg in new[] { 0f, 25f, -25f, 50f, -50f, 75f, -75f })
            {
                float a = MathHelper.DegreesToRadians(deg);
                var dir = new Vector3(out0.X * MathF.Cos(a) - out0.Z * MathF.Sin(a), 0, out0.X * MathF.Sin(a) + out0.Z * MathF.Cos(a));
                Vector3 spot = at - dir * dist;
                if (!LineupFloor(scene, spot, out float y) || MathF.Abs(y - at.Y) > 0.5f) continue;
                spot = spot.WithY(y);
                if (!LineupClear(scene, spot.AddY(0.2f), spot.AddY(2.4f))) continue;
                if (!LineupClear(scene, spot.AddY(1), at.AddY(1))) continue;
                var facing = new Vector3(at.X - spot.X, 0, at.Z - spot.Z).Normalized();
                NodeRef part = LineupWalk(scene, imp.NodeRef, at, spot);
                Log.Info("MPHMatch", $"try controls: perch at {spot.X:0.00} {spot.Y:0.00} {spot.Z:0.00} ({dist:0.0} behind the Imperialist"
                    + $" at {at.X:0.00} {at.Y:0.00} {at.Z:0.00}, turned {deg:0}), part {part.PartIndex}");
                return (spot, facing, part);
            }
        }
        Log.Info("MPHMatch", "try controls: no room behind the Imperialist, on the pickup itself");
        return (at, out0, imp.NodeRef);
    }

    // BuildInput, every sim step, last: the dev --es morphat / holdfrom presses
    void DevCaptureInput(ref MphRecomp.Campaign.CampaignInput input, PlayerEntity p)
    {
        LineupStep(p);
        DdTestStep();
        TurretBotStep(p);
        if (_devTurretViewed && _devTurretViewAim != 0 && _devTurretViewAimed < 30)
        {
            input.AimDelta = new System.Numerics.Vector2(0, _devTurretViewAim);
            _devTurretViewAimed++;
        }
        if (_devMorphAt <= 0 && _devJumpAt <= 0 && _devHoldFrom <= 0 && _devSeatAt <= 0 && _devGratesAt <= 0) return;
        _devSteps++;
        if (_devGrateCam && _devSteps == _devGratesAt - 30 && _host != null)
        {
            foreach (EntityBase e in _host.Scene.Entities)
            {
                if (e is MphRecomp.Arenas.EchoesBreakableEntity { Grate: true } g) Log.Info("MPHMatch", $"dev gratecam: a grate at {g.Position}");
            }
            foreach (EntityBase e in _host.Scene.Entities)
            {
                if (e is not MphRecomp.Arenas.EchoesBreakableEntity { Grate: true } first) continue;
                Log.Info("MPHMatch", $"dev gratecam: grate at {first.Position}, player at {p.Position}");
                Vector3 next = first.Position + new Vector3(0, 0, 100);
                foreach (EntityBase o in _host.Scene.Entities)
                    if (o is MphRecomp.Arenas.EchoesBreakableEntity { Grate: true } og && og != first && (og.Position - first.Position).Length < (next - first.Position).Length) next = og.Position;
                Vector3 by = (first.Position + next) / 2 + new Vector3(0, 0.5f, 0), look = first.Position - by; // (between two grates of a row, on the walkway)
                p.Teleport(by, look.Normalized(), _host.Scene.GetNodeRefContaining(by));
                p.PrevPosition = by;
                break;
            }
        }
        if (_devSteps == _devGratesAt && _host != null)
        {
            foreach (EntityBase e in _host.Scene.Entities)
            {
                if (e is MphRecomp.Arenas.EchoesBreakableEntity { Grate: true, Up: true } g) g.Hit(g.Hp);
            }
        }
        if (_devSteps == _devSeatAt && _host != null && MphRecomp.Arenas.EchoesTurret.For(_host.RoomId) is { } spot)
        {
            var enter = new Vector3((spot.EnterMin[0] + spot.EnterMax[0]) / 2, spot.EnterMax[1], (spot.EnterMin[2] + spot.EnterMax[2]) / 2);
            Log.Info("MPHMatch", $"dev seat: hp {p.Health} at {p.Position} before the teleport");
            p.Teleport(enter, -Vector3.UnitZ, _host.Scene.GetNodeRefContaining(enter));
            p.PrevPosition = enter; // (else the next step's swept collision runs from the spawn and walls put her back)
        }
        if (_devSeatAt > 0 && _devSteps >= _devSeatAt && (_devSteps - _devSeatAt) % 60 == 0 && _devSteps - _devSeatAt <= 300 && _host != null)
        {
            var t = MphRecomp.Multiplayer.Net.NetWorld.TurretOf(_host.Scene);
            var spotNow = MphRecomp.Arenas.EchoesTurret.For(_host.RoomId);
            Log.Info("MPHMatch", $"dev seat +{_devSteps - _devSeatAt}: turret {(t == null ? "none" : $"{t.State} occ {t.Occupant?.SlotIndex}")}, "
                + $"player at {p.Position.X:0.00} {p.Position.Y:0.00} {p.Position.Z:0.00} alt {p.IsAltForm} seated {p.HostSeated} active {p.LoadFlags.TestFlag(LoadFlags.Active)} hp {p.Health} held {p.HostSlotHeld} morphing {p.IsMorphing}, enter y {spotNow?.EnterMin[1]:0.00}..{spotNow?.EnterMax[1]:0.00}");
        }
        // (the turret's aim: the move stick, from 2.5 s after the seat (its rise) for the steps asked; occupant view captures)
        if (_devSeatAt > 0 && _devSteps >= _devSeatAt + 150 && _devSteps < _devSeatAt + 150 + _devSeatMoveFor) input.Move = _devSeatMove;
        if (_devSteps == _devMorphAt) input.Buttons |= MphRecomp.Campaign.CampaignButtons.Morph;
        if (_devSteps == _devJumpAt) input.Buttons |= MphRecomp.Campaign.CampaignButtons.Jump;
        if (_devHoldFrom > 0 && _devSteps >= _devHoldFrom)
        {
            input.Buttons |= MphRecomp.Campaign.CampaignButtons.Shoot;
            if (p.IsAltForm) input.Buttons |= MphRecomp.Campaign.CampaignButtons.AltAttack;
            if (_devPullBot > 0)
            {
                p.Health = p.HealthMax; // the bot shoots back from there: the capture's player stays up
                var ahead = new Vector3(p.FacingVector.X, 0, p.FacingVector.Z);
                ahead = ahead.LengthSquared > 1e-6f ? ahead.Normalized() : Vector3.UnitZ;
                foreach (PlayerEntity other in PlayerEntity.Players)
                {
                    if (other != null && other != p && other.Health > 0) other.Position = p.Position + ahead * _devPullBot;
                }
            }
        }
    }

    // dev --es turretbot 1, every sim step: while the turret is free, the first live practice target is put on its seat
    // spot (the turret takes it in). Not while the player is within reach of the entry kill (5 Echoes units): it would
    // kill them as they come back from bombing it
    void TurretBotStep(PlayerEntity p)
    {
        if (!_devTurretBot || _host == null) return;
        if (_devTurretView && !_devTurretViewed && p.Health > 0 && MphRecomp.Multiplayer.Net.NetWorld.TurretOf(_host.Scene) is { State: MphRecomp.Arenas.EchoesTurretEntity.TurretState.Active } on
            && MphRecomp.Arenas.EchoesTurret.For(_host.RoomId) is { } at)
        {
            // on the floor (the enter pad's height), most of the way from the turret to the slot
            var slot = new Vector3((at.SlotMin[0] + at.SlotMax[0]) / 2, 0, (at.SlotMin[2] + at.SlotMax[2]) / 2);
            var pad = new Vector3((at.EnterMin[0] + at.EnterMax[0]) / 2, 0, (at.EnterMin[2] + at.EnterMax[2]) / 2);
            var piv = new Vector3(on.PivotNow.X, 0, on.PivotNow.Z);
            Vector3 by = _devTurretViewFrom is Vector2 vf ? piv + new Vector3(vf.X, 0, vf.Y) : piv + (slot - piv) * 0.8f;
            by.Y = _devTurretViewFrom == null ? at.EnterMax[1] : on.PivotNow.Y + 1; // (from away: dropped onto whatever floor is there)
            Vector3 look = on.PivotNow - by;
            look.Y = 0;
            p.Teleport(by, look.LengthSquared > 1e-6f ? look.Normalized() : -Vector3.UnitZ, _host.Scene.GetNodeRefContaining(by));
            p.PrevPosition = by;
            _devTurretViewed = true;
        }
        if (_devTurretSlot && !_devTurretSlotted && _devTurretViewed && p.IsAltForm && !p.IsMorphing
            && MphRecomp.Arenas.EchoesTurret.For(_host.RoomId) is { } held)
        {
            var slot = new Vector3((held.SlotMin[0] + held.SlotMax[0]) / 2, (held.SlotMin[1] + held.SlotMax[1]) / 2, (held.SlotMin[2] + held.SlotMax[2]) / 2);
            p.Teleport(slot, -Vector3.UnitZ, _host.Scene.GetNodeRefContaining(slot));
            p.PrevPosition = slot;
            _devTurretSlotted = true;
        }
        if (MphRecomp.Multiplayer.Net.NetWorld.TurretOf(_host.Scene) is not { State: MphRecomp.Arenas.EchoesTurretEntity.TurretState.Free }
            || MphRecomp.Arenas.EchoesTurret.For(_host.RoomId) is not { } spot)
        {
            return;
        }
        var enter = new Vector3((spot.EnterMin[0] + spot.EnterMax[0]) / 2, spot.EnterMax[1], (spot.EnterMin[2] + spot.EnterMax[2]) / 2);
        var away = new Vector2(p.Position.X - enter.X, p.Position.Z - enter.Z);
        if (p.Health > 0 && away.Length < (MphRecomp.Arenas.EchoesTurret.EntryKillRadius + 1) * MphRecomp.Arenas.EchoesArena.DefaultScale) return;
        foreach (PlayerEntity bot in PlayerEntity.Players)
        {
            if (bot == null || !bot.IsBot || bot.Health == 0 || bot.HostSeated || !bot.LoadFlags.TestFlag(LoadFlags.Active)) continue;
            bot.Teleport(enter, -Vector3.UnitZ, _host.Scene.GetNodeRefContaining(enter));
            bot.PrevPosition = enter; // (else the swept collision runs from where it stood)
            Log.Info("MPHMatch", $"dev turret bot: seat {bot.SlotIndex} ({bot.Hunter}) put in the turret");
            return;
        }
    }

    // BuildInput, every sim step: R3 let go (and not half of L3 + R3, the gun-adjust pause) switches orbit / aim.
    // True while the orbit camera has the right stick (no aim from it)
    bool SoloStick(PadState pad)
    {
        // MphRead: -1 UA = never runs out, for the Hyper Ball; a beam's charge wants UA >= its cost (no -1 case: owner 10-15
        // "Charging isn't working" with the Paralyzer), so on foot the pool is kept full instead
        if (_soloInfUa && PlayerEntity.Main is PlayerEntity me)
        {
            if (me.IsAltForm) me.HyperBallUa = -1;
            else me.HostRefillUa();
        }
        if (_soloTargets)
        {
            foreach (PlayerEntity bot in PlayerEntity.Players)
            {
                if (bot != null && bot.IsBot && bot.Health > 0) bot.Health = bot.HealthMax; // hit, never killed
            }
        }
        if (!_solo) return false;
        bool r3 = pad[PadButton.ThumbR];
        if (r3 && pad[PadButton.ThumbL]) _soloR3Combo = true;
        if (!r3 && _prevSoloR3)
        {
            if (!_soloR3Combo && !_gunComboUsed) // (L3 + R3 can open the pause between two steps: _gunComboUsed)
            {
                // orbit -> over the shoulder -> first person -> orbit
                if (_soloOrbit) _soloOrbit = false;
                else if (!_soloFirstPerson) _soloFirstPerson = true;
                else
                {
                    _soloFirstPerson = false;
                    _soloOrbit = true;
                }
                SetThirdPerson(!_soloFirstPerson);
                _orbitPlaced = false; // starts from where the view is now
                _statusCountdown = 0;
                Log.Info("MPHMatch", "solo camera: " + (_soloOrbit ? "orbit" : _soloFirstPerson ? "first person" : "aim"));
            }
            _soloR3Combo = false;
        }
        _prevSoloR3 = r3;
        if (_soloOrbit)
        {
            float v = _aimInvertY ? -pad.Ry : pad.Ry;
            _orbitYaw -= (_aimInvertX ? -pad.Rx : pad.Rx) * OrbitSpeed / 60f;
            _orbitPitch = Math.Clamp(_orbitPitch + v * OrbitSpeed / 60f, OrbitPitchMin, OrbitPitchMax);
        }
        return _soloOrbit;
    }

    // BuildInput: in the orbit camera, D-pad up / down held = zoom in / out (owner 2026-10-15, a close look at Brawl Zero
    // Suit's springs); true = it took them (the D-pad's weapon picks wait)
    bool SoloZoom(bool up, bool down)
    {
        if (!_solo || !_soloOrbit || up == down) return false;
        _orbitZoom = Math.Clamp(_orbitZoom * MathF.Exp((up ? -OrbitZoomRate : OrbitZoomRate) / 60f), OrbitZoomMin, OrbitZoomMax);
        return true;
    }

    // R3 is the camera switch here, not zoom
    bool SoloTakesR3(PadState pad) => _solo && pad[PadButton.ThumbR];

    // UpdateThirdPerson, after each sim step: true when the orbit camera placed the view (Scene.HostView); false hands
    // the view back to the over-the-shoulder camera
    bool UpdateSoloOrbit(Scene scene, PlayerEntity p)
    {
        if (!_solo) return false;
        if (!_soloOrbit || CameraSequence.Current != null || !(MathF.Abs(p.CameraInfo.ViewMatrix.Determinant) > 1e-3f))
        {
            // (the match intro's fly-over and a player without a camera yet keep the game's own view)
            scene.HostView = null;
            _orbitPlaced = false;
            return false;
        }
        Vector3 center = p.Position;
        if (!p.IsAltForm)
        {
            center.Y += (Fixed.ToFloat(p.Values.MinPickupHeight) + Fixed.ToFloat(p.Values.MaxPickupHeight)) / 2 + 0.2f;
        }
        float len = (p.IsAltForm ? OrbitDistBall : OrbitDistBody) * _orbitZoom;
        if (!_orbitPlaced)
        {
            // from where the view is now, so the switch doesn't jump (or --es orbit's angle round from behind)
            Vector3 from = scene.CameraPosition - center;
            if (_orbitStart is float deg)
            {
                _orbitStart = null;
                var back = new Vector3(-p.FacingVector.X, 0, -p.FacingVector.Z);
                from = back.LengthSquared > 1e-6f ? back.Normalized() : Vector3.UnitZ;
                _orbitYaw = MathF.Atan2(from.X, from.Z) + MathHelper.DegreesToRadians(deg);
                _orbitPitch = 0.25f;
            }
            else if (from.LengthSquared > 1e-4f)
            {
                from.Normalize();
                _orbitYaw = MathF.Atan2(from.X, from.Z);
                _orbitPitch = Math.Clamp(MathF.Asin(Math.Clamp(from.Y, -1, 1)), OrbitPitchMin, OrbitPitchMax);
            }
            _orbitDist = len;
            _orbitPlaced = true;
        }
        float cp = MathF.Cos(_orbitPitch);
        var dir = new Vector3(MathF.Sin(_orbitYaw) * cp, MathF.Sin(_orbitPitch), MathF.Cos(_orbitYaw) * cp);
        // pulled in where the room's collision is closer (as the over-the-shoulder view): snap in, ease back out
        float allowed = len;
        CollisionResult res = default;
        if (CollisionDetection.CheckBetweenPoints(center, center + dir * (len + OrbitMargin), TestFlags.Players, scene, ref res))
        {
            allowed = Math.Clamp(res.Distance * (len + OrbitMargin) - OrbitMargin, 0.2f, len);
        }
        _orbitDist = allowed < _orbitDist ? allowed : _orbitDist + (allowed - _orbitDist) * 0.15f;
        scene.HostCameraOffset = Vector3.Zero;
        scene.HostView = new HostView(center + dir * _orbitDist, center, Vector3.UnitY, 0);
        return true;
    }

    // the match's status line (MatchStatusText): which camera has the right stick
    string SoloStatusLine() => !_solo ? ""
        : (_soloOrbit ? "\nSOLO  ORBIT CAMERA: right stick circles you, D-pad up / down zooms  (R3: over the shoulder)"
        : _soloFirstPerson ? "\nSOLO  FIRST PERSON  (R3: orbit camera)"
        : "\nSOLO  right stick aims  (R3: first person)") + SelfBodyStatusLine() + PlacerStatusLine();

    // --es selfbody 1: the body's hits so far, how many in the head band, the last one's place, the body's health
    static string SelfBodyStatusLine()
    {
        if (!PlayerEntity.HostSelfBodyHits || PlayerEntity.Players.Count == 0) return "";
        PlayerEntity p = PlayerEntity.Players[0];
        string body = p.Flags2.TestFlag(PlayerFlags2.Halfturret) ? $"body {p.Halfturret.Health} hp" : "drone in (Morph: send it out, shoot the body)";
        return $"\nSELF HITS {p.BodyHits} (head band {p.BodyHeadHits})  last: {(p.BodyLastHit == "" ? "-" : p.BodyLastHit)}  {body}";
    }
}
