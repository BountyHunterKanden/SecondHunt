using System;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    [Flags]
    public enum CampaignButtons
    {
        None = 0,
        Shoot = 1 << 0,
        Jump = 1 << 1,
        Morph = 1 << 2,
        Boost = 1 << 3,
        AltAttack = 1 << 4, // morph ball bomb / hunter alt attack
        Zoom = 1 << 5,
        ScanVisor = 1 << 6,
        Scan = 1 << 7,
        NextWeapon = 1 << 8,
        PrevWeapon = 1 << 9,
        WeaponMenu = 1 << 10,
        AffinitySlot = 1 << 11 // the last special (non-Power/Missile) weapon
    }

    // One frame of player intent for the campaign sim. Movement is digital like the DS d-pad the game was built
    // around (a stick past the dead zone counts as a press); aim is a delta in the game's mouse units.
    public struct CampaignInput
    {
        public System.Numerics.Vector2 Move; // X = strafe (+right), Y = forward (+fwd)
        public System.Numerics.Vector2 AimDelta; // X = yaw, Y = pitch
        public CampaignButtons Buttons;
        public BeamType SelectWeapon; // held to pick a weapon directly; None = no change
        // stick direction for the weapon wheel while WeaponMenu is held (six 60-degree sectors, clockwise from up:
        // Volt Driver, Battlehammer, Imperialist, Judicator, Magmaul, Shock Coil)
        public System.Numerics.Vector2 WeaponWheel;
        // a dialog button to press this frame (OK / YES / NO / page arrows -- touch buttons on the DS); null = none.
        // Nullable on purpose: HostDialogButton.Okay is 0, so a plain field left unset tapped OK on every frame, which
        // closed the scan dialog of anything already in the logbook (its OK is live at once) the frame it opened
        public HostDialogButton? DialogButton;

        public const float MoveDeadZone = 0.35f;
    }

    // Hosts MphRead's own adventure simulation (Scene + RoomEntity transitions + GameState/StorySave + all entity
    // logic) with no GL, audio or OpenTK input: Scene.Headless. The presentation layer (Android renderer, HUD)
    // reads the scene's entities; the sim never knows it is being drawn. One room change after another happens
    // inside the same scene, exactly as in MphRead (connector corridors, teleporters, movies' after-actions).
    // Saves stay with the host: MphRead runs with save slot 0 (never touches disk) on the StorySave given here.
    // partial: a multiplayer match starts through the same host (MphRecomp.Core/Multiplayer/MatchStart.cs)
    public sealed partial class CampaignHost : IDisposable
    {
        public Scene Scene { get; }
        public PlayerEntity Player => PlayerEntity.Main;
        public int RoomId => Scene.RoomId;
        public long Frame { get; private set; }
        // a prompt / scan result / item dialog is up and the game is paused on it (answer with DialogButton)
        public bool DialogPaused => GameState.DialogPause;
        public DialogType Dialog => Player.DialogType;
        public bool WeaponMenuOpen => Player.Flags1.TestFlag(PlayerFlags1.WeaponMenuOpen);
        // set when MphRead asks to close its window: entering the ship, game over quit, or the ending
        public bool Ended { get; private set; }

        private CampaignHost(Scene scene)
        {
            Scene = scene;
        }

        // Starts a campaign session in a story room (e.g. "UNIT2_LAND"). setupSave runs on the fresh or supplied
        // StorySave before the room loads (boss flags, weapons, visited rooms...) so it decides the entity layer.
        // collectDrawItems: a renderer will draw this session -- build MphRead's draw list every frame (see
        // Scene.CollectDrawItems); the viewport size drives the camera frustum and portal culling.
        // arriving: false for a landing room you're RETURNING to on foot (the ship menu's EXIT SHIP) -- no landing
        // movie, no landing camseq/forced-inactive-spawn skip (Cheats.SkipPlanetIntros, restored after the room
        // loads either way). Every session also disables the hatch's own takeoff movie (GameState.SkipShipTakeoffMovie):
        // a ship-menu LAUNCH SHIP action is expected to play that cutscene itself (see MoviePlayer.Open, used the same
        // standalone way the front end plays its own movies), never the hatch's YES by itself.
        // holdSpawn: the main player stays unspawned until ReleaseSpawn (the cockpit view: vanilla spawns her, effect and
        // PLAYER_SPAWN, when she steps out of the ship, not when the room loads under the cockpit).
        public static CampaignHost Start(string roomName, StorySave? save = null, Action<StorySave>? setupSave = null,
            Hunter hunter = Hunter.Samus, bool collectDrawItems = false, int viewWidth = 256, int viewHeight = 192,
            bool arriving = true, bool holdSpawn = false)
        {
            Scene.Headless = true;
            PlayerEntity.HostHoldMainSpawn = holdSpawn;
            Scene.CollectDrawItems = collectDrawItems;
            // populates Music's internal seq/room tables; without it, the first room's Music.TryPlayRoomMusic (called
            // during scene setup below) throws on the still-null tables -- desktop callers get this from Renderer.cs,
            // which headless hosts never run. Safe before a host is registered: MusicPlayer.Load no-ops with no
            // playback device (and no Host set yet), same as it always has under Scene.Headless.
            MphRead.Music.Init();
            Menu.SaveSlot = 0;
            // MphRead's default hands out any weapon on its direct key (a desktop testing convenience)
            Cheats.FreeWeaponSelect = false;
            CampaignHost? host = null;
            var scene = new Scene(new Vector2i(viewWidth, viewHeight), null!, null!, _ => { }, () =>
            {
                if (host != null)
                {
                    host.Ended = true;
                }
            });
            host = new CampaignHost(scene);
            if (save != null)
            {
                GameState.UseStorySave(save);
            }
            if (setupSave != null)
            {
                setupSave(GameState.StorySave);
                GameState.UpdateCleanSave(force: true);
            }
            scene.AddPlayer(hunter);
            bool skipBefore = Cheats.SkipPlanetIntros;
            Cheats.SkipPlanetIntros = !arriving;
            scene.AddRoom(roomName, GameMode.SinglePlayer);
            scene.OnLoad();
            Cheats.SkipPlanetIntros = skipBefore;
            GameState.SkipShipTakeoffMovie = true;
            return host;
        }

        public void Step(in CampaignInput input)
        {
            if (Ended)
            {
                return;
            }
            PlayerControls c = Player.Controls;
            c.MoveUp.HostDown = input.Move.Y > CampaignInput.MoveDeadZone;
            c.MoveDown.HostDown = input.Move.Y < -CampaignInput.MoveDeadZone;
            c.MoveRight.HostDown = input.Move.X > CampaignInput.MoveDeadZone;
            c.MoveLeft.HostDown = input.Move.X < -CampaignInput.MoveDeadZone;
            // the morph ball reads its own roll controls (MphRead binds them to the same keys as walking)
            c.RollUp.HostDown = c.MoveUp.HostDown;
            c.RollDown.HostDown = c.MoveDown.HostDown;
            c.RollRight.HostDown = c.MoveRight.HostDown;
            c.RolltLeft.HostDown = c.MoveLeft.HostDown;
            CampaignButtons b = input.Buttons;
            c.Shoot.HostDown = (b & CampaignButtons.Shoot) != 0;
            c.Jump.HostDown = (b & CampaignButtons.Jump) != 0;
            c.Morph.HostDown = (b & CampaignButtons.Morph) != 0;
            c.Boost.HostDown = (b & CampaignButtons.Boost) != 0;
            c.AltAttack.HostDown = (b & CampaignButtons.AltAttack) != 0;
            c.Zoom.HostDown = (b & CampaignButtons.Zoom) != 0;
            c.ScanVisor.HostDown = (b & CampaignButtons.ScanVisor) != 0;
            c.Scan.HostDown = (b & CampaignButtons.Scan) != 0;
            c.NextWeapon.HostDown = (b & CampaignButtons.NextWeapon) != 0;
            c.PrevWeapon.HostDown = (b & CampaignButtons.PrevWeapon) != 0;
            c.WeaponMenu.HostDown = (b & CampaignButtons.WeaponMenu) != 0;
            c.AffinitySlot.HostDown = (b & CampaignButtons.AffinitySlot) != 0;
            c.PowerBeam.HostDown = input.SelectWeapon == BeamType.PowerBeam;
            c.VoltDriver.HostDown = input.SelectWeapon == BeamType.VoltDriver;
            c.Missile.HostDown = input.SelectWeapon == BeamType.Missile;
            c.Battlehammer.HostDown = input.SelectWeapon == BeamType.Battlehammer;
            c.Imperialist.HostDown = input.SelectWeapon == BeamType.Imperialist;
            c.Judicator.HostDown = input.SelectWeapon == BeamType.Judicator;
            c.Magmaul.HostDown = input.SelectWeapon == BeamType.Magmaul;
            c.ShockCoil.HostDown = input.SelectWeapon == BeamType.ShockCoil;
            c.OmegaCannon.HostDown = input.SelectWeapon == BeamType.OmegaCannon;
            PlayerEntity.HostAimX = input.AimDelta.X;
            PlayerEntity.HostAimY = input.AimDelta.Y;
            PlayerEntity.HostDialogPress = input.DialogButton ?? HostDialogButton.None;
            SetWheelPointer(input.WeaponWheel);
            // the desktop window applies pending dialog/menu pauses right before each update frame
            GameState.ApplyPause();
            Scene.OnUpdateFrame();
            CarryWeapon();
            // Renderer.OnUpdateFrame only calls these `if (!Headless)` (they drive the desktop OpenAL/SoundFlow
            // pump, which doesn't exist here) -- but Music.UpdateMusic is also where a *queued* PlaySeq (PlayMusic,
            // TryPlayRoomMusic, PlayPausedMusic -- anything that fades the old track out first) actually loads the
            // next one; skipping it left every queued room-music change stuck forever after its own Stop(). Sfx.Update
            // drives AndroidSfxPlayer's free-sfx-script delays the same way. Both are no-ops with no host registered.
            MphRead.Sound.Sfx.Update(1 / 60f);
            MphRead.Music.UpdateMusic();
            Frame++;
        }

        // Owner queue #8b (2026-10-03): the beam wasn't kept through a full room load -- a portal, a boss door whose
        // cutscene loads the room, a movie's room load. Each one makes a NEW main player (RoomEntity.LoadRoom), and
        // MphRead's spawn always equips the Power Beam; in vanilla you come out holding what you went in with. The new
        // player re-equips it once spawned (silently, as the spawn's own equip; only if it's still available with ammo).
        // Not after a death (the old player at 0 energy: game over / checkpoint) and not in a match.
        private PlayerEntity? _weaponPlayer;
        private BeamType _weaponHeld = BeamType.PowerBeam;
        private BeamType _weaponCarry = BeamType.None;
        // the last carry: the weapon taken through a full room load, what the new player spawned with, whether it got it
        // (Tools -campaignportal ... weapon=<beam>)
        public (BeamType Wanted, BeamType Spawned, bool Equipped)? LastWeaponCarry { get; private set; }

        private void CarryWeapon()
        {
            PlayerEntity p = Player;
            if (!GameState.SinglePlayer)
            {
                return;
            }
            if (!ReferenceEquals(p, _weaponPlayer))
            {
                _weaponCarry = _weaponPlayer != null && _weaponPlayer.Health > 0 ? _weaponHeld : BeamType.None;
                _weaponPlayer = p;
            }
            if (!p.LoadFlags.TestFlag(LoadFlags.Spawned) || p.Health <= 0)
            {
                return;
            }
            if (_weaponCarry != BeamType.None)
            {
                BeamType spawned = p.CurrentWeapon;
                bool ok = spawned == _weaponCarry || p.HostEquipWeapon(_weaponCarry);
                LastWeaponCarry = (_weaponCarry, spawned, ok);
                _weaponCarry = BeamType.None;
            }
            _weaponHeld = p.CurrentWeapon;
        }

        // The DS radial lives in the touch screen's top-right corner: six 15-degree sectors in the quarter below-left
        // of (224, 38), measured from straight down toward the left. A stick's full circle maps onto it -- sector
        // s of six 60-degree stick sectors becomes a pointer in the middle of the radial's sector s.
        private void SetWheelPointer(System.Numerics.Vector2 stick)
        {
            float ratioX = Scene.Size.X / 256f;
            float ratioY = Scene.Size.Y / 192f;
            float px = 224, py = 38; // neutral: on the anchor, which selects nothing
            if (stick.LengthSquared() > 0.25f)
            {
                float clockwiseFromUp = MathF.Atan2(stick.X, stick.Y) * 180 / MathF.PI;
                int sector = (int)(((clockwiseFromUp + 390) % 360) / 60); // 0 = up, centred
                float q = (sector * 15 + 7.5f) * MathF.PI / 180;
                px = 224 - 40 * MathF.Sin(q);
                py = 38 + 40 * MathF.Cos(q);
            }
            PlayerEntity.HostPointerX = px * ratioX;
            PlayerEntity.HostPointerY = py * ratioY;
        }

        public DoorEntity? FindDoor(int entityId)
        {
            foreach (DoorEntity door in Scene.GetDoorEntities())
            {
                if (door.Id == entityId)
                {
                    return door;
                }
            }
            return null;
        }

        // test/debug placement (same call the game's in-room teleporters use)
        public void PlacePlayer(Vector3 position, Vector3 facing)
        {
            Player.Teleport(position, facing, Scene.GetNodeRefByPosition(position));
        }

        // the held main player spawns on its next step (at the room's respawn point, as on any arrival)
        public void ReleaseSpawn()
        {
            PlayerEntity.HostHoldMainSpawn = false;
        }

        public void Dispose()
        {
            PlayerEntity.HostHoldMainSpawn = false;
            Scene.DoCleanup();
        }
    }
}
