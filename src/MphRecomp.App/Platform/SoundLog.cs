using MphRead;
using MphRead.Sound;

namespace MphRecomp.App.Platform;

// The sound lines of the one log (docs/LOGS.md): one MPHAudio line per sound effect that starts (GameSfxPlayer's
// SetUpInstance, so a held loop or a quick repeat is one line, never one per frame), tagged by what it is:
// "MPHAudio[sample,mortar,weapon,magmaul,sound] started MORTAR (sample 152)". The weapon, hunter and enemy tags come from
// the game's own sound tables (Metadata.BeamSfx / HunterSfx / EnemyDamageSfx / EnemyDeathSfx), so every Magmaul sound
// carries "magmaul" whatever its file is called. Detail level; not while a replay plays back (PlayLog.Muted), menu
// sounds always.
public static class SoundLog
{
    static Dictionary<int, string[]>? _table; // sfx id -> its weapon/hunter/enemy tags
    static readonly Dictionary<int, string[]> _tags = new(); // full sfx id -> the line's tags

    public static void Started(int id, SoundSource? source, bool ui)
    {
        if (!ui && PlayLog.Muted?.Invoke() == true) return;
        string[] tags;
        lock (_tags)
        {
            if (!_tags.TryGetValue(id, out tags!))
            {
                tags = [Kind(id), Name(id).ToLowerInvariant(), .. Table().GetValueOrDefault(id, [])];
                _tags[id] = tags;
            }
        }
        string[] lineTags = ui ? [.. tags, "menu"] : source?.Self == true ? [.. tags, "self"] : tags;
        Log.Debug("MPHAudio", lineTags, $"started {Name(id)} ({Kind(id)} {id & 0x3FFF}){(source?.Self == true ? ", yours" : "")}");
    }

    static string Kind(int id) => (id & 0x8000) != 0 ? "dgn" : (id & 0x4000) != 0 ? "script" : "sample";

    static string Name(int id) => Enum.IsDefined(typeof(SfxId), id) ? ((SfxId)id).ToString() : $"SFX_{id}";

    static Dictionary<int, string[]> Table()
    {
        if (_table != null) return _table;
        var t = new Dictionary<int, List<string>>();
        void Add(int sfx, params string[] tags)
        {
            if (sfx < 0) return;
            if (!t.TryGetValue(sfx, out List<string>? list)) t[sfx] = list = [];
            foreach (string tag in tags)
            {
                if (!list.Contains(tag)) list.Add(tag);
            }
        }
        if (Metadata.BeamSfx is { } beams)
        {
            for (int r = 0; r < beams.GetLength(0); r++)
            {
                string beam = ((BeamType)r).ToString().ToLowerInvariant();
                for (int c = 0; c < beams.GetLength(1); c++)
                {
                    if (r == (int)BeamType.OmegaCannon) Add(beams[r, c], "weapon", "omega", beam);
                    else Add(beams[r, c], "weapon", beam);
                }
            }
        }
        if (Metadata.HunterSfx is { } hunters)
        {
            // (a sound several hunters share, like PLAYER_SPAWN, is just "hunter": naming them all would match any grep)
            var owners = new Dictionary<int, HashSet<int>>();
            for (int r = 0; r < hunters.GetLength(0); r++)
            {
                for (int c = 0; c < hunters.GetLength(1); c++)
                {
                    if (hunters[r, c] < 0) continue;
                    if (!owners.TryGetValue(hunters[r, c], out HashSet<int>? rows)) owners[hunters[r, c]] = rows = [];
                    rows.Add(r);
                }
            }
            foreach ((int sfx, HashSet<int> rows) in owners)
            {
                if (rows.Count == 1) Add(sfx, "hunter", ((Hunter)rows.First()).ToString().ToLowerInvariant());
                else Add(sfx, "hunter");
            }
        }
        // a hunter named in the sound's own name (DGN_TRACE_ALT_WALK_ROCK, SPIRE_JUMP) tags it too
        foreach (SfxId e in Enum.GetValues<SfxId>())
        {
            foreach (string part in e.ToString().ToLowerInvariant().Split('_'))
            {
                if (Enum.TryParse(part, true, out Hunter h) && h != Hunter.Random && h.ToString().Equals(part, StringComparison.OrdinalIgnoreCase))
                {
                    Add((int)e, "hunter", part);
                }
            }
        }
        foreach (int[]? enemy in new[] { Metadata.EnemyDamageSfx, Metadata.EnemyDeathSfx })
        {
            if (enemy == null) continue;
            foreach (int sfx in enemy) Add(sfx, "enemy");
        }
        return _table = t.ToDictionary(p => p.Key, p => p.Value.ToArray());
    }
}
