using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// SETTINGS > LOOK > DISPLAY "smooth frames" (RecompSettings.SmoothFrames, owner 2026-10-18). The game steps 60 times a
// second (OnDrawFrame's accumulator) and MphRead builds its draw list inside the step, so a 120 Hz display (the Odin) or
// a PC running faster than 60 showed every step twice or more: the same picture again. With this on, a picture drawn
// between two steps is carried on past the last one by how far into the next step it falls (_accum, 0 to 1 of a step):
// - the camera: moved and turned on by that share of the last step's own move and turn;
// - every entity that drew in both of the last two steps (hunters, enemies, shots, platforms): moved on by that share of
//   its last step's move. Carried forward, never blended between two steps, so no picture waits for a step to come and
//   an entity reused from a pool can't be smeared from its old self (a move longer than a jump is not carried);
// - what rides the camera (the gun, the arm cannon, the main hunter the camera follows) keeps its place in the picture:
//   it draws with the step's own view.
// Draw only: the step's view goes back on the scene after the draw (MphRead SceneHostSmoothFrames.cs), and nothing the
// game reads changes. Off, every picture is the step's, as before.
// Hooks: SmoothRecordStep (after each step, OnDrawFrame), SmoothBegin / SmoothEnd (around Render), SmoothItemView
// (DrawItem: an item's view by its entity, MphRead RenderItem.HostOwner), SmoothView (the HD suits, skins and bodies),
// GunView (DrawPrimeGun, the arm cannon, the Zero Suit gun tip), SmoothCarry (the custom hunters' fx batches).
internal sealed partial class CampaignRenderer
{
    // longer than this in a step is a cut or a jump (a door, a respawn, a teleport, a cutscene cut), not motion
    const float SmoothCamJump = 3f, SmoothEntityJump = 6f, SmoothTurnJump = 1.2f; // world units, radians

    volatile bool _smoothFrames = !MphRecomp.Config.RecompSettings.PublicBuild; // off in a public build (owner 2026-10-18)
    Scene? _sfScene;
    bool _sfCamPrev, _sfCamCur; // a camera recorded for the step before last / the last step
    Vector3 _sfPos0, _sfPos1, _sfFwd0, _sfFwd1, _sfUp1;
    Dictionary<EntityBase, Vector3> _sfPrevAt = new(), _sfCurAt = new();
    readonly Dictionary<EntityBase, Vector3> _sfMove = new(); // the last step's move, for entities in both steps
    // this picture's
    bool _sfOn;
    float _sfPhase;
    Scene.HostViewState _sfStep;
    static readonly object SmoothNone = new();
    object? _sfBound = SmoothNone; // whose view u_view holds (SmoothItemView)

