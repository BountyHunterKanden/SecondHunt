using System;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The bots' live switches in the gun-adjust pause (L3 + R3), for comparing on and off in one match. Bots this device
// hosts only, and only with BOT VANILLA PARITY on (always on in a bot match). A replay keeps the values the match
// started with, not a flip. Touch only (owner 2026-10-26: no stick or button toggles, they fired by accident while
// closing the pause): a tap on a switch's line flips it.
//  - stuck fix (PlayerEntity.HostBotStuckFix, owner 2026-10-25: bots never stuck; PlayerAiUnstick.cs)
//  - exit order (PlayerEntity.HostBotSwitchOrder, 10-25: vanilla's in-order exits, so Kanden/Spire keep their alt form
//    as long as vanilla's)
//  - flat node check (PlayerEntity.HostBotNodeFlat, 10-25: vanilla drops a node by horizontal distance, so a bot that
//    fell below a ledge node isn't pinned)
//  - stuck in sight (PlayerEntity.HostBotStuckSight, 10-26: Oubliette, Spire walked into a wall 9.5 s in sight of the
//    owner; the stuck fix waits while a bot sees a foe, and skipped her as "in a pad's flight" while wedged off the ground)
//  - break walls (PlayerEntity.HostBotBreakWalls, 10-26: Crossfire Chaos, bots walked into a breakable wall on their route
//    forever; now they shoot it down and walk on)
//  - stuck fix vertical (PlayerEntity.HostBotStuckVertical, 10-26 link audit: the stuck fix's clock ran while a bot fell
//    or rode a lift's shaft and pushed her out of Fuel Stack's lifts; now it waits while she moves up or down in the air,
//    and she lets a lift carry her up to her node without steering off it)
//  - edge jump (PlayerEntity.HostBotEdgeJump, docs/BOT_MOVE_SOLVER.md P2: a bot jumps at a ledge or a wall short of its
//    next node, the move that crossed 269 of the 357 links vanilla bots fail; the stuck fix's climbs become a backstop:
//    step aside, jump again, a run-up, then the node is marked bad for 5 s; needs the stuck fix on)
//  - pad jump (PlayerEntity.HostBotEdgePadJump, owner 10-27: thrown by a jump pad and coming down short of her next node,
//    a bot uses her air jump when it reaches the node, as a player does; needs the edge jump on)
//  - lift guard (PlayerEntity.HostBotLiftGuard, owner GO 10-28: no stuck-fix climb or edge jump into a lift her route
//    doesn't go up; Fuel Stack's climbs threw bots into the shaft and its death volume over the top), and no edge jump
//    mid-launch (Harvester's ramps to the far ends: the jump cut the rise and she fell short)
// Demos (owner 10-26: "We should do this for any of these fix tests"): a tap on a DEMO line puts a bot where a fix's
// symptom shows (PlayerAiDemo.cs) and, once the pause closes, a camera watches it for 20 s. The exit order has no spot,
// so the line under it counts Kanden/Spire bots' alt-form runs instead, since the switch last flipped.
internal sealed partial class CampaignRenderer
{
    const string BotStuckLine = "BOT STUCK FIX", BotOrderLine = "BOT EXIT ORDER", BotFlatLine = "BOT FLAT NODE CHECK",
        BotSightLine = "BOT STUCK IN SIGHT FIX", BotWallsLine = "BOT BREAK WALLS", BotVerticalLine = "BOT STUCK FIX VERTICAL",
        BotEdgeLine = "BOT EDGE JUMP", BotPadLine = "BOT PAD JUMP", BotLiftLine = "BOT LIFT/PAD GUARD";
    const string BotDemoFlatLine = "DEMO: FLAT NODE CHECK", BotDemoStuckLine = "DEMO: STUCK FIX";
    int _botSwitchTap; // 0 none, 1 stuck fix, 2 exit order, 3 flat node check, 4/5 demo 1/2, 6 stuck in sight, 7 break walls, 8 stuck fix vertical, 9 edge jump, 10 pad jump, 11 lift guard (UI thread -> BotStuckInput)
    string _botDemoResult = "";
    bool _botDemoCam;

