using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CursorSync.Models;
using CursorSync.Services;

namespace CursorSync.ViewModels;

public partial class ProjectManagerViewModel : ObservableObject
{
    private readonly MainViewModel _host;
    private readonly AgentTransferService _transfer = new();

    public ProjectManagerViewModel(MainViewModel host)
    {
        _host = host;
        SafetyNote = "Delete and clean up remove only Cursor’s project cache under ~/.cursor/projects — never your source code. Move sends transcripts and memory into a workspace Cursor already knows. CursorSync will not invent workspace IDs. Renaming a linked project also renames the folder on disk.";
        EmptyText = "No Cursor project caches were found.";
        CleanupHint = "Unused or unusable project caches can be removed without deleting your repos.";
        MoveHint = "Choose a workspace Cursor already opened, then move this project’s agents into it.";
    }

    public ObservableCollection<ProjectRowViewModel> Items { get; } = [];
    public ObservableCollection<WorkspaceOption> MoveTargets { get; } = [];

    [ObservableProperty] private ProjectRowViewModel? _selectedProject;
    [ObservableProperty] private WorkspaceOption? _selectedMoveTarget;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _showEmpty = true;
    [ObservableProperty] private string _emptyText = "No Cursor project caches were found.";
    [ObservableProperty] private string _safetyNote = "";
    [ObservableProperty] private string _cleanupHint = "";
    [ObservableProperty] private string _moveHint = "";
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private int _cleanupCount;
    [ObservableProperty] private string _renameHint = "Select a project to rename.";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedProjectChanged(ProjectRowViewModel? value)
    {
        RenameText = value is null
            ? ""
            : value.CanRenameFolder
                ? Path.GetFileName(value.FolderPath?.TrimEnd('\\', '/') ?? value.Label)
                : value.Slug;
        UpdateHints();
    }

    partial void OnSelectedMoveTargetChanged(WorkspaceOption? value) => UpdateHints();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        _host.BeginBackgroundWork("Scanning project caches…");
        try
        {
            var settings = _host.Settings;
            var snapshot = await Task.Run(() =>
            {
                var paths = CursorPaths.FromSettings(settings);
                var workspaces = CursorWorkspaceLocator.List(paths);
                var agents = AgentCatalog.ListLocal(paths, CancellationToken.None);
                return (
                    Workspaces: workspaces,
                    Agents: agents,
                    Projects: ProjectService.List(paths, workspaces, agents)
                );
            });
            ApplyCatalog(snapshot.Workspaces, snapshot.Agents, snapshot.Projects);
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
        finally
        {
            IsLoading = false;
            _host.EndBackgroundWork();
        }
    }

    [RelayCommand]
    private void MoveToWorkspace()
    {
        var row = SelectedProject;
        var target = SelectedMoveTarget?.Info;
        if (row is null)
        {
            _host.Notify("Select a project cache first.", "warn");
            return;
        }

        if (target is null)
        {
            _host.Notify("Choose a destination workspace Cursor already knows.", "warn");
            return;
        }

        if (!target.IsReady)
        {
            _host.Notify($"Open “{target.Label}” in Cursor once so it becomes a real workspace, then move the data.", "warn");
            return;
        }

        var agents = row.Agents;
        if (agents.Count == 0)
        {
            _host.Notify("That project cache has no agent transcripts to move.", "warn");
            return;
        }

        if (string.Equals(row.Slug, target.Slug, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(row.FolderPath) || CursorWorkspaceLocator.SameFolder(row.FolderPath, target.FolderPath)))
        {
            _host.Notify($"Those agents already belong to “{target.Label}”.", "warn");
            return;
        }

        _host.Confirm(
            "Move this project into a workspace?",
            $"{agents.Count} agent{(agents.Count == 1 ? "" : "s")} from “{row.Label}” will move into “{target.Label}”, including transcripts and memory. The project cache is not converted into a new workspace ID. Close Cursor first.",
            () => _ = AssignAsync(agents, target, "Move project"),
            CursorRisk.High);
    }

