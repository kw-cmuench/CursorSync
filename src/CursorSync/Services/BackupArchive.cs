using System.IO.Compression;
using System.Text;

namespace CursorSync.Services;

public static class BackupArchive
{
    public static string FileName(DateTime? timestamp = null)
    {
        var date = (timestamp ?? DateTime.Now).ToString("yyyyMMdd-HHmmss");
        return $"{date}_{Safe(Environment.UserName)}_{Safe(Environment.MachineName)}.zip";
    }

    public static string UniquePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination))
            return destination;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 100; i++)
        {
            destination = Path.Combine(directory, $"{stem}-{i}{ext}");
            if (!File.Exists(destination))
                return destination;
        }

        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():n}{ext}");
    }

    public static string CompressDirectory(string sourceDirectory, string destinationZip)
    {
        var folder = Path.GetDirectoryName(destinationZip);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        if (File.Exists(destinationZip))
            File.Delete(destinationZip);

        ZipFile.CreateFromDirectory(sourceDirectory, destinationZip, CompressionLevel.Optimal, includeBaseDirectory: false);
        return destinationZip;
    }

    public static string ExtractToTemp(string zipPath)
    {
        var destination = Path.Combine(Path.GetTempPath(), "CursorSync", "restore-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(destination);
        ZipFile.ExtractToDirectory(zipPath, destination);
        return destination;
    }

    public static bool IsZip(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string Safe(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or ' ')
                builder.Append('-');
            else
                builder.Append(c);
        }

        var cleaned = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }
}