    public bool GunPauseOpen => _paused && !PauseMenuOpen;

    // a tap on the status text while the gun-adjust pause is up: text = the status text as shown, line = the tapped
    // line of it (UI thread). True when it was a bot switch's line.
    public bool BotSwitchTap(string text, int line)
    {
        string[] lines = text.Split('\n');
        if (!_paused || line < 0 || line >= lines.Length) return false;
        string l = lines[line];
        // (the vertical line first: it starts with the stuck fix's)
        int which = l.StartsWith(BotVerticalLine) ? 8 : l.StartsWith(BotStuckLine) ? 1 : l.StartsWith(BotOrderLine) ? 2 : l.StartsWith(BotFlatLine) ? 3
            : l.StartsWith(BotDemoFlatLine) ? 4 : l.StartsWith(BotDemoStuckLine) ? 5 : l.StartsWith(BotSightLine) ? 6
            : l.StartsWith(BotWallsLine) ? 7 : l.StartsWith(BotEdgeLine) ? 9 : l.StartsWith(BotPadLine) ? 10 : l.StartsWith(BotLiftLine) ? 11 : 0;
        if (which == 0) return false;
        System.Threading.Volatile.Write(ref _botSwitchTap, which);
        return true;
    }

    // PausedInput, every frame while paused
    void BotStuckInput(PadState pad)
    {
        int tap = System.Threading.Interlocked.Exchange(ref _botSwitchTap, 0);
        switch (tap)
        {
        case 1:
            PlayerEntity.HostBotStuckFix = !PlayerEntity.HostBotStuckFix;
            Log.Info("MPHMatch", $"Bot stuck fix: {(PlayerEntity.HostBotStuckFix ? "on" : "off")}");
            break;
        case 2:
            PlayerEntity.HostBotSwitchOrder = !PlayerEntity.HostBotSwitchOrder;
            PlayerEntity.HostBotAltStatsReset();
            Log.Info("MPHMatch", $"Bot exit order: {(PlayerEntity.HostBotSwitchOrder ? "on" : "off")}");
            break;
        case 3:
            PlayerEntity.HostBotNodeFlat = !PlayerEntity.HostBotNodeFlat;
            Log.Info("MPHMatch", $"Bot flat node check: {(PlayerEntity.HostBotNodeFlat ? "on" : "off")}");
            break;
        case 6:
            PlayerEntity.HostBotStuckSight = !PlayerEntity.HostBotStuckSight;
            Log.Info("MPHMatch", $"Bot stuck in sight fix: {(PlayerEntity.HostBotStuckSight ? "on" : "off")}");
            break;
        case 7:
            PlayerEntity.HostBotBreakWalls = !PlayerEntity.HostBotBreakWalls;
            Log.Info("MPHMatch", $"Bot break walls: {(PlayerEntity.HostBotBreakWalls ? "on" : "off")}");
            break;
        case 8:
            PlayerEntity.HostBotStuckVertical = !PlayerEntity.HostBotStuckVertical;
            Log.Info("MPHMatch", $"Bot stuck fix vertical: {(PlayerEntity.HostBotStuckVertical ? "on" : "off")}");
            break;
        case 9:
            PlayerEntity.HostBotEdgeJump = !PlayerEntity.HostBotEdgeJump;
            Log.Info("MPHMatch", $"Bot edge jump: {(PlayerEntity.HostBotEdgeJump ? "on" : "off")}");
            break;
        case 10:
            PlayerEntity.HostBotEdgePadJump = !PlayerEntity.HostBotEdgePadJump;
            Log.Info("MPHMatch", $"Bot pad jump: {(PlayerEntity.HostBotEdgePadJump ? "on" : "off")}");
            break;
        case 11:
            PlayerEntity.HostBotLiftGuard = !PlayerEntity.HostBotLiftGuard;
            Log.Info("MPHMatch", $"Bot lift guard: {(PlayerEntity.HostBotLiftGuard ? "on" : "off")}");
            break;
        case 4:
        case 5:
            _botDemoResult = _host == null ? "" : PlayerEntity.HostBotDemo(_host.Scene, tap - 3);
            Log.Info("MPHMatch", $"Bot demo: {_botDemoResult}");
            break;
        default:
            return;
        }
        _statusCountdown = 0;
    }

