using DiscDecoders.Common;
using DvdXporter.Core.Storage;

namespace DvdXporter.Core.Discs;

/// <summary>
/// Remembers fingerprints by path so unchanged discs don't need to be re-read.
/// Each entry stores a cheap signature (sizes and timestamps); a changed signature means re-fingerprint.
/// </summary>
public sealed class FingerprintCache
{
    private const int CurrentFormatVersion = 1;

    private readonly string _filePath;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries;
    private bool _dirty;

    public sealed record Entry(string Signature, DiscSourceKind Kind, DiscFormat Format, string Fingerprint);

    private sealed class CacheFile
    {
        public int FormatVersion { get; set; } = CurrentFormatVersion;

        public Dictionary<string, Entry> Entries { get; set; } = [];
    }

    private FingerprintCache(string filePath, Dictionary<string, Entry> entries)
    {
        _filePath = filePath;
        _entries = entries;
    }

    public static FingerprintCache Load(string filePath)
    {
        var file = JsonFile.TryRead<CacheFile>(filePath);
        var entries = file?.FormatVersion == CurrentFormatVersion ? file.Entries : [];
        return new FingerprintCache(filePath, entries);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _dirty = true;
        }
    }

    public void Save()
    {
        CacheFile snapshot;
        lock (_lock)
        {
            if (!_dirty)
                return;

            _entries.Keys.Where(path => !File.Exists(path) && !Directory.Exists(path)).ToList()
                .ForEach(path => _entries.Remove(path));
            snapshot = new CacheFile { Entries = new Dictionary<string, Entry>(_entries) };
            _dirty = false;
        }

        JsonFile.Write(_filePath, snapshot);
    }

    internal bool TryGet(string path, string signature, out Entry entry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(path, out var found) && found.Signature == signature)
            {
                entry = found;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    internal void Set(string path, Entry entry)
    {
        lock (_lock)
        {
            _entries[path] = entry;
            _dirty = true;
        }
    }
}
