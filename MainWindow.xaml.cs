using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhoneBackup;

public class CleanRow
{
    public CleanCategory Category = null!;
    public string Name => Category.Name;
    public string Description => Category.Description;
    public string CountText => Category.Count.ToString("N0");
    public string SizeText => BackupEngine.FormatBytes(Category.Bytes);
    public bool IsChecked { get; set; } = true;
}

public class HistoryRow
{
    public HistoryEntry Entry = null!;
    public string WhenText => Entry.Timestamp.ToString("yyyy-MM-dd HH:mm");
    public string Kind => Entry.Kind;
    public string Summary => Entry.Summary;
}

public partial class MainWindow : Window
{
    private AppSettings _settings = AppSettings.Load();
    private List<DeviceInfo> _devices = new();
    private CancellationTokenSource? _cts;
    private LiveStats? _stats;
    private Thread? _worker;
    private readonly DispatcherTimer _uiTimer;
    private long _lastBytes;
    private DateTime _lastTick = DateTime.UtcNow;

    private CancellationTokenSource? _dupCts;
    private ObservableCollection<DupRow> _dupRows = new();
    private bool _dupRunning;
    private bool _dupBulkUpdate;
    private string _dupScanRoot = "";

    private CancellationTokenSource? _cleanCts;
    private List<CleanRow> _cleanRows = new();
    private bool _cleanRunning;
    private string _cleanScanRoot = "";

    private static readonly (string Label, long Bytes)[] SizeOptions =
    {
        ("Off", 0),
        ("100 KB", 100 * 1024),
        ("512 KB", 512 * 1024),
        ("1 MB", 1024 * 1024),
        ("5 MB", 5 * 1024 * 1024),
    };

    public MainWindow()
    {
        InitializeComponent();

        foreach (var (label, _) in SizeOptions)
        {
            MinSizeCombo.Items.Add(label);
            DupMinSizeCombo.Items.Add(label);
        }
        SetSizeCombo(MinSizeCombo, _settings.MinSizeBytes);
        SetSizeCombo(DupMinSizeCombo, _settings.DupMinSizeBytes);

        foreach (var label in new[]
        {
            "Keep oldest copy (original)",
            "Keep newest copy",
            "Keep copy in DCIM / Camera",
            "Keep copy highest in folder tree (shortest path)",
            "Keep copy deepest in folder tree (longest path)",
            "Keep copy with the shortest filename",
            "Keep one copy per device folder",
            "Prefer a folder — keep its copies, select the rest…",
            "Select copies inside a folder…",
        }) DupStrategyCombo.Items.Add(label);
        DupStrategyCombo.SelectedIndex = Math.Clamp(_settings.DupStrategyIndex, 0, DupStrategyCombo.Items.Count - 1);
        DupSameFolderCheck.IsChecked = _settings.DupSameFolderOnly;

        KeepDocsCheck.IsChecked = _settings.AlwaysKeepDocuments;
        DotFoldersCheck.IsChecked = _settings.SkipDotFolders;
        MirrorCheck.IsChecked = _settings.MirrorAppMediaToPictures;
        AvoidDupesCheck.IsChecked = _settings.AvoidCrossBackupDuplicates;
        ExcludedFoldersBox.Text = _settings.ExcludedFolders;
        DestText.Text = _settings.DestinationRoot;
        DupFolderText.Text = _settings.DestinationRoot;
        CleanFolderText.Text = _settings.DestinationRoot;
        DupList.ItemsSource = _dupRows;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTimer.Tick += (_, _) => UpdateProgressUi();

        Loaded += (_, _) => { RefreshDevices(); RefreshHistory(); };
    }

    // ---- size threshold parsing (editable combos accept presets or custom text) ----

    private static void SetSizeCombo(ComboBox cb, long bytes) =>
        cb.Text = bytes <= 0 ? "Off" : BackupEngine.FormatBytes(bytes);

    private static long ReadSize(ComboBox cb, long fallback) => ParseSize(cb.Text) ?? fallback;

