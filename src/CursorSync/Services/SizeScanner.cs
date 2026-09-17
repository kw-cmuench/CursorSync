using CursorSync.Models;

namespace CursorSync.Services;

public static class FileSizeFormatter
{
    public static string FromBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

public static class SizeScanner
{
    public static CategoryScan Scan(CategoryDefinition category, CursorPaths paths, bool deep = true)
    {
        var entries = category.Resolve(paths);
        if (entries.Count == 0)
            return Empty(category.Id);

        if (!deep && category.IsLarge)
        {
            return new CategoryScan
            {
                Id = category.Id,
                Present = entries.Any(Exists),
                Bytes = 0,
                Files = 0,
                Deep = false
            };
        }

        long bytes = 0;
        var files = 0;
        var present = false;

        foreach (var entry in entries)
        {
            var measured = FileInventory.Measure(entry);
            if (!measured.Present)
                continue;

            present = true;
            files += measured.Files;
            bytes += measured.Bytes;
        }

        return new CategoryScan
        {
            Id = category.Id,
            Present = present,
            Bytes = bytes,
            Files = files
        };
    }

    private static CategoryScan Empty(string id) => new()
    {
        Id = id,
        Present = false
    };

    private static bool Exists(SyncEntry entry) =>
        entry.IsDirectory ? Directory.Exists(entry.LocalPath) : File.Exists(entry.LocalPath);
}
