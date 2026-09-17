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
    private List<AgentRecord> _agents = [];
    private List<CursorWorkspaceInfo> _workspaces = [];

    public AgentTransferViewModel(MainViewModel host, SettingsStore store)
    {
        _host = host;
        _store = store;
        IncludeTranscript = true;
        IncludeStore = true;
        IncludeWaypoints = true;
    }

    public ObservableCollection<WorkspaceOption> WorkspaceFilters { get; } = [];
    public ObservableCollection<WorkspaceOption> TargetWorkspaces { get; } = [];
    public ObservableCollection<AgentItemViewModel> Agents { get; } = [];
    public ObservableCollection<AgentBackupInfo> Backups { get; } = [];

    [ObservableProperty] private WorkspaceOption? _selectedWorkspaceFilter;
    [ObservableProperty] private WorkspaceOption? _selectedTargetWorkspace;
    [ObservableProperty] private AgentItemViewModel? _selectedAgent;
    [ObservableProperty] private AgentBackupInfo? _selectedBackup;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _includeTranscript = true;
    [ObservableProperty] private bool _includeStore = true;
    [ObservableProperty] private bool _includeWaypoints = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _emptyAgentsText = "No agents found yet.";
    [ObservableProperty] private bool _showEmptyAgents = true;

    partial void OnSelectedWorkspaceFilterChanged(WorkspaceOption? value) => ApplyAgentFilter();
    partial void OnSearchTextChanged(string value) => ApplyAgentFilter();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        _host.BeginBackgroundWork("Scanning agents and workspaces…");
        try
        {
            var settings = _host.Settings;
            var paths = CursorPaths.FromSettings(settings);
            var agents = await Task.Run(() => AgentCatalog.ListLocal(paths, CancellationToken.None));
            var workspaces = CursorWorkspaceLocator.List(paths);
            var backups = AgentCatalog.ListBackups(BackupRoots(settings));

            _agents = agents.ToList();
            _workspaces = workspaces.ToList();

            var previousFilter = SelectedWorkspaceFilter?.Id;
            var previousTarget = SelectedTargetWorkspace?.Id;
            var previousAgent = SelectedAgent?.Id;
            var previousBackup = SelectedBackup?.FolderPath;

            WorkspaceFilters.Clear();
            WorkspaceFilters.Add(new WorkspaceOption { Id = "", Label = "All workspaces", FolderPath = "" });
            foreach (var workspace in _workspaces)
            {
                WorkspaceFilters.Add(new WorkspaceOption
                {
                    Id = workspace.Id,
                    Label = workspace.Label,
                    FolderPath = workspace.FolderPath,
                    Info = workspace
                });
            }

            TargetWorkspaces.Clear();
            foreach (var workspace in _workspaces)
            {
                TargetWorkspaces.Add(new WorkspaceOption
                {
                    Id = workspace.Id,
                    Label = $"{workspace.Label}  —  {workspace.FolderPath}",
                    FolderPath = workspace.FolderPath,
                    Info = workspace
                });
            }

            Backups.Clear();
            foreach (var backup in backups)
                Backups.Add(backup);

            SelectedWorkspaceFilter = WorkspaceFilters.FirstOrDefault(w => w.Id == previousFilter) ?? WorkspaceFilters[0];
            SelectedTargetWorkspace = TargetWorkspaces.FirstOrDefault(w => w.Id == previousTarget)
                ?? TargetWorkspaces.FirstOrDefault();
            ApplyAgentFilter();
            SelectedAgent = Agents.FirstOrDefault(a => a.Id == previousAgent) ?? Agents.FirstOrDefault();
            SelectedBackup = Backups.FirstOrDefault(b => b.FolderPath == previousBackup) ?? Backups.FirstOrDefault();

            EmptyAgentsText = _agents.Count == 0
                ? "No local agent transcripts were found. Open a chat in Cursor first."
                : "No agents match this filter.";
        }
        catch (Exception ex)
        {
            _host.Notify(ex.Message, "error");
        }
        finally
        {
            IsLoading = false;
            _host.EndBackgroundWork();
        }
    }

    [RelayCommand]
    private void BrowseTargetWorkspace()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the workspace to restore into",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
            return;

        var match = CursorWorkspaceLocator.FindByFolder(_workspaces, dialog.FolderName);
        if (match is null)
        {
            _host.Notify("Open that folder in Cursor once first, then refresh. CursorSync can only restore into workspaces Cursor already knows.", "warn");
            return;
        }

        SelectedTargetWorkspace = TargetWorkspaces.FirstOrDefault(w => w.Id == match.Id);
    }

    [RelayCommand]
    private void BackupSelected()
    {
        if (SelectedAgent is null)
        {
            _host.Notify("Select an agent to back up.", "warn");
            return;
        }

        _ = RunBackupAsync(SelectedAgent.Record);
    }

    [RelayCommand]
    private void RestoreSelectedAgent()
    {
        if (SelectedAgent is null)
        {
            _host.Notify("Select an agent to copy.", "warn");
            return;
        }

        var target = SelectedTargetWorkspace?.Info;
        if (target is null)
        {
            _host.Notify("Choose a target workspace.", "warn");
            return;
        }

        if (string.Equals(CursorWorkspaceLocator.NormalizeFolder(target.FolderPath),
                CursorWorkspaceLocator.NormalizeFolder(SelectedAgent.Record.WorkspacePath ?? ""),
                StringComparison.OrdinalIgnoreCase))
        {
            _host.Notify("Pick a different workspace than the one this agent already belongs to.", "warn");
            return;
        }

        _host.Confirm(
            "Restore this agent to another workspace?",
            $"A copy of “{SelectedAgent.Title}” will be registered in {target.Label}. The original stays where it is. Close Cursor so the chat list can be updated.",
            () => _ = RestoreLiveAsync(SelectedAgent.Record, target));
    }

    [RelayCommand]
    private void RestoreSelectedBackup()
    {
        if (SelectedBackup is null)
        {
            _host.Notify("Select a backup to restore.", "warn");
            return;
        }

        var target = SelectedTargetWorkspace?.Info;
        if (target is null)
        {
            _host.Notify("Choose a target workspace.", "warn");
            return;
        }

        _host.Confirm(
            "Restore this backup?",
            $"“{SelectedBackup.Label}” will be copied into {target.Label} as a new agent. Close Cursor so it can appear in the sidebar.",
            () => _ = RestoreBackupAsync(SelectedBackup.FolderPath, target));
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

    private async Task RunBackupAsync(AgentRecord agent)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var localFolder = Path.Combine(_store.AgentBackupsFolder, $"{stamp}-{SafeName(agent.Title)}");
        Directory.CreateDirectory(localFolder);

        var result = await ExecuteAsync("Backing up agent…", progress =>
            _transfer.Backup(agent, CursorPaths.FromSettings(_host.Settings), localFolder,
                IncludeTranscript, IncludeStore, IncludeWaypoints, progress, CancellationToken.None));

        if (result.Success && !string.IsNullOrWhiteSpace(_host.Settings.HubPath))
        {
            try
            {
                var hubFolder = Path.Combine(_host.Settings.HubPath, "payload", "agent-transfers", Path.GetFileName(localFolder));
                CopyDirectory(localFolder, hubFolder);
                result = result with { OutputPath = hubFolder };
            }
            catch (Exception ex)
            {
                _host.Notify("Local backup succeeded, but copying to the sync folder failed: " + ex.Message, "warn");
            }
        }

        RecordHistory("Agent backup", result, agent.Title);
        if (result.Success)
        {
            _host.Notify($"Backup saved — {result.FilesCopied} files · {FileSizeFormatter.FromBytes(result.BytesCopied)}", "success");
            await RefreshAsync();
        }
        else
        {
            _host.Notify(result.Error ?? "Backup failed.", "error");
        }
    }

    private async Task RestoreLiveAsync(AgentRecord agent, CursorWorkspaceInfo target)
    {
        var temp = Path.Combine(Path.GetTempPath(), "CursorSync", "agent-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(temp);
        try
        {
            if (!await EnsureCursorClosedForDatabaseAsync())
                return;

            var backup = await ExecuteAsync("Preparing agent copy…", progress =>
                _transfer.Backup(agent, CursorPaths.FromSettings(_host.Settings), temp,
                    IncludeTranscript, IncludeStore, IncludeWaypoints, progress, CancellationToken.None));
            if (!backup.Success)
            {
                _host.Notify(backup.Error ?? "Could not collect the agent data.", "error");
                return;
            }

            var restore = await ExecuteAsync("Restoring into the target workspace…", progress =>
                _transfer.Restore(temp, target, CursorPaths.FromSettings(_host.Settings),
                    IncludeTranscript, IncludeStore, IncludeWaypoints, progress, CancellationToken.None));
            FinishRestore(restore, agent.Title);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch { /* temp cleanup is best-effort */ }
        }
    }

    private async Task RestoreBackupAsync(string folder, CursorWorkspaceInfo target)
    {
        if (!await EnsureCursorClosedForDatabaseAsync())
            return;

        var result = await ExecuteAsync("Restoring backup…", progress =>
            _transfer.Restore(folder, target, CursorPaths.FromSettings(_host.Settings),
                IncludeTranscript, IncludeStore, IncludeWaypoints, progress, CancellationToken.None));
        FinishRestore(result, SelectedBackup?.Label ?? "Agent");
    }

    private void FinishRestore(AgentTransferResult result, string title)
    {
        RecordHistory("Agent restore", result, title);
        if (result.Success)
        {
            var extra = result.Warnings.Count > 0 ? " " + result.Warnings[0] : " Reopen the target folder in Cursor to see it.";
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

        _host.Notify("Close Cursor first so the chat list can be updated. Transcript and store files can still copy, but the sidebar will not show the agent until the database is writable.", "warn");
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
                    _host.BeginBackgroundWork(p.Message);
            });
            return await Task.Run(() => work(progress));
        }
        catch (Exception ex)
        {
            return new AgentTransferResult { Success = false, Error = ex.Message };
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

    private void ApplyAgentFilter()
    {
        var query = SearchText?.Trim() ?? "";
        var workspaceId = SelectedWorkspaceFilter?.Id ?? "";
        var filtered = _agents.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(workspaceId))
            filtered = filtered.Where(a => a.WorkspaceId == workspaceId);
        if (query.Length > 0)
        {
            filtered = filtered.Where(a =>
                a.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (a.WorkspaceLabel?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                a.ComposerId.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        Agents.Clear();
        foreach (var agent in filtered)
            Agents.Add(new AgentItemViewModel(agent));
        ShowEmptyAgents = Agents.Count == 0;
    }

    private static string SafeName(string title)
    {
        var trimmed = new string(title.Take(40).ToArray()).Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(c, '-');
        return string.IsNullOrWhiteSpace(trimmed) ? "agent" : trimmed;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(destination, Path.GetRelativePath(source, file));
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Copy(file, dest, overwrite: true);
        }
    }
}

public sealed class WorkspaceOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string FolderPath { get; init; }
    public CursorWorkspaceInfo? Info { get; init; }
}

public sealed class AgentItemViewModel
{
    public AgentItemViewModel(AgentRecord record)
    {
        Record = record;
        Id = record.ComposerId;
        Title = record.Title;
        WorkspaceLabel = record.WorkspaceLabel ?? "Unknown workspace";
        Detail = $"{record.LastWriteUtc.ToLocalTime():g} · {FileSizeFormatter.FromBytes(record.Bytes)} · {record.Files} files";
        Meta = record.HasStore || record.HasWaypoints
            ? string.Join(" · ", new[]
            {
                record.HasStore ? "Store" : null,
                record.HasWaypoints ? $"{record.WaypointDirs.Count} waypoint{(record.WaypointDirs.Count == 1 ? "" : "s")}" : null
            }.Where(s => s is not null))
            : "Transcript only";
        HasStore = record.HasStore;
        HasWaypoints = record.HasWaypoints;
    }

    public AgentRecord Record { get; }
    public string Id { get; }
    public string Title { get; }
    public string WorkspaceLabel { get; }
    public string Detail { get; }
    public string Meta { get; }
    public bool HasStore { get; }
    public bool HasWaypoints { get; }
}
