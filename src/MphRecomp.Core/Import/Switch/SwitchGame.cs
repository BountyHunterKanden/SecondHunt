using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MphRecomp.Import.Disc;

namespace MphRecomp.Import.Switch
{
    public enum SwitchFileFormat { Unknown, Nsp, Xci }

    // The NCAs and tickets of one Switch package file:
    //   .nsp  a PFS0 of <content id>.nca files (+ .cnmt.nca, .tik tickets, .cert)
    //   .xci  a game card image (switchbrew "XCI"): the card header ("HEAD" at 0x100; 0x1100 when the dump keeps the
    //         0x1000-byte key area in front) gives the root HFS0's address at 0x130; the root's "secure" partition
    //         (an HFS0) holds the game's NCAs (and, in a card image with its update merged in, the update's NCAs and tickets)
    // Tickets (switchbrew "Ticket"): u32 signature type, the signature and its padding (0x10000/0x10003 RSA-4096:
    // 0x23C bytes; 0x10001/0x10004 RSA-2048: 0x13C; 0x10002/0x10005 ECDSA: 0x7C; 0x10006 HMAC: 0x3C), then the ticket
    // data: 0x40 the title key block (a common ticket's title key in its first 16 bytes), 0x141 title key type (0
    // common, 1 personalized), 0x160 the rights id.
    public sealed class SwitchPackage : IDisposable
    {
        readonly StreamDiscBlob _file;
        readonly Dictionary<string, byte[]> _tickets = new(StringComparer.OrdinalIgnoreCase);

        public SwitchFileFormat Format { get; }
        public IReadOnlyList<PartitionFile> Files { get; }
        public List<Nca> Ncas { get; } = new();
        public int PersonalizedTickets { get; private set; }
        public int TicketCount => _tickets.Count;

        public static SwitchFileFormat Detect(Stream s)
        {
            if (!s.CanSeek || s.Length < 0x200) return SwitchFileFormat.Unknown;
            long pos = s.Position;
            try
            {
                var b = new byte[4];
                s.Seek(0, SeekOrigin.Begin);
                s.ReadExactly(b);
                if (BinaryPrimitives.ReadUInt32LittleEndian(b) == PartitionFs.Pfs0Magic) return SwitchFileFormat.Nsp;
                foreach (long at in new long[] { 0x100, 0x1100 })
                {
                    if (s.Length < at + 4) continue;
                    s.Seek(at, SeekOrigin.Begin);
                    s.ReadExactly(b);
                    if (Encoding.ASCII.GetString(b) == "HEAD") return SwitchFileFormat.Xci;
                }
                return SwitchFileFormat.Unknown;
            }
            finally { s.Position = pos; }
        }

        public SwitchPackage(Stream s, SwitchKeys keys, bool leaveOpen = true)
        {
            Format = Detect(s);
            _file = new StreamDiscBlob(s, leaveOpen);
            try
            {
                Files = Format switch
                {
                    SwitchFileFormat.Nsp => PartitionFs.Read(_file, 0),
                    SwitchFileFormat.Xci => SecurePartition(),
                    _ => throw new InvalidDataException("not a Switch game file (.nsp or .xci)"),
                };
                if (Files.Any(f => f.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new NotSupportedException("compressed .nsz / .xcz game files are not supported yet: decompress it back to .nsp / .xci first");
                }
                foreach (PartitionFile f in Files)
                {
                    if (!f.Name.EndsWith(".tik", StringComparison.OrdinalIgnoreCase) || f.Size < 0x2C0 || f.Size > 0x10000) continue;
                    var t = new byte[f.Size];
                    _file.Read(f.Offset, t);
                    AddTicket(t);
                }
                foreach (PartitionFile f in Files)
                {
                    if (!f.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase)) continue;
                    Ncas.Add(new Nca(new SubBlob(_file, f.Offset, f.Size), keys, f.Name, TicketKey));
                }
            }
            catch
            {
                _file.Dispose();
                throw;
            }
        }

        List<PartitionFile> SecurePartition()
        {
            var b = new byte[0x200];
            long head = 0;
            _file.Read(0x100, b.AsSpan(0, 4));
            if (Encoding.ASCII.GetString(b, 0, 4) != "HEAD") head = 0x1000;   // a dump with the card key area in front
            _file.Read(head, b);
            long root = head + (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(0x130));
            if (!PartitionFs.IsPartitionFs(_file, root)) throw new InvalidDataException("XCI: no root HFS0 at the card header's address (damaged dump?)");
            List<PartitionFile> parts = PartitionFs.Read(_file, root, rootTable: true);
            PartitionFile secure = parts.FirstOrDefault(p => p.Name == "secure");
            if (secure.Name == null) throw new InvalidDataException("XCI: no secure partition");
            return PartitionFs.Read(_file, secure.Offset);
        }

        void AddTicket(byte[] t)
        {
            int sig = BinaryPrimitives.ReadInt32LittleEndian(t) switch
            {
                0x10000 or 0x10003 => 0x200 + 0x3C,
                0x10001 or 0x10004 => 0x100 + 0x3C,
                0x10002 or 0x10005 => 0x3C + 0x40,
                0x10006 => 0x14 + 0x28,
                _ => -1,
            };
            int d = 4 + sig;
            if (sig < 0 || d + 0x170 > t.Length) return;
            if (t[d + 0x141] != 0)
            {
                PersonalizedTickets++;   // bound to the buyer's console: its key can't be read without that console's keys
                return;
            }
            _tickets[Convert.ToHexString(t, d + 0x160, 16)] = t.AsSpan(d + 0x40, 16).ToArray();
        }

