using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhoneBackup;

/// The "delete" folder: removed files are MOVED here (keeping their relative
/// folder structure) instead of being destroyed, so everything is reviewable
/// and reversible. The folder gets a red icon via desktop.ini.
public static class DeleteVault
{
    public const string FolderName = "delete";

    /// True if the path is the delete vault or lives inside one — matches a path
    /// segment named exactly "delete" at ANY depth, so a vault created when cleaning
    /// a subfolder is still skipped when later scanning a parent folder.
    public static bool IsInsideVault(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        string padded = "\\" + fullPath.Replace('/', '\\').Trim('\\') + "\\";
        return padded.IndexOf("\\" + FolderName + "\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string Ensure(string scanRoot)
    {
        string dir = Path.Combine(scanRoot, FolderName);
        Directory.CreateDirectory(dir);
        try { ApplyRedIcon(dir); } catch { /* cosmetic only */ }
        return dir;
    }

    /// Moves files/folders into scanRoot\delete\&lt;relative path&gt;. Returns what was actually moved.
    public static List<string> MoveToDelete(string scanRoot, IReadOnlyList<string> paths)
    {
        string vault = Ensure(scanRoot);
        string rootFull = Path.GetFullPath(scanRoot).TrimEnd('\\') + "\\";
        string vaultFull = Path.GetFullPath(vault).TrimEnd('\\') + "\\";
        var moved = new List<string>();
        foreach (var p in paths)
        {
            string full;
            try { full = Path.GetFullPath(p); } catch { continue; }
            if (full.StartsWith(vaultFull, StringComparison.OrdinalIgnoreCase)) continue; // already in vault

            string rel = full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(rootFull.Length)
                : Path.GetFileName(full);
            string dest = Path.Combine(vault, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                dest = Unique(dest);
                if (Directory.Exists(full)) Directory.Move(full, dest);
                else if (File.Exists(full)) File.Move(full, dest);
                else continue;
                moved.Add(p);
            }
            catch { /* locked or access denied: leave in place */ }
        }
        return moved;
    }

    private static string Unique(string dest)
    {
        if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
        string dir = Path.GetDirectoryName(dest)!;
        string name = Path.GetFileNameWithoutExtension(dest);
        string ext = Path.GetExtension(dest);
        for (int i = 2; ; i++)
        {
            string c = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(c) && !Directory.Exists(c)) return c;
        }
    }

    // ---- red folder icon ----

    private static void ApplyRedIcon(string dir)
    {
        string ico = Path.Combine(dir, ".folder.ico");
        string ini = Path.Combine(dir, "desktop.ini");
        if (!File.Exists(ico))
        {
            File.WriteAllBytes(ico, BuildIco(RenderFolderPng(256)));
            File.SetAttributes(ico, FileAttributes.Hidden);
        }
        if (!File.Exists(ini))
        {
            File.WriteAllText(ini,
                "[.ShellClassInfo]\r\n" +
                "IconResource=.folder.ico,0\r\n" +
                "InfoTip=Files removed by OnePlus Phone Backup — review, then empty when you're sure\r\n");
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        }
        var attr = File.GetAttributes(dir);
        if ((attr & FileAttributes.ReadOnly) == 0)
            File.SetAttributes(dir, attr | FileAttributes.ReadOnly); // makes Explorer honour desktop.ini
    }

    private static byte[] RenderFolderPng(int size)
    {
        double s = size;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var back = new SolidColorBrush(Color.FromRgb(0xA8, 0x23, 0x16));
            var body = new LinearGradientBrush(
                Color.FromRgb(0xE8, 0x3B, 0x2D), Color.FromRgb(0xC4, 0x2B, 0x1C), 90.0);
            dc.DrawRoundedRectangle(back, null, new Rect(s * 0.06, s * 0.14, s * 0.40, s * 0.22), s * 0.05, s * 0.05);
            dc.DrawRoundedRectangle(back, null, new Rect(s * 0.06, s * 0.24, s * 0.88, s * 0.60), s * 0.06, s * 0.06);
            dc.DrawRoundedRectangle(body, null, new Rect(s * 0.06, s * 0.32, s * 0.88, s * 0.52), s * 0.06, s * 0.06);
            var pen = new Pen(Brushes.White, s * 0.055)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(s * 0.41, s * 0.47), new Point(s * 0.59, s * 0.69));
            dc.DrawLine(pen, new Point(s * 0.59, s * 0.47), new Point(s * 0.41, s * 0.69));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] BuildIco(byte[] png)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1); // ICONDIR: 1 image
        w.Write((byte)0); w.Write((byte)0);   // 256 x 256 (0 means 256)
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(png.Length); w.Write(22);     // data size + offset
        w.Write(png);
        w.Flush();
        return ms.ToArray();
    }
}