    static string OnOff(bool on) => on ? "[ ON  ]" : "[ OFF ]";

    string BotStuckPauseText() =>
        $"\n{BotStuckLine}        {OnOff(PlayerEntity.HostBotStuckFix)}  tap to switch"
        + (PlayerEntity.HostBotVanillaParity ? "" : " (needs bot vanilla parity)")
        + $"\n{BotOrderLine}       {OnOff(PlayerEntity.HostBotSwitchOrder)}  tap to switch"
        + FormattableString.Invariant($"\n    Kanden/Spire bots since the last flip: longest alt form {PlayerEntity.DemoAltLongest / 60f:0.0} s, runs of 12 s+: {PlayerEntity.DemoAltLong}")
        + $"\n{BotFlatLine}  {OnOff(PlayerEntity.HostBotNodeFlat)}  tap to switch"
        + $"\n{BotSightLine} {OnOff(PlayerEntity.HostBotStuckSight)}  tap to switch"
        + $"\n{BotWallsLine}        {OnOff(PlayerEntity.HostBotBreakWalls)}  tap to switch"
        + $"\n{BotVerticalLine} {OnOff(PlayerEntity.HostBotStuckVertical)}  tap to switch"
        + $"\n{BotEdgeLine}        {OnOff(PlayerEntity.HostBotEdgeJump)}  tap to switch"
        + (PlayerEntity.HostBotStuckFix ? "" : " (needs bot stuck fix)")
        + $"\n{BotPadLine}         {OnOff(PlayerEntity.HostBotEdgePadJump)}  tap to switch"
        + (PlayerEntity.HostBotEdgeJump ? "" : " (needs bot edge jump)")
        + $"\n{BotLiftLine}   {OnOff(PlayerEntity.HostBotLiftGuard)}  tap to switch"
        + (PlayerEntity.HostBotStuckFix ? "" : " (needs bot stuck fix)")
        + $"\n{BotDemoFlatLine}  (High Ground)  tap, then resume and watch"
        + $"\n{BotDemoStuckLine}  (Stasis Bunker)  tap, then resume and watch"
        + (_botDemoResult.Length > 0 ? $"\n    {_botDemoResult}" : "");

    // UpdateThirdPerson, after each sim step: while a demo's bot is being watched, a view from behind where it was put
    // (pulled in where walls are closer); false hands the view back
    bool UpdateBotDemoCam(Scene scene)
    {
        PlayerEntity? bot = PlayerEntity.DemoWatchBot;
        if (bot == null || CameraSequence.Current != null)
        {
            if (_botDemoCam)
            {
                _botDemoCam = false;
                scene.HostView = null;
                PlayerEntity.HostMainSpectated = false;
                PlayerEntity.HostHideReticle = false;
            }
            return false;
        }
        _botDemoCam = true;
        Vector3 center = bot.Position + new Vector3(0, 1, 0);
        Vector3 dir = (PlayerEntity.DemoWatchFrom * 5 + new Vector3(0, 3, 0)).Normalized();
        float len = 6;
        CollisionResult res = default;
        if (CollisionDetection.CheckBetweenPoints(center, center + dir * (len + 0.3f), TestFlags.Players, scene, ref res))
        {
            len = Math.Clamp(res.Distance * (len + 0.3f) - 0.3f, 0.5f, len);
        }
        scene.HostCameraOffset = Vector3.Zero;
        scene.HostView = new HostView(center + dir * len, center, Vector3.UnitY, 0);
        PlayerEntity.HostMainSpectated = true;
        PlayerEntity.HostHideReticle = true;
        return true;
    }
}
