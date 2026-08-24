using System.IO;
using System.Security.Cryptography;

namespace PhoneBackup;

/// Shared content-hashing helper used by the duplicate finder and the backup
/// engine's duplicate/overwrite protection, so both agree on what "identical" means.
internal static class Hashing
{
    /// SHA256 of a file's first `limit` bytes, or the whole file if limit &lt; 0.
    public static string HashFile(string path, long limit, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 18);
        var buf = new byte[1 << 20];
        long remaining = limit < 0 ? long.MaxValue : limit;
        int read;
        while (remaining > 0 && (read = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining))) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buf, 0, read, null, 0);
            remaining -= read;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}
