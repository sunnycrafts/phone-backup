using System.Diagnostics;
using System.IO;

namespace PhoneBackup;

public class CleanCategory
{
    public string Name = "";
    public string Description = "";
    public List<string> Paths = new();   // files or whole folder trees
    public long Bytes;
    public bool IsChecked = true;
    public int Count => Paths.Count;
}

/// Scans an existing backup folder on the PC and finds junk to remove,
/// using the same rules the backup itself applies on the phone.
public static class Cleaner
{
    public class Rules
    {
        public long MinSizeBytes = 1024 * 1024;
        public bool AlwaysKeepDocuments = true;
        public bool SkipDotEntries = true;
        public HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase);
    }

    public static List<CleanCategory> Scan(string root, Rules rules, Action<string> progress, CancellationToken ct)
    {
        var nameExcludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathExcludes = new List<string[]>(); // e.g. ["android","data"] — matched anywhere in the path
        foreach (var e in rules.ExcludedFolders)
        {
            var norm = e.Replace('\\', '/').Trim('/');
            if (norm.Length == 0) continue;
            if (norm.Contains('/')) pathExcludes.Add(norm.Split('/'));
            else nameExcludes.Add(norm);
        }

        var junkFolders = new CleanCategory
        {
            Name = "Junk folders",
            Description = "Android/data, Android/obb, caches, trash and other excluded folders (whole trees)"
        };
        var hiddenFiles = new CleanCategory
        {
            Name = "Hidden / deleted-on-phone files",
            Description = "Dot-files: .trashed-* recycle-bin leftovers, .nomedia markers, thumbnails"
        };
        var junkExt = new CleanCategory
        {
            Name = "Junk file types",
            Description = "Temp, cache, log and partial-download files"
        };
        var smallFiles = new CleanCategory
        {
            Name = "Tiny files",
            Description = "Below the size threshold (documents and WhatsApp/Telegram media are kept)"
        };
        var emptyFolders = new CleanCategory
        {
            Name = "Empty folders",
            Description = "Folders with nothing in them"
        };
        var samsungFolders = new CleanCategory
        {
            Name = "Samsung-specific folders",
            Description = "Samsung/Galaxy system folders useless on other phones (Samsung, SmartSwitch, AR Emoji, Bixby, PENUP…)"
        };
        var samsungFiles = new CleanCategory
        {
            Name = "Samsung-only file types",
            Description = "Samsung Notes / Memo formats (.snb, .spd, .sdoc, .sdocx) other phones can't open"
        };

        var throttle = Stopwatch.StartNew();
        long dirsSeen = 0;

        bool IsExcludedDir(string name, string[] relSegments)
        {
            if (nameExcludes.Contains(name)) return true;
            if (rules.SkipDotEntries && name.StartsWith('.')) return true;
            foreach (var pat in pathExcludes)
            {
                // match the segment sequence at any depth, e.g. …\Android\data
                for (int i = 0; i + pat.Length <= relSegments.Length; i++)
                {
                    bool all = true;
                    for (int j = 0; j < pat.Length; j++)
                        if (!relSegments[i + j].Equals(pat[j], StringComparison.OrdinalIgnoreCase)) { all = false; break; }
                    if (all && relSegments.Length == i + pat.Length) return true;
                }
            }
            return false;
        }

        // Same exemption the backup applies: WhatsApp/Telegram photos & videos are
        // real user media even when small.
        bool IsAppMedia(string relPath, string ext)
        {
            if (!MediaExts.Contains(ext)) return false;
            string p = relPath.Replace('\\', '/');
            return p.Contains("com.whatsapp", StringComparison.OrdinalIgnoreCase)
                || p.Contains("/WhatsApp/", StringComparison.OrdinalIgnoreCase)
                || p.Contains("telegram", StringComparison.OrdinalIgnoreCase);
        }

        bool Walk(string dir, string[] relSegments) // returns true if dir has any surviving content
        {
            ct.ThrowIfCancellationRequested();
            if (++dirsSeen % 50 == 0 && throttle.ElapsedMilliseconds > 150)
            {
                throttle.Restart();
                progress($"Scanning… {dirsSeen:N0} folders, {junkFolders.Count + hiddenFiles.Count + junkExt.Count + smallFiles.Count:N0} junk items found");
            }

            bool hasContent = false;
            string[] subdirs, files;
            try
            {
                // materialize here: lazy enumeration would throw inside the foreach
                // below (outside any try) and kill the whole scan on one bad folder
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch { return true; } // unreadable: leave alone

            var localEmpties = new List<string>(); // empty children flagged in THIS folder
            foreach (var sub in subdirs)
            {
                string name = Path.GetFileName(sub);
                if (name.Equals(DeleteVault.FolderName, StringComparison.OrdinalIgnoreCase))
                {
                    hasContent = true; // a delete vault at any depth: never rescan it
                    continue;
                }
                var subSegments = relSegments.Append(name).ToArray();
                if (IsSamsungDir(name))
                {
                    samsungFolders.Paths.Add(sub);
                    samsungFolders.Bytes += DirSize(sub, ct);
                    hasContent = true;
                }
                else if (IsExcludedDir(name, subSegments))
                {
                    junkFolders.Paths.Add(sub);
                    junkFolders.Bytes += DirSize(sub, ct);
                    hasContent = true; // conservative: only truly empty trees count as empty
                }
                else if (Walk(sub, subSegments))
                {
                    hasContent = true;
                }
                else
                {
                    emptyFolders.Paths.Add(sub);
                    localEmpties.Add(sub);
                }
            }

            // If this whole folder turns out empty, the caller will flag it — so drop the
            // empty children we just added (the parent supersedes them) to avoid listing,
            // counting and moving the same empty tree at multiple levels.
            if (!hasContent && files.Length == 0 && localEmpties.Count > 0)
            {
                var drop = new HashSet<string>(localEmpties, StringComparer.OrdinalIgnoreCase);
                emptyFolders.Paths.RemoveAll(drop.Contains);
            }

            string relDir = string.Join('\\', relSegments);
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                hasContent = true; // conservative: any file means the folder is not "empty"
                string name = Path.GetFileName(f);
                if (name.Equals(".phonebackup-index.json", StringComparison.OrdinalIgnoreCase)) continue;

                string ext = Path.GetExtension(name);
                long size;
                try { size = new FileInfo(f).Length; } catch { continue; }

                if (rules.SkipDotEntries && name.StartsWith('.'))
                {
                    hiddenFiles.Paths.Add(f); hiddenFiles.Bytes += size;
                }
                else if (rules.ExcludedExtensions.Contains(ext))
                {
                    junkExt.Paths.Add(f); junkExt.Bytes += size;
                }
                else if (SamsungExts.Contains(ext))
                {
                    samsungFiles.Paths.Add(f); samsungFiles.Bytes += size;
                }
                else if (size < rules.MinSizeBytes
                         && !(rules.AlwaysKeepDocuments && rules.DocumentExtensions.Contains(ext))
                         && !IsAppMedia(relDir + "\\" + name, ext))
                {
                    smallFiles.Paths.Add(f); smallFiles.Bytes += size;
                }
            }
            return hasContent;
        }

        Walk(root, Array.Empty<string>());

        var all = new List<CleanCategory>
        { junkFolders, samsungFolders, samsungFiles, hiddenFiles, junkExt, smallFiles, emptyFolders };
        return all.Where(c => c.Count > 0).ToList();
    }

    // Distinctive Samsung/Galaxy folder names — these only exist on Samsung phones
    // and mean nothing to a OnePlus.
    private static readonly HashSet<string> SamsungFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Samsung", "SmartSwitch", "PENUP", "Bixby", "BixbyVision", "SamsungFlow",
        "Knox", "SamsungCloud", "SamsungPay", "Samsung Notes", "SamsungNotes", "GalaxyWearable",
    };

    private static bool IsSamsungDir(string name) =>
        SamsungFolderNames.Contains(name) ||
        name.StartsWith("AREmoji", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("AR Emoji", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ARDoodle", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("AR Doodle", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> SamsungExts = new(StringComparer.OrdinalIgnoreCase)
    { ".snb", ".spd", ".sdoc", ".sdocx", ".smemo" };

    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".mp4", ".3gp", ".mov", ".mkv", ".avi" };

    private static long DirSize(string dir, CancellationToken ct)
    {
        long total = 0;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }
}
