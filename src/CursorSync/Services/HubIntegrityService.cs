using System.IO.Compression;
using System.Text.Json;
using CursorSync.Models;
using Microsoft.Data.Sqlite;

namespace CursorSync.Services;

public static class HubIntegrityService
{
    public static CursorIntegrityReport Inspect(string? hubPath)
    {
        var issues = new List<IntegrityIssue>();
        if (string.IsNullOrWhiteSpace(hubPath))
        {
            issues.Add(Error("hub.path", "Choose a sync folder first."));
            return Finish(issues);
        }

        var hub = hubPath.Trim();
        if (!Directory.Exists(hub))
        {
            issues.Add(Error("hub.missing", "The sync folder does not exist: " + hub));
            return Finish(issues);
        }

        if (!CanWrite(hub, out var writeError))
            issues.Add(Error("hub.writable", writeError ?? "This PC cannot write to the sync folder."));

        try
        {
            var root = Path.GetPathRoot(hub);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < 50L * 1024 * 1024)
                    issues.Add(Warning("hub.space", "The drive for the sync folder has less than 50 MB free."));
            }
        }
        catch
        {
            // drive info is optional
        }

        var manifestPath = Path.Combine(hub, "cursorsync.json");
        HubManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                manifest = JsonSerializer.Deserialize<HubManifest>(File.ReadAllText(manifestPath), JsonUtil.Options);
                if (manifest is null)
                    issues.Add(Error("hub.manifest", "cursorsync.json is empty."));
                else if (!string.IsNullOrWhiteSpace(manifest.AppName)
                         && !string.Equals(manifest.AppName, "CursorSync", StringComparison.OrdinalIgnoreCase))
                    issues.Add(Warning("hub.app", $"cursorsync.json was written by “{manifest.AppName}”, not CursorSync."));
            }
            catch (Exception ex)
            {
                issues.Add(Error("hub.manifest", "cursorsync.json is not valid JSON. " + UserFacingError.From(ex)));
            }
        }

        var payload = Path.Combine(hub, "payload");
        if (!Directory.Exists(payload))
        {
            if (manifest is not null)
                issues.Add(Warning("hub.payload", "The hub manifest exists, but the payload folder is missing. Push from a PC to recreate it."));
            else
                issues.Add(Info("hub.empty", "This sync folder is empty. The first push will create the payload."));
            return Finish(issues);
        }

        ScanPayload(payload, issues);
        return Finish(issues);
    }

    public static HubEmptyResult Empty(string hubPath)
    {
        if (string.IsNullOrWhiteSpace(hubPath))
            return new HubEmptyResult { Error = "Choose a sync folder first." };

        var hub = hubPath.Trim();
        if (!Directory.Exists(hub))
            return new HubEmptyResult { Error = "The sync folder does not exist." };

        var warnings = new List<string>();
        var removed = 0;

        foreach (var file in new[]
                 {
                     Path.Combine(hub, "cursorsync.json"),
                     Path.Combine(hub, ".cursorsync-health")
                 })
        {
            if (TryDeleteFile(file, warnings))
                removed++;
        }

        var payload = Path.Combine(hub, "payload");
        if (Directory.Exists(payload))
        {
            if (TryDeleteTree(payload, warnings))
                removed++;
            else if (Directory.Exists(payload))
                warnings.Add("Some files in payload could not be deleted. Close other apps using the sync folder, then try again.");
        }

        if (removed == 0 && warnings.Count == 0)
            return new HubEmptyResult { Success = true, Message = "The sync folder was already empty of CursorSync data." };

        if (Directory.Exists(payload) || File.Exists(Path.Combine(hub, "cursorsync.json")))
            return new HubEmptyResult
            {
                Success = false,
                Removed = removed,
                Warnings = warnings,
                Error = warnings.Count > 0 ? warnings[0] : "The sync folder could not be fully emptied."
            };

        return new HubEmptyResult
        {
            Success = true,
            Removed = removed,
            Warnings = warnings,
            Message = "CursorSync data was removed from the sync folder. Push again when you want to refill it."
        };
    }

    private static bool TryDeleteFile(string path, List<string> warnings)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add(Path.GetFileName(path) + ": " + UserFacingError.From(ex));
            return false;
        }
    }

    private static bool TryDeleteTree(string root, List<string> warnings)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch { }
            }

            Directory.Delete(root, recursive: true);
            return !Directory.Exists(root);
        }
        catch (Exception ex)
        {
            warnings.Add("payload: " + UserFacingError.From(ex));
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Reverse())
                    TryDeleteFile(file, warnings);
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                             .OrderByDescending(path => path.Length))
                {
                    try { Directory.Delete(dir, recursive: false); }
                    catch { }
                }
                Directory.Delete(root, recursive: false);
            }
            catch
            {
                // warnings already recorded
            }

            return !Directory.Exists(root);
        }
    }

    private static void ScanPayload(string payload, List<IntegrityIssue> issues)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(payload, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            });
        }
        catch (Exception ex)
        {
            issues.Add(Error("hub.payloadRead", "The payload folder could not be read: " + UserFacingError.From(ex)));
            return;
        }

        var jsonChecked = 0;
        var sqliteChecked = 0;
        var zipChecked = 0;
        foreach (var file in files)
        {
            if (issues.Count >= 40)
            {
                issues.Add(Warning("hub.truncated", "Stopped after 40 findings. Fix these, then check again."));
                break;
            }

            var name = Path.GetFileName(file);
            if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "sync.lock", StringComparison.OrdinalIgnoreCase))
                continue;

            if (LooksConflicted(name))
            {
                issues.Add(Warning("hub.conflict:" + name, "Possible cloud-sync conflict file: " + Rel(payload, file)));
                continue;
            }

            var ext = Path.GetExtension(file);
            if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                jsonChecked++;
                TryParseJson(file, Rel(payload, file), issues);
                continue;
            }

            if (ext.Equals(".vscdb", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".db", StringComparison.OrdinalIgnoreCase))
            {
                sqliteChecked++;
                CheckSqlite(file, Rel(payload, file), issues);
                continue;
            }

            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zipChecked++;
                CheckZip(file, Rel(payload, file), issues);
            }
        }

        if (jsonChecked == 0 && sqliteChecked == 0 && zipChecked == 0)
            issues.Add(Info("hub.payloadEmpty", "The payload folder has no databases, JSON, or zip backups yet."));
    }

    private static bool CanWrite(string hub, out string? error)
    {
        error = null;
        var probe = Path.Combine(hub, ".cursorsync-health");
        try
        {
            File.WriteAllText(probe, DateTime.UtcNow.ToString("o"));
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = "This PC cannot write to the sync folder. Check permissions or cloud-sync status. " + UserFacingError.From(ex);
            try { if (File.Exists(probe)) File.Delete(probe); }
            catch { }
            return false;
        }
    }

    private static void TryParseJson(string path, string label, List<IntegrityIssue> issues)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            _ = doc.RootElement.ValueKind;
        }
        catch (Exception ex)
        {
            issues.Add(Error("hub.json:" + label, label + " is not valid JSON. " + UserFacingError.From(ex)));
        }
    }

    private static void CheckSqlite(string dbPath, string label, List<IntegrityIssue> issues)
    {
        try
        {
            if (new FileInfo(dbPath).Length == 0)
            {
                issues.Add(Warning("hub.sqliteEmpty:" + label, label + " is empty."));
                return;
            }

            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using (var timeout = connection.CreateCommand())
            {
                timeout.CommandText = "PRAGMA busy_timeout=4000;";
                timeout.ExecuteNonQuery();
            }

            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            var result = check.ExecuteScalar()?.ToString();
            if (!string.IsNullOrWhiteSpace(result) && !string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                issues.Add(Error("hub.sqlite:" + label, label + " failed SQLite’s integrity check (" + result + ")."));
        }
        catch (Exception ex)
        {
            issues.Add(Warning("hub.sqlite:" + label, label + " could not be opened. If this is a cloud placeholder, make the file available offline. " + UserFacingError.From(ex)));
        }
    }

    private static void CheckZip(string path, string label, List<IntegrityIssue> issues)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count == 0)
                issues.Add(Warning("hub.zipEmpty:" + label, label + " is an empty zip."));
        }
        catch (Exception ex)
        {
            issues.Add(Error("hub.zip:" + label, label + " is not a readable zip. " + UserFacingError.From(ex)));
        }
    }

    private static bool LooksConflicted(string name) =>
        name.Contains("conflicted copy", StringComparison.OrdinalIgnoreCase)
        || name.Contains("(conflict)", StringComparison.OrdinalIgnoreCase)
        || name.Contains(" - Copy.", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);

    private static string Rel(string root, string file)
    {
        try { return Path.GetRelativePath(root, file); }
        catch { return Path.GetFileName(file); }
    }

    private static CursorIntegrityReport Finish(List<IntegrityIssue> issues) =>
        new()
        {
            CheckedUtc = DateTime.UtcNow,
            Subject = "The sync folder",
            Issues = issues
        };

    private static IntegrityIssue Error(string code, string message) =>
        new() { Severity = IntegritySeverity.Error, Code = code, Message = message };

    private static IntegrityIssue Warning(string code, string message) =>
        new() { Severity = IntegritySeverity.Warning, Code = code, Message = message };

    private static IntegrityIssue Info(string code, string message) =>
        new() { Severity = IntegritySeverity.Info, Code = code, Message = message };
}

public sealed class HubEmptyResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public int Removed { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
