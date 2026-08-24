using System.IO;
using System.Text.Json;

namespace PhoneBackup;

public class HistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Kind { get; set; } = "";   // "Backup", "Clean", "Duplicates"
    public string Summary { get; set; } = "";
    public string Details { get; set; } = "";
}

/// Persistent record of everything this app has done, kept across sessions — separate
/// from the live per-run log box on each tab, which resets every time you start a new
/// operation. Backed by a JSON file (for the History tab) and a plain-text log file
/// (for opening directly, e.g. to check what happened on a specific date).
public static class HistoryLog
{
    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnePlusPhoneBackup");
    private static string JsonPath => Path.Combine(Folder, "history.json");
    public static string TextLogPath => Path.Combine(Folder, "history.log");

    private const int MaxEntries = 500;

    public static void Add(string kind, string summary, string details = "")
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var entries = Load();
            entries.Insert(0, new HistoryEntry { Timestamp = DateTime.Now, Kind = kind, Summary = summary, Details = details });
            if (entries.Count > MaxEntries) entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            File.WriteAllText(JsonPath, JsonSerializer.Serialize(entries));

            string block = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {summary}" +
                           (string.IsNullOrEmpty(details) ? "" : Environment.NewLine + details) +
                           Environment.NewLine;
            File.AppendAllText(TextLogPath, block);
        }
        catch { /* history is a convenience — never let it fail the actual operation */ }
    }

    public static List<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(JsonPath))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(JsonPath)) ?? new List<HistoryEntry>();
        }
        catch { }
        return new List<HistoryEntry>();
    }
}
