using System.Text.Json;
using CursorSync.Models;

namespace CursorSync.Services;

public sealed class AgentTransferService
{
    private static readonly string[] StoreExcludeDirs = [".sync"];
    private static readonly string[] StoreExcludeFiles = ["sync.lock"];

    public AgentTransferResult Backup(
        AgentRecord agent,
        CursorPaths paths,
        string destination,
        bool includeTranscript,
        bool includeStore,
        bool includeWaypoints,
        IProgress<SyncProgress> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var log = new List<string>();
        var warnings = new List<string>();
        var files = 0;
        long bytes = 0;

        void Report(string message) =>
            progress.Report(new SyncProgress { Message = message, FilesCopied = files, BytesCopied = bytes });

        if (includeTranscript && Directory.Exists(agent.TranscriptDir))
        {
            Report("Copying transcript…");
            CopyTree(agent.TranscriptDir, Path.Combine(destination, "transcript"), null, null, ref files, ref bytes, cancellationToken);
            log.Add("Transcript copied.");
        }

        if (includeStore)
        {
            if (Directory.Exists(agent.StoreDir))
            {
                Report("Copying agent store…");
                CopyTree(agent.StoreDir, Path.Combine(destination, "store"), StoreExcludeDirs, StoreExcludeFiles, ref files, ref bytes, cancellationToken);
                log.Add("Agent store copied.");
            }

            var index = 0;
            foreach (var extra in agent.SubagentStoreDirs)
            {
                index++;
                Report($"Copying subagent store {index}…");
                CopyTree(extra, Path.Combine(destination, "subagent-stores", Path.GetFileName(extra)), StoreExcludeDirs, StoreExcludeFiles, ref files, ref bytes, cancellationToken);
            }
        }

        if (includeWaypoints && agent.WaypointDirs.Count > 0)
        {
            Report("Copying waypoints…");
            foreach (var dir in agent.WaypointDirs)
            {
                CopyTree(dir, Path.Combine(destination, "waypoints", Path.GetFileName(dir)), null, null, ref files, ref bytes, cancellationToken);
            }
            log.Add($"{agent.WaypointDirs.Count} waypoint folder(s) copied.");
        }

        AgentSqliteSlice slice;
        try
        {
            Report("Reading chat database…");
            slice = SqliteStateStore.CaptureComposer(paths, agent.ComposerId, cancellationToken);
            WriteSlice(Path.Combine(destination, "sqlite"), slice);
            if (slice.CursorDiskKv.Count > 0)
                log.Add($"Captured {slice.CursorDiskKv.Count} chat database keys.");
            else
                warnings.Add("Chat database keys were empty. Close Cursor and backup again if the sidebar copy is missing later.");
        }
        catch (Exception ex)
        {
            slice = new AgentSqliteSlice();
            warnings.Add("Could not read Cursor's chat database: " + ex.Message);
        }

        var manifest = new AgentBackupManifest
        {
            SchemaVersion = 1,
            ComposerId = agent.ComposerId,
            Title = agent.Title,
            SourceWorkspaceId = agent.WorkspaceId,
            SourceWorkspacePath = agent.WorkspacePath,
            CreatedUtc = DateTime.UtcNow,
            IncludeTranscript = includeTranscript,
            IncludeStore = includeStore,
            IncludeWaypoints = includeWaypoints,
            Files = files,
            Bytes = bytes,
            HeaderJson = agent.HeaderJson ?? slice.ItemTable.GetValueOrDefault("composerHeader")
        };
        File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(manifest, JsonUtil.Options));

