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

    public AgentTransferResult BackupPack(
        IReadOnlyList<AgentRecord> agents,
        CursorPaths paths,
        string destination,
        bool includeTranscript,
        bool includeStore,
        bool includeWaypoints,
        IProgress<SyncProgress> progress,
        CancellationToken cancellationToken)
    {
        if (agents.Count == 0)
            return Fail("Select at least one workspace or agent.");

        Directory.CreateDirectory(destination);
        var warnings = new List<string>();
        var log = new List<string>();
        var files = 0;
        long bytes = 0;

        foreach (var agent in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new SyncProgress { Message = $"Backing up {agent.Title}…" });
            var agentDir = Path.Combine(destination, "agents", agent.ComposerId);
            var result = Backup(agent, paths, agentDir, includeTranscript, includeStore, includeWaypoints, progress, cancellationToken);
            files += result.FilesCopied;
            bytes += result.BytesCopied;
            log.AddRange(result.Log.Select(line => $"{agent.Title}: {line}"));
            warnings.AddRange(result.Warnings);
            if (!result.Success && !string.IsNullOrWhiteSpace(result.Error))
                warnings.Add($"{agent.Title}: {result.Error}");
        }

        var workspaces = agents
            .GroupBy(a => a.WorkspaceId ?? a.WorkspacePath ?? a.WorkspaceLabel ?? "unknown")
            .Select(g => new WorkspaceBackupRef
            {
                Id = g.First().WorkspaceId ?? "",
                Label = g.First().WorkspaceLabel ?? "Unknown workspace",
                FolderPath = g.First().WorkspacePath ?? ""
            })
            .ToList();

        var title = workspaces.Count == 1
            ? $"{workspaces[0].Label} · {agents.Count} agent{(agents.Count == 1 ? "" : "s")}"
            : $"{workspaces.Count} workspaces · {agents.Count} agents";

        var pack = new AgentBackupManifest
        {
            SchemaVersion = 2,
            Kind = "workspacePack",
            Title = title,
            CreatedUtc = DateTime.UtcNow,
            IncludeTranscript = includeTranscript,
            IncludeStore = includeStore,
            IncludeWaypoints = includeWaypoints,
            Files = files,
            Bytes = bytes,
            AgentCount = agents.Count,
            Workspaces = workspaces
        };
        File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(pack, JsonUtil.Options));
        log.Add($"Pack contains {agents.Count} agent(s) from {workspaces.Count} workspace(s).");

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

    public AgentTransferResult RestorePack(
        string backupFolder,
        IReadOnlyList<CursorWorkspaceInfo> localWorkspaces,
        CursorWorkspaceInfo? fallbackTarget,
        CursorPaths paths,
        bool includeTranscript,
        bool includeStore,
        bool includeWaypoints,
        bool forceTarget,
        IProgress<SyncProgress> progress,
        CancellationToken cancellationToken)
    {
        var agentsRoot = Path.Combine(backupFolder, "agents");
        if (!Directory.Exists(agentsRoot))
        {
            if (File.Exists(Path.Combine(backupFolder, "manifest.json")))
            {
                var target = fallbackTarget ?? throw new InvalidOperationException("Choose a workspace to restore into.");
                return Restore(backupFolder, target, paths, includeTranscript, includeStore, includeWaypoints, progress, cancellationToken);
            }
            return Fail("This folder is not a CursorSync agent backup.");
        }

        var warnings = new List<string>();
        var log = new List<string>();
        var files = 0;
        long bytes = 0;
        var restored = 0;

        foreach (var dir in Directory.EnumerateDirectories(agentsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var agentManifestPath = Path.Combine(dir, "manifest.json");
            AgentBackupManifest? agentManifest = null;
            if (File.Exists(agentManifestPath))
            {
                try
                {
                    agentManifest = JsonSerializer.Deserialize<AgentBackupManifest>(File.ReadAllText(agentManifestPath), JsonUtil.Options);
                }
                catch
                {
                    // fall through
                }
            }

            var target = forceTarget && fallbackTarget is not null
                ? fallbackTarget
                : ResolveTarget(agentManifest, localWorkspaces, fallbackTarget);
            if (target is null)
            {
                warnings.Add($"{agentManifest?.Title ?? Path.GetFileName(dir)}: no matching workspace on this PC. Choose a restore target.");
                continue;
            }

            progress.Report(new SyncProgress { Message = $"Restoring {agentManifest?.Title ?? Path.GetFileName(dir)} → {target.Label}…" });
            var result = Restore(dir, target, paths, includeTranscript, includeStore, includeWaypoints, progress, cancellationToken);
            files += result.FilesCopied;
            bytes += result.BytesCopied;
            log.AddRange(result.Log);
            warnings.AddRange(result.Warnings);
            if (result.Success)
                restored++;
            else if (!string.IsNullOrWhiteSpace(result.Error))
                warnings.Add(result.Error);
        }

        if (restored == 0)
            return Fail(warnings.Count > 0 ? warnings[0] : "Nothing could be restored.");

        return new AgentTransferResult
        {
            Success = true,
            OutputPath = fallbackTarget?.FolderPath,
            FilesCopied = files,
            BytesCopied = bytes,
            Warnings = warnings,
            Log = log
        };
    }

    private static CursorWorkspaceInfo? ResolveTarget(
        AgentBackupManifest? manifest,
        IReadOnlyList<CursorWorkspaceInfo> localWorkspaces,
        CursorWorkspaceInfo? fallback)
    {
        if (!string.IsNullOrWhiteSpace(manifest?.SourceWorkspacePath))
        {
            var match = CursorWorkspaceLocator.FindByFolder(localWorkspaces, manifest.SourceWorkspacePath);
            if (match is not null)
                return match;
        }

        if (!string.IsNullOrWhiteSpace(manifest?.SourceWorkspaceId))
        {
            var match = localWorkspaces.FirstOrDefault(w =>
                string.Equals(w.Id, manifest.SourceWorkspaceId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        if (!string.IsNullOrWhiteSpace(manifest?.SourceProjectSlug))
        {
            var match = localWorkspaces.FirstOrDefault(w =>
                string.Equals(w.Slug, manifest.SourceProjectSlug, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return fallback;
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

    public AgentTransferResult Assign(
        IReadOnlyList<AgentRecord> agents,
        CursorWorkspaceInfo target,
        CursorPaths paths,
        IProgress<SyncProgress> progress,
        CancellationToken cancellationToken)
    {
        if (agents.Count == 0)
            return Fail("Select at least one agent to assign.");
        if (string.IsNullOrWhiteSpace(target.Id) || string.IsNullOrWhiteSpace(target.StorageDir))
            return Fail("Choose a workspace Cursor already knows.");

        CursorWorkspaceLocator.EnsureProjectTranscripts(paths, target.FolderPath);

        var warnings = new List<string>();
        var log = new List<string>();
        var files = 0;
        long bytes = 0;
        var moved = 0;

        if (!target.IsReady)
            warnings.Add($"“{target.Label}” has no workspace database yet. Open that folder in Cursor once so it appears in the sidebar.");

        foreach (var agent in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new SyncProgress { Message = $"Assigning {agent.Title} → {target.Label}…" });

            try
            {
                var fromFolder = agent.WorkspacePath;
                var destTranscript = Path.Combine(paths.Projects, target.Slug, "agent-transcripts", agent.ComposerId);
                var sourceTranscript = ResolveExistingDir(
                    agent.TranscriptDir,
                    destTranscript,
                    string.IsNullOrWhiteSpace(agent.ProjectSlug)
                        ? null
                        : Path.Combine(paths.Projects, agent.ProjectSlug, "agent-transcripts", agent.ComposerId));

                if (!string.IsNullOrWhiteSpace(sourceTranscript))
                {
                    if (!CursorWorkspaceLocator.SameFolder(sourceTranscript, destTranscript))
                    {
                        if (Directory.Exists(destTranscript))
                        {
                            warnings.Add($"{agent.Title}: a transcript already exists in {target.Label}. Left the files in place and still retargeted the chat.");
                        }
                        else
                        {
                            MoveTree(sourceTranscript, destTranscript, ref files, ref bytes, cancellationToken);
                        }
                    }

                    var rewriteRoot = Directory.Exists(destTranscript) ? destTranscript : sourceTranscript;
                    RewriteTree(rewriteRoot, agent.ComposerId, agent.ComposerId, fromFolder, target.FolderPath);
                    log.Add($"{agent.Title}: transcript linked to {target.Label}.");
                }

                if (Directory.Exists(agent.StoreDir))
                {
                    RewriteTree(agent.StoreDir, agent.ComposerId, agent.ComposerId, fromFolder, target.FolderPath);
                    log.Add($"{agent.Title}: memory store paths updated.");
                }

                foreach (var extra in agent.SubagentStoreDirs)
                {
                    if (Directory.Exists(extra))
                        RewriteTree(extra, agent.ComposerId, agent.ComposerId, fromFolder, target.FolderPath);
                }

                foreach (var dir in agent.WaypointDirs)
                {
                    if (!Directory.Exists(dir))
                        continue;
                    RewriteTree(dir, agent.ComposerId, agent.ComposerId, fromFolder, target.FolderPath);
                    RewriteWaypointMetadata(dir, target);
                }

                var header = RemapText(agent.HeaderJson, agent.ComposerId, agent.ComposerId, fromFolder, target.FolderPath);
                SqliteStateStore.RetargetComposer(paths, agent, target, header, cancellationToken);
                moved++;
                log.Add($"{agent.Title}: chat list now points at {target.Label}.");
            }
            catch (Exception ex)
            {
                warnings.Add($"{agent.Title}: {UserFacingError.From(ex)}");
            }
        }

        if (moved == 0)
            return Fail(warnings.Count > 0 ? warnings[0] : "Nothing could be assigned.");

        return new AgentTransferResult
        {
            Success = true,
            OutputPath = target.FolderPath,
            FilesCopied = files,
            BytesCopied = bytes,
            Warnings = warnings,
            Log = log
        };
    }

    public AgentTransferResult Delete(
        IReadOnlyList<AgentRecord> agents,
        CursorPaths paths,
        IProgress<SyncProgress> progress,
        CancellationToken cancellationToken)
    {
        var selected = agents
            .Where(agent => AgentCatalog.IsComposerId(agent.ComposerId))
            .GroupBy(agent => agent.ComposerId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (selected.Count == 0)
            return Fail("Check one or more chats to delete.");

        var warnings = new List<string>();
        var log = new List<string>();
        var files = 0;
        long bytes = 0;
        var removed = 0;

        try
        {
            progress.Report(new SyncProgress { Message = "Updating the chat list…" });
            SqliteStateStore.DeleteComposers(paths, selected, cancellationToken);
            log.Add($"Removed {selected.Count} chat{(selected.Count == 1 ? "" : "s")} from Cursor’s sidebar database.");
        }
        catch (Exception ex)
        {
            return Fail("The chats could not be removed from Cursor’s database: " + UserFacingError.From(ex));
        }

        foreach (var agent in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new SyncProgress { Message = $"Deleting {agent.Title}…" });
            TryDeleteUnder(paths.Projects, agent.TranscriptDir, "transcript", agent.Title, warnings, log, ref files, ref bytes);
            TryDeleteUnder(paths.AgentStores, agent.StoreDir, "memory store", agent.Title, warnings, log, ref files, ref bytes);
            foreach (var extra in agent.SubagentStoreDirs)
                TryDeleteUnder(paths.AgentStores, extra, "subagent store", agent.Title, warnings, log, ref files, ref bytes);
            foreach (var waypoint in agent.WaypointDirs)
                TryDeleteUnder(paths.Checkpoints, waypoint, "checkpoint", agent.Title, warnings, log, ref files, ref bytes);

            removed++;
        }

        if (removed == 0)
            return Fail(warnings.Count > 0 ? warnings[0] : "Nothing could be deleted.");

        return new AgentTransferResult
        {
            Success = true,
            FilesCopied = removed,
            BytesCopied = bytes,
            Warnings = warnings,
            Log = log
        };
    }

    public AgentTransferResult SetArchived(
        IReadOnlyList<AgentRecord> agents,
        CursorPaths paths,
        bool archived,
        IProgress<SyncProgress> progress,
        CancellationToken cancellationToken)
    {
        var selected = agents
            .Where(agent => AgentCatalog.IsComposerId(agent.ComposerId))
            .GroupBy(agent => agent.ComposerId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (selected.Count == 0)
            return Fail(archived ? "Check one or more chats to archive." : "Check one or more chats to restore from the archive.");

        var log = new List<string>();
        try
        {
            progress.Report(new SyncProgress
            {
                Message = archived ? "Archiving chats…" : "Restoring archived chats…"
            });
            SqliteStateStore.SetArchived(paths, selected, archived, cancellationToken);
            log.Add(archived
                ? $"Marked {selected.Count} chat{(selected.Count == 1 ? "" : "s")} as archived in Cursor’s chat list."
                : $"Restored {selected.Count} chat{(selected.Count == 1 ? "" : "s")} to Cursor’s Agents list.");
        }
        catch (Exception ex)
        {
            return Fail("The chats could not be updated in Cursor’s database: " + UserFacingError.From(ex));
        }

        return new AgentTransferResult
        {
            Success = true,
            FilesCopied = selected.Count,
            Warnings = [],
            Log = log
        };
    }

    private static bool TryDeleteUnder(
        string root,
        string? path,
        string kind,
        string title,
        List<string> warnings,
        List<string> log,
        ref int files,
        ref long bytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        try
        {
            var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (full.Length <= prefix.Length || !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{title}: refused to delete {kind} outside Cursor’s data folders.");
                return false;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                files++;
                try { bytes += new FileInfo(file).Length; }
                catch { }
            }

            Directory.Delete(path, recursive: true);
            log.Add($"{title}: deleted {kind}.");
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"{title}: {kind} could not be deleted. {UserFacingError.From(ex)}");
            return false;
        }
    }

    private static AgentTransferResult Fail(string error) =>
        new() { Success = false, Error = error };

    private static string? ResolveExistingDir(params string?[] candidates) =>
        candidates.FirstOrDefault(dir => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir));

    private static void MoveTree(
        string source,
        string destination,
        ref int files,
        ref long bytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            Directory.Move(source, destination);
            foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
            {
                files++;
                try { bytes += new FileInfo(file).Length; }
                catch { }
            }
            return;
        }
        catch
        {
            // copy then delete if a move across volumes fails
        }

        CopyTree(source, destination, null, null, ref files, ref bytes, cancellationToken);
        try { Directory.Delete(source, recursive: true); }
        catch { }
    }

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
        updated = ReplaceInsensitivePlain(updated, CursorWorkspaceLocator.ToFileUri(from), CursorWorkspaceLocator.ToFileUri(to));
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
