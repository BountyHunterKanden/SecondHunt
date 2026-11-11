using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Pickups, jump pads and flags behind the arena's walls aren't drawn (Mining Plaza frame dips, Call 3, 2026-11-01).
// MPH's own rooms hide them through their room node's visibility (EntityBase.IsVisible); an imported Echoes
// arena has no MPH nodes, so after the frustum cull (CampaignRenderer.PickupCull.cs) Mining Plaza still drew ~250 pickup
// items a frame (Combat Hall: ~7). Each frame, right after the arena's opaque depth (DrawArena pass 0), one invisible
// padded box per entity is drawn in a GPU occlusion query; a later frame reads the result without waiting. An entity
// with no answer yet counts as seen, and one stays seen for OccHoldFrames after its last hit, so nothing pops while the
// GPU catches up. Render only: the sim never reads it (replays, bots and other seats are untouched); the cull follows
// whichever camera the frame draws.
internal sealed partial class CampaignRenderer
{
    const int GlAnySamplesPassedConservative = 0x8D6A, GlQueryResult = 0x8866, GlQueryResultAvailable = 0x8867;
    const int OccHoldFrames = 8, OccMaxEntities = 256;
    const float OccCameraJump = 3f;

    sealed class OccState
    {
        public int Query;
        public bool Pending;
        public long Frame; // last frame the entity was in the scene
        public long SeenFrame = long.MaxValue; // never answered: seen
    }

    readonly Dictionary<EntityBase, OccState> _occ = new(ReferenceEqualityComparer.Instance);
    readonly List<EntityBase> _occGone = new();
    readonly int[] _occTmp = new int[1];
    int _occProg, _occMvp, _occVao, _occVbo, _occRoom = -1, _occGlGen = -1;
    long _occFrame;
    Vector3 _occLastCam;
    bool _occOn;
    int _occTested, _occCulled; // per frame: boxes tested, entities hidden (for the MPHArena line)
    public int OccTestedSum, OccCulledSum, OccFrames;

    // GL thread, right after DrawArena(scene, 0, GlLess): the arena's opaque depth is in the buffer
    void OccQuery(Scene scene)
    {
        _occOn = false;
        if (_arenaHd == null || _arenaHd.Failed || _host == null || scene != _host.Scene) return;
        if (_occGlGen != s_arenaGlGen || _occRoom != _host.RoomId)
        {
            // new context (its names are gone) or a new room (old entities): start over
            if (_occGlGen == s_arenaGlGen) foreach (OccState s in _occ.Values) { _occTmp[0] = s.Query; GLES30.GlDeleteQueries(1, _occTmp, 0); }
            else { _occProg = 0; _occVao = 0; }
            _occ.Clear();
            _occGlGen = s_arenaGlGen;
            _occRoom = _host.RoomId;
        }
        if (_occProg == 0 && !OccInitGl()) return;
        _occOn = true;
        _occFrame++;
        Vector3 cam = scene.CameraPosition;
        bool jumped = (cam - _occLastCam).LengthSquared > OccCameraJump * OccCameraJump;
        _occLastCam = cam;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        _occTested = _occCulled = 0;

        GLES30.GlUseProgram(_occProg);
        GLES30.GlBindVertexArray(_occVao);
        GLES30.GlColorMask(false, false, false, false);
        GLES30.GlDepthMask(false);
        GLES30.GlDepthFunc(GLES30.GlLequal);
        GLES30.GlDisable(CullFaceCap);
        foreach (EntityBase e in scene.Entities)
        {
            if (!OccBox(e, out Vector3 lo, out Vector3 hi)) continue;
            if (!_occ.TryGetValue(e, out OccState? s))
            {
                if (_occ.Count >= OccMaxEntities) continue;
                GLES30.GlGenQueries(1, _occTmp, 0);
                s = new OccState { Query = _occTmp[0] };
                _occ[e] = s;
            }
            s.Frame = _occFrame;
            if (_occFrame - s.SeenFrame > OccHoldFrames) _occCulled++;
            if (s.Pending)
            {
                GLES30.GlGetQueryObjectuiv(s.Query, GlQueryResultAvailable, _occTmp, 0);
                if (_occTmp[0] == 0) continue; // still in flight: keep the last answer
                GLES30.GlGetQueryObjectuiv(s.Query, GlQueryResult, _occTmp, 0);
                s.Pending = false;
                if (_occTmp[0] != 0) s.SeenFrame = _occFrame;
                else if (s.SeenFrame == long.MaxValue) s.SeenFrame = _occFrame - OccHoldFrames - 1;
            }
            // the camera inside the box (or a seat switch / free cam jump): seen this frame, no query needed
            if (jumped || cam.X >= lo.X && cam.X <= hi.X && cam.Y >= lo.Y && cam.Y <= hi.Y && cam.Z >= lo.Z && cam.Z <= hi.Z)
            {
                s.SeenFrame = _occFrame;
                continue;
            }
            Vector3 c = (lo + hi) * 0.5f, h = (hi - lo) * 0.5f;
            SetMatrix(_occMvp, Matrix4.CreateScale(h) * Matrix4.CreateTranslation(c) * vp);
            GLES30.GlBeginQuery(GlAnySamplesPassedConservative, s.Query);
            GLES30.GlDrawArrays(GLES30.GlTriangles, 0, 36);
            GLES30.GlEndQuery(GlAnySamplesPassedConservative);
            s.Pending = true;
            _occTested++;
        }
        OccTestedSum += _occTested; OccCulledSum += _occCulled; OccFrames++;
        // entities that left the scene (a picked-up item): free their queries
        foreach (KeyValuePair<EntityBase, OccState> kv in _occ) if (kv.Value.Frame != _occFrame) _occGone.Add(kv.Key);
        foreach (EntityBase e in _occGone)
        {
            _occTmp[0] = _occ[e].Query;
            GLES30.GlDeleteQueries(1, _occTmp, 0);
            _occ.Remove(e);
        }
        _occGone.Clear();
        GLES30.GlColorMask(true, true, true, true);
        GLES30.GlDepthMask(true);
        GLES30.GlDepthFunc(GLES30.GlLess);
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
    }