    /// Accepts "Off", "0", "500 KB", "2 MB", "1.5gb", or a bare number (treated as MB).
    private static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Equals("Off", StringComparison.OrdinalIgnoreCase)) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(
            text, @"^([\d]+(?:[.,]\d+)?)\s*(b|kb|mb|gb|tb)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        double num = double.Parse(m.Groups[1].Value.Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture);
        long mult = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "b" => 1L,
            "kb" => 1024L,
            "gb" => 1024L * 1024 * 1024,
            "tb" => 1024L * 1024 * 1024 * 1024,
            _ => 1024L * 1024, // "mb" or no unit -> MB
        };
        return (long)Math.Round(num * mult);
    }

    private static string? PickFolder(Window owner, string current)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder",
            InitialDirectory = Directory.Exists(current)
                ? current
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        return dlg.ShowDialog(owner) == true ? dlg.FolderName : null;
    }

    // ================= BACKUP TAB =================

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        RefreshBtn.IsEnabled = false;
        DeviceStatus.Text = "Searching for devices…";
        var t = new Thread(() =>
        {
            List<DeviceInfo> found;
            try { found = BackupEngine.FindDevices(); }
            catch { found = new List<DeviceInfo>(); }
            Dispatcher.Invoke(() =>
            {
                _devices = found;
                DeviceCombo.Items.Clear();
                foreach (var d in found) DeviceCombo.Items.Add(d);
                if (found.Count == 0)
                {
                    DeviceStatus.Text = "No phone found. Plug it in via USB and choose \"File Transfer\" on the phone, then Refresh.";
                }
                else
                {
                    int sel = found.FindIndex(d => d.Name.Equals(_settings.LastDeviceName, StringComparison.OrdinalIgnoreCase));
                    DeviceCombo.SelectedIndex = sel >= 0 ? sel : 0;
                }
                RefreshBtn.IsEnabled = true;
            });
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is DeviceInfo d)
            DeviceStatus.Text = $"Connected — storage: {string.Join(", ", d.Storages)}";
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var f = PickFolder(this, _settings.DestinationRoot);
        if (f != null)
        {
            _settings.DestinationRoot = f;
            DestText.Text = f;
            if (string.IsNullOrWhiteSpace(DupFolderText.Text)) DupFolderText.Text = f;
            if (string.IsNullOrWhiteSpace(CleanFolderText.Text)) CleanFolderText.Text = f;
        }
    }

    private void BackupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not DeviceInfo device)
        {
            MessageBox.Show(this, "No phone selected. Plug it in via USB, set it to \"File Transfer\" mode, and hit Refresh.",
                "OnePlus Phone Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(DestText.Text))
        {
            MessageBox.Show(this, "Choose a backup folder first.", "OnePlus Phone Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _settings.LastDeviceName = device.Name;
        _settings.DestinationRoot = DestText.Text;
        _settings.MinSizeBytes = ReadSize(MinSizeCombo, _settings.MinSizeBytes);
        _settings.AlwaysKeepDocuments = KeepDocsCheck.IsChecked == true;
        _settings.SkipDotFolders = DotFoldersCheck.IsChecked == true;
        _settings.MirrorAppMediaToPictures = MirrorCheck.IsChecked == true;
        _settings.AvoidCrossBackupDuplicates = AvoidDupesCheck.IsChecked == true;
        _settings.ExcludedFolders = ExcludedFoldersBox.Text;
        _settings.Save();

        var opt = new BackupOptions
        {
            DeviceName = device.Name,
            DestRoot = _settings.DestinationRoot,
            MinSizeBytes = _settings.MinSizeBytes,
            AlwaysKeepDocuments = _settings.AlwaysKeepDocuments,
            SkipDotFolders = _settings.SkipDotFolders,
            MirrorAppMedia = _settings.MirrorAppMediaToPictures,
            AvoidCrossBackupDuplicates = _settings.AvoidCrossBackupDuplicates,
            ExcludedFolders = AppSettings.ParseList(_settings.ExcludedFolders),
            ExcludedExtensions = AppSettings.ParseList(_settings.ExcludedExtensions),
            DocumentExtensions = AppSettings.ParseList(_settings.DocumentExtensions),
        };

        _stats = new LiveStats();
        _cts = new CancellationTokenSource();
        _lastBytes = 0;
        _lastTick = DateTime.UtcNow;
        LogBox.Clear();
        Progress.Value = 0;
        SetBackupRunning(true);
        StatusText.Text = "Backing up…";

        var engine = new BackupEngine(opt, _stats, _cts.Token);
        _worker = new Thread(() =>
        {
            string finalStatus;
            try
            {
                engine.Run();
                finalStatus = "Backup complete.";
            }
            catch (OperationCanceledException)
            {
                finalStatus = "Stopped.";
            }
            catch (Exception ex)
            {
                _stats.Log.Enqueue("ERROR: " + ex.Message);
                finalStatus = "Failed — see log.";
            }
            Dispatcher.Invoke(() =>
            {
                UpdateProgressUi();
                _uiTimer.Stop();
                SetBackupRunning(false);
                StatusText.Text = finalStatus;
                if (finalStatus == "Backup complete.") Progress.Value = 100;
                RefreshHistory();
            });
        });
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.IsBackground = true;
        _worker.Start();
        _uiTimer.Start();
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
        StatusText.Text = "Stopping…";
    }

    private bool _backupRunning;
    private void SetBackupRunning(bool running)
    {
        _backupRunning = running;
        BackupBtn.IsEnabled = !running;
        StopBtn.IsEnabled = running;
        RefreshBtn.IsEnabled = !running;
        BrowseBtn.IsEnabled = !running;
        DeviceCombo.IsEnabled = !running;
        MinSizeCombo.IsEnabled = !running;
        KeepDocsCheck.IsEnabled = !running;
        DotFoldersCheck.IsEnabled = !running;
        MirrorCheck.IsEnabled = !running;
        AvoidDupesCheck.IsEnabled = !running;
        ExcludedFoldersBox.IsEnabled = !running;
        UpdateCrossLocks();
    }

    /// Don't let cleanup/dedupe delete files while a backup is writing them (and vice versa).
    private void UpdateCrossLocks()
    {
        DupScanBtn.IsEnabled = !_backupRunning && !_dupRunning;
        DupDeleteBtn.IsEnabled = !_backupRunning && !_dupRunning && _dupRows.Any(r => r.IsChecked);
        DupAutoBtn.IsEnabled = !_dupRunning && _dupRows.Count > 0;
        DupInvertBtn.IsEnabled = !_dupRunning && _dupRows.Count > 0;
        DupClearBtn.IsEnabled = !_dupRunning && _dupRows.Count > 0;
        CleanScanBtn.IsEnabled = !_backupRunning && !_cleanRunning;
        CleanDeleteBtn.IsEnabled = !_backupRunning && !_cleanRunning && _cleanRows.Any(r => r.IsChecked);
        BackupBtn.IsEnabled = !_backupRunning && !_dupRunning && !_cleanRunning;
    }

    private void UpdateProgressUi()
    {
        if (_stats is null) return;
        var s = _stats.Snapshot();

        StatNew.Text = $"{s.NewFiles:N0} new";
        StatUpdated.Text = $"{s.UpdatedFiles:N0} updated";
        StatSkipped.Text = $"{s.SkippedFiles:N0} unchanged";
        StatExcluded.Text = $"{s.ExcludedFiles:N0} files + {s.ExcludedFolders:N0} folders excluded";
        StatDuplicates.Text = $"{s.DuplicatesAvoided:N0} duplicates avoided";
        StatConflicts.Text = s.Conflicts > 0 ? $"{s.Conflicts:N0} conflicts kept separate" : "";
        StatData.Text = BackupEngine.FormatBytes(s.BytesCopied) + " copied";
        CurrentFileText.Text = s.CurrentItem;

        var now = DateTime.UtcNow;
        double secs = (now - _lastTick).TotalSeconds;
        if (secs >= 0.8)
        {
            double rate = (s.BytesCopied - _lastBytes) / secs;
            StatSpeed.Text = rate > 1024 ? BackupEngine.FormatBytes((long)rate) + "/s" : "";
            _lastBytes = s.BytesCopied;
            _lastTick = now;
        }

        if (s.UnitsTotal > 0)
            Progress.Value = Math.Min(99.0, 100.0 * s.UnitsDone / s.UnitsTotal);

        bool appended = false;
        while (_stats.Log.TryDequeue(out var line))
        {
            LogBox.AppendText(line + Environment.NewLine);
            appended = true;
        }
        if (appended) LogBox.ScrollToEnd();
    }

    // ================= CLEANER TAB =================

    private void CleanBrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var f = PickFolder(this, CleanFolderText.Text);
        if (f != null) CleanFolderText.Text = f;
    }

    private void CleanScanBtn_Click(object sender, RoutedEventArgs e)
    {
        string root = CleanFolderText.Text;
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Choose a folder to clean first.", "OnePlus Phone Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rules = new Cleaner.Rules
        {
            MinSizeBytes = ReadSize(MinSizeCombo, _settings.MinSizeBytes),
            AlwaysKeepDocuments = KeepDocsCheck.IsChecked == true,
            SkipDotEntries = DotFoldersCheck.IsChecked == true,
            ExcludedFolders = AppSettings.ParseList(ExcludedFoldersBox.Text),
            ExcludedExtensions = AppSettings.ParseList(_settings.ExcludedExtensions),
            DocumentExtensions = AppSettings.ParseList(_settings.DocumentExtensions),
        };

        _cleanScanRoot = root;
        _cleanCts = new CancellationTokenSource();
        _cleanRunning = true;
        CleanCancelBtn.IsEnabled = true;
        CleanList.ItemsSource = null;
        _cleanRows = new List<CleanRow>();
        CleanPreview.Text = "";
        CleanSummaryText.Text = "";
        UpdateCrossLocks();
        CleanStatusText.Text = "Scanning…";

        var token = _cleanCts.Token;
        Task.Run(() =>
        {
            try
            {
                var cats = Cleaner.Scan(root, rules,
                    msg => Dispatcher.BeginInvoke(() => CleanStatusText.Text = msg),
                    token);
                Dispatcher.Invoke(() =>
                {
                    _cleanRows = cats.Select(c => new CleanRow { Category = c }).ToList();
                    CleanList.ItemsSource = _cleanRows;
                    long bytes = cats.Sum(c => c.Bytes);
                    int items = cats.Sum(c => c.Count);
                    CleanStatusText.Text = items == 0
                        ? "Nothing to clean — this folder is already tidy."
                        : $"Found {items:N0} junk items ({BackupEngine.FormatBytes(bytes)}). Review the categories, then delete.";
                    UpdateCleanSummary();
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => CleanStatusText.Text = "Scan stopped.");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => CleanStatusText.Text = "Scan failed: " + ex.Message);
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    _cleanRunning = false;
                    CleanCancelBtn.IsEnabled = false;
                    UpdateCrossLocks();
                });
            }
        });
    }

    private void CleanCancelBtn_Click(object sender, RoutedEventArgs e) => _cleanCts?.Cancel();

    private void CleanCheck_Changed(object sender, RoutedEventArgs e) => UpdateCleanSummary();

    private void UpdateCleanSummary()
    {
        var sel = _cleanRows.Where(r => r.IsChecked).ToList();
        long bytes = sel.Sum(r => r.Category.Bytes);
        int items = sel.Sum(r => r.Category.Count);
        CleanSummaryText.Text = items > 0 ? $"Selected: {items:N0} items, {BackupEngine.FormatBytes(bytes)}" : "";
        UpdateCrossLocks();
    }

    private void CleanList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CleanList.SelectedItem is not CleanRow row) return;
        var sample = row.Category.Paths.Take(500);
        string text = string.Join(Environment.NewLine, sample);
        if (row.Category.Count > 500) text += Environment.NewLine + $"… and {row.Category.Count - 500:N0} more";
        CleanPreview.Text = text;
    }

    private void CleanDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        var sel = _cleanRows.Where(r => r.IsChecked).ToList();
        var paths = sel.SelectMany(r => r.Category.Paths).ToList();
        if (paths.Count == 0) return;
        long bytes = sel.Sum(r => r.Category.Bytes);
        // use the folder that was actually scanned, not whatever the textbox shows now
        string root = Directory.Exists(_cleanScanRoot) ? _cleanScanRoot : CleanFolderText.Text;

        var res = MessageBox.Show(this,
            $"Move {paths.Count:N0} items ({BackupEngine.FormatBytes(bytes)}) to the \"delete\" folder?\n\n" +
            $"They are moved (with their folder structure) to:\n{Path.Combine(root, DeleteVault.FolderName)}\n\n" +
            "Nothing is destroyed — review the folder and empty it when you're sure.",
            "Clean backup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;

        // Drop paths nested inside other selected paths — moving the parent covers them.
        var roots = PruneNested(paths);
        var moved = DeleteVault.MoveToDelete(root, roots);
        int suppressed = DuplicateFinder.MarkSuppressedInBackupIndex(_settings.DestinationRoot, paths);

        _cleanRows = _cleanRows.Where(r => !r.IsChecked).ToList();
        CleanList.ItemsSource = null;
        CleanList.ItemsSource = _cleanRows;
        CleanPreview.Text = "";
        CleanStatusText.Text = $"Moved {moved.Count:N0} items ({BackupEngine.FormatBytes(bytes)}) to {DeleteVault.FolderName}\\" +
                               (suppressed > 0 ? " — the backup won't re-copy them from the phone." : ".");
        UpdateCleanSummary();

        HistoryLog.Add("Clean", $"Moved {moved.Count:N0} items ({BackupEngine.FormatBytes(bytes)}) to the delete folder",
            $"Folder: {root}\nCategories: {string.Join(", ", sel.Select(r => r.Name))}");
        RefreshHistory();
    }

    private static List<string> PruneNested(List<string> paths)
    {
        // Sorted so children come right after their ancestor; comparing each path
        // only against the last kept root keeps this O(n log n). A nested path that
        // slips through is harmless: MoveToDelete skips sources that no longer exist.
        var sorted = paths.Select(Path.GetFullPath)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                          .ToList();
        var roots = new List<string>();
        string? current = null;
        foreach (var p in sorted)
        {
            if (current != null &&
                p.Length > current.Length &&
                p[current.Length] == '\\' &&
                p.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                continue;
            roots.Add(p);
            current = p;
        }
        return roots;
    }

    // ================= DUPLICATES TAB =================

    private void DupBrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var f = PickFolder(this, DupFolderText.Text);
        if (f != null) DupFolderText.Text = f;
    }

    private void DupScanBtn_Click(object sender, RoutedEventArgs e)
    {
        string root = DupFolderText.Text;
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Choose a folder to search first.", "OnePlus Phone Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        long min = ReadSize(DupMinSizeCombo, _settings.DupMinSizeBytes);
        bool sameFolderOnly = DupSameFolderCheck.IsChecked == true;
        _dupScanRoot = root;

        // persist duplicate-tab preferences
        _settings.DupMinSizeBytes = min;
        _settings.DupSameFolderOnly = sameFolderOnly;
        _settings.DupStrategyIndex = DupStrategyCombo.SelectedIndex;
        _settings.Save();

        _dupCts = new CancellationTokenSource();
        _dupRunning = true;
        DupCancelBtn.IsEnabled = true;
        SetDupRows(new List<DupRow>());
        DupProgress.Value = 0;
        UpdateCrossLocks();

        var token = _dupCts.Token;
        Task.Run(() =>
        {
            try
            {
                var groups = DuplicateFinder.Scan(root, min, sameFolderOnly,
                    (stage, pct) => Dispatcher.BeginInvoke(() => { DupStatusText.Text = stage; DupProgress.Value = pct; }),
                    token);
                Dispatcher.Invoke(() => ShowDupResults(root, groups));
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => { DupStatusText.Text = "Scan stopped."; DupProgress.Value = 0; });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => DupStatusText.Text = "Scan failed: " + ex.Message);
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    _dupRunning = false;
                    DupCancelBtn.IsEnabled = false;
                    UpdateCrossLocks();
                });
            }
        });
    }

    private void DupCancelBtn_Click(object sender, RoutedEventArgs e) => _dupCts?.Cancel();

    private static readonly Brush ShadeA = Brushes.Transparent;
    private static readonly Brush ShadeB = MakeShade();
    private static Brush MakeShade()
    {
        var b = new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFC));
        b.Freeze();
        return b;
    }

    private void ShowDupResults(string root, List<DupGroup> groups)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var rows = new List<DupRow>();
        int gi = 0;
        foreach (var g in groups)
        {
            gi++;
            foreach (var f in g.Files)
            {
                rows.Add(new DupRow
                {
                    Path = f.Path,
                    Display = f.Path.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                        ? f.Path.Substring(rootFull.Length) : f.Path,
                    Size = f.Size,
                    SizeText = BackupEngine.FormatBytes(f.Size),
                    Modified = f.Modified,
                    ModifiedText = f.Modified == DateTime.MinValue ? "" : f.Modified.ToString("yyyy-MM-dd HH:mm"),
                    Group = gi,
                    Shade = gi % 2 == 0 ? ShadeB : ShadeA,
                });
            }
        }
        SetDupRows(rows);
        DupProgress.Value = rows.Count > 0 ? 100 : 0;

        long wasted = groups.Sum(g => g.Wasted);
        int dupCount = groups.Sum(g => g.Files.Count - 1);
        DupStatusText.Text = groups.Count == 0
            ? "No duplicates found."
            : $"{groups.Count:N0} groups, {dupCount:N0} redundant copies, {BackupEngine.FormatBytes(wasted)} wasted. " +
              "Use Auto-select to keep one of each.";
    }

    private void SetDupRows(List<DupRow> rows)
    {
        _dupRows = new ObservableCollection<DupRow>(rows);
        foreach (var r in _dupRows)
            r.PropertyChanged += (_, _) => { if (!_dupBulkUpdate) UpdateDupButtons(); };
        DupList.ItemsSource = _dupRows;
        UpdateDupButtons();
    }

    private void UpdateDupButtons()
    {
        int n = _dupRows.Count(r => r.IsChecked);
        DupDeleteBtn.Content = n > 0 ? $"Move selected to delete folder ({n:N0})" : "Move selected to delete folder";
        UpdateCrossLocks();
    }

    private void BulkSelect(Action body)
    {
        _dupBulkUpdate = true;
        try { body(); }
        finally { _dupBulkUpdate = false; UpdateDupButtons(); }
    }

    private void DupAutoBtn_Click(object sender, RoutedEventArgs e)
    {
        switch (DupStrategyCombo.SelectedIndex)
        {
            case 8: SelectInsideFolder(); return;          // pick folder, select copies in it
            case 7: PreferFolder(); return;                // pick folder, keep its copies
            case 6: KeepOnePerDeviceFolder(); return;      // keep one in each device folder
            default:
                var strategy = (DuplicateFinder.KeepStrategy)Math.Clamp(DupStrategyCombo.SelectedIndex, 0, 5);
                BulkSelect(() =>
                {
                    foreach (var g in _dupRows.GroupBy(r => r.Group))
                    {
                        var rows = g.ToList();
                        var keep = DuplicateFinder.ChooseKeeper(rows, strategy);
                        foreach (var r in rows) r.IsChecked = !ReferenceEquals(r, keep);
                    }
                });
                DupStatusText.Text = $"Auto-selected ({DupStrategyCombo.SelectedItem}) — one copy of each group is kept.";
                return;
        }
    }

    private void DupInvertBtn_Click(object sender, RoutedEventArgs e) =>
        BulkSelect(() => { foreach (var r in _dupRows) r.IsChecked = !r.IsChecked; });

    /// Keep the oldest copy in EACH device (top-level) folder, selecting only the
    /// extra copies that sit within the same folder. Cross-device copies are all kept.
    private void KeepOnePerDeviceFolder()
    {
        int sel = 0;
        BulkSelect(() =>
        {
            foreach (var g in _dupRows.GroupBy(r => r.Group))
            {
                foreach (var dev in g.GroupBy(r => DuplicateFinder.TopLevel(_dupScanRoot, r.Path), StringComparer.OrdinalIgnoreCase))
                {
                    var rows = dev.ToList();
                    var keep = DuplicateFinder.ChooseKeeper(rows, DuplicateFinder.KeepStrategy.Oldest);
                    foreach (var r in rows) { r.IsChecked = !ReferenceEquals(r, keep); if (r.IsChecked) sel++; }
                }
            }
        });
        DupStatusText.Text = $"Selected {sel:N0} within-folder copies — one is kept in each device folder.";
    }

    /// Pick a preferred folder: keep the copies that live there, select the rest.
    /// Groups with no copy in that folder are left untouched (nothing gets removed).
    private void PreferFolder()
    {
        var f = PickFolder(this, _dupScanRoot);
        if (f == null) return;
        string prefix = Path.GetFullPath(f).TrimEnd('\\') + "\\";

        int affected = 0, skipped = 0;
        BulkSelect(() =>
        {
            foreach (var g in _dupRows.GroupBy(r => r.Group))
            {
                var rows = g.ToList();
                bool anyInside = rows.Any(r => Path.GetFullPath(r.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (!anyInside) { skipped++; continue; }
                foreach (var r in rows)
                {
                    bool inside = Path.GetFullPath(r.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                    r.IsChecked = !inside;
                    if (r.IsChecked) affected++;
                }
            }
        });
        DupStatusText.Text = $"Selected {affected:N0} copies outside that folder — copies inside it are kept" +
                             (skipped > 0 ? $"; {skipped:N0} group(s) had no copy there and were left alone." : ".");
    }

    /// Selects duplicates living under a chosen folder, but only in groups that
    /// also have a copy elsewhere — so at least one copy always survives.
    private void SelectInsideFolder()
    {
        var f = PickFolder(this, _dupScanRoot);
        if (f == null) return;
        string prefix = Path.GetFullPath(f).TrimEnd('\\') + "\\";

        int affected = 0, skippedGroups = 0;
        BulkSelect(() =>
        {
            foreach (var g in _dupRows.GroupBy(r => r.Group))
            {
                var rows = g.ToList();
                var inside = rows.Where(r => Path.GetFullPath(r.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (inside.Count == 0) continue;
                if (inside.Count == rows.Count) { skippedGroups++; continue; } // would leave no copy
                foreach (var r in inside) { r.IsChecked = true; affected++; }
            }
        });
        DupStatusText.Text = $"Selected {affected:N0} copies inside {f}" +
                             (skippedGroups > 0 ? $" — {skippedGroups:N0} groups skipped (all copies were inside that folder)." : ".");
    }

    private void DupClearBtn_Click(object sender, RoutedEventArgs e)
    {
        BulkSelect(() => { foreach (var r in _dupRows) r.IsChecked = false; });
    }

    private void DupDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        var sel = _dupRows.Where(r => r.IsChecked).ToList();
        if (sel.Count == 0) return;
        long bytes = sel.Sum(r => r.Size);
        string root = Directory.Exists(_dupScanRoot) ? _dupScanRoot : DupFolderText.Text;

        var fullySelected = sel.GroupBy(r => r.Group)
            .Count(g => g.Count() == _dupRows.Count(r => r.Group == g.Key));
        string warn = fullySelected > 0
            ? $"\n\nWARNING: in {fullySelected:N0} group(s) ALL copies are selected — no copy would remain outside the delete folder."
            : "";

        var res = MessageBox.Show(this,
            $"Move {sel.Count:N0} files ({BackupEngine.FormatBytes(bytes)}) to the \"delete\" folder?\n\n" +
            $"They are moved (with their folder structure) to:\n{Path.Combine(root, DeleteVault.FolderName)}{warn}",
            "Remove duplicates", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;

        var moved = DeleteVault.MoveToDelete(root, sel.Select(r => r.Path).ToList());
        var movedSet = new HashSet<string>(moved, StringComparer.OrdinalIgnoreCase);
        int suppressed = DuplicateFinder.MarkSuppressedInBackupIndex(_settings.DestinationRoot, moved);

        // Keep rows that survived; drop groups reduced to a single file.
        var remaining = _dupRows.Where(r => !movedSet.Contains(r.Path)).ToList();
        var groupCounts = remaining.GroupBy(r => r.Group).ToDictionary(g => g.Key, g => g.Count());
        remaining = remaining.Where(r => groupCounts[r.Group] > 1).ToList();
        SetDupRows(remaining);

        DupStatusText.Text = $"Moved {moved.Count:N0} files ({BackupEngine.FormatBytes(bytes)}) to {DeleteVault.FolderName}\\" +
                             (suppressed > 0 ? " — the backup won't re-copy them from the phone." : ".");

        HistoryLog.Add("Duplicates", $"Moved {moved.Count:N0} duplicate files ({BackupEngine.FormatBytes(bytes)}) to the delete folder",
            $"Folder: {root}");
        RefreshHistory();
    }

    // ================= HISTORY TAB =================

    private void RefreshHistory()
    {
        var rows = HistoryLog.Load().Select(e => new HistoryRow { Entry = e }).ToList();
        HistoryList.ItemsSource = rows;
        HistoryDetail.Text = rows.Count == 0
            ? "Nothing recorded yet. Run a backup, cleanup or duplicate removal and it will show up here."
            : "Select an entry above to see its details.";
    }

    private void HistoryRefreshBtn_Click(object sender, RoutedEventArgs e) => RefreshHistory();

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;
        HistoryDetail.Text = string.IsNullOrWhiteSpace(row.Entry.Details)
            ? row.Entry.Summary
            : row.Entry.Summary + Environment.NewLine + Environment.NewLine + row.Entry.Details;
    }

    private void HistoryOpenLogBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryLog.TextLogPath)!);
            if (!File.Exists(HistoryLog.TextLogPath)) File.Create(HistoryLog.TextLogPath).Dispose();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HistoryLog.TextLogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the log file: " + ex.Message, "OnePlus Phone Backup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _dupCts?.Cancel();
        _cleanCts?.Cancel();
        base.OnClosed(e);
    }
}