    [RelayCommand]
    private void RenameProject()
    {
        var row = SelectedProject;
        if (row is null)
        {
            _host.Notify("Select a project to rename.", "warn");
            return;
        }

        if (row.CanRenameFolder && row.Workspace is not null)
        {
            if (!CursorWorkspaceLocator.TryBuildRenamePath(row.Workspace.FolderPath, RenameText, out var destination, out var error))
            {
                _host.Notify(error, "warn");
                return;
            }

            var newName = Path.GetFileName(destination.TrimEnd('\\', '/'));
            _host.Confirm(
                "Rename this project folder?",
                $"The folder will be renamed from “{row.Label}” to “{newName}”. Cursor’s workspace ID stays the same. {row.Agents.Count} agent{(row.Agents.Count == 1 ? "" : "s")} will have paths rewritten. Close Cursor first so files are not locked.",
                () => _ = RenameFolderAsync(row.Workspace, destination, row.Agents),
                CursorRisk.High);
            return;
        }

        if (!CursorWorkspaceLocator.TryBuildProjectCacheName(RenameText, out var slug, out var cacheError))
        {
            _host.Notify(cacheError, "warn");
            return;
        }

        if (string.Equals(slug, row.Slug, StringComparison.OrdinalIgnoreCase))
        {
            _host.Notify("That is already this project cache’s name.", "warn");
            return;
        }

        var destinationCache = Path.Combine(CursorPaths.FromSettings(_host.Settings).Projects, slug);
        _host.Confirm(
            "Rename this project cache?",
            $"Cursor’s project cache will be renamed from “{row.Slug}” to “{slug}”. Your source code is not renamed. Close Cursor first.",
            () => _ = RenameCacheAsync(row.Info, destinationCache),
            CursorRisk.High);
    }

    [RelayCommand]
    private void OpenSelectedCache()
    {
        var path = SelectedProject?.ProjectDir;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _host.Notify("That project cache is missing.", "warn");
            return;
        }