        return new AgentTransferResult
        {
            Success = true,
            OutputPath = destination,
            FilesCopied = files,
            BytesCopied = bytes,
            Warnings = warnings,
            Log = log
        };
    }

    public AgentTransferResult Restore(
        string backupFolder,
        CursorWorkspaceInfo target,
        CursorPaths paths,
        bool includeTranscript,
        bool includeStore,
        bool includeWaypoints,
        IProgress<SyncProgress> progress,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(backupFolder, "manifest.json");
        if (!File.Exists(manifestPath))
            return Fail("This folder is not an agent backup.");

        AgentBackupManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AgentBackupManifest>(File.ReadAllText(manifestPath), JsonUtil.Options)
                ?? throw new InvalidOperationException("Backup manifest was empty.");
        }
        catch (Exception ex)
        {
            return Fail("Could not read the backup manifest: " + ex.Message);
        }

        var newId = Guid.NewGuid().ToString();
        var oldId = manifest.ComposerId;
        var log = new List<string>();
        var warnings = new List<string>();
        var files = 0;
        long bytes = 0;

        void Report(string message) =>
            progress.Report(new SyncProgress { Message = message, FilesCopied = files, BytesCopied = bytes });

        if (includeTranscript)
        {
            var source = Path.Combine(backupFolder, "transcript");
            if (Directory.Exists(source))
            {
                Report("Restoring transcript…");
                var dest = Path.Combine(paths.Projects, target.Slug, "agent-transcripts", newId);
                CopyTree(source, dest, null, null, ref files, ref bytes, cancellationToken);
                RenameTranscript(dest, oldId, newId);
                RewriteTree(dest, oldId, newId, manifest.SourceWorkspacePath, target.FolderPath);
                log.Add("Transcript restored.");
            }
        }

        if (includeStore)
        {
            var storesRoot = Path.Combine(paths.AgentStores, "cursor_agent_stores");
            Directory.CreateDirectory(storesRoot);
            var storeSource = Path.Combine(backupFolder, "store");
            if (Directory.Exists(storeSource))
            {
                Report("Restoring agent store…");
                var dest = Path.Combine(storesRoot, newId);
                CopyTree(storeSource, dest, StoreExcludeDirs, StoreExcludeFiles, ref files, ref bytes, cancellationToken);
                RewriteTree(dest, oldId, newId, manifest.SourceWorkspacePath, target.FolderPath);
                log.Add("Agent store restored.");
            }

            var subRoot = Path.Combine(backupFolder, "subagent-stores");
            if (Directory.Exists(subRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(subRoot))
                {
                    var dest = Path.Combine(storesRoot, Path.GetFileName(dir));
                    CopyTree(dir, dest, StoreExcludeDirs, StoreExcludeFiles, ref files, ref bytes, cancellationToken);
                    RewriteTree(dest, oldId, newId, manifest.SourceWorkspacePath, target.FolderPath);
                }
            }
        }

        if (includeWaypoints)
        {
            var source = Path.Combine(backupFolder, "waypoints");
            if (Directory.Exists(source))
            {
                Report("Restoring waypoints…");
                var destRoot = Path.Combine(paths.Checkpoints, "checkpoints");
                Directory.CreateDirectory(destRoot);
                foreach (var dir in Directory.EnumerateDirectories(source))
                {
                    var dest = Path.Combine(destRoot, Path.GetFileName(dir));
                    CopyTree(dir, dest, null, null, ref files, ref bytes, cancellationToken);
                    RewriteTree(dest, oldId, newId, manifest.SourceWorkspacePath, target.FolderPath);
                    RewriteWaypointMetadata(dest, target);
                }
                log.Add("Waypoints restored.");
            }
        }

        var slice = ReadSlice(Path.Combine(backupFolder, "sqlite"));
        slice = RemapSlice(slice, oldId, newId, manifest.SourceWorkspacePath, target.FolderPath);
        try
        {
            Report("Registering chat in Cursor…");
            SqliteStateStore.RegisterComposer(
                paths,
                target,
                newId,
                manifest.Title,
                RemapText(manifest.HeaderJson, oldId, newId, manifest.SourceWorkspacePath, target.FolderPath),
                slice,
                cancellationToken);
            log.Add("Chat list updated for the target workspace.");
        }
        catch (Exception ex)
        {
            warnings.Add("Files were copied, but the chat list could not be updated: " + ex.Message);
            warnings.Add("Close Cursor completely and restore again so the agent appears in the sidebar.");
        }

        return new AgentTransferResult
        {
            Success = true,
            OutputPath = target.FolderPath,
            FilesCopied = files,
            BytesCopied = bytes,
            Warnings = warnings,
            Log = log,
            RestoredComposerId = newId
        };
    }

    private static AgentTransferResult Fail(string error) =>
        new() { Success = false, Error = error };

    private static void CopyTree(
        string source,
        string destination,
        IReadOnlyList<string>? excludeDirs,
        IReadOnlyList<string>? excludeFiles,
        ref int files,
        ref long bytes,
        CancellationToken cancellationToken)
    {
        var excludeDirSet = new HashSet<string>(excludeDirs ?? [], StringComparer.OrdinalIgnoreCase);
        var excludeFileSet = new HashSet<string>(excludeFiles ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Take(parts.Length - 1).Any(excludeDirSet.Contains))
                continue;
            if (excludeFileSet.Contains(Path.GetFileName(file)))
                continue;

            var dest = Path.Combine(destination, relative);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            File.Copy(file, dest, overwrite: true);
            files++;
            try { bytes += new FileInfo(dest).Length; }
            catch { }
        }
    }

    private static void RenameTranscript(string transcriptDir, string oldId, string newId)
    {
        var oldFile = Path.Combine(transcriptDir, oldId + ".jsonl");
        var newFile = Path.Combine(transcriptDir, newId + ".jsonl");
        if (File.Exists(oldFile) && !File.Exists(newFile))
            File.Move(oldFile, newFile);
    }

    private static void RewriteTree(string root, string oldId, string newId, string? fromFolder, string toFolder)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (ext is not ".json" and not ".jsonl" and not ".md" and not ".txt")
                continue;

            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var updated = RemapText(text, oldId, newId, fromFolder, toFolder);
            if (!string.Equals(text, updated, StringComparison.Ordinal))
                File.WriteAllText(file, updated);
        }
    }

    private static void RewriteWaypointMetadata(string checkpointDir, CursorWorkspaceInfo target)
    {
        var meta = Path.Combine(checkpointDir, "metadata.json");
        if (!File.Exists(meta))
            return;

        try
        {
            var json = File.ReadAllText(meta);
            var updated = ReplaceWorkspaceId(json, target.Id);
            if (!string.Equals(json, updated, StringComparison.Ordinal))
                File.WriteAllText(meta, updated);
        }
        catch
        {
            // leave metadata as copied
        }
    }

    private static string ReplaceWorkspaceId(string json, string workspaceId)
    {
        const string marker = "\"workspaceId\"";
        var index = json.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return json;
        var colon = json.IndexOf(':', index);
        var firstQuote = json.IndexOf('"', colon + 1);
        var secondQuote = json.IndexOf('"', firstQuote + 1);
        if (colon < 0 || firstQuote < 0 || secondQuote < 0)
            return json;
        return json[..(firstQuote + 1)] + workspaceId + json[secondQuote..];
    }

    private static void WriteSlice(string folder, AgentSqliteSlice slice)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "itemTable.json"), JsonSerializer.Serialize(slice.ItemTable, JsonUtil.Options));
        File.WriteAllText(Path.Combine(folder, "cursorDiskKV.json"), JsonSerializer.Serialize(slice.CursorDiskKv, JsonUtil.Options));
    }

    private static AgentSqliteSlice ReadSlice(string folder)
    {
        var slice = new AgentSqliteSlice();
        var items = Path.Combine(folder, "itemTable.json");
        var kv = Path.Combine(folder, "cursorDiskKV.json");
        if (File.Exists(items))
            slice.ItemTable = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(items), JsonUtil.Options) ?? slice.ItemTable;
        if (File.Exists(kv))
            slice.CursorDiskKv = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(kv), JsonUtil.Options) ?? slice.CursorDiskKv;
        return slice;
    }

    private static AgentSqliteSlice RemapSlice(AgentSqliteSlice slice, string oldId, string newId, string? fromFolder, string toFolder)
    {
        var remapped = new AgentSqliteSlice();
        foreach (var (key, value) in slice.ItemTable)
            remapped.ItemTable[RemapText(key, oldId, newId, fromFolder, toFolder)] = RemapText(value, oldId, newId, fromFolder, toFolder);
        foreach (var (key, value) in slice.CursorDiskKv)
            remapped.CursorDiskKv[RemapText(key, oldId, newId, fromFolder, toFolder)] = RemapText(value, oldId, newId, fromFolder, toFolder);
        return remapped;
    }

    private static string RemapText(string? text, string oldId, string newId, string? fromFolder, string toFolder)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        var updated = text.Replace(oldId, newId, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(fromFolder) || string.IsNullOrWhiteSpace(toFolder))
            return updated;

        var from = CursorWorkspaceLocator.NormalizeFolder(fromFolder);
        var to = CursorWorkspaceLocator.NormalizeFolder(toFolder);
        updated = ReplaceInsensitivePlain(updated, from, to);
        updated = ReplaceInsensitivePlain(updated, from.Replace('\\', '/'), to.Replace('\\', '/'));
        updated = ReplaceInsensitivePlain(updated, Uri.EscapeDataString(from.Replace('\\', '/')), Uri.EscapeDataString(to.Replace('\\', '/')));
        updated = ReplaceInsensitivePlain(updated, CursorWorkspaceLocator.ToProjectSlug(from), CursorWorkspaceLocator.ToProjectSlug(to));
        return updated;
    }

    private static string ReplaceInsensitivePlain(string text, string from, string to)
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
