using System.Text.Json;
using CursorSync.Models;
using Microsoft.Data.Sqlite;

namespace CursorSync.Services;

public static class CursorIntegrityService
{
    public static CursorIntegrityReport Inspect(CursorPaths paths, bool cursorRunning = false)
    {
        var issues = new List<IntegrityIssue>();
        var workspaces = new List<CursorWorkspaceInfo>();

        if (!Directory.Exists(paths.UserFolder))
        {
            issues.Add(Error("paths.user", "Cursor’s user-data folder was not found. Expected %APPDATA%\\Cursor\\User, or set a custom folder in Settings."));
            return Finish(issues, workspaces);
        }

        if (!Directory.Exists(paths.GlobalStorage))
            issues.Add(Error("paths.global", "Cursor’s globalStorage folder is missing. Chats cannot be read until Cursor recreates it."));

        if (!File.Exists(paths.StateDb))
            issues.Add(Warning("sqlite.missing", "The global chat database (state.vscdb) is missing. Open Cursor once to create it."));
        else
            CheckSqlite(paths.StateDb, "global chat database", "sqlite.global", cursorRunning, issues);

        if (File.Exists(paths.ConversationSearchDb))
            CheckSqlite(paths.ConversationSearchDb, "conversation search database", "sqlite.search", cursorRunning, issues);

        TryParseJson(paths.SettingsFile, "settings.json", "json.settings", issues);
        TryParseJson(paths.KeybindingsFile, "keybindings.json", "json.keybindings", issues);
        TryParseJson(paths.McpFile, "mcp.json", "json.mcp", issues);

        workspaces.AddRange(CursorWorkspaceLocator.List(paths));
        var byId = workspaces.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        var seenFolders = new Dictionary<string, CursorWorkspaceInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var workspace in workspaces)
        {
            var jsonPath = Path.Combine(workspace.StorageDir, "workspace.json");
            if (!File.Exists(jsonPath))
            {
                issues.Add(Error("workspace.json:" + workspace.Id, $"Workspace “{workspace.Label}” has no workspace.json."));
                continue;
            }

            if (!TryReadWorkspaceJson(jsonPath, out var parseError))
                issues.Add(Error("workspace.invalid:" + workspace.Id, $"Workspace “{workspace.Label}” has invalid workspace.json. {parseError}"));

            if (!workspace.FolderExists)
                issues.Add(Warning("workspace.folder:" + workspace.Id, $"Workspace “{workspace.Label}” points at a folder that is missing: {workspace.FolderPath}"));

            var key = CursorWorkspaceLocator.NormalizeFolder(workspace.FolderPath);
            if (!string.IsNullOrWhiteSpace(key) && !seenFolders.TryAdd(key, workspace))
                issues.Add(Warning("workspace.duplicate:" + workspace.Id, $"“{workspace.Label}” and “{seenFolders[key].Label}” point at the same folder."));

            var db = Path.Combine(workspace.StorageDir, "state.vscdb");
            if (File.Exists(db))
                CheckSqlite(db, $"workspace database for “{workspace.Label}”", "sqlite.workspace:" + workspace.Id, cursorRunning, issues);
        }

