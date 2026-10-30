using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace MphRecomp.Arenas.Editor
{
    // MAP MAKER E1: the map document and its .shmap file (docs/MAP_EDITOR.md section 2). A .shmap is a zip package:
    //   map.json    the settings below (versioned: Version, upgraded in Upgrade() on load)
    //   blocks.bin  the block tree with its face textures (BlockWorld.Write)
    // Later milestones add entries (meshes/, markers, volumes, thumb.png); readers ignore entries they don't know, and a
    // file from a newer build than this one is refused rather than half-read. E0's map.smm (just the block tree) still loads.
    public sealed class MapDocument
    {
        public const int CurrentVersion = 1;
        public const string Extension = ".shmap";

        public int Version { get; set; } = CurrentVersion;
        public string Name { get; set; } = "Untitled map";
        public float[] CameraPosition { get; set; } = { 0, 8, 20 };
        public float CameraYaw { get; set; }
        public float CameraPitch { get; set; } = -0.35f;
        public int GridIndex { get; set; } = 2;   // 1 unit: the Hunters survey (docs/MAP_EDITOR.md 5b)

        [System.Text.Json.Serialization.JsonIgnore]
        public BlockWorld World { get; set; } = BlockWorld.Starter();

        static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        // null when the file isn't there; throws InvalidDataException for a file that can't be read
        public static MapDocument? Load(string path)
        {
            if (!File.Exists(path)) return null;
            if (path.EndsWith(".smm", StringComparison.OrdinalIgnoreCase))
            {
                BlockWorld? old = BlockWorld.LoadLegacy(path);
                if (old == null) throw new InvalidDataException("not a map file");
                return new MapDocument { Name = Path.GetFileNameWithoutExtension(path), World = old };
            }
            using FileStream file = File.OpenRead(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            ZipArchiveEntry? meta = zip.GetEntry("map.json"), blocks = zip.GetEntry("blocks.bin");
            if (meta == null || blocks == null) throw new InvalidDataException("not a SECOND HUNT map");
            MapDocument doc;
            using (Stream s = meta.Open())
            {
                doc = JsonSerializer.Deserialize<MapDocument>(s, Json) ?? throw new InvalidDataException("empty map.json");
            }
            if (doc.Version > CurrentVersion) throw new InvalidDataException($"made by a newer SECOND HUNT (map version {doc.Version})");
            using (Stream s = blocks.Open())
            {
                doc.World = BlockWorld.Read(s) ?? throw new InvalidDataException("unreadable blocks.bin");
            }
            doc.Upgrade();
            return doc;
        }

        // older documents are brought up to CurrentVersion here, one step per version
        void Upgrade()
        {
            // no older versions yet
            Version = CurrentVersion;
        }

        // written beside the target and moved over it, so a crash never leaves half a file
        public void Save(string path)
        {
            Version = CurrentVersion;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = path + ".tmp";
            using (FileStream file = File.Create(temp))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (Stream s = zip.CreateEntry("map.json").Open()) JsonSerializer.Serialize(s, this, Json);
                using (Stream s = zip.CreateEntry("blocks.bin").Open()) World.Write(s);
            }
            File.Move(temp, path, overwrite: true);
        }
    }
}
