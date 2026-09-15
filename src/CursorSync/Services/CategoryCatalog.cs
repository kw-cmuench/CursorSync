using CursorSync.Models;

namespace CursorSync.Services;

public static class CategoryCatalog
{
    public static IReadOnlyList<CategoryDefinition> All { get; } = Build();

    public static CategoryDefinition? Find(string id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<CategoryDefinition> Build()
    {
        var sqliteSidecars = new[] { "-wal", "-shm" };

        return
        [
            new CategoryDefinition
            {
                Id = "chats",
                Title = "Chats & composers",
                Description = "Composer history, chat index, and per-workspace chat databases.",
                Glyph = "\uE8F2",
                Group = CategoryGroup.Conversations,
                RequiresCursorClosed = true,
                Resolve = paths =>
                {
                    var entries = new List<SyncEntry>
                    {
                        FileWithSidecars(paths.StateDb, "chats/state.vscdb", sqliteSidecars),
                        FileWithSidecars(paths.ConversationSearchDb, "chats/conversation-search.db", sqliteSidecars)
                    };

                    if (Directory.Exists(paths.WorkspaceStorage))
                    {
                        foreach (var dir in Directory.EnumerateDirectories(paths.WorkspaceStorage))
                        {
                            var id = Path.GetFileName(dir);
                            entries.Add(new SyncEntry
                            {
                                LocalPath = dir,
                                RelativePayloadPath = $"chats/workspaceStorage/{id}",
                                IsDirectory = true,
                                IncludeFileNames = ["workspace.json", "state.vscdb", "state.vscdb-wal", "state.vscdb-shm"]
                            });
                        }
                    }

                    return entries;
                }
            },
            new CategoryDefinition
            {
                Id = "transcripts",
                Title = "Agent transcripts",
                Description = "Local agent conversation transcripts from ~/.cursor/projects.",
                Glyph = "\uE8A1",
                Group = CategoryGroup.Conversations,
                Resolve = paths =>
                {
                    var entries = new List<SyncEntry>();
                    if (!Directory.Exists(paths.Projects))
                        return entries;

                    foreach (var project in Directory.EnumerateDirectories(paths.Projects))
                    {
                        var transcripts = Path.Combine(project, "agent-transcripts");
                        if (!Directory.Exists(transcripts))
                            continue;

                        entries.Add(new SyncEntry
                        {
                            LocalPath = transcripts,
                            RelativePayloadPath = $"transcripts/projects/{Path.GetFileName(project)}/agent-transcripts",
                            IsDirectory = true
                        });
                    }

                    return entries;
                }
            },
            new CategoryDefinition
            {
                Id = "agentStores",
                Title = "Agent memory stores",
                Description = "Persistent agent stores used across sessions.",
                Glyph = "\uE9F5",
                Group = CategoryGroup.Agents,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.AgentStores,
                        RelativePayloadPath = "agentStores",
                        IsDirectory = true,
                        ExcludeFileNames = ["sync.lock"],
                        ExcludeDirectoryNames = [".sync"]
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "customAgents",
                Title = "Custom agents",
                Description = "User-defined agent configurations from ~/.cursor/agents.",
                Glyph = "\uE7EE",
                Group = CategoryGroup.Agents,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.Agents,
                        RelativePayloadPath = "agents",
                        IsDirectory = true
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "skills",
                Title = "User skills",
                Description = "Your custom skills from ~/.cursor/skills. Cursor-managed skills are skipped.",
                Glyph = "\uE7BE",
                Group = CategoryGroup.Agents,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.Skills,
                        RelativePayloadPath = "skills",
                        IsDirectory = true
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "mcp",
                Title = "MCP servers",
                Description = "User-level MCP configuration (~/.cursor/mcp.json).",
                Glyph = "\uE909",
                Group = CategoryGroup.Agents,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.McpFile,
                        RelativePayloadPath = "mcp/mcp.json",
                        IsDirectory = false
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "plugins",
                Title = "Plugins",
                Description = "Cursor plugin data from ~/.cursor/plugins.",
                Glyph = "\uE99A",
                Group = CategoryGroup.Agents,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.Plugins,
                        RelativePayloadPath = "plugins",
                        IsDirectory = true
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "plans",
                Title = "Plans",
                Description = "Saved Cursor plans from ~/.cursor/plans.",
                Glyph = "\uE8A5",
                Group = CategoryGroup.Agents,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.Plans,
                        RelativePayloadPath = "plans",
                        IsDirectory = true
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "hooks",
                Title = "Hooks",
                Description = "User hooks.json and hook scripts.",
                Glyph = "\uE945",
                Group = CategoryGroup.Agents,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.HooksFile,
                        RelativePayloadPath = "hooks/hooks.json",
                        IsDirectory = false
                    },
                    new SyncEntry
                    {
                        LocalPath = paths.HooksFolder,
                        RelativePayloadPath = "hooks/scripts",
                        IsDirectory = true
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "settings",
                Title = "Settings",
                Description = "Editor and Cursor settings from User/settings.json.",
                Glyph = "\uE713",
                Group = CategoryGroup.Editor,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.SettingsFile,
                        RelativePayloadPath = "editor/settings.json",
                        IsDirectory = false
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "keybindings",
                Title = "Keybindings",
                Description = "Custom keyboard shortcuts.",
                Glyph = "\uE765",
                Group = CategoryGroup.Editor,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.KeybindingsFile,
                        RelativePayloadPath = "editor/keybindings.json",
                        IsDirectory = false
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "snippets",
                Title = "Snippets",
                Description = "User code snippets.",
                Glyph = "\uE8C1",
                Group = CategoryGroup.Editor,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.Snippets,
                        RelativePayloadPath = "editor/snippets",
                        IsDirectory = true
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "extensionsList",
                Title = "Extension list",
                Description = "Names of installed extensions. Does not copy the extension binaries.",
                Glyph = "\uE74C",
                Group = CategoryGroup.Editor,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = Path.Combine(paths.Extensions, "extensions.json"),
                        RelativePayloadPath = "extensions/extensions.json",
                        IsDirectory = false
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "extensionsBinaries",
                Title = "Extension binaries",
                Description = "Full ~/.cursor/extensions folder. Large, and often better reinstalled on each machine.",
                Glyph = "\uE74C",
                Group = CategoryGroup.Optional,
                DefaultEnabled = false,
                IsLarge = true,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.Extensions,
                        RelativePayloadPath = "extensions/binaries",
                        IsDirectory = true,
                        ExcludeDirectoryNames = [".obsolete"]
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "checkpoints",
                Title = "Agent checkpoints",
                Description = "Composer checkpoint diffs and file snapshots. Can be very large.",
                Glyph = "\uE81C",
                Group = CategoryGroup.Optional,
                DefaultEnabled = false,
                IsLarge = true,
                RequiresCursorClosed = true,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.Checkpoints,
                        RelativePayloadPath = "checkpoints",
                        IsDirectory = true
                    }
                ]
            },
            new CategoryDefinition
            {
                Id = "localHistory",
                Title = "Local file history",
                Description = "VS Code-style local history of edited files. Machine-heavy and often skippable.",
                Glyph = "\uE81C",
                Group = CategoryGroup.Optional,
                DefaultEnabled = false,
                IsLarge = true,
                Resolve = paths =>
                [
                    new SyncEntry
                    {
                        LocalPath = paths.History,
                        RelativePayloadPath = "localHistory",
                        IsDirectory = true
                    }
                ]
            }
        ];
    }

    private static SyncEntry FileWithSidecars(string localPath, string relative, IReadOnlyList<string> suffixes) =>
        new()
        {
            LocalPath = localPath,
            RelativePayloadPath = relative,
            IsDirectory = false,
            CompanionSuffixes = suffixes
        };
}
