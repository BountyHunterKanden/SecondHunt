using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Util;
using MphRecomp.Import;

namespace MphRecomp.App;

// IMPORT GAMES' picked folder (a game imported from its files: Prime 4's RomFS) as a file tree, read in place through the
// folder's document tree (the system's folder picker gives no file paths). The tree is rooted where the game's marker
// is, as FolderSource.Locate roots a folder: the picked folder, its romfs/, files/ or DATA/files/. Each folder's
// children are listed once, when a path first goes through it; a file opens as a seekable stream over its own file
// descriptor (DuplicateData.pak is 3.85 GB: it's read where it is, never copied).
internal sealed class ImportTreeSource : IFileSource
{
    sealed record Entry(string DocId, bool Dir, long Size);

    readonly ContentResolver _resolver;
    readonly Android.Net.Uri _tree;
    readonly string _rootId;
    readonly Dictionary<string, Dictionary<string, Entry>> _children = new(StringComparer.Ordinal);

    public string GameId => "";
    public string Title => "";

    ImportTreeSource(ContentResolver resolver, Android.Net.Uri tree, string rootId)
    {
        _resolver = resolver;
        _tree = tree;
        _rootId = rootId;
    }

    // the picked tree, rooted where `marker` is; null when it's gone, its access was withdrawn or none of its roots has it
    public static ImportTreeSource? Open(ContentResolver resolver, string token, string marker)
    {
        try
        {
            Android.Net.Uri tree = Android.Net.Uri.Parse(token)!;
            var top = new ImportTreeSource(resolver, tree, DocumentsContract.GetTreeDocumentId(tree)!);
            foreach (string sub in new[] { "", "romfs", "files", "DATA/files" })
            {
                if (sub.Length > 0 && top.Find(sub) is not { Dir: true }) continue;
                string id = sub.Length == 0 ? top._rootId : top.Find(sub)!.DocId;
                var at = new ImportTreeSource(resolver, tree, id);
                if (marker.Length == 0 || at.Find(marker) != null) return at;
            }
            Log.Warn("MPHFrontend", $"import: no {marker} in the picked folder");
            return null;
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.Lang.IllegalArgumentException or Java.IO.FileNotFoundException)
        {
            Log.Warn("MPHFrontend", $"import: can't open the folder {token}: {ex.Message}");
            return null;
        }
    }

    Dictionary<string, Entry> Children(string docId)
    {
        if (_children.TryGetValue(docId, out Dictionary<string, Entry>? known)) return known;
        var list = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        Android.Net.Uri uri = DocumentsContract.BuildChildDocumentsUriUsingTree(_tree, docId)!;
        string[] columns =
        {
            DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType, DocumentsContract.Document.ColumnSize,
        };
        using (ICursor? c = _resolver.Query(uri, columns, null, null, null))
        {
            while (c != null && c.MoveToNext())
            {
                string? id = c.GetString(0), name = c.GetString(1);
                if (id == null || string.IsNullOrEmpty(name)) continue;
                bool dir = c.GetString(2) == DocumentsContract.Document.MimeTypeDir;
                list[name] = new Entry(id, dir, c.IsNull(3) ? 0 : c.GetLong(3));
            }
        }
        _children[docId] = list;
        return list;
    }

    // a relative path ("Preload/DuplicateData.pak"), segment by segment from the root; null when it isn't there
    Entry? Find(string path)
    {
        Entry at = new(_rootId, true, 0);
        foreach (string part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!at.Dir || !Children(at.DocId).TryGetValue(part, out Entry? next)) return null;
            at = next;
        }
        return at;
    }

    // every file's path (the whole tree listed: only for a caller that walks it)
    public IEnumerable<string> Paths
    {
        get
        {
            var stack = new Stack<(string Id, string Prefix)>();
            stack.Push((_rootId, ""));
            while (stack.Count > 0)
            {
                (string id, string prefix) = stack.Pop();
                foreach (KeyValuePair<string, Entry> kv in Children(id))
                {
                    if (kv.Value.Dir) stack.Push((kv.Value.DocId, prefix + kv.Key + "/"));
                    else yield return prefix + kv.Key;
                }
            }
        }
    }

    public bool Contains(string path) => Find(path) is { Dir: false };

    public long Length(string path) => Find(path) is { Dir: false } e ? e.Size : throw new FileNotFoundException(path);

    public byte[] ReadFile(string path)
    {
        using Stream s = OpenRead(path);
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    public Stream OpenRead(string path)
    {
        Entry e = Find(path) is { Dir: false } f ? f : throw new FileNotFoundException(path);
        ParcelFileDescriptor pfd = _resolver.OpenFileDescriptor(DocumentsContract.BuildDocumentUriUsingTree(_tree, e.DocId)!, "r")
            ?? throw new FileNotFoundException(path);
        int fd = pfd.DetachFd(); // ours now: the stream closes it
        pfd.Close();
        var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, ownsHandle: true), FileAccess.Read, 1 << 16);
        if (!stream.CanSeek)
        {
            stream.Dispose();
            throw new IOException($"{path} can't be read in place (its app streams it); copy the folder to the device first");
        }
        return stream;
    }

    public void Dispose() { }
}
