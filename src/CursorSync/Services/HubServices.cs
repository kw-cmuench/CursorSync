using System.Text.Json;
using CursorSync.Models;

namespace CursorSync.Services;

public static class PathRewriter
{
    public static void RewriteWorkspaceFile(string path, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PathReplaceFrom) || string.IsNullOrWhiteSpace(settings.PathReplaceTo))
            return;

        if (!File.Exists(path))
            return;

        var text = File.ReadAllText(path);
        var from = settings.PathReplaceFrom.Trim().Replace('\\', '/');
        var to = settings.PathReplaceTo.Trim().Replace('\\', '/');

        var updated = ReplaceInsensitive(text, settings.PathReplaceFrom.Trim(), settings.PathReplaceTo.Trim());
        updated = ReplaceInsensitive(updated, Uri.EscapeDataString(from), Uri.EscapeDataString(to));
        updated = ReplaceInsensitive(updated, from, to);

        if (!string.Equals(text, updated, StringComparison.Ordinal))
            File.WriteAllText(path, updated);
    }

    private static string ReplaceInsensitive(string text, string from, string to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(text))
            return text;

        var index = 0;
        while ((index = text.IndexOf(from, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            text = text[..index] + to + text[(index + from.Length)..];
            index += to.Length;
        }

        return text;
    }
}

public sealed class HubManifestStore
{
    public HubManifest? Load(string hubPath)
    {
        var file = Path.Combine(hubPath, "cursorsync.json");
        if (!File.Exists(file))
            return null;

        try
        {
            return JsonSerializer.Deserialize<HubManifest>(File.ReadAllText(file), JsonUtil.Options);
        }
        catch
        {
            return null;
        }
    }

    public void Update(string hubPath, string machine, SyncKind kind, List<string> categories)
    {
        var file = Path.Combine(hubPath, "cursorsync.json");
        var manifest = Load(hubPath) ?? new HubManifest();
        manifest.UpdatedUtc = DateTime.UtcNow;
        manifest.LastMachine = machine;
        manifest.LastKind = kind.ToString();
        manifest.LastCategories = categories;

        var existing = manifest.Machines.FirstOrDefault(m =>
            string.Equals(m.Name, machine, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            manifest.Machines.Add(new HubMachine { Name = machine, LastSeenUtc = DateTime.UtcNow });
        }
        else
        {
            existing.LastSeenUtc = DateTime.UtcNow;
        }

        File.WriteAllText(file, JsonSerializer.Serialize(manifest, JsonUtil.Options));
    }
}

public sealed class BackupService
{
    private readonly SettingsStore _store = new();

    public string? Backup(CursorPaths paths, IReadOnlyList<CategoryDefinition> categories, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(Path.GetTempPath(), "CursorSync", "pre-pull-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(staging);

        try
        {
            var copied = 0;
            foreach (var category in categories)
            {
                foreach (var entry in category.Resolve(paths))
                {
                    foreach (var pair in FileInventory.Enumerate(entry, sourceIsLocal: true, staging))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var destDir = Path.GetDirectoryName(pair.DestinationPath);
                        if (!string.IsNullOrEmpty(destDir))
                            Directory.CreateDirectory(destDir);
                        File.Copy(pair.SourcePath, pair.DestinationPath, overwrite: true);
                        copied++;
                    }
                }
            }

            if (copied == 0)
                return null;

            var zipPath = BackupArchive.UniquePath(_store.BackupsFolder, BackupArchive.FileName());
            BackupArchive.CompressDirectory(staging, zipPath);
            TrimOldBackups();
            return zipPath;
        }
        finally
        {
            BackupArchive.TryDeleteDirectory(staging);
        }
    }

    public void TrimOldBackups(int keep = 10)
    {
        if (!Directory.Exists(_store.BackupsFolder))
            return;

        var items = Directory.EnumerateFileSystemEntries(_store.BackupsFolder)
            .Select(path => (Path: path, Write: File.GetLastWriteTimeUtc(path), IsDir: Directory.Exists(path)))
            .OrderByDescending(item => item.Write)
            .Skip(keep);

        foreach (var item in items)
        {
            try
            {
                if (item.IsDir)
                    Directory.Delete(item.Path, recursive: true);
                else
                    File.Delete(item.Path);
            }
            catch { }
        }
    }
}

public static class ExtensionListWriter
{
    public static void Ensure(CursorPaths paths)
    {
        if (!Directory.Exists(paths.Extensions))
            return;

        var file = Path.Combine(paths.Extensions, "extensions.json");
        if (File.Exists(file))
            return;

        var ids = Directory.GetDirectories(paths.Extensions)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, ".obsolete", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        File.WriteAllText(file, JsonSerializer.Serialize(ids, JsonUtil.Options));
    }
}

public sealed class HubSuggestion
{
    public required string Label { get; init; }
    public required string FolderPath { get; init; }
}

public static class HubSuggestions
{
    public static IReadOnlyList<HubSuggestion> Suggest()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<HubSuggestion>();

        void Add(string label, string root)
        {
            if (Directory.Exists(root))
                list.Add(new HubSuggestion { Label = label, FolderPath = System.IO.Path.Combine(root, "CursorSync") });
        }

        Add("OneDrive", System.IO.Path.Combine(home, "OneDrive"));
        Add("OneDrive", System.IO.Path.Combine(home, "OneDrive - Personal"));
        Add("Dropbox", System.IO.Path.Combine(home, "Dropbox"));
        Add("Google Drive", System.IO.Path.Combine(home, "Google Drive"));

        if (list.Count == 0)
        {
            list.Add(new HubSuggestion
            {
                Label = "Documents",
                FolderPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CursorSync")
            });
        }

        return list
            .DistinctBy(x => x.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
