using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Campaign.Retro;
using MphRecomp.Render;

namespace MphRecomp.App;

// Imported Retro enemies Hard Mode placed (Core RetroEnemies / EchoesSplinter; docs/ENEMY_IMPORT.md E1): their bodies load
// with the native bodies (MatchSkins' _bodyLoads / _bodies, keyed "enemy:<Name>"), tick after each sim step and draw in
// DrawSkins. Drawing only: the logic is Core's.
internal sealed partial class CampaignRenderer
{
    string? EnemiesRoot => _gunsRoot != null ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_gunsRoot) ?? "", "enemies") : null;

    readonly HashSet<EchoesSplinter> _retroSeen = new();
    readonly List<EchoesSplinter> _retroLive = new();

    // StartSkinLoads: each imported Retro enemy's body, on a worker
    void StartRetroEnemyLoads()
    {
        if (EnemiesRoot is not string root) return;
        foreach (string name in RetroEnemies.Known)
        {
            string id = "enemy:" + name;
            if (_bodyLoads.ContainsKey(id) || _bodies.ContainsKey(id)) continue;
            if (!NativeBody.Exists(System.IO.Path.Combine(root, name, MphRecomp.Import.Enemies.EnemyImportKind.BodyDir))) continue;
            string n = name;
            _bodyLoads[id] = System.Threading.Tasks.Task.Factory.StartNew(() => CampaignRetroBody.TryLoadEnemy(root, n),
                System.Threading.Tasks.TaskCreationOptions.LongRunning);
        }
    }

    CampaignRetroBody? RetroBodyOf(EchoesSplinter e)
    {
        string id = "enemy:" + System.IO.Path.GetFileName(e.Config.Dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return _bodies.TryGetValue(id, out CampaignRetroBody? b) && !b.Failed ? b : null;
    }

    // TickSkins: their clip clocks
    void TickRetroEnemies()
    {
        if (_host?.Scene is not Scene scene) return;
        _retroLive.Clear();
        foreach (EnemyInstanceEntity e in scene.GetEnemyInstanceEntities())
        {
            if (e is not EchoesSplinter s || RetroBodyOf(s) is not CampaignRetroBody b) continue;
            b.TickEnemy(s);
            _retroLive.Add(s);
        }
        // the gone ones' clocks
        foreach (EchoesSplinter s in _retroSeen)
        {
            if (!_retroLive.Contains(s)) RetroBodyOf(s)?.ForgetEnemy(s);
        }
        _retroSeen.Clear();
        _retroSeen.UnionWith(_retroLive);
    }

    // DrawSkins
    void DrawRetroEnemies(Scene scene, SuitPass pass)
    {
        if (_host == null || scene != _host.Scene || _retroLive.Count == 0) return;
        foreach (EchoesSplinter s in _retroLive)
        {
            if (RetroBodyOf(s) is CampaignRetroBody b) b.DrawEnemy(s, SmoothView(s, scene), scene.PerspectiveMatrix, pass, s.HitFlash);
        }
    }
}
