using System;
using System.Collections.Generic;
using System.IO;

// mph-recomp moddability tier 1: a layered virtual file system for asset overrides. Mount the base
// game asset root (extracted from the user's ROM) plus any number of mod asset roots; a logical
// path resolves to the highest-priority real file that has it, so a mod replaces or adds any asset
// by dropping a file at the same relative path. Read-only resolution (never writes base assets),
// with path-traversal protection. Pure IO/logic -> headlessly testable.
namespace MphRecomp.Assets
{
    public sealed class AssetVfs
    {
        private sealed class Layer { public string Name = ""; public string Root = ""; }

        // Add base assets first, then mod layers; LATER-added layers override earlier ones.
        private readonly List<Layer> _layers = new();

        public void AddLayer(string name, string rootDir)
            => _layers.Add(new Layer { Name = name, Root = Path.GetFullPath(rootDir) });

        public int LayerCount => _layers.Count;

        // Real filesystem path of the winning file for `logicalPath`, or null if no layer has it.
        public string? Resolve(string logicalPath)
        {
            string? norm = Normalize(logicalPath);
            if (norm == null) return null;
            for (int i = _layers.Count - 1; i >= 0; i--) // last-added layer = highest priority
            {
                string full = Path.GetFullPath(Path.Combine(_layers[i].Root, norm));
                if (IsUnder(full, _layers[i].Root) && File.Exists(full)) return full;
            }
            return null;
        }

        public bool Exists(string logicalPath) => Resolve(logicalPath) != null;

        public byte[]? ReadAllBytes(string logicalPath)
        {
            string? p = Resolve(logicalPath);
            return p == null ? null : File.ReadAllBytes(p);
        }

        public Stream? Open(string logicalPath)
        {
            string? p = Resolve(logicalPath);
            return p == null ? null : File.OpenRead(p);
        }

        // Name of the layer that provides `logicalPath` (for diagnostics / "who overrode this"), or null.
        public string? SourceLayer(string logicalPath)
        {
            string? norm = Normalize(logicalPath);
            if (norm == null) return null;
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                string full = Path.GetFullPath(Path.Combine(_layers[i].Root, norm));
                if (IsUnder(full, _layers[i].Root) && File.Exists(full)) return _layers[i].Name;
            }
            return null;
        }

        // Union of files directly under a logical directory across all layers (deduped by logical
        // path; the winning layer for any name is what Resolve returns). Returns logical paths.
        public IReadOnlyList<string> List(string logicalDir, string pattern = "*")
        {
            string norm = Normalize(logicalDir) ?? "";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (Layer layer in _layers)
            {
                string dir = norm.Length == 0 ? layer.Root : Path.GetFullPath(Path.Combine(layer.Root, norm));
                if (!IsUnder(dir, layer.Root) || !Directory.Exists(dir)) continue;
                foreach (string f in Directory.GetFiles(dir, pattern))
                {
                    string rel = norm.Length == 0 ? Path.GetFileName(f) : norm + "/" + Path.GetFileName(f);
                    if (seen.Add(rel)) result.Add(rel);
                }
            }
            return result;
        }

        // Normalize a logical path to forward slashes with no leading slash; reject empty and any
        // ".." segment (path traversal). Returns null if invalid.
        private static string? Normalize(string logical)
        {
            if (string.IsNullOrWhiteSpace(logical)) return null;
            string[] parts = logical.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts) if (p == "..") return null;
            return string.Join('/', parts);
        }

        // Is `full` inside `root` (defends against traversal even if Normalize is bypassed)?
        private static bool IsUnder(string full, string root)
        {
            string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Equals(r, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
