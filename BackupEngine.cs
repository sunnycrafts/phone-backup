using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace PhoneBackup;

public class DeviceInfo
{
    public string Name { get; set; } = "";
    public List<string> Storages { get; set; } = new();
    public override string ToString() => Name;
}

public class LiveStats
{
    private readonly object _lock = new();
    public long NewFiles, UpdatedFiles, SkippedFiles, ExcludedFiles, ExcludedFolders, Errors;
    public long DuplicatesAvoided, Conflicts;
    public long BytesCopied, FilesSeen;
    public int UnitsDone, UnitsTotal;
    public string CurrentItem = "";
    public ConcurrentQueue<string> Log = new();

    public void With(Action<LiveStats> a) { lock (_lock) a(this); }
    public LiveStats Snapshot()
    {
        lock (_lock)
        {
            return new LiveStats
            {
                NewFiles = NewFiles, UpdatedFiles = UpdatedFiles, SkippedFiles = SkippedFiles,
                ExcludedFiles = ExcludedFiles, ExcludedFolders = ExcludedFolders, Errors = Errors,
                DuplicatesAvoided = DuplicatesAvoided, Conflicts = Conflicts,
                BytesCopied = BytesCopied, FilesSeen = FilesSeen,
                UnitsDone = UnitsDone, UnitsTotal = UnitsTotal, CurrentItem = CurrentItem,
            };
        }
    }
}

public class IndexEntry
{
    public long Size { get; set; }
    public long MTimeTicks { get; set; }
    // True when the user deliberately removed the PC copy (duplicate/junk cleanup):
    // the file is then not re-copied from the phone on later runs.
    public bool Suppressed { get; set; }
    // Set when this phone file's content turned out to be identical to a file already
    // sitting elsewhere in the destination tree (relative path) — nothing is physically
    // stored for this entry; the existing file is treated as covering it.
    public string? DuplicateOfPath { get; set; }
    // Set when the "natural" destination path was already occupied by a file this app
    // didn't recognize as its own — the phone's version was saved under this alternate
    // relative path instead of overwriting the unknown file.
    public string? ConflictPath { get; set; }
}

public class BackupOptions
{
    public string DeviceName = "";
    public string DestRoot = "";
    public long MinSizeBytes = 1024 * 1024;
    public bool AlwaysKeepDocuments = true;
    public bool SkipDotFolders = true;
    public bool MirrorAppMedia = true; // copy WhatsApp/Telegram pics+videos to phone's Pictures
    // Before writing a file, check whether its content already exists anywhere else in
    // the destination tree (e.g. an old S23U backup sitting alongside) and, if so, don't
    // store it twice. Also guarantees a file this app doesn't recognize is never overwritten.
    public bool AvoidCrossBackupDuplicates = true;
    public HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase);
}

public class BackupEngine
{
    private const int SsfDrives = 17; // "This PC" shell namespace