    // DrawItem: an occludable entity's items when the arena's walls hid its box
    bool OccHidden(RenderItem item)
    {
        if (!_occOn || item.HostOwner is not { } e || !_occ.TryGetValue(e, out OccState? s)) return false;
        return _occFrame - s.SeenFrame > OccHoldFrames;
    }

    // the padded world box an entity's looks fit in (bobbing, spinning, the pad's beam, the flag on its base)
    static bool OccBox(EntityBase e, out Vector3 lo, out Vector3 hi)
    {
        Vector3 p = e.Position;
        switch (e)
        {
        case ItemInstanceEntity:
        case OctolithFlagEntity:
            lo = p - new Vector3(1.5f); hi = p + new Vector3(1.5f); return true;
        case JumpPadEntity:
            lo = p + new Vector3(-3, -1, -3); hi = p + new Vector3(3, 7, 3); return true;
        case FlagBaseEntity:
            lo = p + new Vector3(-2.5f, -1, -2.5f); hi = p + new Vector3(2.5f, 4, 2.5f); return true;
        default:
            lo = hi = default; return false;
        }
    }

    bool OccInitGl()
    {
        _occProg = CampaignGlCache.Program(ArenaHdDraw.DepthVert, ArenaHdDraw.DepthFrag, "occlusion box");
        if (_occProg == 0) return false;
        _occMvp = GLES30.GlGetUniformLocation(_occProg, "uMvp");
        // a unit cube (-1..1), 12 triangles
        var v = new List<float>(108);
        int[] f = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
        foreach (int i in f) { v.Add((i & 4) != 0 ? 1 : -1); v.Add((i & 2) != 0 ? 1 : -1); v.Add((i & 1) != 0 ? 1 : -1); }
        GLES30.GlGenVertexArrays(1, _occTmp, 0); _occVao = _occTmp[0];
        GLES30.GlGenBuffers(1, _occTmp, 0); _occVbo = _occTmp[0];
        GLES30.GlBindVertexArray(_occVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _occVbo);
        float[] data = v.ToArray();
        GLES30.GlBufferData(GLES30.GlArrayBuffer, data.Length * sizeof(float), data, GLES30.GlStaticDraw);
        GLES30.GlEnableVertexAttribArray(0);
        GLES30.GlVertexAttribPointer(0, 3, GLES30.GlFloat, false, 12, 0);
        GLES30.GlBindVertexArray(0);
        return true;
    }
}
