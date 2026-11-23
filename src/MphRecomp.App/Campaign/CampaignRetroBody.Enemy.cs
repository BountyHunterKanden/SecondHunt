using System;
using System.Diagnostics;
using System.IO;
using MphRecomp.Campaign;
using MphRecomp.Campaign.Retro;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// An imported Retro enemy's body (docs/ENEMY_IMPORT.md E1; enemies/<Name>/body, the "enemies" import kind): one clip clock
// per enemy, stepped after each sim step on the clip its Core logic picked (EchoesSplinter.Clip), drawn with the native
// bodies (CampaignRenderer's MatchRetroEnemies.cs)
internal sealed partial class CampaignRetroBody
{
    public static CampaignRetroBody? TryLoadEnemy(string enemiesRoot, string name)
    {
        string dir = Path.Combine(enemiesRoot, name, MphRecomp.Import.Enemies.EnemyImportKind.BodyDir);
        if (!NativeBody.Exists(dir) || !MphRecomp.Import.ImportedContent.Usable(enemiesRoot, name)) return null;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            NativeBody body = NativeBody.Load(dir);
            if (BodyClips.For(body) is not BodyClips clips)
            {
                Log.Warn(Tag, $"enemy {name}: no clip map for its clips");
                return null;
            }
            var b = new CampaignRetroBody("enemy:" + name, dir, body, clips);
            Log.Info(Tag, $"enemy {name}: {body.Data.BoneCount} bones, {body.Data.Anims.Count} clips; loaded in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms");
            return b;
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"enemy {name}: load failed: {ex.Message}");
            return null;
        }
    }

    public void TickEnemy(EchoesSplinter e)
    {
        AnimatorOf(e).Step(new BodyClips.Choice(e.Clip + "#" + e.ClipSerial, e.Clip, e.ClipLoop), Dt);
    }

    public void DrawEnemy(EchoesSplinter e, Matrix4 view, Matrix4 proj, SuitPass pass, bool flash)
    {
        if (!_anims.ContainsKey(e)) return;
        Draw(AnimatorOf(e), null, EchoesSplinter.ModelMatrix(Body, e.Position, e.Yaw), view, proj, pass, 1f, flash);
    }

    // a gone enemy's clock
    public void ForgetEnemy(object e) => _anims.Remove(e);
}
