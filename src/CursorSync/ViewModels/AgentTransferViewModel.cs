using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CursorSync.Models;
using CursorSync.Services;
using Microsoft.Win32;

namespace CursorSync.ViewModels;

public partial class AgentTransferViewModel : ObservableObject
{
    private readonly MainViewModel _host;
    private readonly SettingsStore _store;
    private readonly AgentTransferService _transfer = new();
    private List<CursorWorkspaceInfo> _workspaces = [];
    private readonly List<WorkspaceGroupViewModel> _allGroups = [];

    public AgentTransferViewModel(MainViewModel host, SettingsStore store)
    {
        _host = host;
        _store = store;
        IncludeTranscript = true;
        IncludeStore = true;
        IncludeWaypoints = true;
        Preview = new ConversationPreview();
        UpdateSelectionSummary();
        UpdateRestoreHint();
    }

    public ObservableCollection<WorkspaceGroupViewModel> Workspaces { get; } = [];
    public ObservableCollection<WorkspaceOption> TargetWorkspaces { get; } = [];
    public ObservableCollection<AgentBackupInfo> Backups { get; } = [];

    [ObservableProperty] private WorkspaceOption? _selectedTargetWorkspace;
    [ObservableProperty] private AgentBackupInfo? _selectedBackup;
    [ObservableProperty] private AgentItemViewModel? _focusedAgent;
    [ObservableProperty] private ConversationPreview _preview;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _includeTranscript = true;
    [ObservableProperty] private bool _includeStore = true;
    [ObservableProperty] private bool _includeWaypoints = true;
    [ObservableProperty] private bool _forceRestoreTarget;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _emptyAgentsText = "No agents found yet.";
    [ObservableProperty] private bool _showEmptyAgents = true;
    [ObservableProperty] private string _selectionSummary = "Nothing selected";
    [ObservableProperty] private string _backupButtonText = "Back up selected";
    [ObservableProperty] private string _restoreHint = "Select a saved backup, then choose whether agents return to their original folders or one destination.";
    [ObservableProperty] private AgentListFilter _listFilter = AgentListFilter.Active;

    private int _localAgentCount;
    private int _archivedAgentCount;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnListFilterChanged(AgentListFilter value) => ApplyFilter();
    partial void OnFocusedAgentChanged(AgentItemViewModel? value)
    {
        foreach (var agent in _allGroups.SelectMany(g => g.Agents))
            agent.IsPreviewing = agent == value;
        Preview = ConversationPreviewService.Load(value?.Record);
    }
    partial void OnForceRestoreTargetChanged(bool value) => UpdateRestoreHint();
    partial void OnSelectedBackupChanged(AgentBackupInfo? value) => UpdateRestoreHint();
    partial void OnSelectedTargetWorkspaceChanged(WorkspaceOption? value) => UpdateRestoreHint();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        _host.BeginBackgroundWork("Scanning workspaces and agents…");
        try
        {
            var settings = _host.Settings;
            var roots = BackupRoots(settings).ToList();
            var snapshot = await Task.Run(() =>
            {
                var paths = CursorPaths.FromSettings(settings);
                return (
                    Agents: AgentCatalog.ListLocal(paths, CancellationToken.None),
                    Workspaces: CursorWorkspaceLocator.List(paths),
                    Backups: AgentCatalog.ListBackups(roots)
                );
            });
            ApplyCatalog(snapshot.Agents, snapshot.Workspaces, snapshot.Backups);
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

    private async Task RefreshBackupListAsync(string? preferPath = null)
    {
        _host.BeginBackgroundWork("Updating backup list…");
        try
        {
            var roots = BackupRoots(_host.Settings).ToList();
            var previousBackup = preferPath ?? SelectedBackup?.FolderPath;
            var backups = await Task.Run(() => AgentCatalog.ListBackups(roots));
            Backups.Clear();
            foreach (var backup in backups)
                Backups.Add(backup);
            SelectedBackup = Backups.FirstOrDefault(b => string.Equals(b.FolderPath, previousBackup, StringComparison.OrdinalIgnoreCase))
                ?? Backups.FirstOrDefault();
            UpdateRestoreHint();
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
        finally
        {
            _host.EndBackgroundWork();
        }
    }

    private void ApplyCatalog(
        IReadOnlyList<AgentRecord> agents,
        IReadOnlyList<CursorWorkspaceInfo> workspaces,
        IReadOnlyList<AgentBackupInfo> backups)
    {
        _workspaces = workspaces.ToList();

        var checkedIds = _allGroups
            .SelectMany(g => g.Agents)
            .Where(a => a.IsSelected)
            .Select(a => a.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hadSelection = checkedIds.Count > 0;
        var previousFocus = FocusedAgent?.Id;
        var previousBackup = SelectedBackup?.FolderPath;
        var previousTarget = SelectedTargetWorkspace?.Id;

        _allGroups.Clear();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workspace in workspaces)
        {
            var groupAgents = agents
                .Where(a => a.WorkspaceId == workspace.Id
                            || CursorWorkspaceLocator.SameFolder(a.WorkspacePath, workspace.FolderPath))
                .ToList();
            foreach (var agent in groupAgents)
                assigned.Add(agent.ComposerId);
            if (groupAgents.Count == 0)
                continue;
            _allGroups.Add(CreateGroup(workspace, groupAgents, checkedIds, hadSelection));
        }

        var leftover = agents.Where(a => !assigned.Contains(a.ComposerId)).ToList();
        if (leftover.Count > 0)
            _allGroups.Add(CreateGroup(null, leftover, checkedIds, hadSelection, "Other / unknown workspace", ""));

        TargetWorkspaces.Clear();
        foreach (var workspace in workspaces)
        {
            TargetWorkspaces.Add(new WorkspaceOption
            {
                Id = workspace.Id,
                Label = workspace.Label,
                FolderPath = workspace.FolderPath,
                Info = workspace
            });
        }

        Backups.Clear();
        foreach (var backup in backups)
            Backups.Add(backup);

        SelectedTargetWorkspace = TargetWorkspaces.FirstOrDefault(w => w.Id == previousTarget)
            ?? TargetWorkspaces.FirstOrDefault();
        SelectedBackup = Backups.FirstOrDefault(b => b.FolderPath == previousBackup) ?? Backups.FirstOrDefault();
        _localAgentCount = agents.Count;
        _archivedAgentCount = agents.Count(a => a.IsArchived);
        ApplyFilter();
        FocusedAgent = Workspaces.SelectMany(g => g.VisibleAgents).FirstOrDefault(a => a.Id == previousFocus)
            ?? Workspaces.SelectMany(g => g.VisibleAgents).FirstOrDefault();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var group in Workspaces)
            group.SetChecked(true);
        UpdateSelectionSummary();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var group in Workspaces)
            group.SetChecked(false);
        UpdateSelectionSummary();
    }