        OpenExplorer(path);
    }

    [RelayCommand]
    private void OpenSelectedFolder()
    {
        var path = SelectedProject?.FolderPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _host.Notify("That project folder is missing on disk.", "warn");
            return;
        }

        OpenExplorer(path);
    }

    [RelayCommand]
    private void OpenSelectedInCursor()
    {
        var path = SelectedProject?.FolderPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _host.Notify("This cache is not linked to a folder Cursor already opened. Open the folder in Cursor first, then assign the agents.", "warn");
            return;
        }

        if (!CursorProcessService.TryOpenFolder(path, out var error))
            _host.Notify(error ?? "Could not start Cursor.", "error");
        else
            _host.Notify("Opening that folder in Cursor…", "success");
    }

    [RelayCommand]
    private void DeleteProject()
    {
        var row = SelectedProject;
        if (row is null)
        {
            _host.Notify("Select a project cache to delete.", "warn");
            return;
        }

        ConfirmDelete([row], cleanup: false);
    }

    [RelayCommand]
    private void DeleteCheckedProjects()
    {
        var selected = Items.Where(item => item.IsChecked).ToList();
        if (selected.Count == 0)
        {
            _host.Notify("Check one or more project caches to delete, or select one and use Delete this project.", "warn");
            return;
        }

        ConfirmDelete(selected, cleanup: false);
    }

    [RelayCommand]
    private void CleanupProjects()
    {
        var selected = Items.Where(item => item.CanCleanup).ToList();
        if (selected.Count == 0)
        {
            _host.Notify("There are no unused or unusable project caches to clean up.", "success");
            return;
        }

        ConfirmDelete(selected, cleanup: true);
    }

    private void ConfirmDelete(List<ProjectRowViewModel> rows, bool cleanup)
    {
        var agentCount = rows.Sum(row => row.Agents.Count);
        var names = rows.Count == 1
            ? $"“{rows[0].Label}”"
            : $"{rows.Count} project caches";
        var agentText = agentCount == 0
            ? "No agent transcripts are in these folders."
            : $"{agentCount} agent transcript{(agentCount == 1 ? "" : "s")} will be removed from this cache. Agent memory stays on this PC.";
        var title = cleanup
            ? (rows.Count == 1 ? "Clean up this unused project?" : "Clean up unused project caches?")
            : (rows.Count == 1 ? "Delete this project cache?" : "Delete these project caches?");
        var lead = cleanup
            ? $"{names} look unused or unusable (no matching workspace, missing folder, or empty cache)."
            : $"{names} will be removed from Cursor’s project cache.";

        _host.Confirm(
            title,
            $"{lead} Your source code is not deleted. {agentText} Close Cursor first.",
            () => _ = DeleteAsync(rows, cleanup ? "Clean up projects" : "Delete project"),
            CursorRisk.High,
            cleanup ? "Clean up" : (rows.Count == 1 ? "Delete project" : "Delete projects"));
    }

    private async Task DeleteAsync(List<ProjectRowViewModel> rows, string kind)
    {
        try
        {
            if (!await EnsureCursorClosedForDatabaseAsync())
                return;

            AgentTransferResult? result = null;
            var guard = await _host.GuardAsync(
                kind,
                IntegrityPlan.DeleteProjects(rows.Count, rows.Sum(row => row.Agents.Count)),
                async () =>
                {
                    result = await ExecuteAsync("Deleting project cache…", _ =>
                        ProjectService.Delete(
                            CursorPaths.FromSettings(_host.Settings),
                            rows.Select(row => row.Info).ToList(),
                            rows.SelectMany(row => row.Agents).ToList(),
                            CancellationToken.None));
                    return result.Success;
                });
            if (!guard.Completed || result is null)
                return;

            var title = rows.Count == 1 ? rows[0].Label : $"{rows.Count} projects";
            _host.AddHistory(new SyncHistoryEntry
            {
                Utc = DateTime.UtcNow,
                Kind = kind,
                Machine = _host.Settings.MachineName,
                Success = result.Success,
                Error = result.Error,
                FilesCopied = result.FilesCopied,
                BytesCopied = result.BytesCopied,
                Categories = [title],
                Warnings = result.Warnings.ToList(),
                HubPath = result.OutputPath ?? ""
            });

            if (result.Success)
            {
                var extra = result.Warnings.Count > 0 ? " " + result.Warnings[0] : "";
                _host.Notify($"Removed {title} from Cursor’s project cache.{extra}", result.Warnings.Count > 0 ? "warn" : "success");
                await RefreshAsync();
            }
            else
            {
                _host.Notify(result.Error ?? "Could not delete that project cache.", "error");
            }
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
    }

    private async Task RenameFolderAsync(CursorWorkspaceInfo workspace, string destination, List<AgentRecord> agents)
    {
        try
        {
            if (!await EnsureCursorClosedForDatabaseAsync())
                return;

            var paths = CursorPaths.FromSettings(_host.Settings);
            var oldPath = workspace.FolderPath;
            CursorWorkspaceInfo? updated = null;
            AgentTransferResult? moved = null;
            var guard = await _host.GuardAsync(
                "Renaming project",
                IntegrityPlan.Rename(workspace, destination, agents.Count),
                async () =>
                {
                    updated = await ExecuteAsync("Renaming folder…", _ =>
                        CursorWorkspaceLocator.RenameFolder(paths, workspace, destination));

                    if (agents.Count == 0)
                        return true;

                    moved = await ExecuteAsync("Updating agent paths…", progress =>
                        _transfer.Assign(agents, updated, paths, progress, CancellationToken.None));
                    return moved.Success;
                },
                () => CursorWorkspaceLocator.TryMoveFolder(destination, oldPath));

            if (!guard.Completed || updated is null)
                return;

            if (agents.Count > 0 && moved is not null)
            {
                FinishAssign(moved, "Project rename", updated.Label);
                if (!moved.Success)
                    return;
            }
            else
            {
                _host.Notify($"Renamed project to “{updated.Label}”.", "success");
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
    }

    private async Task RenameCacheAsync(CursorProjectInfo project, string destination)
    {
        try
        {
            if (!await EnsureCursorClosedForDatabaseAsync())
                return;

            var oldDir = project.ProjectDir;
            var guard = await _host.GuardAsync(
                "Renaming project cache",
                IntegrityPlan.RenameProjectCache(destination),
                async () =>
                {
                    await ExecuteAsync("Renaming project cache…", _ =>
                    {
                        CursorWorkspaceLocator.RenameProjectCache(
                            CursorPaths.FromSettings(_host.Settings),
                            project.Slug,
                            Path.GetFileName(destination.TrimEnd('\\', '/')));
                        return true;
                    });
                    return true;
                },
                () => CursorWorkspaceLocator.TryMoveFolder(destination, oldDir));

            if (!guard.Completed)
                return;

            _host.Notify($"Renamed project cache to “{Path.GetFileName(destination.TrimEnd('\\', '/'))}”.", "success");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
    }

    private async Task AssignAsync(List<AgentRecord> agents, CursorWorkspaceInfo target, string kind)
    {
        try
        {
            if (!await EnsureCursorClosedForDatabaseAsync())
                return;

            AgentTransferResult? result = null;
            var guard = await _host.GuardAsync(
                "Moving project data",
                IntegrityPlan.Assign(target, agents.Count),
                async () =>
                {
                    result = await ExecuteAsync("Moving project data…", progress =>
                        _transfer.Assign(agents, target, CursorPaths.FromSettings(_host.Settings), progress, CancellationToken.None));
                    return result.Success;
                });
            if (!guard.Completed || result is null)
                return;

            FinishAssign(result, kind, target.Label);
            if (result.Success)
                await RefreshAsync();
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
    }

    private void FinishAssign(AgentTransferResult result, string kind, string title)
    {
        _host.AddHistory(new SyncHistoryEntry
        {
            Utc = DateTime.UtcNow,
            Kind = kind,
            Machine = _host.Settings.MachineName,
            Success = result.Success,
            Error = result.Error,
            FilesCopied = result.FilesCopied,
            BytesCopied = result.BytesCopied,
            Categories = [title],
            Warnings = result.Warnings.ToList(),
            HubPath = result.OutputPath ?? ""
        });

        if (result.Success)
        {
            var extra = result.Warnings.Count > 0 ? " " + result.Warnings[0] : " Reopen the destination folder in Cursor to see the agents.";
            _host.Notify($"Moved agents into “{title}”.{extra}", result.Warnings.Count > 0 ? "warn" : "success");
        }
        else
        {
            _host.Notify(result.Error ?? "Could not move that project data.", "error");
        }
    }

    private async Task<bool> EnsureCursorClosedForDatabaseAsync()
    {
        _host.RefreshCursorStatus();
        if (!_host.IsCursorRunning)
            return true;

        if (_host.CloseCursorBeforeSync)
        {
            _host.BeginBackgroundWork("Closing Cursor…");
            var closed = await CursorProcessService.TryCloseAsync(TimeSpan.FromSeconds(12), CancellationToken.None);
            _host.RefreshCursorStatus();
            _host.EndBackgroundWork();
            if (closed)
                return true;
        }

        _host.Notify("Close Cursor first so the chat list can be updated.", "warn");
        return true;
    }

    private async Task<T> ExecuteAsync<T>(string status, Func<IProgress<SyncProgress>, T> work)
    {
        _host.BeginBackgroundWork(status);
        try
        {
            var progress = new Progress<SyncProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Message))
                    _host.SetBusyStatus(p.Message);
            });
            return await Task.Run(() => work(progress));
        }
        finally
        {
            _host.EndBackgroundWork();
        }
    }

    private void ApplyCatalog(
        IReadOnlyList<CursorWorkspaceInfo> workspaces,
        IReadOnlyList<AgentRecord> agents,
        IReadOnlyList<CursorProjectInfo> projects)
    {
        var previousSlug = SelectedProject?.Slug;
        var previousMove = SelectedMoveTarget?.Id;
        var checkedSlugs = Items.Where(item => item.IsChecked).Select(item => item.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Items.Clear();
        MoveTargets.Clear();
        foreach (var workspace in workspaces)
        {
            MoveTargets.Add(new WorkspaceOption
            {
                Id = workspace.Id,
                Label = workspace.Label,
                FolderPath = workspace.FolderPath,
                Info = workspace
            });
        }

        foreach (var project in projects)
        {
            var row = new ProjectRowViewModel(
                project,
                ProjectService.AgentsInProject(project.ProjectDir, project.Slug, agents).ToList());
            row.IsChecked = checkedSlugs.Contains(row.Slug);
            Items.Add(row);
        }

        ApplyFilter();
        SelectedProject = Items.FirstOrDefault(item => item.Slug == previousSlug) ?? Items.FirstOrDefault();
        SelectedMoveTarget = MoveTargets.FirstOrDefault(item => item.Id == previousMove)
            ?? MoveTargets.FirstOrDefault(item => item.Info?.IsReady == true)
            ?? MoveTargets.FirstOrDefault();
        EmptyText = projects.Count == 0
            ? "No folders were found in ~/.cursor/projects. Open a folder in Cursor, then refresh."
            : "No project caches match this search.";
        UpdateHints();
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? "";
        foreach (var item in Items)
        {
            item.IsVisible = query.Length == 0
                || item.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Slug.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (item.FolderPath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || item.StatusText.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        ShowEmpty = Items.All(item => !item.IsVisible);
        if (SelectedProject is not null && !SelectedProject.IsVisible)
            SelectedProject = Items.FirstOrDefault(item => item.IsVisible);
        CleanupCount = Items.Count(item => item.CanCleanup);
    }

    private void UpdateHints()
    {
        CleanupCount = Items.Count(item => item.CanCleanup);
        CleanupHint = CleanupCount == 0
            ? "Every project cache is linked to a folder Cursor still knows."
            : $"{CleanupCount} unused or unusable project cache{(CleanupCount == 1 ? "" : "s")} can be removed. Source code stays on disk.";

        var destination = SelectedMoveTarget?.Label ?? "a workspace Cursor already knows";
        MoveHint = SelectedProject is null
            ? "Select a project cache, then choose the destination workspace."
            : $"Move agents from “{SelectedProject.Label}” into {destination}. CursorSync will not invent a workspace ID.";

        RenameHint = SelectedProject is null
            ? "Select a project to rename."
            : SelectedProject.CanRenameFolder
                ? "This cache is linked to a folder on disk. Rename changes that folder name and keeps the Cursor workspace ID."
                : "This cache is not linked to a live folder. Only the ~/.cursor/projects name will change.";
    }

    private static void OpenExplorer(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}

public partial class ProjectRowViewModel : ObservableObject
{
    public ProjectRowViewModel(CursorProjectInfo info, List<AgentRecord> agents)
    {
        Info = info;
        Slug = info.Slug;
        Label = info.Label;
        ProjectDir = info.ProjectDir;
        FolderPath = info.FolderPath;
        Workspace = info.Workspace;
        CanRenameFolder = info.CanRenameFolder;
        CanCleanup = info.CanCleanup;
        Agents = agents;
        StatusText = info.StatusText;
        AgentCountText = $"{agents.Count} agent{(agents.Count == 1 ? "" : "s")}";
        SizeText = FileSizeFormatter.FromBytes(info.Bytes);
        DetailText = string.IsNullOrWhiteSpace(info.FolderPath) ? info.Slug : info.FolderPath;
        IsVisible = true;
    }

    public CursorProjectInfo Info { get; }
    public string Slug { get; }
    public string Label { get; }
    public string ProjectDir { get; }
    public string? FolderPath { get; }
    public CursorWorkspaceInfo? Workspace { get; }
    public bool CanRenameFolder { get; }
    public bool CanCleanup { get; }
    public string StatusText { get; }
    public string AgentCountText { get; }
    public string SizeText { get; }
    public string DetailText { get; }
    public List<AgentRecord> Agents { get; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isVisible = true;
}
