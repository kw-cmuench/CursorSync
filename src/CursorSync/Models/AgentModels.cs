namespace CursorSync.Models;

public sealed class CursorWorkspaceInfo
{
    public required string Id { get; init; }
    public required string FolderPath { get; init; }
    public required string FolderUri { get; init; }
    public required string Slug { get; init; }
    public required string Label { get; init; }
    public required string StorageDir { get; init; }
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
    public string? HeaderJson { get; set; }
}

public sealed class AgentBackupInfo
{
    public required string FolderPath { get; init; }
    public required AgentBackupManifest Manifest { get; init; }
    public string Label => string.IsNullOrWhiteSpace(Manifest.Title) ? Manifest.ComposerId : Manifest.Title;
    public string Detail { get; init; } = "";
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
