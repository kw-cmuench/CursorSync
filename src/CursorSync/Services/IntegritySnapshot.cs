using System.Text.Json;
using CursorSync.Models;

namespace CursorSync.Services;

public static class IntegritySnapshot
{
    private sealed class Manifest
    {
        public DateTime CreatedUtc { get; set; }
        public List<SnapshotFile> Files { get; set; } = [];
    }

    private sealed class SnapshotFile
    {
        public string Source { get; set; } = "";
        public string Relative { get; set; } = "";
    }

    public static string Capture(CursorPaths paths, string safetyFolder)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("n")[..8];
        var staging = Path.Combine(Path.GetTempPath(), "CursorSync", "safety-" + stamp);
        Directory.CreateDirectory(staging);

        try
        {
            var files = new List<SnapshotFile>();
            CopyFile(paths.StateDb, "global/state.vscdb", staging, files);
            CopyFile(paths.StateDb + "-wal", "global/state.vscdb-wal", staging, files);
            CopyFile(paths.StateDb + "-shm", "global/state.vscdb-shm", staging, files);
            CopyFile(paths.ConversationSearchDb, "global/conversation-search.db", staging, files);
            CopyFile(paths.ConversationSearchDb + "-wal", "global/conversation-search.db-wal", staging, files);
            CopyFile(paths.ConversationSearchDb + "-shm", "global/conversation-search.db-shm", staging, files);

            if (Directory.Exists(paths.WorkspaceStorage))
            {
                foreach (var dir in Directory.EnumerateDirectories(paths.WorkspaceStorage))
                {
                    var id = Path.GetFileName(dir);
                    foreach (var name in new[] { "workspace.json", "state.vscdb", "state.vscdb-wal", "state.vscdb-shm" })
                        CopyFile(Path.Combine(dir, name), Path.Combine("workspaceStorage", id, name), staging, files);
                }
            }

            if (Directory.Exists(paths.Projects))
            {
                foreach (var project in Directory.EnumerateDirectories(paths.Projects))
                    CopyTree(project, Path.Combine("projects", Path.GetFileName(project)), staging, files);
            }

            var stores = Path.Combine(paths.AgentStores, "cursor_agent_stores");
            if (!Directory.Exists(stores))
                stores = paths.AgentStores;
            if (Directory.Exists(stores))
                CopyTree(stores, "agentStores", staging, files, excludeDirs: [".sync"], excludeFiles: ["sync.lock"]);

            if (Directory.Exists(paths.Checkpoints))
                CopyTree(paths.Checkpoints, "checkpoints", staging, files);

            var manifest = new Manifest { CreatedUtc = DateTime.UtcNow, Files = files };
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, JsonUtil.Options));

            if (files.Count == 0)
                throw new InvalidOperationException("Nothing could be copied into the safety snapshot.");

            return BackupArchive.CompressDirectory(
                staging,
                BackupArchive.UniquePath(safetyFolder, $"safety-{stamp}.zip"));
        }
        finally
        {
            BackupArchive.TryDeleteDirectory(staging);
        }
    }

    public static void Restore(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("The safety snapshot is missing.", zipPath);

        var extracted = BackupArchive.ExtractToTemp(zipPath);
        try
        {
            var manifestPath = Path.Combine(extracted, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new InvalidOperationException("The safety snapshot has no manifest.");

            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonUtil.Options)
                ?? throw new InvalidOperationException("The safety snapshot manifest was empty.");

            var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                var source = Path.Combine(extracted, file.Relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source) || string.IsNullOrWhiteSpace(file.Source))
                    continue;

                var destDir = Path.GetDirectoryName(file.Source);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);
                File.Copy(source, file.Source, overwrite: true);
                restored.Add(file.Source);
            }

            ClearMissingSidecar(restored, "state.vscdb");
            ClearMissingSidecar(restored, "conversation-search.db");
        }
        finally
        {
            BackupArchive.TryDeleteDirectory(extracted);
        }
    }

    public static void Discard(string? zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return;
        try { File.Delete(zipPath); }
        catch { /* keep the file if it is locked */ }
    }

    private static void CopyFile(string source, string relative, string staging, List<SnapshotFile> files)
    {
        if (!File.Exists(source))
            return;

        var dest = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Copy(source, dest, overwrite: true);
        files.Add(new SnapshotFile { Source = source, Relative = relative.Replace('\\', '/') });
    }

    private static void CopyTree(
        string root,
        string prefix,
        string staging,
        List<SnapshotFile> files,
        IReadOnlyList<string>? excludeDirs = null,
        IReadOnlyList<string>? excludeFiles = null)
    {
        var skipDirs = new HashSet<string>(excludeDirs ?? [], StringComparer.OrdinalIgnoreCase);
        var skipFiles = new HashSet<string>(excludeFiles ?? [], StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> walk;
        try
        {
            walk = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            });
        }
        catch
        {
            return;
        }

        foreach (var file in walk)
        {
            var relative = Path.GetRelativePath(root, file);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Take(parts.Length - 1).Any(skipDirs.Contains))
                continue;
            if (skipFiles.Contains(Path.GetFileName(file)))
                continue;
            CopyFile(file, Path.Combine(prefix, relative), staging, files);
        }
    }

    private static void ClearMissingSidecar(HashSet<string> restored, string dbName)
    {
        foreach (var db in restored.Where(path =>
                     string.Equals(Path.GetFileName(path), dbName, StringComparison.OrdinalIgnoreCase)))
        {
            TryDeleteIfAbsent(restored, db + "-wal");
            TryDeleteIfAbsent(restored, db + "-shm");
        }
    }

    private static void TryDeleteIfAbsent(HashSet<string> restored, string path)
    {
        if (restored.Contains(path) || !File.Exists(path))
            return;
        try { File.Delete(path); }
        catch { }
    }
}
