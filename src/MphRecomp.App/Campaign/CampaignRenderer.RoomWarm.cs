using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Render;

namespace MphRecomp.App;

// Room pre-warm (2026-11-16, campaign first-draw frame drops): a mesh used to be decoded (DsDisplayList) and uploaded the
// first time it was drawn, so a door crossing uploaded the next room's parts in view in one frame, and every part, enemy
// or pickup seen later uploaded on its first sight (13-21 ms frames on the Odin). Now:
// - while crossing (TransitionState.Process) a worker decodes the incoming room model's display lists, so the swap-in
//   frame only uploads them (GetMesh takes the decoded vertices);
// - whatever registers (the corridor's entities one a step, the room model at the swap) and the room's own parts on
//   entering it (a revisit after Evict) go up RoomWarmMs a frame, before their first draw.
// First Hunt and the movie keep their own warm-up (WarmSceneGl). Dev: roomwarm 0 turns it all off (before/after numbers).
internal sealed partial class CampaignRenderer
{
    const double RoomWarmMs = 2;
    bool _roomWarmOff;
    Scene? _rwScene;
    int _rwMeshSeen, _rwTexSeen, _rwRoom = -1, _rwNext, _rwMeshN, _rwTexN, _rwFrames, _rwTaken;
    readonly List<int> _rwMeshQueue = new();
    readonly List<int> _rwTexQueue = new();
    double _rwMs;

    // the incoming room model's vertices, decoded on a worker while crossing; taken (and dropped) by GetMesh
    readonly ConcurrentDictionary<(Model, int), float[]> _rwDecoded = new();
    Model? _rwDecodeModel;
    int _rwDecodeGen;

    void ConfigureRoomWarm(LaunchArgs args)
    {
        _roomWarmOff = !MphRecomp.Config.RecompSettings.PublicBuild && args.Get("roomwarm") == "0";
        ConfigureDevWalk(args);
    }

    // GetMesh: the room's vertices if the worker has them
    float[]? TakePredecoded(Model model, Mesh mesh, bool isRoom)
    {
        // the menus' backdrop's holiday snow cover, decoded on its load's worker (HolidayGround.DecodeCoverAhead)
        if (isRoom && _menuBackdrop && MphRecomp.Holiday.HolidayGround.TakeCoverVerts(model, mesh) is float[] cover) return cover;
        if (!isRoom || _rwDecoded.IsEmpty || !_rwDecoded.TryRemove((model, mesh.DlistId), out float[]? verts))
        {
            return null;
        }
        _rwTaken++;
        return verts;
    }

