using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using MphRecomp.Import;

// IMPORT GAMES' imported extras (owner 2026-10-08: "do the disc banners and renders"): a picture of each imported thing on
// its tile and on its game's card, in place of the kind's pictogram. An HD model (a suit, a morph ball, a character) is
// rendered once by the app (ImportItemRender, on the GL thread, through the campaign suit's own GX programs: the model as
// it was built, straight on) and kept as <root>/import/renders/<name>_<stamp>.png -- the stamp is its .dae's time, so an
// import made again is drawn again; an Echoes arena shows its own picture (the import's thumb.png). A host that can't
// render (the PC tools) shows the pictograms unless a picture is already there. Nothing here is needed for the base look.
// The Scout Drones (bodies/MP1AlphaDrone, MP1GammaDrone: no .dae, a native body.bin) are drawn the same way from their
// own body (CampaignRetroBody, held in its idle clip's first frame), square like a ball, stamped by body.bin's time.
namespace MphRecomp.Frontend
{
    // one model to render: its folder, the PNG to write, and whether it's a morph ball (a square picture, all of it) or a
    // body (a tall picture, head to waist); Native: a bodies/<name> import (body.bin, no .dae), framed as a ball
    public sealed record ImportRenderJob(string Name, string Folder, string Png, bool Ball, bool Native = false);

    public static class ImportRenders
    {
        public const int BodyW = 384, BodyH = 576, BallSize = 384;

        // a body picture's framing, in its file name: "_2" = framed on its head (2026-10-08, ImportItemRender.HeadRow); "_3" =
        // the Early Phazon suit's visor solid like the other suits' (2026-10-18, MenuSamus.json "visor"); "_4" (balls "_e", drones "_h") = see-through
        // around the model (2026-10-30, ImportItemRender.Render)
        private const string BodyLook = "_4";

        // the app renders the jobs (Android sets it); else nothing is queued
        public static bool HostRenders { get; set; }

        // per folder (or arena): the picture's path, "" = none; jobs queued or rendering are in s_pending
        private static readonly ConcurrentDictionary<string, string> s_known = new();
        private static readonly ConcurrentDictionary<string, bool> s_pending = new();
        private static readonly ConcurrentQueue<ImportRenderJob> s_jobs = new();

        public static string RendersDir(string root) => Path.Combine(root, "import", "renders");

        // an imported item's picture, or null: not imported, nothing to show (music), or not rendered yet (pending = it's
        // queued: the first call queues it)
        public static string? For(string? root, ImportItemInfo item, out bool pending)
        {
            pending = false;
            if (root == null || !ImportGames.Imported(item)) return null;
            if (item.Kind == "arenas") return Arena(item.Name);
            bool native = item.Kind == "bodies" && IsDrone(item.Name);
            if (item.Kind != "hd" && !native) return null;
            string folder = Path.Combine(root, native ? "bodies" : "hd", item.Name);
            if (s_known.TryGetValue(folder, out string? known))
            {
                pending = s_pending.ContainsKey(folder);
                return known.Length > 0 ? known : null;
            }
            string? dae = null; // the model file whose time stamps the picture: its .dae, or a native body's body.bin
            try
            {
                if (native) dae = File.Exists(MphRecomp.Campaign.NativeBody.BinPath(folder)) ? MphRecomp.Campaign.NativeBody.BinPath(folder) : null;
                else dae = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.dae").FirstOrDefault() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("import render: " + folder + " unreadable (" + ex.Message + ")");
            }
            if (dae == null)
            {
                s_known[folder] = "";
                return null;
            }
            bool ball = native || ImportGames.IsBall(item);
            string look = native ? "_h" : ball ? "_e" : BodyLook; // a body framed anew is drawn again (the drones: unfolded; the balls: turned, with their lit core, 2026-10-26)
            string png = Path.Combine(RendersDir(root), $"{item.Name}_{File.GetLastWriteTimeUtc(dae).Ticks:x}{look}.png");
            if (File.Exists(png))
            {
                s_known[folder] = png;
                return png;
            }
            s_known[folder] = "";
            if (HostRenders && s_pending.TryAdd(folder, true))
            {
                s_jobs.Enqueue(new ImportRenderJob(item.Name, folder, png, ball, native));
                pending = true;
            }
            return null;
        }

        public static string? For(string? root, ImportItemInfo item) => For(root, item, out _);

        // the bodies/ imports with a picture: the Scout Drones (the other bodies/ items keep their pictogram)
        private static bool IsDrone(string name) => name is "MP1AlphaDrone" or "MP1GammaDrone";

        // an Echoes arena's own picture (its import's thumb.png, beside its HD look)
        private static string? Arena(string name)
        {
            string key = "arena " + name;
            if (s_known.TryGetValue(key, out string? known)) return known.Length > 0 ? known : null;
            MphRecomp.Arenas.EchoesArena.Def? def = MphRecomp.Arenas.EchoesArena.Find(name);
            string? dir = def == null ? null : MphRecomp.Arenas.EchoesArena.HdDir(def.Room);
            string? file = dir == null ? null : Path.Combine(dir, MphRecomp.Arenas.ArenaThumbnail.FileName);
            string found = file != null && File.Exists(file) ? file : "";
            s_known[key] = found;
            return found.Length > 0 ? found : null;
        }

        // the app's side (GL thread): the next model to render
        public static bool TryTake(out ImportRenderJob job)
        {
            if (s_jobs.TryDequeue(out ImportRenderJob? j))
            {
                job = j;
                return true;
            }
            job = null!;
            return false;
        }

        // a job ended: its picture written (ok) or not (then the pictogram stays until the next import)
        public static void Finished(ImportRenderJob job, bool ok)
        {
            s_known[job.Folder] = ok && File.Exists(job.Png) ? job.Png : "";
            s_pending.TryRemove(job.Folder, out _);
        }

        // an import ended or something was deleted: look again (a queued job stays queued)
        public static void Forget()
        {
            foreach (string key in s_known.Keys)
            {
                if (!s_pending.ContainsKey(key)) s_known.TryRemove(key, out _);
            }
        }
    }
}