    // after each step: the camera and where each entity that drew is
    void SmoothRecordStep()
    {
        Scene? scene = _host?.Scene;
        if (scene == null) return;
        if (scene != _sfScene)
        {
            _sfScene = scene;
            _sfCamPrev = _sfCamCur = false;
            _sfCurAt.Clear();
        }
        Matrix4 world = scene.ViewMatrix.Inverted();
        _sfPos0 = _sfPos1;
        _sfFwd0 = _sfFwd1;
        _sfCamPrev = _sfCamCur;
        _sfPos1 = world.Row3.Xyz;
        _sfFwd1 = -world.Row2.Xyz.Normalized();
        _sfUp1 = world.Row1.Xyz.Normalized();
        _sfCamCur = true;
        (_sfPrevAt, _sfCurAt) = (_sfCurAt, _sfPrevAt);
        _sfCurAt.Clear();
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p != null && p.LoadFlags.TestFlag(LoadFlags.Active)) _sfCurAt[p] = p.Position; // HD looks draw without items
        }
        RecordOwners(scene.OpaqueItems); // the translucent list repeats these
        RecordOwners(scene.DecalItems);
        // shots the app draws its own way (the Pirate Blaster's bolts, the Paralyzer's, the Scout Drone's streaks) have no MphRead items
        foreach (BeamProjectileEntity b in _blasterBolts.Keys) _sfCurAt.TryAdd(b, b.Position);
        foreach (BeamProjectileEntity b in _paraShots.Keys) _sfCurAt.TryAdd(b, b.Position);
        foreach (PlayerEntity p in _fxSeats.Keys)
        {
            if (p.DroneBeams is not BeamProjectileEntity[] beams) continue;
            foreach (BeamProjectileEntity b in beams)
            {
                if (b.HostOwnLook && b.Lifespan > 0) _sfCurAt.TryAdd(b, b.Position);
            }
        }
        _sfMove.Clear();
        foreach (KeyValuePair<EntityBase, Vector3> at in _sfCurAt)
        {
            if (!_sfPrevAt.TryGetValue(at.Key, out Vector3 was)) continue;
            Vector3 move = at.Value - was;
            float d2 = move.LengthSquared;
            if (d2 > 0 && d2 < SmoothEntityJump * SmoothEntityJump) _sfMove[at.Key] = move;
        }
    }

    void RecordOwners(IReadOnlyList<RenderItem> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].HostOwner is EntityBase e && !_sfCurAt.ContainsKey(e)) _sfCurAt[e] = e.Position;
        }
    }

    // before Render: the carried view on the scene
    void SmoothBegin(Scene? scene)
    {
        _sfOn = false;
        _sfBound = SmoothNone;
        SmoothCount();
        if (!_smoothFrames || scene == null || scene != _sfScene || _host == null || MovieShowing) return;
        float phase = (float)(_accum * 60);
        if (phase > 1.25f) return; // the steps have stopped (a cutscene holds them, the end): the step's picture
        phase = Math.Min(phase, 1);
        if (phase <= 0) return;
        Matrix4 view = scene.ViewMatrix;
        if (_sfCamPrev && _sfCamCur && CameraSequence.Current == null)
        {
            Vector3 move = _sfPos1 - _sfPos0;
            bool moves = move.LengthSquared < SmoothCamJump * SmoothCamJump;
            Vector3 axis = Vector3.Cross(_sfFwd0, _sfFwd1);
            float sin = axis.Length, angle = MathF.Atan2(sin, Vector3.Dot(_sfFwd0, _sfFwd1));
            bool turns = sin > 1e-5f && angle < SmoothTurnJump;
            if (moves || turns)
            {
                Vector3 pos = moves ? _sfPos1 + move * phase : _sfPos1, fwd = _sfFwd1, up = _sfUp1;
                if (turns)
                {
                    axis /= sin;
                    fwd = Rotate(fwd, axis, angle * phase);
                    up = Rotate(up, axis, angle * phase);
                }
                view = Matrix4.LookAt(pos, pos + fwd, up);
            }
        }
        _sfPhase = phase;
        _sfStep = scene.HostPresentView(view);
        _sfOn = true;
        _sfCarried++;
    }

    // logcat / log.txt MPHSmooth, every 600 pictures: how many were carried pictures between steps
    int _sfPictures, _sfCarried;

    void SmoothCount()
    {
        if (++_sfPictures < 600) return;
        Log.Info("MPHSmooth", $"{(_smoothFrames ? "on" : "off")}: {_sfCarried} of {_sfPictures} pictures carried, {_sfMove.Count} entities moving");
        _sfPictures = _sfCarried = 0;
    }

    void SmoothEnd(Scene? scene)
    {
        if (!_sfOn) return;
        _sfOn = false;
        scene?.HostRestoreView(_sfStep);
    }

    static Vector3 Rotate(Vector3 v, Vector3 axis, float angle) // Rodrigues, axis unit length
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return v * c + Vector3.Cross(axis, v) * s + axis * (Vector3.Dot(axis, v) * (1 - c));
    }

    // the entity the camera follows: drawn from the step's view, so it holds its place in the picture
    bool RidesCamera(EntityBase e, Scene scene) =>
        e == PlayerEntity.Main && CameraSequence.Current == null && scene.HostView == null;

    // the view to draw an entity's HD look with (the scene's own when smooth frames is off or this isn't a carried picture)
    Matrix4 SmoothView(EntityBase e, Scene scene)
    {
        if (!_sfOn || scene != _sfScene) return scene.ViewMatrix;
        if (RidesCamera(e, scene)) return _sfStep.View;
        return _sfMove.TryGetValue(e, out Vector3 move) ? Matrix4.CreateTranslation(move * _sfPhase) * scene.ViewMatrix : scene.ViewMatrix;
    }

    // the same carry as a world-to-world move, for effects drawn in one batch with the scene's view (the custom hunters'
    // particles, streaks and swooshes): a point of e's moved by it lands where SmoothView(e) draws it. Identity when off.
    Matrix4 SmoothCarry(EntityBase? e, Scene scene)
    {
        if (e == null || !_sfOn || scene != _sfScene) return Matrix4.Identity;
        if (RidesCamera(e, scene)) return _sfStep.View * scene.ViewMatrix.Inverted();
        return _sfMove.TryGetValue(e, out Vector3 move) ? Matrix4.CreateTranslation(move * _sfPhase) : Matrix4.Identity;
    }

    // the Prime gun, the arm cannon, the Zero Suit gun tip: placed from the step's camera, so drawn with its view
    Matrix4 GunView(Scene scene) => _sfOn && scene == _sfScene ? _sfStep.View : scene.ViewMatrix;

    // DrawItem: u_view by the item's entity (set only when it changes from the last item's)
    void SmoothItemView(RenderItem item, Scene scene)
    {
        if (!_sfOn || scene != _sfScene) return;
        object key = item.HostOwner is EntityBase e && (_sfMove.ContainsKey(e) || RidesCamera(e, scene)) ? e : SmoothNone;
        if (key == _sfBound) return;
        _sfBound = key;
        SetMatrix(U("u_view"), key is EntityBase owner ? SmoothView(owner, scene) : scene.ViewMatrix);
    }
}
