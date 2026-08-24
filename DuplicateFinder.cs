using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PhoneBackup;

public class DupFile
{
    public string Path = "";
    public long Size;
    public DateTime Modified;
}

public class DupGroup
{
    public long Size;
    public List<DupFile> Files = new();
    public long Wasted => Size * (Files.Count - 1);
}

public static class DuplicateFinder
{
    /// First path segment under the scan root (the "device folder", e.g. "OnePlus 15"),
    /// or "" if the file sits directly in the root.
    public static string TopLevel(string root, string fullPath)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        string full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return "";
        string rel = full.Substring(rootFull.Length);
        int slash = rel.IndexOf('\\');
        return slash < 0 ? "" : rel.Substring(0, slash);
    }

    public static List<DupGroup> Scan(string root, long minSize, bool sameTopLevelOnly,
        Action<string, double> progress, CancellationToken ct)
    {
        var throttle = Stopwatch.StartNew();
        void Report(string stage, double pct)
        {
            if (throttle.ElapsedMilliseconds < 150) return;
            throttle.Restart();
            progress(stage, pct);
        }

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        progress("Listing files…", 0);
        var bySize = new Dictionary<long, List<string>>();
        long seen = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (DeleteVault.IsInsideVault(f)) continue; // never scan the delete vault, at any depth
            string name = Path.GetFileName(f);
            if (name.Equals(".phonebackup-index.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
            long len;
            try { len = new FileInfo(f).Length; } catch { continue; }
            if (len < Math.Max(minSize, 1)) continue;
            if (!bySize.TryGetValue(len, out var list)) bySize[len] = list = new List<string>();
            list.Add(f);
            if (++seen % 500 == 0) Report($"Listing files… {seen:N0}", 0);
        }

        var candidates = bySize.Where(kv => kv.Value.Count > 1).OrderByDescending(kv => kv.Key).ToList();
        long totalBytes = candidates.Sum(kv => kv.Key * kv.Value.Count);
        long doneBytes = 0;
        int gi = 0;
        var groups = new List<DupGroup>();

        foreach (var kv in candidates)
        {
            ct.ThrowIfCancellationRequested();
            long size = kv.Key;
            const int partialLen = 256 * 1024;

            // Stage 1: hash only the first 256 KB — eliminates most same-size non-duplicates cheaply.
            var byPartial = new Dictionary<string, List<string>>();
            foreach (var f in kv.Value)
            {
                ct.ThrowIfCancellationRequested();
                string h;
                try { h = Hashing.HashFile(f, partialLen, ct); } catch { continue; }
                if (!byPartial.TryGetValue(h, out var l)) byPartial[h] = l = new List<string>();
                l.Add(f);
            }

            foreach (var part in byPartial.Values.Where(v => v.Count > 1))
            {
                // Stage 2: full hash, unless the partial hash already covered the whole file.
                IEnumerable<List<string>> confirmed;
                if (size <= partialLen)
                {
                    confirmed = new[] { part };
                }
                else
                {
                    var byFull = new Dictionary<string, List<string>>();
                    foreach (var f in part)
                    {
                        ct.ThrowIfCancellationRequested();
                        string h;
                        try { h = Hashing.HashFile(f, -1, ct); } catch { continue; }
                        if (!byFull.TryGetValue(h, out var l)) byFull[h] = l = new List<string>();
                        l.Add(f);
                    }
                    confirmed = byFull.Values.Where(v => v.Count > 1);
                }

                foreach (var dup in confirmed)
                {
                    // When "same device folder only" is on, never match identical files
                    // that live in different top-level folders (e.g. an S23U copy vs an
                    // OnePlus copy you pasted across) — split the set per device folder
                    // and keep only the within-folder duplicates.
                    IEnumerable<List<string>> sets = sameTopLevelOnly
                        ? dup.GroupBy(f => TopLevel(root, f), StringComparer.OrdinalIgnoreCase)
                             .Select(grp => grp.ToList())
                             .Where(l => l.Count > 1)
                        : new[] { dup };

                    foreach (var set in sets)
                    {
                        var g = new DupGroup { Size = size };
                        foreach (var f in set)
                        {
                            DateTime mt;
                            try { mt = File.GetLastWriteTime(f); } catch { mt = DateTime.MinValue; }
                            g.Files.Add(new DupFile { Path = f, Size = size, Modified = mt });
                        }
                        g.Files.Sort((a, b) => a.Modified.CompareTo(b.Modified)); // oldest first = the "original"
                        groups.Add(g);
                    }
                }
            }

            doneBytes += size * kv.Value.Count;
            gi++;
            Report($"Comparing content… {gi:N0}/{candidates.Count:N0} size groups",
                   totalBytes > 0 ? 100.0 * doneBytes / totalBytes : 0);
        }

        groups.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
        return groups;
    }

    // ---- auto-select strategies ----

    public enum KeepStrategy
    {
        Oldest = 0,
        Newest = 1,
        PreferDcim = 2,
        ShortestPath = 3,
        LongestPath = 4,
        ShortestName = 5,
    }

    /// Picks which single copy of a duplicate group to KEEP under the given strategy.
    public static DupRow ChooseKeeper(IReadOnlyList<DupRow> group, KeepStrategy s)
    {
        static int DcimScore(DupRow r) =>
            r.Path.Contains(@"\DCIM\Camera\", StringComparison.OrdinalIgnoreCase) ? 2 :
            r.Path.Contains(@"\DCIM\", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        static int NameLen(DupRow r) => Path.GetFileName(r.Path).Length;

        return s switch
        {
            KeepStrategy.Newest =>
                group.OrderByDescending(r => r.Modified).ThenBy(r => r.Path.Length).First(),
            KeepStrategy.PreferDcim =>
                group.OrderByDescending(DcimScore).ThenBy(r => r.Modified).ThenBy(r => r.Path.Length).First(),
            KeepStrategy.ShortestPath =>
                group.OrderBy(r => r.Path.Length).ThenBy(r => r.Modified).First(),
            KeepStrategy.LongestPath =>
                group.OrderByDescending(r => r.Path.Length).ThenBy(r => r.Modified).First(),
            KeepStrategy.ShortestName =>
                group.OrderBy(NameLen).ThenBy(r => r.Modified).ThenBy(r => r.Path.Length).First(),
            _ => group.OrderBy(r => r.Modified).ThenBy(r => r.Path.Length).First(),
        };
    }

    /// Marks deleted backup files as suppressed in the backup index so the next
    /// backup run doesn't re-copy them from the phone.
    public static int MarkSuppressedInBackupIndex(string destRoot, IEnumerable<string> deletedPaths)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destRoot)) return 0;
            string indexPath = Path.Combine(destRoot, ".phonebackup-index.json");
            if (!File.Exists(indexPath)) return 0;
            var index = JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(File.ReadAllText(indexPath));
            if (index is null) return 0;

            string rootFull = Path.GetFullPath(destRoot).TrimEnd('\\') + "\\";
            int n = 0;
            foreach (var p in deletedPaths)
            {
                string full = Path.GetFullPath(p);
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = full.Substring(rootFull.Length);
                if (index.TryGetValue(rel, out var e) && !e.Suppressed) { e.Suppressed = true; n++; }
            }
            if (n > 0) File.WriteAllText(indexPath, JsonSerializer.Serialize(index));
            return n;
        }
        catch { return 0; }
    }
}

// Row model for the duplicates ListView.
public class DupRow : INotifyPropertyChanged
{
    public string Path = "";
    public string Display { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string ModifiedText { get; set; } = "";
    public int Group { get; set; }
    public long Size;
    public DateTime Modified;
    public System.Windows.Media.Brush? Shade { get; set; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
