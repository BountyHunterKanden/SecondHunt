using System;
using System.IO;
using System.Linq;
using System.Text;
using MphRecomp.Assets;

namespace MphRead
{
    // Headless regression tests for the mph-recomp asset VFS (MphRecomp.Assets, moddability tier 1).
    // Run: MphRead.Tools.dll -vfstest  (no ROM/device). Covers layer precedence (mod overrides base),
    // adding new assets, base-only fallthrough, missing files, source-layer reporting, directory
    // listing union, path-traversal protection, and byte reads.
    internal static class VfsTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }

        public static void Run(string[] args)
        {
            string root = Path.Combine(Path.GetTempPath(), "mphrecomp_vfstest_" + Guid.NewGuid().ToString("N"));
            string baseDir = Path.Combine(root, "base");
            string modDir = Path.Combine(root, "mod");
            try
            {
                // base assets
                Write(baseDir, "data/x.txt", "base-x");
                Write(baseDir, "data/z.txt", "base-z");
                Write(baseDir, "models/crate.bin", "BASECRATE");
                // a mod that overrides x and adds y (but not z)
                Write(modDir, "data/x.txt", "mod-x");
                Write(modDir, "data/y.txt", "mod-y");

                var vfs = new AssetVfs();
                vfs.AddLayer("base", baseDir);
                vfs.AddLayer("mymod", modDir); // added later -> higher priority

                Check("override: mod file wins over base", ReadText(vfs, "data/x.txt") == "mod-x");
                Check("added: mod-only file resolves", ReadText(vfs, "data/y.txt") == "mod-y");
                Check("fallthrough: base-only file still resolves", ReadText(vfs, "data/z.txt") == "base-z");
                Check("fallthrough: unrelated base asset resolves", ReadText(vfs, "models/crate.bin") == "BASECRATE");
                Check("missing file -> null + Exists false", vfs.Resolve("data/none.txt") == null && !vfs.Exists("data/none.txt"));

                Check("source layer: overridden file attributed to mod", vfs.SourceLayer("data/x.txt") == "mymod");
                Check("source layer: base-only file attributed to base", vfs.SourceLayer("data/z.txt") == "base");

                var list = vfs.List("data").OrderBy(s => s).ToList();
                Check("list: union of layers, deduped (x,y,z)",
                    list.Count == 3 && list.Contains("data/x.txt") && list.Contains("data/y.txt") && list.Contains("data/z.txt"));

                Check("traversal: '..' rejected", vfs.Resolve("../base/data/z.txt") == null);
                Check("traversal: nested '..' rejected", vfs.Resolve("data/../../mod/data/x.txt") == null);

                byte[]? bytes = vfs.ReadAllBytes("models/crate.bin");
                Check("ReadAllBytes returns content", bytes != null && Encoding.UTF8.GetString(bytes) == "BASECRATE");
                using (Stream? s = vfs.Open("data/y.txt"))
                    Check("Open returns a readable stream", s != null && s.CanRead);
            }
            finally { try { Directory.Delete(root, recursive: true); } catch { } }

            Console.WriteLine($"\nVFS TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }

        static void Write(string root, string rel, string content)
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        static string? ReadText(AssetVfs vfs, string logical)
        {
            byte[]? b = vfs.ReadAllBytes(logical);
            return b == null ? null : Encoding.UTF8.GetString(b);
        }
    }
}
