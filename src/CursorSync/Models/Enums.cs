namespace CursorSync.Models;

public enum AppPage
{
    Dashboard,
    Categories,
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

public enum CategoryGroup
{
    Conversations,
    Agents,
    Editor,
    Optional
}
