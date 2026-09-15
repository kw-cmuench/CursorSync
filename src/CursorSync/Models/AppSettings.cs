namespace CursorSync.Models;

public sealed class AppSettings
{
    public string? HubPath { get; set; }
    public string MachineName { get; set; } = Environment.MachineName;
    public string? CursorUserDataDir { get; set; }
    public string? CursorHomeDir { get; set; }
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.NewerWins;
    public bool BackupBeforePull { get; set; } = true;
    public bool CloseCursorBeforeSync { get; set; }
    public string? PathReplaceFrom { get; set; }
    public string? PathReplaceTo { get; set; }
    public Dictionary<string, bool> CategoryEnabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastSyncUtc { get; set; }
    public string? LastSyncKind { get; set; }
    public bool LastSyncSuccess { get; set; }
}
