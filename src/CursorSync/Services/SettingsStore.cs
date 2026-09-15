using System.Text.Json;
using CursorSync.Models;

namespace CursorSync.Services;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}

public sealed class SettingsStore
{
    public string SettingsPath { get; }
    public string HistoryPath { get; }
    public string BackupsFolder { get; }
    public string AppDataFolder { get; }

    public SettingsStore()
    {
        AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CursorSync");
        SettingsPath = Path.Combine(AppDataFolder, "settings.json");
        HistoryPath = Path.Combine(AppDataFolder, "history.json");
        BackupsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CursorSync", "Backups");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonUtil.Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonUtil.Options));
    }

    public List<SyncHistoryEntry> LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath))
                return [];

            var json = File.ReadAllText(HistoryPath);
            return JsonSerializer.Deserialize<List<SyncHistoryEntry>>(json, JsonUtil.Options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveHistory(IReadOnlyList<SyncHistoryEntry> history)
    {
        Directory.CreateDirectory(AppDataFolder);
        var trimmed = history.OrderByDescending(h => h.Utc).Take(200).ToList();
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(trimmed, JsonUtil.Options));
    }
}
