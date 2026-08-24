using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PhoneBackup;

public class ContentIndexEntry
{
    public long Size { get; set; }
    public long MTimeTicks { get; set; }
    public string? Hash { get; set; }
}

/// A lightweight index of every file already sitting under the backup's destination
/// root — across ALL device folders, e.g. an existing Galaxy S23 Ultra backup living
/// next to the OnePlus one in the same folder. The backup engine consults this before
/// writing each phone file so it can:
///   1. recognize when the incoming file's content already exists somewhere in the
///      tree (so it isn't stored a second time), and
///   2. know for certain whether a file already sitting at the destination path is
///      one this app put there, so it never overwrites something it doesn't recognize.
/// Hashes are computed lazily (only when actually needed for a comparison) and cached
/// on disk, so the cost is paid once, not on every run.
public class ContentIndex
{
    private readonly string _destRoot;
    private readonly string _cachePath;
    private readonly Dictionary<string, ContentIndexEntry> _entries; // relPath -> entry
    private Dictionary<long, List<string>> _bySize;
    private int _dirty;

    private ContentIndex(string destRoot, string cachePath, Dictionary<string, ContentIndexEntry> entries)
    {
        _destRoot = destRoot;
        _cachePath = cachePath;
        _entries = entries;
        _bySize = BuildSizeIndex(entries);
    }

    private static Dictionary<long, List<string>> BuildSizeIndex(Dictionary<string, ContentIndexEntry> entries)
    {
        var bySize = new Dictionary<long, List<string>>();
        foreach (var kv in entries)
        {
            if (!bySize.TryGetValue(kv.Value.Size, out var list)) bySize[kv.Value.Size] = list = new List<string>();
            list.Add(kv.Key);
        }
        return bySize;
    }

    public static ContentIndex LoadOrScan(string destRoot, Action<string> log, CancellationToken ct)
    {
        string cachePath = Path.Combine(destRoot, ".phonebackup-content-index.json");
        var cached = new Dictionary<string, ContentIndexEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(cachePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, ContentIndexEntry>>(File.ReadAllText(cachePath));
                if (loaded != null) cached = new Dictionary<string, ContentIndexEntry>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { cached = new Dictionary<string, ContentIndexEntry>(StringComparer.OrdinalIgnoreCase); }

        var fresh = new Dictionary<string, ContentIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        long seen = 0;
        var sw = Stopwatch.StartNew();
        string rootFull = Path.GetFullPath(destRoot);
        foreach (var f in Directory.EnumerateFiles(rootFull, "*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (DeleteVault.IsInsideVault(f)) continue;
            string name = Path.GetFileName(f);
            if (name.StartsWith(".phonebackup-", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;

            string rel;
            try { rel = Path.GetRelativePath(rootFull, f); } catch { continue; }

            long size; long mtimeTicks;
            try
            {
                var fi = new FileInfo(f);
                size = fi.Length;
                mtimeTicks = fi.LastWriteTimeUtc.Ticks;
            }
            catch { continue; }

            // Unchanged since the cache was last written -> keep its (possibly already
            // computed) hash; otherwise start fresh so a stale hash is never trusted.
            fresh[rel] = cached.TryGetValue(rel, out var prior) && prior.Size == size && prior.MTimeTicks == mtimeTicks
                ? prior
                : new ContentIndexEntry { Size = size, MTimeTicks = mtimeTicks, Hash = null };

            if (++seen % 2000 == 0 && sw.ElapsedMilliseconds > 400)
            {
                sw.Restart();
                log($"Indexing existing backup for duplicate protection… {seen:N0} files");
            }
        }

        if (seen > 0) log($"Indexed {seen:N0} existing files for duplicate/overwrite protection.");
        return new ContentIndex(rootFull, cachePath, fresh);
    }

    /// Existing relative paths with this exact file size, excluding one path (typically
    /// the file currently being processed, so it never "matches" against itself).
    public IEnumerable<string> CandidatesForSize(long size, string? exclude)
    {
        if (!_bySize.TryGetValue(size, out var list)) return Array.Empty<string>();
        return exclude == null ? list : list.Where(p => !p.Equals(exclude, StringComparison.OrdinalIgnoreCase));
    }

    public string GetOrComputeHash(string relPath, CancellationToken ct)
    {
        if (!_entries.TryGetValue(relPath, out var entry))
            throw new FileNotFoundException("Not in content index", relPath);
        if (entry.Hash != null) return entry.Hash;

        string full = LongPath(Path.Combine(_destRoot, relPath));
        string hash = Hashing.HashFile(full, -1, ct);
        entry.Hash = hash;
        _dirty++;
        return hash;
    }

    /// Records a file this backup run just wrote (or a conflict-renamed copy), so later
    /// files in the SAME run can also be recognized as duplicates of it immediately,
    /// without waiting for the next run's rescan.
    public void Register(string relPath, long size, string? hash)
    {
        _entries[relPath] = new ContentIndexEntry { Size = size, MTimeTicks = DateTime.UtcNow.Ticks, Hash = hash };
        if (!_bySize.TryGetValue(size, out var list)) _bySize[size] = list = new List<string>();
        if (!list.Contains(relPath, StringComparer.OrdinalIgnoreCase)) list.Add(relPath);
        _dirty++;
    }

    public void Save()
    {
        if (_dirty == 0) return;
        try
        {
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_entries));
            _dirty = 0;
        }
        catch { /* non-fatal: a future run just rescans/rehashes what's missing */ }
    }

    private static string LongPath(string path) =>
        path.StartsWith(@"\\?\") ? path : @"\\?\" + Path.GetFullPath(path);
}