        public byte[]? TicketKey(byte[] rightsId) => _tickets.TryGetValue(Convert.ToHexString(rightsId), out byte[]? k) ? k : null;

        public void Dispose() => _file.Dispose();
    }

    // Opens a Switch game file (.nsp / .xci) as its RomFS, with the player's own keys, optionally with an update
    // (.nsp) applied through its BKTR patch. A card image that carries its update in the secure partition gets it
    // applied without a second file (unless baseOnly). The game's Program NCA is the one with a plain RomFS section (content index 0
    // first); the update's is the Program NCA of the same program whose RomFS section is a patch.
    // TODO (Phase 3): .nsz / .xcz (an .nsp / .xci whose NCAs are .ncz: the NCA's first 0x4000 bytes as they are, then
    // the sections' decrypted data zstd-compressed, in one solid stream or in independently compressed blocks). Not
    // done: no such file to test against. Plan: block mode maps to random access through Disc/Zstd.cs (one block per
    // read, cached) with the sections re-encrypted on the fly or read as plaintext; a solid stream can't be seeked, so
    // it would be refused with "re-compress in block mode, or decompress to .nsp" rather than inflated to a temp file
    // of many GB. Until then SwitchPackage refuses .ncz contents with that message.
    public static class SwitchGame
    {
        public static readonly string[] Extensions = { ".xci", ".nsp" };

        public static SwitchFileFormat Detect(Stream s) => SwitchPackage.Detect(s);

        public static SwitchRomFs Open(string path, SwitchKeys keys, string? updatePath = null, bool verify = false, bool baseOnly = false)
        {
            FileStream OpenFile(string p) => new(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            FileStream game = OpenFile(path);
            FileStream? update = null;
            try
            {
                update = updatePath != null ? OpenFile(updatePath) : null;
                return Open(game, keys, update, verify, leaveOpen: false, baseOnly);
            }
            catch
            {
                game.Dispose();
                update?.Dispose();
                throw;
            }
        }

        public static SwitchRomFs Open(Stream game, SwitchKeys keys, Stream? update = null, bool verify = false, bool leaveOpen = true, bool baseOnly = false)
        {
            var packages = new List<SwitchPackage>();
            try
            {
                var pkg = new SwitchPackage(game, keys, leaveOpen);
                packages.Add(pkg);
                SwitchPackage? upd = null;
                if (update != null)
                {
                    upd = new SwitchPackage(update, keys, leaveOpen);
                    packages.Add(upd);
                }
                (Nca nca, NcaSection romfs)? baseProgram = Programs(pkg).Where(p => !p.romfs.IsPatch)
                    .OrderBy(p => p.nca.ContentIndex).ThenByDescending(p => p.romfs.Size).Cast<(Nca, NcaSection)?>().FirstOrDefault();
                if (baseProgram == null)
                {
                    throw new InvalidDataException(Programs(pkg).Any() ? "this file is an update; give the game itself (and the update as its update)"
                        : pkg.Ncas.Any(n => n.ContentType == NcaContentType.Program) ? "this game has no RomFS (its data is inside its program)"
                        : "this file holds no game program (a DLC or a damaged dump?)");
                }
                var (bnca, bsec) = baseProgram.Value;
                ulong app = bnca.ProgramId & ~0x800UL;
                bool Same((Nca nca, NcaSection romfs) p) => (p.nca.ProgramId & ~0x800UL) == app && p.nca.ContentIndex == bnca.ContentIndex;
                (Nca nca, NcaSection romfs)? patch = null;
                if (upd != null)
                {
                    var ps = Programs(upd).Where(p => p.romfs.IsPatch).ToList();
                    if (ps.Count == 0) throw new InvalidDataException("the update file holds no update program (is it the game itself, or a DLC?)");
                    patch = ps.Where(Same).Cast<(Nca, NcaSection)?>().FirstOrDefault()
                        ?? throw new InvalidDataException($"the update is for another game ({ps[0].nca.ProgramId:X16}, not {bnca.ProgramId:X16})");
                }
                else if (!baseOnly)
                {
                    patch = Programs(pkg).Where(p => p.romfs.IsPatch && Same(p)).Cast<(Nca, NcaSection)?>().FirstOrDefault();
                }

                IDiscBlob baseSection = bnca.OpenSection(bsec);
                SwitchRomFs fs;
                var owner = new Owner(packages);
                if (patch is var (pnca, psec))
                {
                    var patched = new PatchedSection(baseSection, (CtrBlob)pnca.OpenSection(psec), psec);
                    fs = new SwitchRomFs(patched, psec.HashData, bnca.ProgramId, patched: true, verify, owner);
                }
                else
                {
                    fs = new SwitchRomFs(baseSection, bsec.HashData, bnca.ProgramId, patched: false, verify, owner);
                }
                return fs;
            }
            catch
            {
                foreach (SwitchPackage p in packages) p.Dispose();
                throw;
            }
        }

        // every Program NCA with a RomFS section
        static IEnumerable<(Nca nca, NcaSection romfs)> Programs(SwitchPackage pkg)
        {
            foreach (Nca n in pkg.Ncas)
            {
                if (n.ContentType != NcaContentType.Program) continue;
                foreach (NcaSection? s in n.Sections)
                {
                    if (s != null && s.IsRomFs && s.HashType is NcaHashType.HierarchicalIntegrity or NcaHashType.Auto) yield return (n, s);
                }
            }
        }

        sealed class Owner : IDisposable
        {
            readonly List<SwitchPackage> _p;
            public Owner(List<SwitchPackage> p) { _p = p; }
            public void Dispose() { foreach (SwitchPackage p in _p) p.Dispose(); }
        }
    }
}
