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

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] dirs;
            try { dirs = Directory.GetDirectories(current); }
            catch { dirs = []; }

            foreach (var dir in dirs)
            {
                if (!excludeDirs.Contains(Path.GetFileName(dir)))
                    stack.Push(dir);
            }

            string[] files;
            try { files = Directory.GetFiles(current); }
            catch { files = []; }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (excludeFiles.Contains(name))
                    continue;
                if (includeFiles is not null && !includeFiles.Contains(name))
                    continue;
                yield return file;
            }
        }
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
