namespace CursorSync.Models;

public enum IntegritySeverity
{
    Info,
    Warning,
    Error
}

public sealed class IntegrityIssue
{
    public required IntegritySeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string Display =>
        Severity == IntegritySeverity.Error ? "Error: " + Message
        : Severity == IntegritySeverity.Warning ? "Warning: " + Message
        : Message;
}

public sealed class CursorIntegrityReport
{
    public DateTime CheckedUtc { get; init; } = DateTime.UtcNow;
    public string Subject { get; init; } = "Cursor user data";
    public IReadOnlyList<IntegrityIssue> Issues { get; init; } = [];
    public IReadOnlyList<CursorWorkspaceInfo> Workspaces { get; init; } = [];
    public int ErrorCount => Issues.Count(i => i.Severity == IntegritySeverity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == IntegritySeverity.Warning);
    public bool HasErrors => ErrorCount > 0;
    public bool HasWarnings => WarningCount > 0;

    public string Summary
    {
        get
        {
            if (ErrorCount == 0 && WarningCount == 0)
                return $"{Subject} looks consistent.";
            if (ErrorCount > 0 && WarningCount > 0)
                return $"{ErrorCount} error{(ErrorCount == 1 ? "" : "s")} and {WarningCount} warning{(WarningCount == 1 ? "" : "s")}.";
            if (ErrorCount > 0)
                return $"{ErrorCount} error{(ErrorCount == 1 ? "" : "s")} in {Subject.ToLowerInvariant()}.";
            return $"{WarningCount} warning{(WarningCount == 1 ? "" : "s")} in {Subject.ToLowerInvariant()}.";
        }
    }
}

public sealed class IntegrityPlan
{
    public required string Action { get; init; }
    public bool CursorRunning { get; init; }
    public bool TouchesSqlite { get; init; }
    public bool TouchesWorkspaceStorage { get; init; }
    public bool DeletesWorkspace { get; init; }
    public bool DeletesProject { get; init; }
    public CursorWorkspaceInfo? Target { get; init; }
    public string? NewFolder { get; init; }
    public IReadOnlyList<CursorWorkspaceInfo> Sources { get; init; } = [];
    public int AgentCount { get; init; }
    public int ProjectCount { get; init; }

    public static IntegrityPlan Assign(CursorWorkspaceInfo target, int agentCount) => new()
    {
        Action = "assign",
        TouchesSqlite = true,
        TouchesWorkspaceStorage = true,
        Target = target,
        AgentCount = agentCount
    };

    public static IntegrityPlan Restore(CursorWorkspaceInfo? target, int agentCount) => new()
    {
        Action = "restore",
        TouchesSqlite = true,
        TouchesWorkspaceStorage = true,
        Target = target,
        AgentCount = agentCount
    };

    public static IntegrityPlan Import(CursorWorkspaceInfo? target, int agentCount) => new()
    {
        Action = "import",
        TouchesSqlite = true,
        TouchesWorkspaceStorage = true,
        Target = target,
        AgentCount = agentCount
    };

    public static IntegrityPlan Rename(CursorWorkspaceInfo workspace, string newFolder, int agentCount) => new()
    {
        Action = "rename",
        TouchesSqlite = agentCount > 0,
        TouchesWorkspaceStorage = true,
        Target = workspace,
        NewFolder = newFolder,
        AgentCount = agentCount
    };

    public static IntegrityPlan Edit(CursorWorkspaceInfo workspace, string newFolder, int agentCount) => new()
    {
        Action = "edit",
        TouchesSqlite = agentCount > 0,
        TouchesWorkspaceStorage = true,
        Target = workspace,
        NewFolder = newFolder,
        AgentCount = agentCount
    };

    public static IntegrityPlan Delete(IReadOnlyList<CursorWorkspaceInfo> workspaces, int agentCount) => new()
    {
        Action = "delete",
        TouchesSqlite = agentCount > 0,
        TouchesWorkspaceStorage = true,
        DeletesWorkspace = true,
        Sources = workspaces,
        AgentCount = agentCount
    };

    public static IntegrityPlan Sync(bool writesLocal) => new()
    {
        Action = "sync",
        TouchesSqlite = writesLocal,
        TouchesWorkspaceStorage = writesLocal
    };

    public static IntegrityPlan DeleteAgents(int agentCount) => new()
    {
        Action = "delete-agents",
        TouchesSqlite = true,
        TouchesWorkspaceStorage = true,
        AgentCount = agentCount
    };

    public static IntegrityPlan DeleteProjects(int projectCount, int agentCount) => new()
    {
        Action = "delete-projects",
        TouchesSqlite = agentCount > 0,
        DeletesProject = true,
        ProjectCount = projectCount,
        AgentCount = agentCount
    };

    public static IntegrityPlan RenameProjectCache(string newFolder) => new()
    {
        Action = "rename-project",
        NewFolder = newFolder
    };
}

public sealed class GuardedActionResult
{
    public bool Ran { get; init; }
    public bool Aborted { get; init; }
    public bool RolledBack { get; init; }
    public bool Completed => !Aborted && !RolledBack;

    public static GuardedActionResult Abort() => new() { Aborted = true };
    public static GuardedActionResult Ok() => new() { Ran = true };
    public static GuardedActionResult Rollback() => new() { Ran = true, RolledBack = true };
}
