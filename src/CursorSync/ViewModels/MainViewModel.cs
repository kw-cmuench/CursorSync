using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CursorSync.Models;
using CursorSync.Services;
using Microsoft.Win32;

namespace CursorSync.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();
    private readonly SyncEngine _engine = new();
    private readonly HubManifestStore _hubManifest = new();
    private CancellationTokenSource? _syncCts;

    public MainViewModel()
    {
        Settings = _store.Load();
        if (string.IsNullOrWhiteSpace(Settings.MachineName))
            Settings.MachineName = Environment.MachineName;

        foreach (var entry in _store.LoadHistory().OrderByDescending(h => h.Utc))
            History.Add(entry);

        var groups = new Dictionary<CategoryGroup, List<CategoryItemViewModel>>();
        foreach (var definition in CategoryCatalog.All)
        {
            var enabled = Settings.CategoryEnabled.TryGetValue(definition.Id, out var value)
                ? value
                : definition.DefaultEnabled;

            var item = new CategoryItemViewModel(definition, enabled, PersistCategorySelection);
            if (!groups.TryGetValue(definition.Group, out var list))
            {
                list = [];
                groups[definition.Group] = list;
            }
            list.Add(item);
            Categories.Add(item);
        }

        CategoryGroups =
        [
            new CategoryGroupViewModel
            {
                Title = "Conversations",
                Subtitle = "Chats, composers, and agent transcripts.",
                Items = groups.GetValueOrDefault(CategoryGroup.Conversations) ?? []
            },
            new CategoryGroupViewModel
            {
                Title = "Agents & tools",
                Subtitle = "Memory stores, skills, MCP, plugins, and plans.",
                Items = groups.GetValueOrDefault(CategoryGroup.Agents) ?? []
            },
            new CategoryGroupViewModel
            {
                Title = "Editor",
                Subtitle = "Settings, keybindings, snippets, and extensions.",
                Items = groups.GetValueOrDefault(CategoryGroup.Editor) ?? []
            },
            new CategoryGroupViewModel
            {
                Title = "Optional / large",
                Subtitle = "Useful, but often bulky. Off by default.",
                Items = groups.GetValueOrDefault(CategoryGroup.Optional) ?? []
            }
        ];

        SelectedKind = SyncKind.TwoWay;
        Agents = new AgentTransferViewModel(this, _store);
        WorkspaceManager = new WorkspaceManagerViewModel(this);
        ProjectManager = new ProjectManagerViewModel(this);
        InitializeFromSettings();
        StatusBarText = "Starting…";
        IsBusy = true;
        IsBusyIndeterminate = true;
        _ = InitializeAsync();
    }

    public AppSettings Settings { get; }
    public AgentTransferViewModel Agents { get; }
    public WorkspaceManagerViewModel WorkspaceManager { get; }
    public ProjectManagerViewModel ProjectManager { get; }
    public ObservableCollection<CategoryItemViewModel> Categories { get; } = [];
    public IReadOnlyList<CategoryGroupViewModel> CategoryGroups { get; }
    public ObservableCollection<SyncHistoryEntry> History { get; } = [];
    public ObservableCollection<string> ActivityLog { get; } = [];
    public ObservableCollection<IntegrityIssue> IntegrityIssues { get; } = [];
    public ObservableCollection<IntegrityIssue> HubHealthIssues { get; } = [];
    public IReadOnlyList<HubSuggestion> SuggestedHubs { get; } = HubSuggestions.Suggest();

    [ObservableProperty] private AppPage _currentPage = AppPage.Dashboard;
    [ObservableProperty] private SyncKind _selectedKind = SyncKind.TwoWay;
    [ObservableProperty] private string _machineName = Environment.MachineName;
    [ObservableProperty] private string? _hubPath;
    [ObservableProperty] private string _hubStatusText = "No sync folder selected";
    [ObservableProperty] private string _hubDetailText = "Pick a OneDrive, Dropbox, or shared folder both PCs can open.";
    [ObservableProperty] private string _cursorStatusText = "Checking Cursor…";
    [ObservableProperty] private string _cursorDetailText = "";
    [ObservableProperty] private bool _isCursorRunning;
    [ObservableProperty] private bool _cursorFound;
    [ObservableProperty] private string _lastSyncText = "Never synced";
    [ObservableProperty] private string _lastSyncDetail = "Push from this PC, then pull on the other.";
    [ObservableProperty] private string _enabledSummary = "";
    [ObservableProperty] private string _totalSizeText = "…";
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _bannerText;
    [ObservableProperty] private string? _bannerKind;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _backupBeforePull;
    [ObservableProperty] private bool _closeCursorBeforeSync;
    [ObservableProperty] private bool _protectCursorData = true;
    [ObservableProperty] private ConflictPolicy _conflictPolicy = ConflictPolicy.NewerWins;
    [ObservableProperty] private string? _cursorUserDataDir;
    [ObservableProperty] private string? _pathReplaceFrom;
    [ObservableProperty] private string? _pathReplaceTo;
    [ObservableProperty] private string? _confirmTitle;
    [ObservableProperty] private string? _confirmMessage;
    [ObservableProperty] private string _confirmContinueText = "Continue";
    [ObservableProperty] private bool _confirmIsDestructive = true;
    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isBusyIndeterminate = true;
    [ObservableProperty] private string _statusBarText = "Starting…";
    [ObservableProperty] private string _integritySummary = "Not checked yet";
    [ObservableProperty] private string _integrityDetail = "Run a check to see whether Cursor’s user data is consistent.";
    [ObservableProperty] private string _integrityKind = "info";
    [ObservableProperty] private bool _hasIntegrityIssues;
    [ObservableProperty] private string _hubHealthSummary = "Not checked yet";
    [ObservableProperty] private string _hubHealthDetail = "Checks that this PC can read and write the shared folder, and that copied databases and JSON look valid.";
    [ObservableProperty] private string _hubHealthKind = "info";
    [ObservableProperty] private bool _hasHubHealthIssues;

    private Action? _confirmAction;
    private int _scanGeneration;
    private bool _suspendCategoryPersist;
    private int _workDepth;

    partial void OnCurrentPageChanged(AppPage value)
    {
        if (value == AppPage.Agents)
            _ = Agents.RefreshCommand.ExecuteAsync(null);
        else if (value == AppPage.Workspaces)
            _ = WorkspaceManager.RefreshCommand.ExecuteAsync(null);
        else if (value == AppPage.Projects)
            _ = ProjectManager.RefreshCommand.ExecuteAsync(null);
    }

    public void Notify(string text, string kind) => ShowBanner(text, kind);

    public void Confirm(
        string title,
        string message,
        Action action,
        CursorRisk cursorRisk = CursorRisk.None,
        string continueText = "Continue")
    {
        RefreshCursorStatus();
        if (IsCursorRunning && cursorRisk != CursorRisk.None)
        {
            AskConfirm(
                cursorRisk == CursorRisk.High ? "Cursor is still running" : "Cursor is still open",
                message.Trim() + Environment.NewLine + Environment.NewLine + DescribeAgentCursorRisk(cursorRisk),
                action,
                continueText: "Continue anyway",
                destructive: cursorRisk == CursorRisk.High);
            return;
        }

        AskConfirm(title, message, action, continueText);
    }

    public void RefreshCursorStatus() => RefreshStatus();

    public void BeginBackgroundWork(string status)
    {
        _workDepth++;
        SetBusyStatus(status);
    }

    public void SetBusyStatus(string status)
    {
        IsBusy = true;
        IsBusyIndeterminate = true;
        StatusBarText = status;
    }

    public void EndBackgroundWork()
    {
        _workDepth = Math.Max(0, _workDepth - 1);
        if (_workDepth > 0 || IsSyncing || IsScanning)
            return;
        IsBusy = false;
        StatusBarText = "Ready";
    }

    public void AddHistory(SyncHistoryEntry entry)
    {
        History.Insert(0, entry);
        _store.SaveHistory(History.ToList());
    }

    [RelayCommand]
    private async Task CheckCursorDataAsync()
    {
        if (IsSyncing)
            return;
        BeginBackgroundWork("Checking Cursor data…");
        try
        {
            RefreshCursorStatus();
            var paths = CursorPaths.FromSettings(Settings);
            var running = IsCursorRunning;
            var report = await Task.Run(() => CursorIntegrityService.Inspect(paths, running));
            ApplyIntegrity(report);
        }
        catch (Exception ex)
        {
            IntegritySummary = "Could not check Cursor data.";
            IntegrityDetail = UserFacingError.From(ex);
            IntegrityKind = "error";
            Notify(UserFacingError.From(ex), "error");
        }
        finally
        {
            EndBackgroundWork();
        }
    }

    [RelayCommand]
    private async Task CheckHubHealthAsync()
    {
        if (IsSyncing)
            return;

        BeginBackgroundWork("Checking the sync folder…");
        try
        {
            var report = await Task.Run(() => HubIntegrityService.Inspect(HubPath));
            ApplyHubHealth(report);
            Notify(report.Summary, report.HasErrors ? "error" : report.HasWarnings ? "warn" : "success");
        }
        catch (Exception ex)
        {
            HubHealthSummary = "Could not check the sync folder.";
            HubHealthDetail = UserFacingError.From(ex);
            HubHealthKind = "error";
            Notify(UserFacingError.From(ex), "error");
        }
        finally
        {
            EndBackgroundWork();
        }
    }

    public async Task<GuardedActionResult> GuardAsync(
        string actionName,
        IntegrityPlan plan,
        Func<Task<bool>> work,
        Action? onRollback = null)
    {
        RefreshCursorStatus();
        plan = new IntegrityPlan
        {
            Action = plan.Action,
            CursorRunning = IsCursorRunning,
            TouchesSqlite = plan.TouchesSqlite,
            TouchesWorkspaceStorage = plan.TouchesWorkspaceStorage,
            DeletesWorkspace = plan.DeletesWorkspace,
            DeletesProject = plan.DeletesProject,
            Target = plan.Target,
            NewFolder = plan.NewFolder,
            Sources = plan.Sources,
            AgentCount = plan.AgentCount,
            ProjectCount = plan.ProjectCount
        };

        var paths = CursorPaths.FromSettings(Settings);
        CursorIntegrityReport before;
        BeginBackgroundWork("Checking Cursor data…");
        try
        {
            before = await Task.Run(() => CursorIntegrityService.Inspect(paths, plan.CursorRunning));
            ApplyIntegrity(before);
        }
        catch (Exception ex)
        {
            EndBackgroundWork();
            Notify("Could not check Cursor data before this change: " + UserFacingError.From(ex), "error");
            return GuardedActionResult.Abort();
        }
        EndBackgroundWork();

        var predicted = CursorIntegrityService.Predict(plan, before);
        if (predicted.HasErrors)
        {
            Notify(predicted.Issues.First(i => i.Severity == IntegritySeverity.Error).Message, "error");
            return GuardedActionResult.Abort();
        }

        string? snapshot = null;
        if (ProtectCursorData)
        {
            BeginBackgroundWork("Saving a safety snapshot…");
            try
            {
                snapshot = await Task.Run(() => IntegritySnapshot.Capture(paths, _store.SafetyFolder));
            }
            catch (Exception ex)
            {
                EndBackgroundWork();
                Notify("Could not create a safety snapshot, so the change was not applied: " + UserFacingError.From(ex), "error");
                return GuardedActionResult.Abort();
            }
            EndBackgroundWork();
        }

        SqliteStateStore.PrepareForWrite();

        Exception? thrown = null;
        try
        {
            _ = await work();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        CursorIntegrityReport after = before;
        BeginBackgroundWork("Checking Cursor data…");
        try
        {
            after = await Task.Run(() => CursorIntegrityService.Inspect(paths, IsCursorRunning));
            ApplyIntegrity(after);
        }
        catch (Exception ex)
        {
            thrown ??= ex;
        }
        finally
        {
            EndBackgroundWork();
        }

        var worse = thrown is not null || CursorIntegrityService.IsWorse(before, after);
        if (worse && snapshot is not null)
        {
            BeginBackgroundWork("Restoring the safety snapshot…");
            try
            {
                await Task.Run(() => IntegritySnapshot.Restore(snapshot));
                onRollback?.Invoke();
                var restored = await Task.Run(() => CursorIntegrityService.Inspect(paths, IsCursorRunning));
                ApplyIntegrity(restored);
                Notify(
                    thrown is not null
                        ? $"{actionName} failed and was rolled back. {UserFacingError.From(thrown)}"
                        : $"{actionName} made Cursor data worse, so the safety snapshot was restored.",
                    "error");
            }
            catch (Exception ex)
            {
                try { onRollback?.Invoke(); }
                catch { }
                Notify(
                    $"{actionName} may have broken Cursor data, and the rollback failed. The snapshot is at {snapshot}. {UserFacingError.From(ex)}",
                    "error");
            }
            finally
            {
                EndBackgroundWork();
            }

            return GuardedActionResult.Rollback();
        }

        if (worse)
        {
            try { onRollback?.Invoke(); }
            catch { /* snapshot may already have been restored */ }
            Notify(
                thrown is not null
                    ? UserFacingError.From(thrown)
                    : $"{actionName} finished, but Cursor data now looks worse. Turn on safety snapshots in Settings before trying again.",
                "error");
            return GuardedActionResult.Rollback();
        }

        if (thrown is not null)
        {
            IntegritySnapshot.Discard(snapshot);
            Notify(UserFacingError.From(thrown), "error");
            return new GuardedActionResult { Ran = true };
        }

        IntegritySnapshot.Discard(snapshot);
        return GuardedActionResult.Ok();
    }

    private void ApplyIntegrity(CursorIntegrityReport report)
    {
        IntegritySummary = report.Summary;
        IntegrityKind = report.HasErrors ? "error" : report.HasWarnings ? "warn" : "success";
        IntegrityDetail = report.CheckedUtc.ToLocalTime().ToString("g")
            + (ProtectCursorData
                ? " · Risky changes are snapshotted and rolled back if this check fails."
                : " · Safety snapshots are off.");
        IntegrityIssues.Clear();
        foreach (var issue in report.Issues.Take(8))
            IntegrityIssues.Add(issue);
        HasIntegrityIssues = IntegrityIssues.Count > 0;
    }

    private void ApplyHubHealth(CursorIntegrityReport report)
    {
        HubHealthSummary = report.Summary;
        HubHealthKind = report.HasErrors ? "error" : report.HasWarnings ? "warn" : "success";
        HubHealthDetail = report.CheckedUtc.ToLocalTime().ToString("g");
        HubHealthIssues.Clear();
        foreach (var issue in report.Issues.Take(8))
            HubHealthIssues.Add(issue);
        HasHubHealthIssues = HubHealthIssues.Count > 0;
    }

    partial void OnHubPathChanged(string? value)
    {
        Settings.HubPath = value;
        SaveSettings();
        RefreshHubInfo();
        HubHealthSummary = "Not checked yet";
        HubHealthDetail = "Checks that this PC can read and write the shared folder, and that copied databases and JSON look valid.";
        HubHealthKind = "info";
        HubHealthIssues.Clear();
        HasHubHealthIssues = false;
    }

    partial void OnMachineNameChanged(string value)
    {
        Settings.MachineName = string.IsNullOrWhiteSpace(value) ? Environment.MachineName : value.Trim();
        SaveSettings();
    }

    partial void OnBackupBeforePullChanged(bool value)
    {
        Settings.BackupBeforePull = value;
        SaveSettings();
    }

    partial void OnCloseCursorBeforeSyncChanged(bool value)
    {
        Settings.CloseCursorBeforeSync = value;
        SaveSettings();
    }

    partial void OnProtectCursorDataChanged(bool value)
    {
        Settings.ProtectCursorData = value;
        SaveSettings();
    }

    partial void OnConflictPolicyChanged(ConflictPolicy value)
    {
        Settings.ConflictPolicy = value;
        SaveSettings();
    }

    partial void OnCursorUserDataDirChanged(string? value)
    {
        Settings.CursorUserDataDir = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        SaveSettings();
        RefreshStatus();
        _ = RefreshSizesAsync();
    }

    partial void OnPathReplaceFromChanged(string? value)
    {
        Settings.PathReplaceFrom = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        SaveSettings();
    }

    partial void OnPathReplaceToChanged(string? value)
    {
        Settings.PathReplaceTo = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        SaveSettings();
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        if (Enum.TryParse<AppPage>(page, out var parsed))
            CurrentPage = parsed;
    }

    [RelayCommand]
    private void SelectKind(string kind)
    {
        if (Enum.TryParse<SyncKind>(kind, out var parsed))
            SelectedKind = parsed;
    }

    [RelayCommand]
    private void BrowseHub()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the CursorSync folder",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(HubPath) && Directory.Exists(HubPath))
            dialog.InitialDirectory = HubPath;

        if (dialog.ShowDialog() == true)
            HubPath = dialog.FolderName;
    }

    [RelayCommand]
    private void UseSuggestedHub(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Directory.CreateDirectory(path);
        HubPath = path;
        ShowBanner("Sync folder ready", "success");
    }

    [RelayCommand]
    private void BrowseCursorData()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Cursor user-data folder (the folder that contains User)",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
            CursorUserDataDir = dialog.FolderName;
    }

    [RelayCommand]
    private void EnableRecommended()
    {
        SetEnabledBulk(item => item.IsRecommended);
    }

    [RelayCommand]
    private void EnableNone()
    {
        SetEnabledBulk(_ => false);
    }

    [RelayCommand]
    private void EnableAll()
    {
        SetEnabledBulk(_ => true);
    }

    private void SetEnabledBulk(Func<CategoryItemViewModel, bool> selector)
    {
        _suspendCategoryPersist = true;
        try
        {
            foreach (var item in Categories)
                item.IsEnabled = selector(item);
        }
        finally
        {
            _suspendCategoryPersist = false;
        }

        PersistCategorySelection();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        StatusBarText = "Refreshing…";
        IsBusy = true;
        IsBusyIndeterminate = true;
        await RefreshStatusAsync();
        RefreshHubInfo();
        await RefreshSizesAsync();
    }

    [RelayCommand]
    private async Task CloseCursorAsync()
    {
        StatusMessage = "Closing Cursor…";
        var closed = await CursorProcessService.TryCloseAsync(TimeSpan.FromSeconds(12), CancellationToken.None);
        RefreshStatus();
        StatusMessage = closed ? "Cursor closed." : "Cursor is still running. Quit it from the tray if needed.";
        if (!closed)
            ShowBanner("Cursor is still running. Chat databases stay locked until it fully quits.", "warn");
    }

    [RelayCommand]
    private void OpenHub()
    {
        if (string.IsNullOrWhiteSpace(HubPath) || !Directory.Exists(HubPath))
            return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = HubPath,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void EmptyHub()
    {
        if (IsSyncing)
        {
            Notify("Wait for the current sync to finish before emptying the sync folder.", "warn");
            return;
        }

        if (string.IsNullOrWhiteSpace(HubPath) || !Directory.Exists(HubPath))
        {
            Notify("Choose a sync folder first.", "warn");
            return;
        }

        Confirm(
            "Empty the sync folder?",
            $"CursorSync will delete the payload and hub manifest in {HubPath}. Your project files and Cursor data on this PC are not touched. Other files in that folder stay. Other PCs will have nothing to pull until you push again.",
            () => _ = EmptyHubAsync(),
            continueText: "Empty folder");
    }

    private async Task EmptyHubAsync()
    {
        BeginBackgroundWork("Emptying the sync folder…");
        try
        {
            var hub = HubPath;
            if (string.IsNullOrWhiteSpace(hub))
            {
                Notify("Choose a sync folder first.", "warn");
                return;
            }

            var result = await Task.Run(() => HubIntegrityService.Empty(hub));
            AddHistory(new SyncHistoryEntry
            {
                Utc = DateTime.UtcNow,
                Kind = "Empty sync folder",
                Machine = Settings.MachineName,
                Success = result.Success,
                Error = result.Error,
                FilesCopied = result.Removed,
                Warnings = result.Warnings.ToList(),
                HubPath = hub ?? ""
            });

            RefreshHubInfo();
            try
            {
                ApplyHubHealth(HubIntegrityService.Inspect(hub));
            }
            catch
            {
                // health check is secondary
            }

            if (result.Success)
                Notify(result.Message ?? "The sync folder was emptied.", result.Warnings.Count > 0 ? "warn" : "success");
            else
                Notify(result.Error ?? "Could not empty the sync folder.", "error");
        }
        catch (Exception ex)
        {
            Notify(UserFacingError.From(ex), "error");
        }
        finally
        {
            EndBackgroundWork();
        }
    }

    [RelayCommand]
    private void OpenBackups()
    {
        Directory.CreateDirectory(_store.BackupsFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _store.BackupsFolder,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void StartSyncFromDashboard()
    {
        CurrentPage = AppPage.Sync;
        _ = RunSelectedSyncAsync();
    }

    [RelayCommand]
    private async Task RunSelectedSyncAsync()
    {
        if (IsSyncing)
            return;

        var selected = Categories.Where(c => c.IsEnabled).ToList();
        if (selected.Count == 0)
        {
            ShowBanner("Turn on at least one data category.", "warn");
            CurrentPage = AppPage.Categories;
            return;
        }

        if (string.IsNullOrWhiteSpace(HubPath))
        {
            ShowBanner("Choose a sync folder first.", "warn");
            CurrentPage = AppPage.Settings;
            return;
        }

        RefreshStatus();
        var risk = GetSyncCursorRisk(selected);

        if (SelectedKind == SyncKind.Pull)
        {
            var message = "This overwrites local Cursor files for the selected categories. A backup is created first if that option is on.";
            if (risk != CursorRisk.None)
                message += Environment.NewLine + Environment.NewLine + DescribeSyncCursorRisk(selected);

            AskConfirm(
                risk != CursorRisk.None ? "Pull while Cursor is open?" : "Pull from the sync folder?",
                message,
                () => _ = ExecuteSyncAsync(selected),
                continueText: risk != CursorRisk.None ? "Continue anyway" : "Continue",
                destructive: true);
            return;
        }

        if (risk != CursorRisk.None)
        {
            AskConfirm(
                risk == CursorRisk.High ? "Cursor is still running" : "Cursor is still open",
                DescribeSyncCursorRisk(selected),
                () => _ = ExecuteSyncAsync(selected),
                continueText: "Continue anyway",
                destructive: risk == CursorRisk.High);
            return;
        }

        await ExecuteSyncAsync(selected);
    }

    [RelayCommand]
    private void Confirm()
    {
        IsConfirmOpen = false;
        var action = _confirmAction;
        _confirmAction = null;
        action?.Invoke();
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirmOpen = false;
        _confirmAction = null;
    }

    [RelayCommand]
    private void CancelSync()
    {
        _syncCts?.Cancel();
    }

    [RelayCommand]
    private void PushNow()
    {
        SelectedKind = SyncKind.Push;
        CurrentPage = AppPage.Sync;
        _ = RunSelectedSyncAsync();
    }

    [RelayCommand]
    private void PullNow()
    {
        SelectedKind = SyncKind.Pull;
        CurrentPage = AppPage.Sync;
        _ = RunSelectedSyncAsync();
    }

    [RelayCommand]
    private void TwoWayNow()
    {
        SelectedKind = SyncKind.TwoWay;
        CurrentPage = AppPage.Sync;
        _ = RunSelectedSyncAsync();
    }

    public void InitializeFromSettings()
    {
        HubPath = Settings.HubPath;
        MachineName = Settings.MachineName;
        BackupBeforePull = Settings.BackupBeforePull;
        CloseCursorBeforeSync = Settings.CloseCursorBeforeSync;
        ProtectCursorData = Settings.ProtectCursorData ?? true;
        ConflictPolicy = Settings.ConflictPolicy;
        CursorUserDataDir = Settings.CursorUserDataDir;
        PathReplaceFrom = Settings.PathReplaceFrom;
        PathReplaceTo = Settings.PathReplaceTo;
        UpdateLastSyncText();
        UpdateEnabledSummary();
    }

    private async Task ExecuteSyncAsync(List<CategoryItemViewModel> selected)
    {
        RefreshStatus();
        var locked = selected.Where(c => c.RequiresCursorClosed).ToList();
        if (IsCursorRunning && locked.Count > 0)
        {
            if (CloseCursorBeforeSync)
            {
                StatusMessage = "Closing Cursor before sync…";
                var closed = await CursorProcessService.TryCloseAsync(TimeSpan.FromSeconds(12), CancellationToken.None);
                RefreshStatus();
                if (!closed)
                {
                    ShowBanner("Cursor is still running, so chat databases will be skipped.", "warn");
                    selected = selected.Where(c => !c.RequiresCursorClosed).ToList();
                    if (selected.Count == 0)
                        return;
                }
            }
            else
            {
                ShowBanner("Cursor is running. Chat and checkpoint files will be skipped to avoid corrupting SQLite.", "warn");
                selected = selected.Where(c => !c.RequiresCursorClosed).ToList();
                if (selected.Count == 0)
                    return;
            }
        }

        IsSyncing = true;
        IsBusy = true;
        IsBusyIndeterminate = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressText = "Preparing…";
        StatusBarText = "Preparing sync…";
        ActivityLog.Clear();
        _syncCts = new CancellationTokenSource();
        CurrentPage = AppPage.Sync;

        var progress = new Progress<SyncProgress>(p =>
        {
            ProgressText = p.Message;
            StatusBarText = p.Message;
            IsBusy = true;
            if (p.Fraction is { } fraction)
            {
                IsBusyIndeterminate = false;
                IsProgressIndeterminate = false;
                ProgressValue = Math.Clamp(fraction, 0, 1);
            }
            else
            {
                IsBusyIndeterminate = true;
                IsProgressIndeterminate = true;
            }
            if (!string.IsNullOrWhiteSpace(p.Message))
                AppendLog(p.Message);
        });

        SyncResult result;
        try
        {
            if (SelectedKind is SyncKind.Pull or SyncKind.TwoWay)
            {
                SyncResult? captured = null;
                var guard = await GuardAsync(
                    "Sync",
                    IntegrityPlan.Sync(writesLocal: true),
                    async () =>
                    {
                        captured = await _engine.RunAsync(
                            SelectedKind,
                            Settings,
                            selected.Select(c => c.Definition).ToList(),
                            progress,
                            _syncCts.Token);
                        return captured.Success;
                    });

                if (guard.Aborted || guard.RolledBack)
                    result = new SyncResult
                    {
                        Success = false,
                        Error = guard.RolledBack
                            ? "Sync was rolled back because Cursor data no longer looked consistent."
                            : "Sync was not started because it looked unsafe."
                    };
                else
                    result = captured ?? new SyncResult { Success = false, Error = "Sync did not return a result." };
            }
            else
            {
                result = await _engine.RunAsync(
                    SelectedKind,
                    Settings,
                    selected.Select(c => c.Definition).ToList(),
                    progress,
                    _syncCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            result = new SyncResult { Success = false, Error = "Sync cancelled." };
        }
        catch (Exception ex)
        {
            result = new SyncResult { Success = false, Error = UserFacingError.From(ex) };
        }
        finally
        {
            IsSyncing = false;
            if (!IsScanning)
                IsBusy = false;
            _syncCts.Dispose();
            _syncCts = null;
        }

        foreach (var line in result.Log)
            AppendLog(line);
        foreach (var warning in result.Warnings)
            AppendLog("Warning: " + warning);

        var entry = new SyncHistoryEntry
        {
            Utc = DateTime.UtcNow,
            Kind = SelectedKind.ToString(),
            Machine = Settings.MachineName,
            Success = result.Success,
            Error = result.Error,
            FilesCopied = result.FilesCopied,
            BytesCopied = result.BytesCopied,
            Categories = result.Categories.ToList(),
            Warnings = result.Warnings.ToList(),
            HubPath = HubPath ?? ""
        };
        History.Insert(0, entry);
        _store.SaveHistory(History.ToList());

        Settings.LastSyncUtc = DateTime.UtcNow.ToString("o");
        Settings.LastSyncKind = SelectedKind.ToString();
        Settings.LastSyncSuccess = result.Success;
        SaveSettings();
        UpdateLastSyncText();
        RefreshHubInfo();
        await RefreshSizesAsync();

        if (result.Success)
        {
            var summary = $"{result.FilesCopied} files · {FileSizeFormatter.FromBytes(result.BytesCopied)}";
            ShowBanner($"{SelectedKindLabel} complete — {summary}", "success");
            StatusBarText = $"{SelectedKindLabel} complete — {summary}";
            StatusMessage = summary;
            if (result.Warnings.Count > 0)
                AppendLog($"{result.Warnings.Count} warning(s).");
        }
        else
        {
            ShowBanner(result.Error ?? "Sync failed.", "error");
            StatusBarText = result.Error ?? "Sync failed.";
            StatusMessage = result.Error;
        }
    }

    private string SelectedKindLabel => SelectedKind switch
    {
        SyncKind.Push => "Push",
        SyncKind.Pull => "Pull",
        _ => "Two-way sync"
    };

    private CursorRisk GetSyncCursorRisk(IReadOnlyList<CategoryItemViewModel> selected)
    {
        if (!IsCursorRunning)
            return CursorRisk.None;

        if (selected.Any(c => c.RequiresCursorClosed))
            return CursorRisk.High;

        return SelectedKind is SyncKind.Pull or SyncKind.TwoWay ? CursorRisk.Medium : CursorRisk.None;
    }

    private string DescribeSyncCursorRisk(IReadOnlyList<CategoryItemViewModel> selected)
    {
        var parts = new List<string>();
        var locked = selected.Where(c => c.RequiresCursorClosed).Select(c => c.Title).ToList();
        if (locked.Count > 0)
        {
            parts.Add("High risk: " + string.Join(", ", locked)
                + " use databases Cursor keeps open. Those categories will be skipped while Cursor is running, so this sync will be incomplete.");
        }

        if (SelectedKind is SyncKind.Pull or SyncKind.TwoWay)
        {
            parts.Add("Medium risk: settings and other files Cursor already has open can be overwritten when Cursor saves, or they may not show up until you restart it.");
        }

        parts.Add("Close Cursor completely (including the tray), then run this again. Continue anyway only if you accept an incomplete or overwritten result.");
        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private static string DescribeAgentCursorRisk(CursorRisk risk) =>
        risk == CursorRisk.High
            ? "High risk: Cursor is using the chat database. Files can still copy, but backups may miss the sidebar snapshot and restored agents may stay hidden until Cursor is fully closed and you try again. Quit Cursor completely (including the tray)."
            : "Medium risk: Cursor may overwrite files it still has open, or ignore the new copies until you restart it. Close Cursor completely (including the tray).";

    private void AskConfirm(string title, string message, Action action, string continueText = "Continue", bool destructive = true)
    {
        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmContinueText = continueText;
        ConfirmIsDestructive = destructive;
        _confirmAction = action;
        IsConfirmOpen = true;
    }

    private void PersistCategorySelection()
    {
        if (_suspendCategoryPersist)
            return;
        Settings.CategoryEnabled = Categories.ToDictionary(c => c.Id, c => c.IsEnabled, StringComparer.OrdinalIgnoreCase);
        SaveSettings();
        UpdateEnabledSummary();
        UpdateEnabledTotal();
        if (Categories.Any(c => c.IsEnabled && !c.WasMeasured))
            _ = RefreshSizesAsync();
    }

    private void SaveSettings() => _store.Save(Settings);

    private async Task InitializeAsync()
    {
        try
        {
            StatusBarText = "Checking Cursor…";
            IsBusy = true;
            IsBusyIndeterminate = true;
            await RefreshStatusAsync();
            RefreshHubInfo();
            await RefreshSizesAsync();
            await CheckCursorDataAsync();
        }
        catch (Exception ex)
        {
            StatusBarText = "Startup check failed.";
            ShowBanner(UserFacingError.From(ex), "error");
            IsBusy = false;
        }
    }

    private async Task RefreshStatusAsync()
    {
        var paths = CursorPaths.FromSettings(Settings);
        var status = await Task.Run(() => CursorProcessService.GetStatus(paths));
        ApplyCursorStatus(status, paths);
    }

    private void RefreshStatus()
    {
        var paths = CursorPaths.FromSettings(Settings);
        ApplyCursorStatus(CursorProcessService.GetStatus(paths), paths);
    }

    private void ApplyCursorStatus(CursorStatus status, CursorPaths paths)
    {
        IsCursorRunning = status.IsRunning;
        CursorFound = status.UserFolderFound;
        CursorStatusText = status.IsRunning
            ? $"Cursor is running ({status.ProcessCount} process{(status.ProcessCount == 1 ? "" : "es")})"
            : status.UserFolderFound ? "Cursor is closed" : "Cursor data not found";
        CursorDetailText = status.UserFolderFound
            ? paths.UserFolder
            : "Expected %APPDATA%\\Cursor\\User. Set a custom user-data folder in Settings if you use a portable install.";
        UpdateEnabledSummary();
    }

    private void RefreshHubInfo()
    {
        if (string.IsNullOrWhiteSpace(HubPath))
        {
            HubStatusText = "No sync folder selected";
            HubDetailText = "Pick a OneDrive, Dropbox, or shared folder both PCs can open.";
            return;
        }

        if (!Directory.Exists(HubPath))
        {
            HubStatusText = "Folder missing";
            HubDetailText = HubPath;
            return;
        }

        var manifest = _hubManifest.Load(HubPath);
        if (manifest is null)
        {
            HubStatusText = "Ready — empty hub";
            HubDetailText = HubPath + " · First push will create the payload.";
            return;
        }

        var machines = manifest.Machines.Select(m => m.Name).DefaultIfEmpty(manifest.LastMachine).Where(n => !string.IsNullOrWhiteSpace(n));
        HubStatusText = $"Hub updated {ToLocal(manifest.UpdatedUtc)}";
        HubDetailText = $"{HubPath} · Last: {manifest.LastKind} from {manifest.LastMachine} · Machines: {string.Join(", ", machines!)}";
    }

    private void UpdateLastSyncText()
    {
        if (string.IsNullOrWhiteSpace(Settings.LastSyncUtc) || !DateTime.TryParse(Settings.LastSyncUtc, out var utc))
        {
            LastSyncText = "Never synced";
            LastSyncDetail = "Push from this PC, then pull on the other.";
            return;
        }

        LastSyncText = $"{Settings.LastSyncKind} · {ToLocal(utc.ToUniversalTime())}";
        LastSyncDetail = Settings.LastSyncSuccess ? "Last operation succeeded." : "Last operation reported an error.";
    }

    private void UpdateEnabledSummary()
    {
        var enabled = Categories.Where(c => c.IsEnabled).ToList();
        EnabledSummary = enabled.Count == 0
            ? "No categories selected"
            : string.Join(" · ", enabled.Select(c => c.Title));
    }

    private void UpdateEnabledTotal()
    {
        var enabled = Categories.Where(c => c.IsEnabled).ToList();
        var measured = enabled.Where(c => c.WasMeasured).ToList();
        var pending = enabled.Count - measured.Count;
        var total = measured.Sum(c => c.Bytes);
        TotalSizeText = pending > 0 && measured.Count == 0
            ? "…"
            : pending > 0
                ? FileSizeFormatter.FromBytes(total) + "+"
                : FileSizeFormatter.FromBytes(total);
    }

    private async Task RefreshSizesAsync()
    {
        var generation = Interlocked.Increment(ref _scanGeneration);
        IsScanning = true;
        IsBusy = true;
        IsBusyIndeterminate = true;
        StatusBarText = "Measuring local Cursor data…";
        var paths = CursorPaths.FromSettings(Settings);

        try
        {
            var ordered = Categories
                .OrderByDescending(c => c.IsEnabled)
                .ThenBy(c => c.IsLarge)
                .ToList();

            foreach (var item in ordered)
            {
                if (generation != _scanGeneration)
                    return;

                var deep = item.IsEnabled || !item.IsLarge;
                StatusBarText = deep
                    ? $"Measuring {item.Title}…"
                    : $"Checking {item.Title}…";

                var scan = await Task.Run(() => SizeScanner.Scan(item.Definition, paths, deep));
                if (generation != _scanGeneration)
                    return;

                item.ApplyScan(scan);
                UpdateEnabledTotal();
            }

            if (!IsSyncing)
                StatusBarText = "Ready";
        }
        catch (Exception ex)
        {
            StatusBarText = "Could not measure local data.";
            ShowBanner(UserFacingError.From(ex), "warn");
        }
        finally
        {
            if (generation == _scanGeneration)
            {
                IsScanning = false;
                if (!IsSyncing)
                    IsBusy = false;
            }
        }
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        ActivityLog.Add(line);
        while (ActivityLog.Count > 300)
            ActivityLog.RemoveAt(0);
    }

    private void ShowBanner(string text, string kind)
    {
        BannerText = text;
        BannerKind = kind;
    }

    private static string ToLocal(DateTime utc)
    {
        var local = utc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()
            : utc.ToLocalTime();
        return local.ToString("g");
    }
}
