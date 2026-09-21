namespace CursorSync.Models;

public enum AppPage
{
    Dashboard,
    Categories,
    Workspaces,
    Projects,
    Agents,
    Sync,
    History,
    Settings
}

public enum SyncKind
{
    Push,
    Pull,
    TwoWay
}

public enum ConflictPolicy
{
    NewerWins,
    PreferLocal,
    PreferHub
}

public enum CursorRisk
{
    None,
    Medium,
    High
}

public enum CategoryGroup
{
    Conversations,
    Agents,
    Editor,
    Optional
}