    [RelayCommand]
    private void BrowseTargetWorkspace()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the workspace folder to restore into",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
            return;

        var match = CursorWorkspaceLocator.FindByFolder(_workspaces, dialog.FolderName);
        if (match is null)
        {
            _host.Notify("Open that folder in Cursor once, then refresh. Restore can only target workspaces Cursor already knows.", "warn");
            return;
        }

        SelectedTargetWorkspace = TargetWorkspaces.FirstOrDefault(w => w.Id == match.Id);
    }

    [RelayCommand]
    private void BackupSelected()
    {
        var selected = SelectedAgents();
        if (selected.Count == 0)
        {
            _host.Notify("Check one or more workspaces (or agents) to back up.", "warn");
            return;
        }

        var workspaces = selected
            .Select(a => a.WorkspaceLabel ?? "Unknown")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _host.Confirm(
            "Back up selected workspaces?",
            $"{selected.Count} agent{(selected.Count == 1 ? "" : "s")} from {workspaces.Count} workspace{(workspaces.Count == 1 ? "" : "s")} will be saved locally"
            + (string.IsNullOrWhiteSpace(_host.Settings.HubPath) ? "." : " and copied to your sync folder."),
            () => _ = RunBackupAsync(selected),
            CursorRisk.High);
    }

    [RelayCommand]
    private void RestoreSelectedBackup()
    {
        if (SelectedBackup is null)
        {
            _host.Notify("Select a saved backup first.", "warn");
            return;
        }

        var target = SelectedTargetWorkspace?.Info;
        if (ForceRestoreTarget && target is null)
        {
            _host.Notify("Choose the workspace that should receive the agents.", "warn");
            return;
        }

        var label = SelectedBackup.Label;
        _host.Confirm(
            "Restore this backup?",
            ForceRestoreTarget
                ? $"All agents in “{label}” will be copied into {target?.Label}. Originals stay in the backup. Close Cursor so the sidebar can update."
                : $"“{label}” will be restored into matching workspaces on this PC. Unmatched agents go to {target?.Label ?? "the selected workspace"}. Close Cursor so the sidebar can update.",
            () => _ = RestoreBackupAsync(SelectedBackup.FolderPath, target),
            CursorRisk.High);
    }

    [RelayCommand]
    private void ImportZip()
    {
        Directory.CreateDirectory(_store.AgentBackupsFolder);
        var dialog = new OpenFileDialog
        {
            Title = "Choose an agent backup zip",
            Filter = "Zip archives (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = _store.AgentBackupsFolder
        };
        if (dialog.ShowDialog() == true)
            _ = PrepareImportAsync(dialog.FileName);
    }

    [RelayCommand]
    private void ImportFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a backup folder (CursorSync pack, copied ~/.cursor/projects, or sync payload)",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
            _ = PrepareImportAsync(dialog.FolderName);
    }

    private async Task PrepareImportAsync(string path)
    {
        _host.BeginBackgroundWork("Checking the backup…");
        AgentImportInspection inspection;
        try
        {
            inspection = await Task.Run(() => AgentImportService.Inspect(path));
        }
        catch (Exception ex)
        {
            _host.EndBackgroundWork();
            _host.Notify(UserFacingError.From(ex), "error");
            return;
        }
        _host.EndBackgroundWork();

        if (!inspection.CanImport)
        {
            var first = inspection.Report.Issues.FirstOrDefault(i => i.Severity == IntegritySeverity.Error)?.Message
                        ?? inspection.Summary;
            _host.Notify(first, "error");
            return;
        }

        var target = SelectedTargetWorkspace?.Info;
        if (_workspaces.Count == 0)
        {
            _host.Notify("Open a folder in Cursor first so there is a real workspace to import into.", "warn");
            return;
        }

        if (ForceRestoreTarget && target is null)
        {
            _host.Notify("Choose the workspace that should receive the imported agents.", "warn");
            return;
        }

        var extra = "";
        if (inspection.Report.HasWarnings)
        {
            var warnings = inspection.Report.Issues
                .Where(i => i.Severity == IntegritySeverity.Warning)
                .Select(i => i.Message)
                .Take(3);
            extra = Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, warnings);
        }

        var destination = ForceRestoreTarget
            ? $"All of them will be copied into “{target?.Label}”."
            : $"Matching folders on this PC get their agents back. Anything unmatched uses “{target?.Label ?? "the selected workspace"}”.";

        _host.Confirm(
            "Import this backup?",
            $"Integrity check passed. {inspection.Summary}. {destination} Originals stay in the backup. Close Cursor so the sidebar can update.{extra}",
            () => _ = ImportBackupAsync(path, target, inspection.AgentCount),
            CursorRisk.High,
            "Import");
    }

    private async Task ImportBackupAsync(string path, CursorWorkspaceInfo? fallback, int agentCount)
    {
        if (!await EnsureCursorClosedForDatabaseAsync())
            return;

        try
        {
            AgentTransferResult? result = null;
            var guard = await _host.GuardAsync(
                "Importing agents",
                IntegrityPlan.Import(fallback, agentCount),
                async () =>
                {
                    result = await ExecuteAsync("Importing agents…", progress =>
                        AgentImportService.Import(
                            path,
                            _workspaces,
                            fallback,
                            CursorPaths.FromSettings(_host.Settings),
                            IncludeTranscript,
                            IncludeStore,
                            IncludeWaypoints,
                            ForceRestoreTarget,
                            progress,
                            CancellationToken.None));
                    return result.Success;
                });
            if (!guard.Completed || result is null)
                return;

            RecordHistory("Import agents", result, Path.GetFileName(path.TrimEnd('\\', '/')));
            if (result.Success)
            {
                var extra = result.Warnings.Count > 0 ? " " + result.Warnings[0] : " Reopen Cursor on the target folder to see the agents.";
                _host.Notify($"Imported {agentCount} agent{(agentCount == 1 ? "" : "s")} — {result.FilesCopied} files.{extra}",
                    result.Warnings.Count > 0 ? "warn" : "success");
                await RefreshAsync();
            }
            else
            {
                _host.Notify(result.Error ?? "Import failed the integrity check or could not copy the agents.", "error");
            }
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
    }

    [RelayCommand]
    private void CopySelectedToWorkspace()
    {
        var selected = SelectedAgents();
        var target = SelectedTargetWorkspace?.Info;
        if (selected.Count == 0)
        {
            _host.Notify("Check the agents you want to copy.", "warn");
            return;
        }
        if (target is null)
        {
            _host.Notify("Choose a destination workspace on the right.", "warn");
            return;
        }

        _host.Confirm(
            "Copy agents into this workspace?",
            $"{selected.Count} agent{(selected.Count == 1 ? "" : "s")} will be copied into {target.Label}. The originals stay where they are. Close Cursor so the new copies appear.",
            () => _ = CopyLiveAsync(selected, target),
            CursorRisk.High);
    }

    [RelayCommand]
    private void AssignSelectedToWorkspace()
    {
        var selected = SelectedAgents();
        var target = SelectedTargetWorkspace?.Info;
        if (selected.Count == 0)
        {
            _host.Notify("Check the agents you want to assign.", "warn");
            return;
        }
        if (target is null)
        {
            _host.Notify("Choose a destination workspace on the right.", "warn");
            return;
        }

        _host.Confirm(
            "Assign agents to this workspace?",
            $"{selected.Count} agent{(selected.Count == 1 ? "" : "s")} will move into {target.Label}, including transcripts and memory. Composer IDs stay the same, so this is a move rather than a copy. Close Cursor so the sidebar can update.",
            () => _ = AssignLiveAsync(selected, target),
            CursorRisk.High);
    }

    [RelayCommand]
    private void DeleteSelectedAgents()
    {
        var selected = SelectedAgents();
        if (selected.Count == 0)
        {
            _host.Notify("Check one or more chats to delete.", "warn");
            return;
        }

        var names = selected.Count == 1
            ? $"“{selected[0].Title}”"
            : $"{selected.Count} chats";
        var scope = selected
            .Select(agent => agent.WorkspaceLabel ?? "Unknown workspace")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var where = scope.Count == 1 ? $" in {scope[0]}" : $" across {scope.Count} workspaces";

        _host.Confirm(
            selected.Count == 1 ? "Delete this chat?" : "Delete these chats?",
            $"{names}{where} will be removed from Cursor’s sidebar, including transcripts and memory for those chats. Your project files are not deleted. Close Cursor first. A safety snapshot can roll this back if that setting is on.",
            () => _ = DeleteSelectedAsync(selected),
            CursorRisk.High,
            selected.Count == 1 ? "Delete chat" : "Delete chats");
    }

    [RelayCommand]
    private void ArchiveSelectedAgents() => ConfirmArchiveChange(archived: true);

    [RelayCommand]
    private void UnarchiveSelectedAgents() => ConfirmArchiveChange(archived: false);

    private void ConfirmArchiveChange(bool archived)
    {
        var selected = SelectedAgents();
        if (selected.Count == 0)
        {
            _host.Notify(archived
                ? "Check one or more chats to archive."
                : "Check one or more archived chats to restore.", "warn");
            return;
        }

        if (archived && selected.All(agent => agent.IsArchived))
        {
            _host.Notify("Those chats are already archived. Switch to Archived to restore or delete them.", "warn");
            return;
        }

        if (!archived && selected.All(agent => !agent.IsArchived))
        {
            _host.Notify("Those chats are already in the active list.", "warn");
            return;
        }

        var names = selected.Count == 1
            ? $"“{selected[0].Title}”"
            : $"{selected.Count} chats";
        var scope = selected
            .Select(agent => agent.WorkspaceLabel ?? "Unknown workspace")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var where = scope.Count == 1 ? $" in {scope[0]}" : $" across {scope.Count} workspaces";

        _host.Confirm(
            archived
                ? (selected.Count == 1 ? "Archive this chat?" : "Archive these chats?")
                : (selected.Count == 1 ? "Restore this archived chat?" : "Restore these archived chats?"),
            archived
                ? $"{names}{where} will leave Cursor’s Agents list and move to Archived. Transcripts and memory stay on disk. Close Cursor so the list can update."
                : $"{names}{where} will return to Cursor’s Agents list. Close Cursor so the list can update.",
            () => _ = SetArchivedAsync(selected, archived),
            CursorRisk.Medium,
            archived
                ? (selected.Count == 1 ? "Archive chat" : "Archive chats")
                : (selected.Count == 1 ? "Restore chat" : "Restore chats"));
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        Directory.CreateDirectory(_store.AgentBackupsFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _store.AgentBackupsFolder,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void FocusAgent(AgentItemViewModel? agent)
    {
        if (agent is not null)
            FocusedAgent = agent;
    }

    public void OnGroupChanged() => UpdateSelectionSummary();

    private void UpdateRestoreHint()
    {
        var destination = SelectedTargetWorkspace?.Label ?? "the destination workspace";
        if (SelectedBackup is null)
        {
            RestoreHint = ForceRestoreTarget
                ? $"No backup selected yet. Restores and imports will send every agent into {destination}."
                : "Select a saved backup, or import a zip / copied folder. Agents return to their original workspaces when those folders exist here.";
            return;
        }

        RestoreHint = ForceRestoreTarget
            ? $"“{SelectedBackup.Label}” will be copied entirely into {destination}."
            : $"“{SelectedBackup.Label}” returns to matching folders on this PC. Anything unmatched uses {destination}.";
    }

    private WorkspaceGroupViewModel CreateGroup(
        CursorWorkspaceInfo? info,
        List<AgentRecord> agents,
        HashSet<string> checkedIds,
        bool hadSelection,
        string? fallbackLabel = null,
        string? fallbackPath = null)
    {
        var group = new WorkspaceGroupViewModel(
            info,
            info?.Label ?? fallbackLabel ?? "Unknown workspace",
            info?.FolderPath ?? fallbackPath ?? "",
            OnGroupChanged);
        foreach (var agent in agents)
        {
            var item = new AgentItemViewModel(agent, group);
            item.SetSelected(hadSelection ? checkedIds.Contains(agent.ComposerId) : false);
            group.Agents.Add(item);
        }

        group.RefreshState();
        return group;
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? "";
        Workspaces.Clear();
        foreach (var group in _allGroups)
        {
            group.ApplyFilter(query, ListFilter);
            if (group.IsVisible)
                Workspaces.Add(group);
        }

        ShowEmptyAgents = Workspaces.Count == 0;
        UpdateEmptyAgentsText();
        if (FocusedAgent is not null && Workspaces.SelectMany(g => g.VisibleAgents).All(a => a != FocusedAgent))
            FocusedAgent = Workspaces.SelectMany(g => g.VisibleAgents).FirstOrDefault();
        UpdateSelectionSummary();
    }

    private void UpdateEmptyAgentsText()
    {
        if (_localAgentCount == 0)
        {
            EmptyAgentsText = "No local agents were found. Open a chat in Cursor first, then refresh.";
            return;
        }

        if (ListFilter == AgentListFilter.Archived && _archivedAgentCount == 0 && string.IsNullOrWhiteSpace(SearchText))
        {
            EmptyAgentsText = "No archived chats. Archive a chat in Cursor’s Agents window, or archive checked chats here.";
            return;
        }

        if (ListFilter == AgentListFilter.Active && _archivedAgentCount == _localAgentCount && string.IsNullOrWhiteSpace(SearchText))
        {
            EmptyAgentsText = "Every chat on this PC is archived. Switch to Archived to restore or delete them.";
            return;
        }

        EmptyAgentsText = "No chats match this search.";
    }

    private List<AgentRecord> SelectedAgents() =>
        Workspaces.SelectMany(g => g.VisibleAgents).Where(a => a.IsSelected).Select(a => a.Record).ToList();

    private void UpdateSelectionSummary()
    {
        var agents = Workspaces.SelectMany(g => g.VisibleAgents).Where(a => a.IsSelected).ToList();
        var workspaces = agents
            .Select(a => a.Record.WorkspaceId ?? a.Record.WorkspaceLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var archived = agents.Count(a => a.Record.IsArchived);
        SelectionSummary = agents.Count == 0
            ? "Nothing selected"
            : archived == 0
                ? $"{workspaces} workspace{(workspaces == 1 ? "" : "s")} · {agents.Count} agent{(agents.Count == 1 ? "" : "s")}"
                : $"{workspaces} workspace{(workspaces == 1 ? "" : "s")} · {agents.Count} agent{(agents.Count == 1 ? "" : "s")} ({archived} archived)";
        BackupButtonText = agents.Count == 0
            ? "Back up selected"
            : $"Back up {SelectionSummary}";
    }

    private async Task RunBackupAsync(List<AgentRecord> agents)
    {
        var staging = Path.Combine(Path.GetTempPath(), "CursorSync", "pack-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(staging);

        try
        {
            var result = await ExecuteAsync("Backing up workspaces…", progress =>
                _transfer.BackupPack(agents, CursorPaths.FromSettings(_host.Settings), staging,
                    IncludeTranscript, IncludeStore, IncludeWaypoints, progress, CancellationToken.None));

            if (!result.Success)
            {
                RecordHistory("Workspace backup", result, PackName(agents));
                _host.Notify(result.Error ?? "Backup failed.", "error");
                return;
            }

            var zipPath = BackupArchive.UniquePath(_store.AgentBackupsFolder, BackupArchive.FileName());
            result = await ExecuteAsync("Compressing backup…", _ =>
            {
                BackupArchive.CompressDirectory(staging, zipPath);
                return result with { OutputPath = zipPath, BytesCopied = new FileInfo(zipPath).Length };
            });

            if (!result.Success)
            {
                RecordHistory("Workspace backup", result, PackName(agents));
                _host.Notify(result.Error ?? "Could not compress the backup.", "error");
                return;
            }

            if (!string.IsNullOrWhiteSpace(_host.Settings.HubPath))
            {
                try
                {
                    var hubFolder = Path.Combine(_host.Settings.HubPath, "payload", "agent-transfers");
                    Directory.CreateDirectory(hubFolder);
                    var hubFile = Path.Combine(hubFolder, Path.GetFileName(zipPath));
                    File.Copy(zipPath, hubFile, overwrite: true);
                    result = result with { OutputPath = hubFile };
                }
                catch (Exception ex)
                {
                    _host.Notify("Local backup succeeded, but copying to the sync folder failed: " + ex.Message, "warn");
                }
            }

            RecordHistory("Workspace backup", result, Path.GetFileNameWithoutExtension(zipPath));
            _host.Notify($"Backup saved as {Path.GetFileName(zipPath)} · {FileSizeFormatter.FromBytes(result.BytesCopied)}", "success");
            await RefreshBackupListAsync(zipPath);
        }
        finally
        {
            BackupArchive.TryDeleteDirectory(staging);
        }
    }

    private async Task RestoreBackupAsync(string path, CursorWorkspaceInfo? fallback)
    {
        if (!await EnsureCursorClosedForDatabaseAsync())
            return;

        string? extracted = null;
        try
        {
            var folder = path;
            if (BackupArchive.IsZip(path))
            {
                extracted = BackupArchive.ExtractToTemp(path);
                folder = extracted;
            }

            AgentTransferResult? result = null;
            var guard = await _host.GuardAsync(
                "Restoring backup",
                IntegrityPlan.Restore(fallback, 1),
                async () =>
                {
                    result = await ExecuteAsync("Restoring backup…", progress =>
                        _transfer.RestorePack(folder, _workspaces, fallback, CursorPaths.FromSettings(_host.Settings),
                            IncludeTranscript, IncludeStore, IncludeWaypoints, ForceRestoreTarget, progress, CancellationToken.None));
                    return result.Success;
                });
            if (!guard.Completed || result is null)
                return;

            FinishRestore(result, SelectedBackup?.Label ?? Path.GetFileName(path));
            if (result.Success)
                await RefreshAsync();
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
        finally
        {
            BackupArchive.TryDeleteDirectory(extracted);
        }
    }

    private async Task CopyLiveAsync(List<AgentRecord> agents, CursorWorkspaceInfo target)
    {
        var temp = Path.Combine(Path.GetTempPath(), "CursorSync", "pack-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(temp);
        try
        {
            if (!await EnsureCursorClosedForDatabaseAsync())
                return;

            var backup = await ExecuteAsync("Preparing copies…", progress =>
                _transfer.BackupPack(agents, CursorPaths.FromSettings(_host.Settings), temp,
                    IncludeTranscript, IncludeStore, IncludeWaypoints, progress, CancellationToken.None));
            if (!backup.Success)
            {
                _host.Notify(backup.Error ?? "Could not collect the selected agents.", "error");
                return;
            }

            AgentTransferResult? restore = null;
            var guard = await _host.GuardAsync(
                "Copying agents",
                IntegrityPlan.Restore(target, agents.Count),
                async () =>
                {
                    restore = await ExecuteAsync("Copying into the destination workspace…", progress =>
                        _transfer.RestorePack(temp, _workspaces, target, CursorPaths.FromSettings(_host.Settings),
                            IncludeTranscript, IncludeStore, IncludeWaypoints, true, progress, CancellationToken.None));
                    return restore.Success;
                });
            if (!guard.Completed || restore is null)
                return;

            FinishRestore(restore, target.Label);
            if (restore.Success)
                await RefreshAsync();
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private async Task AssignLiveAsync(List<AgentRecord> agents, CursorWorkspaceInfo target)
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

            RecordHistory("Assign agents", result, target.Label);
            if (result.Success)
            {
                var extra = result.Warnings.Count > 0 ? " " + result.Warnings[0] : " Reopen Cursor on the destination folder to see the agents.";
                _host.Notify($"Assigned {agents.Count} agent{(agents.Count == 1 ? "" : "s")} to “{target.Label}”.{extra}",
                    result.Warnings.Count > 0 ? "warn" : "success");
                await RefreshAsync();
            }
            else
            {
                _host.Notify(result.Error ?? "Could not assign those agents.", "error");
            }
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
    }

    private async Task DeleteSelectedAsync(List<AgentRecord> agents)
    {
        try
        {
            if (!await EnsureCursorClosedForDatabaseAsync())
                return;

            AgentTransferResult? result = null;
            var guard = await _host.GuardAsync(
                "Deleting chats",
                IntegrityPlan.DeleteAgents(agents.Count),
                async () =>
                {
                    result = await ExecuteAsync("Deleting chats…", progress =>
                        _transfer.Delete(agents, CursorPaths.FromSettings(_host.Settings), progress, CancellationToken.None));
                    return result.Success;
                });
            if (!guard.Completed || result is null)
                return;

            RecordHistory("Delete chats", result, agents.Count == 1 ? agents[0].Title : $"{agents.Count} chats");
            if (result.Success)
            {
                var extra = result.Warnings.Count > 0 ? " " + result.Warnings[0] : " Reopen Cursor to refresh the Agents list.";
                _host.Notify($"Deleted {agents.Count} chat{(agents.Count == 1 ? "" : "s")}.{extra}",
                    result.Warnings.Count > 0 ? "warn" : "success");
                await RefreshAsync();
            }
            else
            {
                _host.Notify(result.Error ?? "Could not delete those chats.", "error");
            }
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
    }

    private async Task SetArchivedAsync(List<AgentRecord> agents, bool archived)
    {
        try
        {
            if (!await EnsureCursorClosedForDatabaseAsync())
                return;

            AgentTransferResult? result = null;
            var verb = archived ? "Archiving chats" : "Restoring archived chats";
            var guard = await _host.GuardAsync(
                verb,
                IntegrityPlan.ArchiveAgents(agents.Count),
                async () =>
                {
                    result = await ExecuteAsync(archived ? "Archiving chats…" : "Restoring archived chats…", progress =>
                        _transfer.SetArchived(agents, CursorPaths.FromSettings(_host.Settings), archived, progress, CancellationToken.None));
                    return result.Success;
                });
            if (!guard.Completed || result is null)
                return;

            RecordHistory(archived ? "Archive chats" : "Unarchive chats", result,
                agents.Count == 1 ? agents[0].Title : $"{agents.Count} chats");
            if (result.Success)
            {
                var extra = result.Warnings.Count > 0 ? " " + result.Warnings[0] : " Reopen Cursor to refresh the Agents list.";
                _host.Notify(archived
                    ? $"Archived {agents.Count} chat{(agents.Count == 1 ? "" : "s")}.{extra}"
                    : $"Restored {agents.Count} chat{(agents.Count == 1 ? "" : "s")} from the archive.{extra}",
                    result.Warnings.Count > 0 ? "warn" : "success");
                await RefreshAsync();
                if (ListFilter != AgentListFilter.All)
                    ListFilter = archived ? AgentListFilter.Archived : AgentListFilter.Active;
            }
            else
            {
                _host.Notify(result.Error ?? "Could not update those chats.", "error");
            }
        }
        catch (Exception ex)
        {
            _host.Notify(UserFacingError.From(ex), "error");
        }
    }

    private void FinishRestore(AgentTransferResult result, string title)
    {
        RecordHistory("Workspace restore", result, title);
        if (result.Success)
        {
            var extra = result.Warnings.Count > 0 ? " " + result.Warnings[0] : " Reopen Cursor on the target folder to see the agents.";
            _host.Notify($"Restored “{title}” — {result.FilesCopied} files.{extra}", result.Warnings.Count > 0 ? "warn" : "success");
        }
        else
        {
            _host.Notify(result.Error ?? "Restore failed.", "error");
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

        _host.Notify("Close Cursor first so the chat list can be updated. Files can still copy, but new agents may stay hidden until the database is writable.", "warn");
        return true;
    }

    private async Task<AgentTransferResult> ExecuteAsync(string status, Func<IProgress<SyncProgress>, AgentTransferResult> work)
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
        catch (Exception ex)
        {
            return new AgentTransferResult { Success = false, Error = UserFacingError.From(ex) };
        }
        finally
        {
            _host.EndBackgroundWork();
        }
    }

    private void RecordHistory(string kind, AgentTransferResult result, string title)
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
    }

    private IEnumerable<string> BackupRoots(AppSettings settings)
    {
        yield return _store.AgentBackupsFolder;
        if (!string.IsNullOrWhiteSpace(settings.HubPath))
            yield return Path.Combine(settings.HubPath, "payload", "agent-transfers");
    }

    private static string PackName(IReadOnlyList<AgentRecord> agents)
    {
        var names = agents
            .Select(a => a.WorkspaceLabel ?? "workspace")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 1 ? names[0] : $"{names.Count}-workspaces";
    }
}

public sealed class WorkspaceOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string FolderPath { get; init; }
    public CursorWorkspaceInfo? Info { get; init; }

    public override string ToString() => Label;
}

public partial class WorkspaceGroupViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _suppress;

    public WorkspaceGroupViewModel(CursorWorkspaceInfo? info, string label, string folderPath, Action changed)
    {
        Info = info;
        Label = label;
        FolderPath = folderPath;
        _changed = changed;
    }

    public CursorWorkspaceInfo? Info { get; }
    public string Label { get; }
    public string FolderPath { get; }
    public ObservableCollection<AgentItemViewModel> Agents { get; } = [];

    [ObservableProperty] private bool? _isChecked = false;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private string _countText = "0 agents";

    public IEnumerable<AgentItemViewModel> VisibleAgents => Agents.Where(a => a.IsVisible);

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suppress)
            return;

        var selected = value != false;
        if (value is null)
        {
            _suppress = true;
            IsChecked = false;
            _suppress = false;
            selected = false;
        }

        foreach (var agent in Agents.Where(a => a.IsVisible))
            agent.SetSelected(selected);
        _changed();
    }

    public void SetChecked(bool value)
    {
        _suppress = true;
        IsChecked = value;
        _suppress = false;
        foreach (var agent in Agents.Where(a => a.IsVisible))
            agent.SetSelected(value);
    }

    public void NotifyHost() => _changed();

    public void RefreshState()
    {
        var visible = Agents.Where(a => a.IsVisible).ToList();
        _suppress = true;
        if (visible.Count == 0 || visible.All(a => !a.IsSelected))
            IsChecked = false;
        else if (visible.All(a => a.IsSelected))
            IsChecked = true;
        else
            IsChecked = null;
        _suppress = false;
        CountText = $"{visible.Count} agent{(visible.Count == 1 ? "" : "s")}";
        IsVisible = visible.Count > 0;
    }

    public void ApplyFilter(string query, AgentListFilter filter)
    {
        foreach (var agent in Agents)
        {
            var matchesQuery = query.Length == 0
                || agent.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || FolderPath.Contains(query, StringComparison.OrdinalIgnoreCase);
            var matchesArchive = filter switch
            {
                AgentListFilter.Active => !agent.Record.IsArchived,
                AgentListFilter.Archived => agent.Record.IsArchived,
                _ => true
            };
            agent.IsVisible = matchesQuery && matchesArchive;
        }
        RefreshState();
    }
}

public partial class AgentItemViewModel : ObservableObject
{
    private readonly WorkspaceGroupViewModel _group;

    public AgentItemViewModel(AgentRecord record, WorkspaceGroupViewModel group)
    {
        Record = record;
        _group = group;
        Id = record.ComposerId;
        Title = record.Title;
        WorkspaceLabel = record.WorkspaceLabel ?? "Unknown workspace";
        Detail = $"{record.LastWriteUtc.ToLocalTime():g} · {FileSizeFormatter.FromBytes(record.Bytes)}";
        var bits = new List<string>();
        if (record.IsArchived)
            bits.Add("Archived");
        if (record.HasStore)
            bits.Add("Store");
        if (record.HasWaypoints)
            bits.Add($"{record.WaypointDirs.Count} waypoint{(record.WaypointDirs.Count == 1 ? "" : "s")}");
        Meta = bits.Count > 0 ? string.Join(" · ", bits) : "Transcript";
        HasStore = record.HasStore;
        HasWaypoints = record.HasWaypoints;
        IsVisible = true;
    }

    public AgentRecord Record { get; }
    public string Id { get; }
    public string Title { get; }
    public string WorkspaceLabel { get; }
    public string Detail { get; }
    public string Meta { get; }
    public bool HasStore { get; }
    public bool HasWaypoints { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isPreviewing;

    partial void OnIsSelectedChanged(bool value)
    {
        _group.RefreshState();
        _group.NotifyHost();
    }

    public void SetSelected(bool value)
    {
        if (IsSelected == value)
            return;
        IsSelected = value;
    }
}
