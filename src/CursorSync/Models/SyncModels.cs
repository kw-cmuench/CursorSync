namespace CursorSync.Models;

public sealed class SyncEntry
{
    public required string LocalPath { get; init; }
    public required string RelativePayloadPath { get; init; }
    public bool IsDirectory { get; init; }
    public IReadOnlyList<string>? IncludeFileNames { get; init; }
    public IReadOnlyList<string>? ExcludeDirectoryNames { get; init; }
    public IReadOnlyList<string>? ExcludeFileNames { get; init; }
    public IReadOnlyList<string>? CompanionSuffixes { get; init; }
}

public sealed class CategoryDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public required CategoryGroup Group { get; init; }
    public bool DefaultEnabled { get; init; } = true;
    public bool RequiresCursorClosed { get; init; }
    public bool IsLarge { get; init; }
    public required Func<CursorPaths, IReadOnlyList<SyncEntry>> Resolve { get; init; }
}

public sealed class SyncProgress
{
    public string Message { get; init; } = "";
    public int FilesCopied { get; init; }
    public long BytesCopied { get; init; }
    public double? Fraction { get; init; }
}

public sealed class SyncResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int FilesCopied { get; init; }
    public long BytesCopied { get; init; }
    public int FilesSkipped { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Log { get; init; } = [];
}

public sealed class SyncHistoryEntry
{
    public DateTime Utc { get; set; }
    public string Kind { get; set; } = "";
    public string Machine { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int FilesCopied { get; set; }
    public long BytesCopied { get; set; }
    public List<string> Categories { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string HubPath { get; set; } = "";
    public string DisplayTime =>
        (Utc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(Utc, DateTimeKind.Utc)
            : Utc).ToLocalTime().ToString("g");

    public string ResultText => Success
        ? $"{FilesCopied} files copied"
        : string.IsNullOrWhiteSpace(Error) ? "Failed" : Error;
}

public sealed class HubManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string AppName { get; set; } = "CursorSync";
    public DateTime UpdatedUtc { get; set; }
    public string? LastMachine { get; set; }
    public string? LastKind { get; set; }
    public List<string> LastCategories { get; set; } = [];
    public List<HubMachine> Machines { get; set; } = [];
}

public sealed class HubMachine
{
    public string Name { get; set; } = "";
    public DateTime LastSeenUtc { get; set; }
}

public sealed class CursorStatus
{
    public bool UserFolderFound { get; init; }
    public bool HomeFolderFound { get; init; }
    public bool IsRunning { get; init; }
    public int ProcessCount { get; init; }
    public string UserFolder { get; init; } = "";
    public string HomeFolder { get; init; } = "";
}

public sealed class CategoryScan
{
    public required string Id { get; init; }
    public bool Present { get; init; }
    public long Bytes { get; init; }
    public int Files { get; init; }
}
