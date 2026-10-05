using System;
using System.IO;

namespace MphRecomp.Import.Disc
{
    public enum DiscImageFormat { Unknown, GameCubeIso, WiiIso, Wbfs, Wia, Rvz }

    // Opens whatever disc image the user picked, as the game's file tree (IFileSource): GameCube .iso/.gcm (GcDisc),
    // Wii .iso, .wbfs, and .wia/.rvz of either platform. A Wii disc gives its DATA partition's file system. Plain Wii
    // images need the user's own common key file; WIA/RVZ images never do (Dolphin stores partition data decrypted).
    // The stream is read in place (SAF content:// streams included) and must be seekable.
    public static class DiscImage
    {
        public static readonly string[] Extensions = { ".iso", ".gcm", ".wbfs", ".wia", ".rvz" };

        public static DiscImageFormat Detect(Stream s)
        {
            if (!s.CanSeek || s.Length < 0x80) return DiscImageFormat.Unknown;
            long pos = s.Position;
            try
            {
                var h = new byte[0x20];
                s.Seek(0, SeekOrigin.Begin);
                s.ReadExactly(h);
                uint magic = Be.U32(h, 0);
                if (magic == WiaReader.RvzMagic) return DiscImageFormat.Rvz;
                if (magic == WiaReader.WiaMagic) return DiscImageFormat.Wia;
                if (magic == WbfsReader.Magic) return DiscImageFormat.Wbfs;
                if (Be.U32(h, 0x1C) == GcDisc.Magic) return DiscImageFormat.GameCubeIso;
                if (Be.U32(h, 0x18) == WiiDisc.Magic) return DiscImageFormat.WiiIso;
                return DiscImageFormat.Unknown;
            }
            finally { s.Position = pos; }
        }

        // the platform without opening the whole image (for "this is a GameCube disc, not ..." messages)
        public static DiscPlatform? PlatformOf(Stream s)
        {
            switch (Detect(s))
            {
                case DiscImageFormat.GameCubeIso: return DiscPlatform.GameCube;
                case DiscImageFormat.WiiIso:
                case DiscImageFormat.Wbfs: return DiscPlatform.Wii;
                case DiscImageFormat.Rvz:
                case DiscImageFormat.Wia:
                {
                    long pos = s.Position;
                    try
                    {
                        var b = new byte[4];
                        s.Seek(0x48, SeekOrigin.Begin);   // wia_disc_t.disc_type
                        s.ReadExactly(b);
                        uint t = Be.U32(b, 0);
                        return t == 1 ? DiscPlatform.GameCube : t == 2 ? DiscPlatform.Wii : null;
                    }
                    finally { s.Position = pos; }
                }
                default: return null;
            }
        }

        // keys: the user's own Wii common key file, only for plain .iso / .wbfs Wii images (else WiiKeyRequiredException)
        public static IFileSource Open(Stream s, WiiCommonKeys? keys = null, bool leaveOpen = true)
        {
            switch (Detect(s))
            {
                case DiscImageFormat.GameCubeIso:
                    return new GcDisc(s, leaveOpen);
                case DiscImageFormat.Rvz:
                case DiscImageFormat.Wia:
                {
                    var wia = new WiaReader(s, leaveOpen);
                    try
                    {
                        if (wia.Platform == DiscPlatform.GameCube) return new FstFileSystem(wia, DiscPlatform.GameCube, wia);
                        return new WiiDisc(wia, ownsReader: true).OpenFileSystem(disposeDisc: true);
                    }
                    catch
                    {
                        wia.Dispose();
                        throw;
                    }
                }
                case DiscImageFormat.Wbfs:
                {
                    var wbfs = new WbfsReader(s, leaveOpen);
                    try
                    {
                        return new WiiDisc(wbfs, keys, wbfs).OpenFileSystem(disposeDisc: true);
                    }
                    catch
                    {
                        wbfs.Dispose();
                        throw;
                    }
                }
                case DiscImageFormat.WiiIso:
                {
                    var disc = new WiiDisc(s, keys, leaveOpen);
                    try
                    {
                        return disc.OpenFileSystem(disposeDisc: true);
                    }
                    catch
                    {
                        disc.Dispose();
                        throw;
                    }
                }
                default:
                    throw new InvalidDataException("not a GameCube or Wii disc image (.iso, .gcm, .wbfs, .wia, .rvz)");
            }
        }

        public static IFileSource Open(string path, WiiCommonKeys? keys = null)
        {
            var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            try
            {
                return Open(f, keys, leaveOpen: false);
            }
            catch
            {
                f.Dispose();
                throw;
            }
        }
    }
}
