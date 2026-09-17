using CursorSync.Models;

namespace CursorSync.Services;

public readonly record struct FilePair(string SourcePath, string DestinationPath, DateTime SourceWriteUtc);

public static class FileInventory
{
    public static IEnumerable<FilePair> Enumerate(SyncEntry entry, bool sourceIsLocal, string? hubRoot = null)
    {
        if (entry.IsDirectory)
        {
            var sourceRoot = sourceIsLocal ? entry.LocalPath : HubPath(hubRoot, entry.RelativePayloadPath);
            var destinationRoot = sourceIsLocal ? HubPath(hubRoot, entry.RelativePayloadPath) : entry.LocalPath;
            if (!Directory.Exists(sourceRoot))
                yield break;

            foreach (var file in EnumerateDirectory(sourceRoot, entry))
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                var destination = Path.Combine(destinationRoot, relative);
                yield return new FilePair(file, destination, SafeWriteTime(file));
            }

            yield break;
        }

        var localPrimary = entry.LocalPath;
        var hubPrimary = HubPath(hubRoot, entry.RelativePayloadPath);
        var sourcePrimary = sourceIsLocal ? localPrimary : hubPrimary;
        var destinationPrimary = sourceIsLocal ? hubPrimary : localPrimary;

        foreach (var suffix in PrimaryAndSuffixes(entry.CompanionSuffixes))
        {
            var source = sourcePrimary + suffix;
            if (!File.Exists(source))
                continue;

            yield return new FilePair(source, destinationPrimary + suffix, SafeWriteTime(source));
        }
    }

    public static IEnumerable<string> EnumerateDirectory(string root, SyncEntry entry)
    {
        var excludeDirs = new HashSet<string>(entry.ExcludeDirectoryNames ?? [], StringComparer.OrdinalIgnoreCase);
        var excludeFiles = new HashSet<string>(entry.ExcludeFileNames ?? [], StringComparer.OrdinalIgnoreCase);
        var includeFiles = entry.IncludeFileNames is { Count: > 0 }
            ? new HashSet<string>(entry.IncludeFileNames, StringComparer.OrdinalIgnoreCase)
            : null;

        if (includeFiles is not null)
        {
            string[] files;
            try { files = Directory.GetFiles(root); }
            catch { yield break; }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (!excludeFiles.Contains(name) && includeFiles.Contains(name))
                    yield return file;
            }

            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        IEnumerable<string> filesWalk;
        try { filesWalk = Directory.EnumerateFiles(root, "*", options); }
        catch { yield break; }

        foreach (var file in filesWalk)
        {
            if (excludeDirs.Count > 0 && IsUnderExcludedDirectory(root, file, excludeDirs))
                continue;

            var name = Path.GetFileName(file);
            if (excludeFiles.Contains(name))
                continue;

            yield return file;
        }
    }

    public static (bool Present, int Files, long Bytes) Measure(SyncEntry entry)
    {
        if (entry.IsDirectory)
        {
            if (!Directory.Exists(entry.LocalPath))
                return default;

            var files = 0;
            long bytes = 0;
            foreach (var file in EnumerateDirectory(entry.LocalPath, entry))
            {
                files++;
                try { bytes += new FileInfo(file).Length; }
                catch { }
            }

            return (files > 0 || Directory.Exists(entry.LocalPath), files, bytes);
        }

        var present = false;
        var count = 0;
        long size = 0;
        foreach (var suffix in PrimaryAndSuffixes(entry.CompanionSuffixes))
        {
            var path = entry.LocalPath + suffix;
            if (!File.Exists(path))
                continue;

            present = true;
            count++;
            try { size += new FileInfo(path).Length; }
            catch { }
        }

        return (present, count, size);
    }

    private static bool IsUnderExcludedDirectory(string root, string filePath, HashSet<string> excludeDirs)
    {
        var relative = Path.GetRelativePath(root, filePath);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (excludeDirs.Contains(parts[i]))
                return true;
        }

        return false;
    }

    public static string HubPath(string? hubRoot, string relative)
    {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(hubRoot))
            return normalized;

        return Path.Combine(hubRoot, "payload", normalized);
    }

    private static IEnumerable<string> PrimaryAndSuffixes(IReadOnlyList<string>? suffixes)
    {
        yield return "";
        if (suffixes is null)
            yield break;
        foreach (var suffix in suffixes)
            yield return suffix;
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}
