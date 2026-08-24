using System.IO;
using System.Text.Json;

namespace PhoneBackup;

public class AppSettings
{
    public string DestinationRoot { get; set; } = "";
    public string LastDeviceName { get; set; } = "";
    public long MinSizeBytes { get; set; } = 1024 * 1024; // 1 MB default
    public long DupMinSizeBytes { get; set; } = 1024 * 1024; // duplicate-search threshold
    public int DupStrategyIndex { get; set; } = 0;
    public bool DupSameFolderOnly { get; set; } = true; // don't match copies across device folders
    public bool AlwaysKeepDocuments { get; set; } = true;
    public bool SkipDotFolders { get; set; } = true;
    public bool MirrorAppMediaToPictures { get; set; } = true;
    // Before writing a phone file, check whether identical content already exists
    // anywhere in the destination tree (e.g. an old S23U backup sitting alongside)
    // so it's never stored twice. Overwrite protection for files this app doesn't
    // recognize is always on regardless of this setting.
    public bool AvoidCrossBackupDuplicates { get; set; } = true;
    public string ExcludedFolders { get; set; } = "Android/data, Android/obb, LOST.DIR, .GlobalTrash, .oprecyclebin, cache, .cache, .thumbnails, .trash, .Trash, .temp, $RECYCLE.BIN, System Volume Information";
    public string ExcludedExtensions { get; set; } = ".tmp, .temp, .cache, .nomedia, .thumb, .log, .part, .crdownload";
    public string DocumentExtensions { get; set; } = ".pdf, .doc, .docx, .xls, .xlsx, .ppt, .pptx, .txt, .csv, .rtf, .odt, .epub, .vcf";

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnePlusPhoneBackup", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
                MigrateLegacyExclusions(loaded);
                return loaded;
            }
        }
        catch { /* corrupt settings -> defaults */ }
        var s = new AppSettings
        {
            DestinationRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PhoneBackup")
        };
        return s;
    }

    /// Early versions excluded the whole "Android" folder — but since Android 11 WhatsApp
    /// and Telegram keep all their media in Android/media, so that would silently drop
    /// user photos. Rewrite the old entry to the safe data/obb pair.
    private static void MigrateLegacyExclusions(AppSettings s)
    {
        var parts = s.ExcludedFolders
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!parts.Any(p => p.Equals("Android", StringComparison.OrdinalIgnoreCase))) return;
        parts.RemoveAll(p => p.Equals("Android", StringComparison.OrdinalIgnoreCase));
        if (!parts.Contains("Android/data", StringComparer.OrdinalIgnoreCase)) parts.Insert(0, "Android/data");
        if (!parts.Contains("Android/obb", StringComparer.OrdinalIgnoreCase)) parts.Insert(1, "Android/obb");
        s.ExcludedFolders = string.Join(", ", parts);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }

    public static HashSet<string> ParseList(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