    // Render, each frame there's no movie (First Hunt has its own)
    void RoomWarmGl(Scene scene)
    {
        if (_roomWarmOff || PlayerEntity.HostFirstHunt)
        {
            return;
        }
        if (scene != _rwScene)
        {
            _rwScene = scene;
            _rwMeshSeen = _rwTexSeen = 0;
            _rwRoom = -1;
            _rwMeshQueue.Clear();
            _rwTexQueue.Clear();
            _rwNext = 0;
        }
        PredecodeIncomingRoom(scene);
        long start = Stopwatch.GetTimestamp();
        try
        {
            QueueRoomWarm(scene);
            if (_rwNext >= _rwMeshQueue.Count && _rwTexQueue.Count == 0)
            {
                EndRoomWarm();
                return;
            }
            // meshes first (the room's parts, then the entities'), then textures. The budget starts after the first item:
            // the first GL upload of a frame can absorb the driver's frames-in-flight wait (15 ms on the PC, measured), a wait
            // the frame pays anyway, so timing it would leave one item a frame
            long budgetStart = 0;
            int frameStartN = _rwMeshN + _rwTexN;
            while (budgetStart == 0 || Stopwatch.GetElapsedTime(budgetStart).TotalMilliseconds < RoomWarmMs)
            {
                if (budgetStart == 0 && _rwMeshN + _rwTexN > frameStartN)
                {
                    budgetStart = Stopwatch.GetTimestamp();
                }
                if (_rwNext < _rwMeshQueue.Count)
                {
                    int id = _rwMeshQueue[_rwNext++];
                    if (!_meshes.ContainsKey(id))
                    {
                        GetMesh(id, scene);
                        _rwMeshN++;
                    }
                    continue;
                }
                if (_rwTexQueue.Count == 0)
                {
                    break;
                }
                int tex = _rwTexQueue[^1];
                _rwTexQueue.RemoveAt(_rwTexQueue.Count - 1);
                if (!_textures.ContainsKey(tex) && BindTexture(tex, scene))
                {
                    _rwTexN++;
                }
            }
            if (_rwNext >= _rwMeshQueue.Count)
            {
                _rwMeshQueue.Clear();
                _rwNext = 0;
            }
        }
        catch (InvalidOperationException)
        {
            // a list changed under the scan: carry on next frame
        }
        GLES30.GlBindVertexArray(0);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, 0);
        GlNative.BindTexture(GLES30.GlTexture2d, 0);
        _rwMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _rwFrames++;
    }

    void QueueRoomWarm(Scene scene)
    {
        IReadOnlyList<(Model Model, Mesh Mesh, bool IsRoom)> meshes = scene.HostMeshes;
        // registered since the last look (list ids are 1-based); the old rooms' meshes Evict dropped aren't seen again
        for (int id = _rwMeshSeen + 1; id <= meshes.Count; id++)
        {
            _rwMeshQueue.Add(id);
        }
        _rwMeshSeen = meshes.Count;
        // a room entered (a revisit keeps its list ids): its parts not up yet go first
        int room = _host?.RoomId ?? -1;
        if (room != _rwRoom && GameState.TransitionState == TransitionState.None)
        {
            _rwRoom = room;
            IReadOnlyList<ModelInstance>? models = scene.Room?.GetModels();
            if (models is { Count: > 0 })
            {
                var roomIds = new List<int>();
                foreach (Mesh mesh in models[0].Model.Meshes)
                {
                    if (mesh.ListId > 0 && !_meshes.ContainsKey(mesh.ListId))
                    {
                        roomIds.Add(mesh.ListId);
                    }
                }
                _rwMeshQueue.InsertRange(_rwNext, roomIds);
            }
        }
        // textures filed since (binding ids only grow; none are dropped, so the ones not up were never drawn)
        if (scene.HostTextures.Count != _rwTexSeen)
        {
            _rwTexSeen = scene.HostTextures.Count;
            _rwTexQueue.Clear();
            foreach (KeyValuePair<int, Scene.HostTexture> kv in scene.HostTextures)
            {
                if (kv.Value.Width != 0 && !_textures.ContainsKey(kv.Key))
                {
                    _rwTexQueue.Add(kv.Key);
                }
            }
        }
    }

    // the queue ran dry: what went up (MPHPerf)
    void EndRoomWarm()
    {
        if (_rwFrames == 0)
        {
            return;
        }
        if (_rwMeshN + _rwTexN > 0)
        {
            Log.Info("MPHPerf", $"room warm-up [{_rwRoom}]: {_rwMeshN} meshes + {_rwTexN} textures uploaded ahead of their first draw in "
                + $"{_rwMs:0} ms over {_rwFrames} frames; {_rwTaken} meshes' vertices came from the crossing's worker");
        }
        _rwMeshN = _rwTexN = _rwFrames = _rwTaken = 0;
        _rwMs = 0;
    }

    // while crossing: the room model SetUpRoom put in (its meshes not registered yet), decoded on a worker
    void PredecodeIncomingRoom(Scene scene)
    {
        if (GameState.TransitionState != TransitionState.Process)
        {
            return;
        }
        IReadOnlyList<ModelInstance>? models = scene.Room?.GetModels();
        if (models is not { Count: > 0 } || models[0].Model is not Model model || model == _rwDecodeModel
            || model.Meshes.Count == 0 || model.Meshes[0].ListId != 0)
        {
            return; // still the old room's (registered), or this one's already done
        }
        _rwDecodeModel = model;
        _rwDecoded.Clear();
        int gen = ++_rwDecodeGen;
        Task.Run(() =>
        {
            long start = Stopwatch.GetTimestamp();
            int n = 0;
            try
            {
                foreach (Mesh mesh in model.Meshes)
                {
                    if (gen != _rwDecodeGen)
                    {
                        return; // another crossing started
                    }
                    if (_rwDecoded.ContainsKey((model, mesh.DlistId)))
                    {
                        continue; // meshes drawing the same list share one id
                    }
                    _rwDecoded[(model, mesh.DlistId)] = DsDisplayList.Decode(model, mesh, isRoom: true);
                    n++;
                }
                Log.Info("MPHPerf", $"room predecode: {model.Name} {n} display lists in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms on a worker");
            }
            catch (Exception ex)
            {
                _rwDecoded.Clear(); // GetMesh decodes them itself
                Log.Info("MPHPerf", $"room predecode stopped: {ex.GetType().Name}");
            }
        });
    }

    // dev doorwalk <fromStep>[,<steps>]: walk forward from that sim step, firing every 20 steps (shoots a door open and goes
    // through, for the door-crossing frame numbers); a public build never honours it
    int _devWalkFrom = -1, _devWalkSteps, _devWalkStep;

    void ConfigureDevWalk(LaunchArgs args)
    {
        if (MphRecomp.Config.RecompSettings.PublicBuild || args.Get("doorwalk")?.Split(',') is not { Length: >= 1 } s
            || !int.TryParse(s[0], out _devWalkFrom))
        {
            return;
        }
        _devWalkSteps = s.Length > 1 && int.TryParse(s[1], out int n) ? n : 600;
    }

    void DevWalkInput(ref CampaignInput input)
    {
        if (_devWalkFrom < 0)
        {
            return;
        }
        int step = _devWalkStep++;
        if (step < _devWalkFrom || step >= _devWalkFrom + _devWalkSteps)
        {
            return;
        }
        input.Move = new System.Numerics.Vector2(0, 1);
        if ((step - _devWalkFrom) % 20 < 2)
        {
            input.Buttons |= CampaignButtons.Shoot;
        }
    }
}
