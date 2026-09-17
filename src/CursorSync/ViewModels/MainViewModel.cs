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
        InitializeFromSettings();
        StatusBarText = "Starting…";
        IsBusy = true;
        IsBusyIndeterminate = true;
        _ = InitializeAsync();
    }

    public AppSettings Settings { get; }
    public AgentTransferViewModel Agents { get; }
    public ObservableCollection<CategoryItemViewModel> Categories { get; } = [];
    public IReadOnlyList<CategoryGroupViewModel> CategoryGroups { get; }
    public ObservableCollection<SyncHistoryEntry> History { get; } = [];
    public ObservableCollection<string> ActivityLog { get; } = [];
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
    [ObservableProperty] private ConflictPolicy _conflictPolicy = ConflictPolicy.NewerWins;
    [ObservableProperty] private string? _cursorUserDataDir;
    [ObservableProperty] private string? _pathReplaceFrom;
    [ObservableProperty] private string? _pathReplaceTo;
    [ObservableProperty] private string? _confirmTitle;
    [ObservableProperty] private string? _confirmMessage;
    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isBusyIndeterminate = true;
    [ObservableProperty] private string _statusBarText = "Starting…";

    private Action? _confirmAction;
    private int _scanGeneration;
    private bool _suspendCategoryPersist;
    private int _workDepth;

    partial void OnCurrentPageChanged(AppPage value)
    {
        if (value == AppPage.Agents)
            _ = Agents.RefreshCommand.ExecuteAsync(null);
    }

    public void Notify(string text, string kind) => ShowBanner(text, kind);

    public void Confirm(string title, string message, Action action) => AskConfirm(title, message, action);

    public void RefreshCursorStatus() => RefreshStatus();

    public void BeginBackgroundWork(string status)
    {
        _workDepth++;
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

    partial void OnHubPathChanged(string? value)
    {
        Settings.HubPath = value;
        SaveSettings();
        RefreshHubInfo();
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

        if (SelectedKind == SyncKind.Pull)
        {
            AskConfirm(
                "Pull from the sync folder?",
                "This overwrites local Cursor files for the selected categories. A backup is created first if that option is on.",
                () => _ = ExecuteSyncAsync(selected));
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
            result = await _engine.RunAsync(
                SelectedKind,
                Settings,
                selected.Select(c => c.Definition).ToList(),
                progress,
                _syncCts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new SyncResult { Success = false, Error = "Sync cancelled." };
        }
        catch (Exception ex)
        {
            result = new SyncResult { Success = false, Error = ex.Message };
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

    private void AskConfirm(string title, string message, Action action)
    {
        ConfirmTitle = title;
        ConfirmMessage = message;
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
        }
        catch (Exception ex)
        {
            StatusBarText = "Startup check failed.";
            ShowBanner(ex.Message, "error");
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
            ShowBanner(ex.Message, "warn");
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
