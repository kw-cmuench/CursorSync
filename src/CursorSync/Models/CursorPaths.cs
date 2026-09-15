namespace CursorSync.Models;

public sealed class CursorPaths
{
    public string UserDataRoot { get; }
    public string UserFolder { get; }
    public string GlobalStorage { get; }
    public string WorkspaceStorage { get; }
    public string History { get; }
    public string Snippets { get; }
    public string SettingsFile { get; }
    public string KeybindingsFile { get; }
    public string HomeCursor { get; }
    public string LocalCursor { get; }
    public string AgentStores { get; }
    public string Agents { get; }
    public string Plugins { get; }
    public string Plans { get; }
    public string Skills { get; }
    public string Extensions { get; }
    public string Projects { get; }
    public string McpFile { get; }
    public string HooksFile { get; }
    public string HooksFolder { get; }
    public string StateDb { get; }
    public string ConversationSearchDb { get; }
    public string Checkpoints { get; }

    public CursorPaths(string? userDataDir = null, string? homeDir = null)
    {
        UserDataRoot = string.IsNullOrWhiteSpace(userDataDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor")
            : userDataDir.Trim();

        UserFolder = Path.Combine(UserDataRoot, "User");
        GlobalStorage = Path.Combine(UserFolder, "globalStorage");
        WorkspaceStorage = Path.Combine(UserFolder, "workspaceStorage");
        History = Path.Combine(UserFolder, "History");
        Snippets = Path.Combine(UserFolder, "snippets");
        SettingsFile = Path.Combine(UserFolder, "settings.json");
        KeybindingsFile = Path.Combine(UserFolder, "keybindings.json");
        StateDb = Path.Combine(GlobalStorage, "state.vscdb");
        ConversationSearchDb = Path.Combine(GlobalStorage, "conversation-search.db");
        Checkpoints = Path.Combine(GlobalStorage, "anysphere.cursor-commits");

        HomeCursor = string.IsNullOrWhiteSpace(homeDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor")
            : homeDir.Trim();

        LocalCursor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cursor");
        AgentStores = Path.Combine(LocalCursor, "AgentStores");
        Agents = Path.Combine(HomeCursor, "agents");
        Plugins = Path.Combine(HomeCursor, "plugins");
        Plans = Path.Combine(HomeCursor, "plans");
        Skills = Path.Combine(HomeCursor, "skills");
        Extensions = Path.Combine(HomeCursor, "extensions");
        Projects = Path.Combine(HomeCursor, "projects");
        McpFile = Path.Combine(HomeCursor, "mcp.json");
        HooksFile = Path.Combine(HomeCursor, "hooks.json");
        HooksFolder = Path.Combine(HomeCursor, "hooks");
    }

    public static CursorPaths FromSettings(AppSettings settings) =>
        new(settings.CursorUserDataDir, settings.CursorHomeDir);
}