    // ---- Shell / WPD interop for fast stream copy ----

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out ComIStream stream);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItem ppv);

    private static Guid _bhidStream = new("1CEBB3AB-7C10-499a-A417-92CA16C4CB83");
    private static Guid _iidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static Guid _iidIStream = new("0000000C-0000-0000-C000-000000000046");

    // ---- Device discovery (run on an STA thread) ----

    public static List<DeviceInfo> FindDevices()
    {
        var result = new List<DeviceInfo>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic thisPc = shell.NameSpace(SsfDrives);
        foreach (dynamic item in thisPc.Items())
        {
            try
            {
                if (!(bool)item.IsFolder || (bool)item.IsFileSystem) continue; // MTP devices are non-filesystem folders
                var dev = new DeviceInfo { Name = (string)item.Name };
                dynamic folder = item.GetFolder;
                foreach (dynamic storage in folder.Items())
                {
                    if ((bool)storage.IsFolder) dev.Storages.Add((string)storage.Name);
                }
                if (dev.Storages.Count > 0) result.Add(dev);
            }
            catch { /* skip unreadable entries */ }
        }
        return result;
    }

    // ---- Backup run (call on an STA thread) ----

    private readonly BackupOptions _opt;
    private readonly LiveStats _stats;
    private readonly CancellationToken _ct;
    private dynamic _shell = null!;
    private Dictionary<string, IndexEntry> _index = new();
    private string _indexPath = "";
    private int _indexDirty;

    private readonly HashSet<string> _nameExcludes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pathExcludes = new(StringComparer.OrdinalIgnoreCase);
    private ContentIndex? _contentIndex;

    public BackupEngine(BackupOptions opt, LiveStats stats, CancellationToken ct)
    {
        _opt = opt;
        _stats = stats;
        _ct = ct;
        // Entries with a slash match the path relative to the storage root (e.g. "Android/data");
        // plain entries match a folder name at any depth (e.g. "cache").
        foreach (var e in opt.ExcludedFolders)
        {
            var norm = e.Replace('\\', '/').Trim('/');
            if (norm.Length == 0) continue;
            if (norm.Contains('/')) _pathExcludes.Add(norm);
            else _nameExcludes.Add(norm);
        }
        if (opt.MirrorAppMedia)
        {
            // These phone folders are filled by us with copies of already-backed-up
            // app media, so scanning them would only duplicate data in the backup.
            _pathExcludes.Add("Pictures/WhatsApp");
            _pathExcludes.Add("Pictures/Telegram");
        }
    }

    private sealed record PendingUpload(string App, string PcPath, long Size);
    private readonly List<PendingUpload> _uploads = new();

    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".mp4", ".3gp", ".mov", ".mkv", ".avi" };

    // Subfolders of app media we don't want flooding Pictures (stickers, voice notes, …)
    private static readonly string[] SkipMediaSubfolders =
    { "sticker", "voice notes", "audio", "document", "profile photos", "animated gifs", "wallpaper", "backups", "databases" };

    /// Returns "WhatsApp"/"Telegram" if a file in parentPath with this extension is a
    /// photo/video belonging to those apps, else null.
    private static string? DetectAppMedia(string parentPath, string ext)
    {
        if (!MediaExts.Contains(ext)) return null;
        string p = parentPath.Replace('\\', '/') + "/";
        bool wa = p.StartsWith("Android/media/com.whatsapp/", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("WhatsApp/", StringComparison.OrdinalIgnoreCase);
        bool tg = (p.StartsWith("Android/media/", StringComparison.OrdinalIgnoreCase)
                   && p.Contains("telegram", StringComparison.OrdinalIgnoreCase))
               || p.StartsWith("Telegram/", StringComparison.OrdinalIgnoreCase);
        if (!wa && !tg) return null;
        foreach (var skip in SkipMediaSubfolders)
            if (p.Contains(skip, StringComparison.OrdinalIgnoreCase)) return null;
        return wa ? "WhatsApp" : "Telegram";
    }

    public void Run()
    {
        _shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic thisPc = _shell.NameSpace(SsfDrives);

        dynamic? device = null;
        foreach (dynamic item in thisPc.Items())
        {
            try
            {
                if ((bool)item.IsFolder && !(bool)item.IsFileSystem &&
                    string.Equals((string)item.Name, _opt.DeviceName, StringComparison.OrdinalIgnoreCase))
                { device = item; break; }
            }
            catch { }
        }
        if (device is null)
            throw new InvalidOperationException($"Device \"{_opt.DeviceName}\" not found. Is the phone plugged in and set to \"File Transfer\" mode?");

        var deviceDest = Path.Combine(_opt.DestRoot, Sanitize(_opt.DeviceName));
        Directory.CreateDirectory(Long(deviceDest));
        _indexPath = Path.Combine(_opt.DestRoot, ".phonebackup-index.json");
        LoadIndex();

        if (_opt.AvoidCrossBackupDuplicates)
        {
            try { _contentIndex = ContentIndex.LoadOrScan(_opt.DestRoot, Log, _ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log("WARNING: could not index the existing backup — duplicate/overwrite protection is limited this run: " + ex.Message); }
        }

        var sw = Stopwatch.StartNew();
        Log($"Backup started → {deviceDest}");

        try
        {
            dynamic deviceFolder = device.GetFolder;
            foreach (dynamic storage in deviceFolder.Items())
            {
                _ct.ThrowIfCancellationRequested();
                if (!(bool)storage.IsFolder) continue;
                string storageName = (string)storage.Name;
                Log($"Scanning storage: {storageName}");

                dynamic storageFolder = storage.GetFolder;
                var topItems = new List<dynamic>();
                foreach (dynamic t in storageFolder.Items()) topItems.Add(t);
                _stats.With(s => s.UnitsTotal += topItems.Count);

                foreach (dynamic top in topItems)
                {
                    _ct.ThrowIfCancellationRequested();
                    ProcessItem(top, Path.Combine(Sanitize(_opt.DeviceName), Sanitize(storageName)), "");
                    _stats.With(s => s.UnitsDone++);
                }
            }

            if (_opt.MirrorAppMedia && _uploads.Count > 0)
            {
                SaveIndex(); // checkpoint before the (potentially long) phone-copy phase
                _stats.With(s => s.UnitsTotal += _uploads.Count);
                MirrorToPhone(device);
            }

            sw.Stop();
            var snap = _stats.Snapshot();
            string summary = $"{snap.NewFiles} new, {snap.UpdatedFiles} updated, {snap.SkippedFiles} unchanged, " +
                $"{snap.ExcludedFiles} files + {snap.ExcludedFolders} folders excluded, " +
                $"{snap.DuplicatesAvoided} duplicates avoided" +
                (snap.Conflicts > 0 ? $", {snap.Conflicts} conflicts kept separate" : "") +
                $", {FormatBytes(snap.BytesCopied)} copied" + (snap.Errors > 0 ? $", {snap.Errors} errors" : "") + ".";
            Log($"Done in {FormatDuration(sw.Elapsed)} — {summary}");
            HistoryLog.Add("Backup", $"{_opt.DeviceName} — {summary}", $"Duration: {FormatDuration(sw.Elapsed)}\nDestination: {_opt.DestRoot}");
        }
        catch (OperationCanceledException)
        {
            Log("Backup cancelled. Progress so far was saved — the next run resumes where it left off.");
            var snap = _stats.Snapshot();
            HistoryLog.Add("Backup", $"{_opt.DeviceName} — stopped by user",
                $"{snap.NewFiles} new, {snap.UpdatedFiles} updated before stopping.\nDestination: {_opt.DestRoot}");
            throw;
        }
        catch (Exception ex)
        {
            HistoryLog.Add("Backup", $"{_opt.DeviceName} — failed: {ex.Message}", $"Destination: {_opt.DestRoot}");
            throw;
        }
        finally
        {
            // Always persist the index — a yanked cable or COM error mid-run must not
            // lose track of files that were already copied.
            SaveIndex();
            _contentIndex?.Save();
        }
    }

    private void ProcessItem(dynamic item, string relDir, string storagePath)
    {
        _ct.ThrowIfCancellationRequested();
        string name;
        bool isFolder;
        try
        {
            name = (string)item.Name;
            isFolder = (bool)item.IsFolder;
        }
        catch
        {
            _stats.With(s => s.Errors++);
            return;
        }

        if (isFolder && !LooksLikeArchive(name))
        {
            string subStoragePath = storagePath.Length == 0 ? name : storagePath + "/" + name;
            if (IsExcludedFolder(name, subStoragePath))
            {
                _stats.With(s => s.ExcludedFolders++);
                return;
            }
            dynamic sub;
            try { sub = item.GetFolder; }
            catch { _stats.With(s => s.Errors++); return; }

            string subRel = Path.Combine(relDir, Sanitize(name));
            _stats.With(s => s.CurrentItem = subRel);
            foreach (dynamic child in sub.Items())
                ProcessItem(child, subRel, subStoragePath);
        }
        else
        {
            ProcessFile(item, relDir, name, storagePath);
        }
    }

    private void ProcessFile(dynamic item, string relDir, string name, string storagePath)
    {
        _stats.With(s => s.FilesSeen++);
        string ext = Path.GetExtension(name);

        // Hidden files: .nomedia markers, .thumbdata, and Android 11+ ".trashed-*"
        // recycle-bin files (deleted photos waiting out their 30 days).
        if (_opt.SkipDotFolders && name.StartsWith('.'))
        {
            _stats.With(s => s.ExcludedFiles++);
            return;
        }

        if (_opt.ExcludedExtensions.Contains(ext))
        {
            _stats.With(s => s.ExcludedFiles++);
            return;
        }

        long size = GetSize(item);
        bool isDoc = _opt.DocumentExtensions.Contains(ext);
        // WhatsApp/Telegram photos are often under the size threshold but are real
        // user media — exempt them so they're backed up and mirrored to Pictures.
        string? app = _opt.MirrorAppMedia ? DetectAppMedia(storagePath, ext) : null;
        // size < 0 means the 32-bit fallback overflowed (file over 2 GB) — never treat that as "tiny"
        if (size >= 0 && size < _opt.MinSizeBytes && !(_opt.AlwaysKeepDocuments && isDoc) && app is null)
        {
            _stats.With(s => s.ExcludedFiles++);
            return;
        }

        DateTime mtime = GetMTime(item);
        string relPath = Path.Combine(relDir, Sanitize(name));
        string destPath = Path.Combine(_opt.DestRoot, relPath);

        if (_index.TryGetValue(relPath, out var entry) &&
            entry.Size == size &&
            (entry.MTimeTicks == mtime.Ticks || mtime == DateTime.MinValue))
        {
            string? trackedFull = entry.Suppressed ? null
                : entry.DuplicateOfPath != null ? Path.Combine(_opt.DestRoot, entry.DuplicateOfPath)
                : entry.ConflictPath != null ? Path.Combine(_opt.DestRoot, entry.ConflictPath)
                : destPath;

            if (entry.Suppressed || (trackedFull != null && File.Exists(Long(trackedFull))))
            {
                _stats.With(s => s.SkippedFiles++);
                if (app != null && !entry.Suppressed && trackedFull != null) _uploads.Add(new PendingUpload(app, trackedFull, size));
                return;
            }
        }

        // Only true when a PREVIOUS run of this app actually wrote a real file at
        // destPath itself (not a duplicate reference or a conflict-renamed copy) —
        // that's the one case where overwriting destPath is known to be safe.
        bool weOwnDestPath = entry != null && !entry.Suppressed && entry.DuplicateOfPath == null && entry.ConflictPath == null;
        bool existed = File.Exists(Long(destPath));
        _stats.With(s => s.CurrentItem = relPath);
        string partPath = destPath + ".part";
        try
        {
            Directory.CreateDirectory(Long(Path.GetDirectoryName(destPath)!));
            if (!TryStreamCopy(item, partPath, size))
                ShellCopyFallback(item, partPath, name, size);

            var promoted = PromoteStagedFile(partPath, destPath, relPath, size, weOwnDestPath);

            switch (promoted.Kind)
            {
                case PromoteKind.DuplicateElsewhere:
                    _index[relPath] = new IndexEntry { Size = size, MTimeTicks = mtime.Ticks, DuplicateOfPath = promoted.RefPath };
                    _stats.With(s => s.DuplicatesAvoided++);
                    Log($"Duplicate skipped: {relPath} — identical to existing {promoted.RefPath}");
                    if (app != null) _uploads.Add(new PendingUpload(app, Path.Combine(_opt.DestRoot, promoted.RefPath!), size));
                    break;

                case PromoteKind.AlreadyAtDest:
                    _index[relPath] = new IndexEntry { Size = size, MTimeTicks = mtime.Ticks };
                    _stats.With(s => s.DuplicatesAvoided++);
                    if (app != null) _uploads.Add(new PendingUpload(app, destPath, size));
                    break;

                case PromoteKind.Conflict:
                    {
                        string altFull = Path.Combine(_opt.DestRoot, promoted.RefPath!);
                        if (mtime != DateTime.MinValue) { try { File.SetLastWriteTime(Long(altFull), mtime); } catch { } }
                        _index[relPath] = new IndexEntry { Size = size, MTimeTicks = mtime.Ticks, ConflictPath = promoted.RefPath };
                        _stats.With(s => s.Conflicts++);
                        Log($"CONFLICT: {relPath} already existed at the destination with different content — " +
                            $"kept the existing file untouched and saved the phone's version as {promoted.RefPath}");
                        if (app != null) _uploads.Add(new PendingUpload(app, altFull, size));
                        break;
                    }

                case PromoteKind.Written:
                default:
                    if (mtime != DateTime.MinValue) { try { File.SetLastWriteTime(Long(destPath), mtime); } catch { } }
                    _index[relPath] = new IndexEntry { Size = size, MTimeTicks = mtime.Ticks };
                    _stats.With(s => { if (existed) s.UpdatedFiles++; else s.NewFiles++; });
                    if (app != null) _uploads.Add(new PendingUpload(app, destPath, size));
                    break;
            }

            if (Interlocked.Increment(ref _indexDirty) % 100 == 0) SaveIndex();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _stats.With(s => s.Errors++);
            Log($"ERROR  {relPath}: {ex.Message}");
            try { if (File.Exists(Long(partPath))) File.Delete(Long(partPath)); } catch { }
        }
    }

    // ---- Duplicate / overwrite protection ----

    internal enum PromoteKind { Written, DuplicateElsewhere, AlreadyAtDest, Conflict }
    internal sealed record PromoteResult(PromoteKind Kind, string? RefPath);

    /// Decides what to do with a file that has just been staged at `partPath`, then
    /// does it. Never overwrites a file at `destPath` unless `weOwnDestPath` is true
    /// (meaning this app itself put a file there before) — anything else sitting at
    /// destPath is treated as foreign and left alone. Also checks the whole destination
    /// tree for identical content before storing a second physical copy.
    /// Internal (not private) so the test suite can exercise this logic directly,
    /// since it depends only on local files and never touches the phone.
    internal PromoteResult PromoteStagedFile(string partPath, string destPath, string relPath, long size, bool weOwnDestPath)
    {
        string? stagedHash = null;
        string ComputeStagedHash() => stagedHash ??= Hashing.HashFile(Long(partPath), -1, _ct);

        if (_contentIndex != null)
        {
            foreach (var candidateRel in _contentIndex.CandidatesForSize(size, relPath))
            {
                _ct.ThrowIfCancellationRequested();
                string candHash;
                try { candHash = _contentIndex.GetOrComputeHash(candidateRel, _ct); }
                catch (OperationCanceledException) { throw; }
                catch { continue; } // candidate unreadable/missing — skip it, don't abort the whole check

                if (candHash == ComputeStagedHash())
                {
                    try { File.Delete(Long(partPath)); } catch { }
                    if (weOwnDestPath) { try { File.Delete(Long(destPath)); } catch { } } // supersede our own stale copy
                    return new PromoteResult(PromoteKind.DuplicateElsewhere, candidateRel);
                }
            }
        }

        bool existsPhysically = File.Exists(Long(destPath));
        if (!weOwnDestPath && existsPhysically)
        {
            string existingHash;
            try { existingHash = Hashing.HashFile(Long(destPath), -1, _ct); }
            catch (OperationCanceledException) { throw; }
            catch { existingHash = ""; } // can't verify -> never assume it's safe to overwrite

            if (existingHash.Length > 0 && existingHash == ComputeStagedHash())
            {
                try { File.Delete(Long(partPath)); } catch { }
                return new PromoteResult(PromoteKind.AlreadyAtDest, relPath);
            }

            string altPath = MakeConflictPath(destPath);
            File.Move(Long(partPath), Long(altPath));
            string altRel = Path.GetRelativePath(Path.GetFullPath(_opt.DestRoot), altPath);
            _contentIndex?.Register(altRel, size, stagedHash);
            return new PromoteResult(PromoteKind.Conflict, altRel);
        }

        File.Move(Long(partPath), Long(destPath), overwrite: true);
        _contentIndex?.Register(relPath, size, stagedHash);
        return new PromoteResult(PromoteKind.Written, relPath);
    }

    /// Test-only hook: lets the test suite inject a pre-built ContentIndex without
    /// running a full backup, so PromoteStagedFile's logic can be verified directly.
    internal void AttachContentIndexForTests(ContentIndex index) => _contentIndex = index;

    private static string MakeConflictPath(string destPath)
    {
        string dir = Path.GetDirectoryName(destPath)!;
        string name = Path.GetFileNameWithoutExtension(destPath);
        string ext = Path.GetExtension(destPath);
        string candidate = Path.Combine(dir, $"{name} (from phone){ext}");
        int i = 2;
        while (File.Exists(Long(candidate)) || Directory.Exists(Long(candidate)))
            candidate = Path.Combine(dir, $"{name} (from phone {i++}){ext}");
        return candidate;
    }

    /// Downloads the phone file to `stagePath` (a temp ".part" path — never the final
    /// destination). Promotion to the real destination happens separately in
    /// PromoteStagedFile, after the duplicate/overwrite checks.
    private bool TryStreamCopy(dynamic item, string stagePath, long expectedSize)
    {
        string parsingPath;
        try { parsingPath = (string)item.Path; }
        catch { return false; }

        IShellItem? si = null;
        ComIStream? stream = null;
        IntPtr pcb = IntPtr.Zero;
        try
        {
            if (SHCreateItemFromParsingName(parsingPath, IntPtr.Zero, ref _iidShellItem, out si) != 0 || si is null)
                return false;
            if (si.BindToHandler(IntPtr.Zero, ref _bhidStream, ref _iidIStream, out stream) != 0 || stream is null)
                return false;

            pcb = Marshal.AllocHGlobal(8);
            var buffer = new byte[1024 * 1024];
            using (var fs = new FileStream(Long(stagePath), FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 256))
            {
                while (true)
                {
                    _ct.ThrowIfCancellationRequested();
                    stream.Read(buffer, buffer.Length, pcb);
                    int read = Marshal.ReadInt32(pcb);
                    if (read <= 0) break;
                    fs.Write(buffer, 0, read);
                    _stats.With(s => s.BytesCopied += read);
                }
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(Long(stagePath)); } catch { }
            throw;
        }
        catch
        {
            try { File.Delete(Long(stagePath)); } catch { }
            return false;
        }
        finally
        {
            if (pcb != IntPtr.Zero) Marshal.FreeHGlobal(pcb);
            if (stream != null) Marshal.ReleaseComObject(stream);
            if (si != null) Marshal.ReleaseComObject(si);
        }
    }

    /// Slow but reliable fallback: let the Windows copy engine do it, then wait for the
    /// file, landing it at `stagePath` (same staging contract as TryStreamCopy).
    private void ShellCopyFallback(dynamic item, string stagePath, string originalName, long expectedSize)
    {
        string destDir = Path.GetDirectoryName(stagePath)!;
        dynamic destFolder = _shell.NameSpace(destDir)
            ?? throw new IOException("Cannot open destination folder: " + destDir);

        // 4: no progress UI, 16: yes-to-all, 512: no confirm mkdir, 1024: no error UI
        destFolder.CopyHere(item, 4 | 16 | 512 | 1024);

        string landed = Path.Combine(destDir, originalName);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
        bool sizeKnown = expectedSize > 0;
        long lastSize = -1;
        int stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            _ct.ThrowIfCancellationRequested();
            if (File.Exists(landed))
            {
                long sz = new FileInfo(landed).Length;
                if (sizeKnown && sz == expectedSize) break;
                if (!sizeKnown)
                {
                    // size unknown: fall back to "stopped growing for 3s"
                    stable = sz == lastSize && sz > 0 ? stable + 1 : 0;
                    lastSize = sz;
                    if (stable > 30) break;
                }
            }
            Thread.Sleep(100);
        }
        if (!File.Exists(landed))
            throw new IOException("Copy did not complete.");
        if (sizeKnown && new FileInfo(landed).Length != expectedSize)
        {
            // A partial file must never be accepted: the index would record it as done
            // and the corrupt copy would be skipped as "unchanged" on every later run.
            try { File.Delete(landed); } catch { }
            throw new IOException("Copy did not complete (size mismatch).");
        }
        if (!string.Equals(landed, stagePath, StringComparison.OrdinalIgnoreCase))
            File.Move(Long(landed), Long(stagePath), overwrite: true); // overwriting our own staging file is safe
        _stats.With(s => s.BytesCopied += Math.Max(0, expectedSize));
    }

    // ---- Phase 2: copy WhatsApp/Telegram media into the phone's Pictures folder ----

    private void MirrorToPhone(dynamic device)
    {
        Log($"Phone copy: checking {_uploads.Count} WhatsApp/Telegram media files against Pictures…");

        dynamic? picturesItem = null;
        dynamic deviceFolder = device.GetFolder;
        foreach (dynamic storage in deviceFolder.Items())
        {
            if (!(bool)storage.IsFolder) continue;
            try { picturesItem = storage.GetFolder.ParseName("Pictures"); } catch { }
            if (picturesItem != null) break;
        }
        if (picturesItem == null)
        {
            Log("WARNING: no Pictures folder found on the phone — skipped the phone-side copy.");
            return;
        }
        dynamic picturesFolder = picturesItem.GetFolder;

        foreach (var group in _uploads.GroupBy(u => u.App))
        {
            _ct.ThrowIfCancellationRequested();

            dynamic? subItem = null;
            try { subItem = picturesFolder.ParseName(group.Key); } catch { }
            if (subItem == null && !TryCreatePhoneFolder(picturesFolder, group.Key, out subItem))
            {
                Log($"WARNING: could not create Pictures/{group.Key} on the phone.");
                continue;
            }
            dynamic subFolder = subItem!.GetFolder;

            var existing = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic ex in subFolder.Items())
            {
                try { if (!(bool)ex.IsFolder) existing[(string)ex.Name] = GetSize(ex); } catch { }
            }

            int done = 0, already = 0, failed = 0;
            foreach (var u in group)
            {
                _ct.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(u.PcPath);
                if (existing.TryGetValue(fileName, out long sz) && sz == u.Size)
                {
                    already++;
                    _stats.With(s => s.UnitsDone++);
                    continue;
                }
                _stats.With(s => s.CurrentItem = $"Phone ← Pictures\\{group.Key}\\{fileName}");
                try
                {
                    dynamic? localItem = _shell.NameSpace(Path.GetDirectoryName(u.PcPath))?.ParseName(fileName);
                    if (localItem == null) throw new IOException("backup copy not found on PC");
                    subFolder.CopyHere(localItem, 4 | 16 | 512 | 1024);
                    WaitForMtpFile(subFolder, fileName, u.Size);
                    existing[fileName] = u.Size;
                    _stats.With(s => s.BytesCopied += u.Size);
                    done++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex2)
                {
                    failed++;
                    _stats.With(s => s.Errors++);
                    Log($"ERROR copying {fileName} to phone: {ex2.Message}");
                }
                _stats.With(s => s.UnitsDone++);
            }
            Log($"Pictures/{group.Key}: {done} copied to phone, {already} already there" +
                (failed > 0 ? $", {failed} failed" : "") + ".");
        }
    }

    private bool TryCreatePhoneFolder(dynamic parentFolder, string name, out dynamic? created)
    {
        created = null;
        try
        {
            string stage = Path.Combine(Path.GetTempPath(), "PhoneBackupStage");
            Directory.CreateDirectory(Path.Combine(stage, name)); // empty folder to copy over
            dynamic? stageItem = _shell.NameSpace(stage)?.ParseName(name);
            if (stageItem == null) return false;
            parentFolder.CopyHere(stageItem, 4 | 16 | 512 | 1024);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (created == null && DateTime.UtcNow < deadline)
            {
                _ct.ThrowIfCancellationRequested();
                Thread.Sleep(250);
                try { created = parentFolder.ParseName(name); } catch { }
            }
            return created != null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private void WaitForMtpFile(dynamic folder, string name, long expectedSize)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
        long lastSize = -1;
        int stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            _ct.ThrowIfCancellationRequested();
            dynamic? it = null;
            try { it = folder.ParseName(name); } catch { }
            if (it != null)
            {
                long sz = GetSize(it);
                if (sz == expectedSize) return;
                stable = sz == lastSize ? stable + 1 : 0;
                lastSize = sz;
                if (stable > 40) return; // size stopped changing; accept
            }
            Thread.Sleep(250);
        }
        throw new IOException("timed out waiting for the file to land on the phone");
    }

    // ---- Filters ----

    private bool IsExcludedFolder(string name, string storagePath)
    {
        if (_nameExcludes.Contains(name)) return true;
        if (_pathExcludes.Contains(storagePath)) return true;
        if (_opt.SkipDotFolders && name.StartsWith('.')) return true;
        return false;
    }

    private static readonly string[] ArchiveExts = { ".zip", ".rar", ".7z", ".jar", ".cab" };
    private static bool LooksLikeArchive(string name) =>
        ArchiveExts.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    // ---- Item properties ----

    private static long GetSize(dynamic item)
    {
        try
        {
            object? v = item.ExtendedProperty("System.Size");
            if (v != null) return Convert.ToInt64(v);
        }
        catch { }
        // FolderItem.Size is a 32-bit int — files over 2 GB come back negative.
        // Callers treat negative as "size unknown, assume large".
        try { return (long)(int)item.Size; } catch { }
        return 0;
    }

    private static DateTime GetMTime(dynamic item)
    {
        try { return (DateTime)item.ModifyDate; } catch { }
        try
        {
            object? v = item.ExtendedProperty("System.DateModified");
            if (v is DateTime dt) return dt;
        }
        catch { }
        return DateTime.MinValue;
    }

    // ---- Index ----

    private void LoadIndex()
    {
        try
        {
            if (File.Exists(_indexPath))
                _index = JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(File.ReadAllText(_indexPath))
                         ?? new Dictionary<string, IndexEntry>();
        }
        catch { _index = new Dictionary<string, IndexEntry>(); }
        if (_index.Count > 0) Log($"Loaded backup index: {_index.Count} files already tracked.");
    }

    private void SaveIndex()
    {
        try
        {
            File.WriteAllText(_indexPath, JsonSerializer.Serialize(_index));
        }
        catch (Exception ex) { Log("WARNING: could not save index: " + ex.Message); }
    }

    // ---- Helpers ----

    private void Log(string msg) => _stats.Log.Enqueue($"[{DateTime.Now:HH:mm:ss}] {msg}");

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();
    private static string Sanitize(string name)
    {
        var s = new string(name.Select(c => InvalidChars.Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        if (s.Length == 0) s = "_";
        return s;
    }

    /// \\?\ prefix so paths over 260 chars work regardless of registry policy.
    private static string Long(string path) =>
        path.StartsWith(@"\\?\") ? path : @"\\?\" + Path.GetFullPath(path);

    public static string FormatBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";
}
