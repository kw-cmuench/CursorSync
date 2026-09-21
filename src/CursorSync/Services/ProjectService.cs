using CursorSync.Models;

namespace CursorSync.Services;

public static class ProjectService
{
    public static IReadOnlyList<CursorProjectInfo> List(
        CursorPaths paths,
        IReadOnlyList<CursorWorkspaceInfo> workspaces,
        IReadOnlyList<AgentRecord> agents)
    {
        if (!Directory.Exists(paths.Projects))
            return [];

        var bySlug = workspaces
            .Where(workspace => !string.IsNullOrWhiteSpace(workspace.Slug))
            .GroupBy(workspace => workspace.Slug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(workspace => workspace.IsReady).First(),
                StringComparer.OrdinalIgnoreCase);

        var list = new List<CursorProjectInfo>();
        foreach (var dir in Directory.EnumerateDirectories(paths.Projects))
        {
            var slug = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(slug) || slug is "." or "..")
                continue;

            bySlug.TryGetValue(slug, out var workspace);
            var projectAgents = AgentsInProject(dir, slug, agents);
            var stats = Measure(dir);
            var folderExists = workspace?.FolderExists == true;
            var empty = stats.Files == 0;
            var health = Classify(workspace, folderExists, empty);
            list.Add(new CursorProjectInfo
            {
                Slug = slug,
                ProjectDir = dir,
                Label = workspace?.Label ?? slug,
                Workspace = workspace,
                FolderPath = workspace?.FolderPath,
                FolderExists = folderExists,
                CanRenameFolder = folderExists,
                CanCleanup = health != ProjectHealth.InUse,
                Health = health,
                StatusText = StatusText(health, workspace, empty),
                AgentCount = projectAgents.Count,
                Files = stats.Files,
                Bytes = stats.Bytes,
                LastWriteUtc = stats.LastWriteUtc
            });
        }

        return list
            .OrderBy(project => project.Health)
            .ThenBy(project => project.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<AgentRecord> AgentsInProject(
        string projectDir,
        string slug,
        IEnumerable<AgentRecord> agents)
    {
        return agents
            .Where(agent =>
                string.Equals(agent.ProjectSlug, slug, StringComparison.OrdinalIgnoreCase)
                || IsUnder(projectDir, agent.TranscriptDir))
            .ToList();
    }

    public static IReadOnlyList<CursorProjectInfo> CleanupCandidates(IEnumerable<CursorProjectInfo> projects) =>
        projects.Where(project => project.CanCleanup).ToList();

    public static AgentTransferResult Delete(
        CursorPaths paths,
        IReadOnlyList<CursorProjectInfo> projects,
        IReadOnlyList<AgentRecord> agents,
        CancellationToken cancellationToken)
    {
        if (projects.Count == 0)
            return new AgentTransferResult { Success = false, Error = "Select a project cache to delete." };

        var warnings = new List<string>();
        var log = new List<string>();
        var removed = 0;

        var slugs = projects.Select(project => project.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var attached = agents
            .Where(agent =>
                slugs.Contains(agent.ProjectSlug ?? "")
                || projects.Any(project => IsUnder(project.ProjectDir, agent.TranscriptDir)))
            .Select(agent => agent.ComposerId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (attached.Count > 0)
        {
            try
            {
                SqliteStateStore.DetachComposers(paths, attached, cancellationToken);
                log.Add($"Unlinked {attached.Count} agent{(attached.Count == 1 ? "" : "s")} from the deleted project cache.");
            }
            catch (Exception ex)
            {
                warnings.Add("Agents could not be unlinked from the chat list: " + UserFacingError.From(ex));
            }
        }

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CursorWorkspaceLocator.DeleteProjectCache(paths, project.Slug);
                removed++;
                log.Add($"Removed Cursor’s project cache for {project.Label}.");
            }
            catch (Exception ex)
            {
                warnings.Add($"{project.Label}: {UserFacingError.From(ex)}");
            }
        }

        if (removed == 0)
            return new AgentTransferResult
            {
                Success = false,
                Error = warnings.Count > 0 ? warnings[0] : "Nothing could be deleted.",
                Warnings = warnings,
                Log = log
            };

        return new AgentTransferResult
        {
            Success = true,
            FilesCopied = removed,
            Warnings = warnings,
            Log = log
        };
    }

    private static ProjectHealth Classify(CursorWorkspaceInfo? workspace, bool folderExists, bool empty)
    {
        if (workspace is null)
            return ProjectHealth.Unused;
        if (!folderExists || empty)
            return ProjectHealth.Unusable;
        return ProjectHealth.InUse;
    }

    private static string StatusText(ProjectHealth health, CursorWorkspaceInfo? workspace, bool empty) =>
        health switch
        {
            ProjectHealth.InUse when workspace is { IsReady: false } => "In use · open in Cursor to finish",
            ProjectHealth.InUse => "In use",
            ProjectHealth.Unused when empty => "Unused · empty",
            ProjectHealth.Unused => "Unused · no workspace",
            ProjectHealth.Unusable when workspace is { FolderExists: false } => "Unusable · folder missing",
            ProjectHealth.Unusable when empty => "Unusable · empty cache",
            _ => "Unusable"
        };

    private static bool IsUnder(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full) || File.Exists(full))
                full = full.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            else
                full += Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static (int Files, long Bytes, DateTime LastWriteUtc) Measure(string dir)
    {
        var files = 0;
        long bytes = 0;
        var lastWrite = DateTime.MinValue;
        try
        {
            lastWrite = Directory.GetLastWriteTimeUtc(dir);
            foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            }))
            {
                try
                {
                    var info = new FileInfo(file);
                    files++;
                    bytes += info.Length;
                    if (info.LastWriteTimeUtc > lastWrite)
                        lastWrite = info.LastWriteTimeUtc;
                }
                catch
                {
                    // skip locked files
                }
            }
        }
        catch
        {
            // unreadable cache
        }

        return (files, bytes, lastWrite == DateTime.MinValue ? DateTime.UtcNow : lastWrite);
    }
}
