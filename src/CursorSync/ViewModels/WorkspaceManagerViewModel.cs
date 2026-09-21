using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CursorSync.Models;
using CursorSync.Services;
using Microsoft.Win32;

namespace CursorSync.ViewModels;

public partial class WorkspaceManagerViewModel : ObservableObject
{
    private readonly MainViewModel _host;
    private readonly AgentTransferService _transfer = new();
    private List<CursorWorkspaceInfo> _known = [];

    public WorkspaceManagerViewModel(MainViewModel host)
    {
        _host = host;
        SafetyNote = "CursorSync never invents Cursor workspace IDs. Add or split only after Cursor has opened the folder. Rename and edit keep the existing ID. Merge and assign move agents without removing the old workspace record. Delete removes Cursor’s workspace cache only — your project files, transcripts, and agent memory stay on disk.";
        EmptyText = "No workspaces found yet.";
    }

    public ObservableCollection<WorkspaceRowViewModel> Items { get; } = [];
    public ObservableCollection<WorkspaceOption> MergeTargets { get; } = [];
    public ObservableCollection<SplitChildViewModel> SplitChildren { get; } = [];

    [ObservableProperty] private WorkspaceRowViewModel? _selectedWorkspace;
    [ObservableProperty] private WorkspaceOption? _selectedMergeTarget;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _showEmpty = true;
    [ObservableProperty] private string _emptyText = "No workspaces found yet.";
    [ObservableProperty] private string _safetyNote = "";
    [ObservableProperty] private string _splitHint = "Select a workspace to see subfolders that can become their own Cursor windows.";
    [ObservableProperty] private string _mergeHint = "Check the workspaces whose agents should move, then pick the destination.";
    [ObservableProperty] private string _renameText = "";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedWorkspaceChanged(WorkspaceRowViewModel? value)
    {
        ReloadSplitChildren();
        RenameText = value?.Label ?? "";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        _host.BeginBackgroundWork("Scanning workspaces…");
        try
        {
            var settings = _host.Settings;
            var snapshot = await Task.Run(() =>
            {
                var paths = CursorPaths.FromSettings(settings);
                return (
                    Workspaces: CursorWorkspaceLocator.List(paths),
                    Agents: AgentCatalog.ListLocal(paths, CancellationToken.None)
                );
            });
            ApplyCatalog(snapshot.Workspaces, snapshot.Agents);
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
    private void AddWorkspace()
    {
        var folder = BrowseFolder("Choose the project folder Cursor should treat as a workspace");
        if (folder is null)
            return;

        var existing = CursorWorkspaceLocator.FindByFolder(_known, folder);
        if (existing is not null)
        {
            SelectedWorkspace = Items.FirstOrDefault(item => item.Id == existing.Id);
            _host.Notify($"“{existing.Label}” is already a Cursor workspace.", "success");
            return;
        }

        _host.Confirm(
            "Open this folder in Cursor?",
            "CursorSync will not invent a workspace ID. Cursor has to open the folder once so it creates its own workspace. After the window loads, come back here and refresh.",
            () =>
            {
                CursorWorkspaceLocator.EnsureProjectTranscripts(CursorPaths.FromSettings(_host.Settings), folder);
                if (!CursorProcessService.TryOpenFolder(folder, out var error))
                    _host.Notify(error ?? "Could not start Cursor.", "error");
                else
                    _host.Notify("Cursor is opening that folder. Refresh this list after the window has loaded.", "success");
            });
    }

    [RelayCommand]
    private void EditWorkspace()
    {
        var row = SelectedWorkspace;
        if (row is null)
        {
            _host.Notify("Select a workspace to edit.", "warn");
            return;
        }

        var folder = BrowseFolder("Choose the folder this workspace should point at");
        if (folder is null)
            return;

        if (CursorWorkspaceLocator.SameFolder(row.Info.FolderPath, folder))
        {
            _host.Notify("That is already this workspace’s folder.", "warn");
            return;
        }

        var clash = CursorWorkspaceLocator.FindByFolder(_known, folder);
        if (clash is not null && !string.Equals(clash.Id, row.Id, StringComparison.OrdinalIgnoreCase))
        {
            _host.Notify($"“{clash.Label}” already points at that folder.", "warn");
            return;
        }

        var agents = row.Agents;
        _host.Confirm(
            "Point this workspace at a new folder?",
            $"“{row.Label}” keeps its Cursor ID. The folder path will change to {folder}. {agents.Count} agent{(agents.Count == 1 ? "" : "s")} will have transcripts, memory, and sidebar metadata rewritten. Close Cursor first.",
            () => _ = EditAsync(row.Info, folder, agents),
            CursorRisk.High);
    }

    [RelayCommand]
    private void RenameWorkspace()
    {
        var row = SelectedWorkspace;
        if (row is null)
        {
            _host.Notify("Select a workspace to rename.", "warn");
            return;
        }

        if (!CursorWorkspaceLocator.TryBuildRenamePath(row.Info.FolderPath, RenameText, out var destination, out var error))
        {
            _host.Notify(error, "warn");
            return;
        }

        var newName = Path.GetFileName(destination.TrimEnd('\\', '/'));
        var agents = row.Agents;
        _host.Confirm(
            "Rename this workspace?",
            $"The folder will be renamed from “{row.Label}” to “{newName}”. Cursor’s workspace ID stays the same. {agents.Count} agent{(agents.Count == 1 ? "" : "s")} will have paths rewritten. Close Cursor first so files are not locked.",
            () => _ = RenameAsync(row.Info, destination, agents),
            CursorRisk.High);
    }

    [RelayCommand]
    private void OpenSelectedFolder()
    {
        var path = SelectedWorkspace?.FolderPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _host.Notify("That folder is missing on disk.", "warn");
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void OpenSelectedInCursor()
    {
        var path = SelectedWorkspace?.FolderPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _host.Notify("Select a workspace first.", "warn");
            return;
        }

        if (!CursorProcessService.TryOpenFolder(path, out var error))
            _host.Notify(error ?? "Could not start Cursor.", "error");
        else
            _host.Notify("Opening that folder in Cursor…", "success");
    }

    [RelayCommand]
    private void OpenSplitChildrenInCursor()
    {
        var selected = SplitChildren.Where(child => child.IsChecked).ToList();
        if (selected.Count == 0)
        {
            _host.Notify("Check the subfolders you want Cursor to open.", "warn");
            return;
        }

        _host.Confirm(
            "Open these folders in Cursor?",
            $"{selected.Count} folder{(selected.Count == 1 ? "" : "s")} will open in Cursor so it can create real workspace IDs. Refresh after those windows have loaded, then assign agents on the Agents page.",
            () =>
            {
                var opened = 0;
                foreach (var child in selected)
                {
                    CursorWorkspaceLocator.EnsureProjectTranscripts(CursorPaths.FromSettings(_host.Settings), child.FolderPath);
                    if (CursorProcessService.TryOpenFolder(child.FolderPath, out _))
                        opened++;
                }

                _host.Notify(
                    opened == 0
                        ? "Could not start Cursor."
                        : $"Opened {opened} folder{(opened == 1 ? "" : "s")} in Cursor. Refresh after they load.",
                    opened == 0 ? "error" : "success");
            });
    }

    [RelayCommand]
    private void SplitSelected()
    {
        var parent = SelectedWorkspace;
        if (parent is null)
        {
            _host.Notify("Select the parent workspace to split.", "warn");
            return;
        }

        var selected = SplitChildren.Where(child => child.IsChecked).ToList();
        if (selected.Count == 0)
        {
            _host.Notify("Check the subfolders that should become their own workspaces.", "warn");
            return;
        }

        var unknown = selected.Where(child => !child.IsRegistered).ToList();
        if (unknown.Count > 0)
        {
            _host.Confirm(
                "Open the new folders in Cursor?",
                $"{unknown.Count} of the checked folders are not Cursor workspaces yet. CursorSync will not invent IDs. Open them in Cursor once, refresh, then assign agents into those workspaces.",
                () =>
                {
                    foreach (var child in unknown)
                    {
                        CursorWorkspaceLocator.EnsureProjectTranscripts(CursorPaths.FromSettings(_host.Settings), child.FolderPath);
                        CursorProcessService.TryOpenFolder(child.FolderPath, out _);
                    }
                    _host.Notify("Cursor is opening the new folders. Refresh this list after they load.", "success");
                });
            return;
        }

        _host.Notify(
            "Those folders are already separate Cursor workspaces. Assign agents into them on the Agents page. The parent workspace folder is left in place.",
            "success");
    }

    [RelayCommand]
    private void MergeSelected()
    {
        var target = SelectedMergeTarget?.Info;
        if (target is null)
        {
            _host.Notify("Choose the destination workspace.", "warn");
            return;
        }

        var sources = Items.Where(item => item.IsChecked && !string.Equals(item.Id, target.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sources.Count == 0)
        {
            _host.Notify("Check one or more source workspaces to merge from. The destination should stay unchecked.", "warn");
            return;
        }

        var agents = sources.SelectMany(source => source.Agents).ToList();
        if (agents.Count == 0)
        {
            _host.Notify("The checked workspaces have no agents to move.", "warn");
            return;
        }

        _host.Confirm(
            "Merge agents into this workspace?",
            $"{agents.Count} agent{(agents.Count == 1 ? "" : "s")} from {sources.Count} workspace{(sources.Count == 1 ? "" : "s")} will move into {target.Label}, including transcripts and memory. Source workspace folders stay on disk. Close Cursor first.",
            () => _ = AssignAsync(agents, target, "Workspace merge"),
            CursorRisk.High);
    }

    [RelayCommand]
    private void DeleteWorkspace()
    {
        var row = SelectedWorkspace;
        if (row is null)
        {
            _host.Notify("Select a workspace to delete.", "warn");
            return;
        }

        ConfirmDelete([row]);
    }

    [RelayCommand]
    private void DeleteCheckedWorkspaces()
    {
        var selected = Items.Where(item => item.IsChecked).ToList();
        if (selected.Count == 0)
        {
            _host.Notify("Check one or more workspaces to delete, or select one and use Delete this workspace.", "warn");
            return;
        }

        ConfirmDelete(selected);
    }

    private void ConfirmDelete(List<WorkspaceRowViewModel> rows)
    {
        var agentCount = rows.Sum(row => row.Agents.Count);
        var names = rows.Count == 1
            ? $"“{rows[0].Label}”"
            : $"{rows.Count} workspaces";
        var agentText = agentCount == 0
            ? "No agents are attached."
            : $"{agentCount} agent{(agentCount == 1 ? "" : "s")} will stay on this PC under Other until you assign them again.";

        _host.Confirm(
            rows.Count == 1 ? "Delete this workspace?" : "Delete these workspaces?",
            $"{names} will be removed from Cursor’s workspace cache. Your project files on disk are not deleted. Transcripts and agent memory stay. {agentText} Close Cursor first. If you open the folder in Cursor later, Cursor may recreate the workspace.",
            () => _ = DeleteAsync(rows),
            CursorRisk.High,
            rows.Count == 1 ? "Delete workspace" : "Delete workspaces");
    }

    private async Task DeleteAsync(List<WorkspaceRowViewModel> rows)
    {
        try
        {
            if (!await EnsureCursorClosedForDatabaseAsync())
                return;

            AgentTransferResult? result = null;
            var guard = await _host.GuardAsync(
                "Deleting workspace",
                IntegrityPlan.Delete(rows.Select(row => row.Info).ToList(), rows.Sum(row => row.Agents.Count)),
                async () =>
                {
                    result = await ExecuteAsync("Deleting workspace…", _ =>
                        WorkspaceService.Delete(
                            CursorPaths.FromSettings(_host.Settings),
                            rows.Select(row => row.Info).ToList(),
                            rows.SelectMany(row => row.Agents).ToList(),
                            CancellationToken.None));
                    return result.Success;
                });
            if (!guard.Completed || result is null)
                return;

            var title = rows.Count == 1 ? rows[0].Label : $"{rows.Count} workspaces";
            _host.AddHistory(new SyncHistoryEntry
            {
                Utc = DateTime.UtcNow,
                Kind = "Delete workspace",
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
                _host.Notify($"Deleted {title}.{extra}", result.Warnings.Count > 0 ? "warn" : "success");
                await RefreshAsync();
            }
            else
            {
                _host.Notify(result.Error ?? "Could not delete that workspace.", "error");
            }
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
    }

    private async Task EditAsync(CursorWorkspaceInfo workspace, string folder, List<AgentRecord> agents)
    {
        try
        {
            var paths = CursorPaths.FromSettings(_host.Settings);
            CursorWorkspaceInfo? updated = null;
            AgentTransferResult? moved = null;
            var guard = await _host.GuardAsync(
                "Editing workspace",
                IntegrityPlan.Edit(workspace, folder, agents.Count),
                async () =>
                {
                    updated = await ExecuteAsync("Updating workspace folder…", _ =>
                    {
                        var result = CursorWorkspaceLocator.Retarget(paths, workspace, folder);
                        CursorWorkspaceLocator.EnsureProjectTranscripts(paths, result.FolderPath);
                        return result;
                    });

                    if (agents.Count == 0)
                        return true;

                    moved = await ExecuteAsync("Updating agent paths…", progress =>
                        _transfer.Assign(agents, updated, paths, progress, CancellationToken.None));
                    return moved.Success;
                });
            if (!guard.Completed || updated is null)
                return;

            if (agents.Count > 0 && moved is not null)
            {
                FinishAssign(moved, "Workspace edit", updated.Label);
                if (!moved.Success)
                    return;
            }
            else
            {
                _host.Notify($"“{updated.Label}” now points at {updated.FolderPath}.", "success");
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
    }

    private async Task RenameAsync(CursorWorkspaceInfo workspace, string destination, List<AgentRecord> agents)
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
                "Renaming workspace",
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
                FinishAssign(moved, "Workspace rename", updated.Label);
                if (!moved.Success)
                    return;
            }
            else
            {
                _host.Notify($"Renamed workspace to “{updated.Label}”.", "success");
            }

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
                "Assigning agents",
                IntegrityPlan.Assign(target, agents.Count),
                async () =>
                {
                    result = await ExecuteAsync("Assigning agents…", progress =>
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
            _host.Notify(result.Error ?? "Could not assign those agents.", "error");
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

    private void ApplyCatalog(IReadOnlyList<CursorWorkspaceInfo> workspaces, IReadOnlyList<AgentRecord> agents)
    {
        _known = workspaces.ToList();
        var previousId = SelectedWorkspace?.Id;
        var previousMerge = SelectedMergeTarget?.Id;
        var checkedIds = Items.Where(item => item.IsChecked).Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Items.Clear();
        MergeTargets.Clear();
        foreach (var workspace in workspaces)
        {
            var row = new WorkspaceRowViewModel(workspace, WorkspaceService.AgentsInWorkspace(workspace, agents).ToList());
            row.IsChecked = checkedIds.Contains(row.Id);
            Items.Add(row);
            MergeTargets.Add(new WorkspaceOption
            {
                Id = workspace.Id,
                Label = workspace.Label,
                FolderPath = workspace.FolderPath,
                Info = workspace
            });
        }

        ApplyFilter();
        SelectedWorkspace = Items.FirstOrDefault(item => item.Id == previousId) ?? Items.FirstOrDefault();
        SelectedMergeTarget = MergeTargets.FirstOrDefault(item => item.Id == previousMerge)
            ?? MergeTargets.FirstOrDefault();
        EmptyText = workspaces.Count == 0
            ? "No Cursor workspaces were found. Open a folder in Cursor, then refresh."
            : "No workspaces match this search.";
        UpdateMergeHint();
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? "";
        foreach (var item in Items)
        {
            item.IsVisible = query.Length == 0
                || item.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.FolderPath.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        ShowEmpty = Items.All(item => !item.IsVisible);
        if (SelectedWorkspace is not null && !SelectedWorkspace.IsVisible)
            SelectedWorkspace = Items.FirstOrDefault(item => item.IsVisible);
    }

    private void ReloadSplitChildren()
    {
        SplitChildren.Clear();
        if (SelectedWorkspace is null)
        {
            SplitHint = "Select a workspace to see subfolders that can become their own Cursor windows.";
            return;
        }

        var children = WorkspaceService.SuggestSplit(SelectedWorkspace.Info, _known);
        foreach (var child in children)
        {
            SplitChildren.Add(new SplitChildViewModel
            {
                FolderPath = child.FolderPath,
                Label = child.Label,
                StatusText = child.StatusText,
                IsRegistered = child.IsRegistered,
                IsChecked = true
            });
        }

        SplitHint = SplitChildren.Count == 0
            ? "This folder has no project-like subfolders to split out."
            : "Checked folders that Cursor does not know yet must be opened in Cursor once. CursorSync will not invent workspace IDs.";
    }

    private void UpdateMergeHint()
    {
        var destination = SelectedMergeTarget?.Label ?? "the destination workspace";
        MergeHint = $"Check source workspaces, then merge their agents into {destination}. Source folders stay on disk.";
    }

    partial void OnSelectedMergeTargetChanged(WorkspaceOption? value) => UpdateMergeHint();

    private static string? BrowseFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

public partial class WorkspaceRowViewModel : ObservableObject
{
    public WorkspaceRowViewModel(CursorWorkspaceInfo info, List<AgentRecord> agents)
    {
        Info = info;
        Id = info.Id;
        Label = info.Label;
        FolderPath = info.FolderPath;
        Agents = agents;
        AgentCountText = $"{agents.Count} agent{(agents.Count == 1 ? "" : "s")}";
        StatusText = !info.FolderExists
            ? "Folder missing"
            : info.IsReady ? "Ready" : "Open in Cursor to finish";
        IsVisible = true;
    }

    public CursorWorkspaceInfo Info { get; }
    public string Id { get; }
    public string Label { get; }
    public string FolderPath { get; }
    public string StatusText { get; }
    public string AgentCountText { get; }
    public List<AgentRecord> Agents { get; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isVisible = true;
}

public partial class SplitChildViewModel : ObservableObject
{
    public required string FolderPath { get; init; }
    public required string Label { get; init; }
    public required string StatusText { get; init; }
    public bool IsRegistered { get; init; }

    [ObservableProperty] private bool _isChecked;
}
