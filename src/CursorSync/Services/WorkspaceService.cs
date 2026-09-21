using CursorSync.Models;

namespace CursorSync.Services;

public static class WorkspaceService
{
    public static IReadOnlyList<WorkspaceSplitCandidate> SuggestSplit(
        CursorWorkspaceInfo parent,
        IReadOnlyList<CursorWorkspaceInfo> known)
    {
        return CursorWorkspaceLocator.SuggestSplitFolders(parent)
            .Select(folder =>
            {
                var match = CursorWorkspaceLocator.FindByFolder(known, folder);
                return new WorkspaceSplitCandidate
                {
                    FolderPath = folder,
                    Label = Path.GetFileName(folder.TrimEnd('\\', '/')),
                    Workspace = match,
                    IsRegistered = match is not null,
                    IsReady = match?.IsReady ?? false
                };
            })
            .ToList();
    }

    public static IReadOnlyList<AgentRecord> AgentsInWorkspace(
        CursorWorkspaceInfo workspace,
        IEnumerable<AgentRecord> agents)
    {
        return agents
            .Where(agent =>
                string.Equals(agent.WorkspaceId, workspace.Id, StringComparison.OrdinalIgnoreCase)
                || CursorWorkspaceLocator.SameFolder(agent.WorkspacePath, workspace.FolderPath))
            .ToList();
    }

    public static AgentTransferResult Delete(
        CursorPaths paths,
        IReadOnlyList<CursorWorkspaceInfo> workspaces,
        IReadOnlyList<AgentRecord> agents,
        CancellationToken cancellationToken)
    {
        if (workspaces.Count == 0)
            return new AgentTransferResult { Success = false, Error = "Select a workspace to delete." };

        var warnings = new List<string>();
        var log = new List<string>();
        var removed = 0;

        try
        {
            var ids = workspaces.Select(workspace => workspace.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var attached = agents
                .Where(agent =>
                    ids.Contains(agent.WorkspaceId ?? "")
                    || workspaces.Any(workspace => CursorWorkspaceLocator.SameFolder(agent.WorkspacePath, workspace.FolderPath)))
                .Select(agent => agent.ComposerId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            SqliteStateStore.DetachComposers(paths, attached, cancellationToken);
            if (attached.Count > 0)
                log.Add($"Unlinked {attached.Count} agent{(attached.Count == 1 ? "" : "s")} from the deleted workspace{(workspaces.Count == 1 ? "" : "s")}.");
        }
        catch (Exception ex)
        {
            warnings.Add("Agents could not be unlinked from the chat list: " + UserFacingError.From(ex));
        }

        foreach (var workspace in workspaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CursorWorkspaceLocator.DeleteStorage(paths, workspace);
                removed++;
                log.Add($"Removed Cursor’s workspace record for {workspace.Label}.");
            }
            catch (Exception ex)
            {
                warnings.Add($"{workspace.Label}: {UserFacingError.From(ex)}");
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
}
