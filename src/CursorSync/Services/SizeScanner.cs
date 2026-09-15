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
    public static CategoryScan Scan(CategoryDefinition category, CursorPaths paths)
    {
        long bytes = 0;
        var files = 0;
        var present = false;

        foreach (var entry in category.Resolve(paths))
        {
            foreach (var file in FileInventory.Enumerate(entry, sourceIsLocal: true))
            {
                present = true;
                files++;
                try { bytes += new FileInfo(file.SourcePath).Length; }
                catch { }
            }
        }

        return new CategoryScan
        {
            Id = category.Id,
            Present = present,
            Bytes = bytes,
            Files = files
        };
    }
}