        var catalog = SqliteStateStore.ReadCatalog(paths);
        foreach (var (composerId, header) in catalog.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.WorkspaceId))
                continue;

            if (!byId.TryGetValue(header.WorkspaceId, out var workspace))
            {
                issues.Add(Warning(
                    "composer.unknownWorkspace:" + composerId,
                    $"Agent “{header.Title}” refers to workspace {header.WorkspaceId}, which is not in workspaceStorage."));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(header.WorkspacePath)
                && !CursorWorkspaceLocator.SameFolder(header.WorkspacePath, workspace.FolderPath))
            {
                issues.Add(Warning(
                    "composer.pathMismatch:" + composerId,
                    $"Agent “{header.Title}” is tagged with {header.WorkspacePath}, but that workspace ID now points at {workspace.FolderPath}."));
            }
        }

        if (cursorRunning)
            issues.Add(Warning("cursor.running", "Cursor is running, so this check may miss database problems that only show up after it quits."));

        return Finish(issues, workspaces);
    }

    public static CursorIntegrityReport Predict(IntegrityPlan plan, CursorIntegrityReport current)
    {
        var issues = new List<IntegrityIssue>();

        if (plan.TouchesSqlite && plan.CursorRunning)
            issues.Add(Warning("predict.cursor", "Cursor still has the chat database open. The change may not stick, or the snapshot may be incomplete."));

        if (plan.Target is { } target)
        {
            if (!string.IsNullOrWhiteSpace(target.FolderPath) && !Directory.Exists(target.FolderPath))
                issues.Add(Error("predict.folder", $"The folder for “{target.Label}” does not exist."));
            if (plan.TouchesSqlite && !target.IsReady)
                issues.Add(Warning("predict.unready", $"“{target.Label}” has no workspace database yet. Open it in Cursor once so the sidebar can update."));
        }

        if (plan.Action == "rename")
        {
            if (string.IsNullOrWhiteSpace(plan.NewFolder))
                issues.Add(Error("predict.rename", "Enter a new workspace name."));
            else if (Directory.Exists(plan.NewFolder) || File.Exists(plan.NewFolder))
                issues.Add(Error("predict.renameExists", "A folder with that name already exists."));
        }
        else if (plan.Action != "rename-project" && !string.IsNullOrWhiteSpace(plan.NewFolder))
        {
            if (!Directory.Exists(plan.NewFolder))
                issues.Add(Error("predict.newFolder", "The new workspace folder does not exist."));
            else
            {
                var clash = CursorWorkspaceLocator.FindByFolder(current.Workspaces, plan.NewFolder);
                if (clash is not null && plan.Target is not null
                    && !string.Equals(clash.Id, plan.Target.Id, StringComparison.OrdinalIgnoreCase))
                    issues.Add(Error("predict.clash", $"“{clash.Label}” already points at that folder."));
            }
        }

        if (plan.DeletesWorkspace && plan.Sources.Count == 0)
            issues.Add(Error("predict.delete", "No workspace was selected to delete."));

        if (plan.Action == "delete-projects" && plan.ProjectCount <= 0)
            issues.Add(Error("predict.deleteProjects", "No project cache was selected to delete."));

        if (plan.Action == "delete-agents" && plan.AgentCount <= 0)
            issues.Add(Error("predict.deleteAgents", "No chat was selected to delete."));

        if (plan.Action == "archive-agents" && plan.AgentCount <= 0)
            issues.Add(Error("predict.archiveAgents", "No chat was selected to archive or restore."));

        if (plan.Action == "rename-project")
        {
            if (string.IsNullOrWhiteSpace(plan.NewFolder))
                issues.Add(Error("predict.renameProject", "Enter a new project cache name."));
            else if (Directory.Exists(plan.NewFolder) || File.Exists(plan.NewFolder))
                issues.Add(Error("predict.renameProjectExists", "A project cache with that name already exists."));
        }

        if (plan.Action is "assign" or "restore" or "import" && plan.Target is null && plan.Action == "assign")
            issues.Add(Error("predict.target", "Choose a destination workspace Cursor already knows."));

        return new CursorIntegrityReport
        {
            CheckedUtc = DateTime.UtcNow,
            Issues = issues,
            Workspaces = current.Workspaces
        };
    }

    public static bool IsWorse(CursorIntegrityReport before, CursorIntegrityReport after)
    {
        if (after.ErrorCount > before.ErrorCount)
            return true;

        var previous = before.Issues
            .Where(i => i.Severity == IntegritySeverity.Error)
            .Select(i => i.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return after.Issues.Any(i =>
            i.Severity == IntegritySeverity.Error && !previous.Contains(i.Code));
    }

    private static CursorIntegrityReport Finish(List<IntegrityIssue> issues, List<CursorWorkspaceInfo> workspaces) =>
        new()
        {
            CheckedUtc = DateTime.UtcNow,
            Subject = "Cursor user data",
            Issues = issues,
            Workspaces = workspaces
        };

    private static void CheckSqlite(string dbPath, string label, string code, bool cursorRunning, List<IntegrityIssue> issues)
    {
        try
        {
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
            {
                if (cursorRunning)
                    issues.Add(Warning(code, $"Could not fully verify the {label} while Cursor is running."));
                else
                    issues.Add(Error(code, $"The {label} failed SQLite’s integrity check ({result})."));
            }
        }
        catch (Exception ex)
        {
            if (cursorRunning)
                issues.Add(Warning(code, $"The {label} could not be opened while Cursor is running."));
            else
                issues.Add(Error(code, $"The {label} could not be opened: {UserFacingError.From(ex)}"));
        }
    }

    private static void TryParseJson(string path, string name, string code, List<IntegrityIssue> issues)
    {
        if (!File.Exists(path))
            return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            _ = doc.RootElement.ValueKind;
        }
        catch
        {
            issues.Add(Error(code, $"{name} is not valid JSON."));
        }
    }

    private static bool TryReadWorkspaceJson(string path, out string error)
    {
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var hasFolder = root.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(folder.GetString());
            var hasWorkspace = root.TryGetProperty("workspace", out var workspace) && workspace.ValueKind == JsonValueKind.String
                               && !string.IsNullOrWhiteSpace(workspace.GetString());
            if (hasFolder || hasWorkspace)
                return true;
            error = "It has no folder or workspace path.";
            return false;
        }
        catch (Exception ex)
        {
            error = UserFacingError.From(ex);
            return false;
        }
    }

    private static IntegrityIssue Error(string code, string message) =>
        new() { Severity = IntegritySeverity.Error, Code = code, Message = message };

    private static IntegrityIssue Warning(string code, string message) =>
        new() { Severity = IntegritySeverity.Warning, Code = code, Message = message };
}
