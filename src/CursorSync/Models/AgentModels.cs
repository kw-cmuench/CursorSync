namespace CursorSync.Models;

public sealed class CursorWorkspaceInfo
{
    public required string Id { get; init; }
    public required string FolderPath { get; init; }
    public required string FolderUri { get; init; }
    public required string Slug { get; init; }
    public required string Label { get; init; }
    public required string StorageDir { get; init; }
    public bool IsReady { get; init; }
    public bool FolderExists { get; init; }
}

public sealed class AgentRecord
{
    public required string ComposerId { get; init; }
    public required string Title { get; init; }
    public string? WorkspaceId { get; init; }
    public string? WorkspacePath { get; init; }
    public string? WorkspaceLabel { get; init; }
    public string? ProjectSlug { get; init; }
    public string? TranscriptDir { get; init; }
    public string? StoreDir { get; init; }
    public IReadOnlyList<string> SubagentStoreDirs { get; init; } = [];
    public IReadOnlyList<string> WaypointDirs { get; init; } = [];
    public DateTime LastWriteUtc { get; init; }
    public long Bytes { get; init; }
    public int Files { get; init; }
    public bool HasStore { get; init; }
    public bool HasWaypoints { get; init; }
    public bool IsArchived { get; init; }
    public string? HeaderJson { get; init; }
}

public sealed class AgentSqliteSlice
{
    public Dictionary<string, string> ItemTable { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CursorDiskKv { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AgentBackupManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Kind { get; set; } = "agent";
    public string ComposerId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? SourceWorkspaceId { get; set; }
    public string? SourceWorkspacePath { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool IncludeTranscript { get; set; }
    public bool IncludeStore { get; set; }
    public bool IncludeWaypoints { get; set; }
    public int Files { get; set; }
    public long Bytes { get; set; }
    public int AgentCount { get; set; } = 1;
    public string? HeaderJson { get; set; }
    public string? SourceProjectSlug { get; set; }
    public List<WorkspaceBackupRef> Workspaces { get; set; } = [];
}

public sealed class WorkspaceBackupRef
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string FolderPath { get; set; } = "";
}

public enum AgentImportKind
{
    Unknown,
    CursorSyncPack,
    CursorSyncAgent,
    ProjectCache,
    HubPayload,
    CursorHome,
    AgentStores
}

public sealed class AgentImportItem
{
    public required string ComposerId { get; init; }
    public required string Title { get; init; }
    public string? TranscriptDir { get; init; }
    public string? TranscriptFile { get; init; }
    public string? StoreDir { get; init; }
    public string? SqliteDir { get; init; }
    public string? WaypointsDir { get; init; }
    public string? SourceWorkspacePath { get; init; }
    public string? SourceWorkspaceId { get; init; }
    public string? SourceProjectSlug { get; init; }
    public string? HeaderJson { get; init; }
}

public sealed class AgentImportInspection
{
    public required string SourcePath { get; init; }
    public AgentImportKind Kind { get; init; }
    public string KindLabel { get; init; } = "Unknown";
    public string Summary { get; init; } = "";
    public IReadOnlyList<AgentImportItem> Agents { get; init; } = [];
    public CursorIntegrityReport Report { get; init; } = new() { Subject = "This backup" };
    public int Files { get; init; }
    public long Bytes { get; init; }
    public int AgentCount => Agents.Count;
    public bool CanImport => !Report.HasErrors && AgentCount > 0;
}

public sealed class AgentBackupInfo
{
    public required string FolderPath { get; init; }
    public required AgentBackupManifest Manifest { get; init; }
    public string Label => IsArchive
        ? Path.GetFileNameWithoutExtension(FolderPath)
        : string.IsNullOrWhiteSpace(Manifest.Title)
            ? (IsPack ? "Workspace backup" : Manifest.ComposerId)
            : Manifest.Title;
    public string Detail { get; init; } = "";
    public bool IsArchive => FolderPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    public bool IsPack =>
        string.Equals(Manifest.Kind, "workspacePack", StringComparison.OrdinalIgnoreCase)
        || (!IsArchive && Directory.Exists(Path.Combine(FolderPath, "agents")));
    public string KindLabel => IsArchive ? "Zip" : IsPack ? "Workspaces" : "Agent";
}

public enum ProjectHealth
{
    InUse,
    Unused,
    Unusable
}

public sealed class CursorProjectInfo
{
    public required string Slug { get; init; }
    public required string ProjectDir { get; init; }
    public required string Label { get; init; }
    public CursorWorkspaceInfo? Workspace { get; init; }
    public string? FolderPath { get; init; }
    public bool FolderExists { get; init; }
    public bool CanRenameFolder { get; init; }
    public bool CanCleanup { get; init; }
    public ProjectHealth Health { get; init; }
    public required string StatusText { get; init; }
    public int AgentCount { get; init; }
    public int Files { get; init; }
    public long Bytes { get; init; }
    public DateTime LastWriteUtc { get; init; }
}

public sealed class WorkspaceSplitCandidate
{
    public required string FolderPath { get; init; }
    public required string Label { get; init; }
    public CursorWorkspaceInfo? Workspace { get; init; }
    public bool IsRegistered { get; init; }
    public bool IsReady { get; init; }
    public string StatusText =>
        IsReady ? "Ready in Cursor" : IsRegistered ? "Open in Cursor to finish" : "Not a Cursor workspace yet";
}

public sealed class ConversationPreview
{
    public string OpeningUser { get; init; } = "";
    public string OpeningAssistant { get; init; } = "";
    public string ClosingUser { get; init; } = "";
    public string ClosingAssistant { get; init; } = "";
    public string Status { get; init; } = "Select an agent to preview the conversation.";
    public bool HasContent { get; init; }
    public bool HasClosing =>
        !string.IsNullOrWhiteSpace(ClosingUser) || !string.IsNullOrWhiteSpace(ClosingAssistant);
}

public sealed class ComposerCatalog
{
    public Dictionary<string, (string Title, string? WorkspaceId, string? WorkspacePath, string HeaderJson)> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> CheckpointIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record AgentTransferResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? OutputPath { get; init; }
    public int FilesCopied { get; init; }
    public long BytesCopied { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Log { get; init; } = [];
    public string? RestoredComposerId { get; init; }
}
