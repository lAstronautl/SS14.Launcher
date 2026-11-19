using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using Robust.LoaderApi;

namespace Robust.Launcher.Core;

/// <summary>Content made of individual blob files (manifest downloads).</summary>
public sealed class BlobFileApi : IFileApi
{
    private readonly Dictionary<string, string> _files;

    public BlobFileApi(Dictionary<string, string> files)
    {
        _files = files;
    }

    public bool TryOpen(string path, [NotNullWhen(true)] out Stream? stream)
    {
        if (_files.TryGetValue(path.TrimStart('/'), out var blob))
        {
            stream = File.OpenRead(blob);
            return true;
        }

        stream = null;
        return false;
    }

    public IEnumerable<string> AllFiles => _files.Keys;
}

/// <summary>Files inside a zip archive. Thread-safe: zip entries are read under a lock into memory.</summary>
public sealed class ZipFileApi : IFileApi
{
    private readonly ZipArchive _archive;
    private readonly string _prefix;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new();

    public ZipFileApi(ZipArchive archive, string prefix = "")
    {
        _archive = archive;
        _prefix = prefix;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            _entries[entry.FullName[prefix.Length..]] = entry;
        }
    }

    public bool TryOpen(string path, [NotNullWhen(true)] out Stream? stream)
    {
        if (!_entries.TryGetValue(path.TrimStart('/'), out var entry))
        {
            stream = null;
            return false;
        }

        lock (_archive)
        {
            var ms = new MemoryStream((int)entry.Length);
            using (var s = entry.Open())
                s.CopyTo(ms);
            ms.Position = 0;
            stream = ms;
            return true;
        }
    }

    public IEnumerable<string> AllFiles => _entries.Keys;
}

/// <summary>Files in a plain directory.</summary>
public sealed class DirectoryFileApi : IFileApi
{
    private readonly string _root;

    public DirectoryFileApi(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public bool TryOpen(string path, [NotNullWhen(true)] out Stream? stream)
    {
        var full = Path.GetFullPath(Path.Combine(_root, path.TrimStart('/')));
        if (full.StartsWith(_root, StringComparison.Ordinal) && File.Exists(full))
        {
            stream = File.OpenRead(full);
            return true;
        }

        stream = null;
        return false;
    }

    public IEnumerable<string> AllFiles =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'));
}
