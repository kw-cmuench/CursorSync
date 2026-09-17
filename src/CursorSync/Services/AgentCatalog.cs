using System.Text.Json;
using System.Text.RegularExpressions;
using CursorSync.Models;

namespace CursorSync.Services;

public static class AgentCatalog
{
    private static readonly Regex Uuid = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    public static IReadOnlyList<AgentRecord> ListLocal(CursorPaths paths, CancellationToken cancellationToken)
    {
        var workspaces = CursorWorkspaceLocator.List(paths);
        var bySlug = workspaces.ToDictionary(w => w.Slug, w => w, StringComparer.OrdinalIgnoreCase);
        var catalog = TryCatalog(paths);
        var headers = catalog.Headers;
        var agents = new Dictionary<string, AgentBuilder>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(paths.Projects))
        {
            foreach (var project in Directory.EnumerateDirectories(paths.Projects))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transcriptsRoot = Path.Combine(project, "agent-transcripts");
                if (!Directory.Exists(transcriptsRoot))
                    continue;

                var slug = Path.GetFileName(project);
                bySlug.TryGetValue(slug, out var workspace);

                foreach (var dir in Directory.EnumerateDirectories(transcriptsRoot))
                {
                    var id = Path.GetFileName(dir);
                    if (!LooksLikeComposerId(id))
                        continue;

                    var builder = Get(agents, id);
                    builder.TranscriptDir = dir;
                    builder.ProjectSlug = slug;
                    ApplyWorkspace(builder, workspace);
                    builder.Title ??= TitleFromTranscript(dir);
                    Touch(builder, dir);
                }
            }
        }

        foreach (var (id, header) in headers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var builder = Get(agents, id);
            builder.Title = header.Title;
            builder.HeaderJson = header.HeaderJson;
            if (!string.IsNullOrWhiteSpace(header.WorkspaceId))
                builder.WorkspaceId = header.WorkspaceId;
            if (!string.IsNullOrWhiteSpace(header.WorkspacePath))
            {
                builder.WorkspacePath = header.WorkspacePath;
                var match = CursorWorkspaceLocator.FindByFolder(workspaces, header.WorkspacePath)
                    ?? workspaces.FirstOrDefault(w => w.Id == header.WorkspaceId);
                ApplyWorkspace(builder, match);
            }
        }

        var storesRoot = Path.Combine(paths.AgentStores, "cursor_agent_stores");
        if (!Directory.Exists(storesRoot))
            storesRoot = paths.AgentStores;

        foreach (var builder in agents.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var store = Path.Combine(storesRoot, builder.ComposerId);
            if (Directory.Exists(store))
            {
                builder.StoreDir = store;
                Touch(builder, store);
            }

            builder.SubagentStoreDirs = DiscoverSubagentStores(builder.TranscriptDir, storesRoot).ToList();
            foreach (var extra in builder.SubagentStoreDirs)
                Touch(builder, extra);

            builder.WaypointDirs = DiscoverWaypoints(paths, builder.ComposerId, catalog).ToList();
            foreach (var waypoint in builder.WaypointDirs)
                Touch(builder, waypoint);
        }

        return agents.Values
            .Select(ToRecord)
            .OrderByDescending(a => a.LastWriteUtc)
            .ToList();
    }

    public static IReadOnlyList<AgentBackupInfo> ListBackups(IEnumerable<string> roots)
    {
        var list = new List<AgentBackupInfo>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath))
                    continue;

                try
                {
                    var manifest = JsonSerializer.Deserialize<AgentBackupManifest>(File.ReadAllText(manifestPath), JsonUtil.Options);
                    if (manifest is null || string.IsNullOrWhiteSpace(manifest.ComposerId))
                        continue;

                    var workspace = string.IsNullOrWhiteSpace(manifest.SourceWorkspacePath)
                        ? "Unknown workspace"
                        : Path.GetFileName(manifest.SourceWorkspacePath.TrimEnd('\\', '/'));
                    list.Add(new AgentBackupInfo
                    {
                        FolderPath = dir,
                        Manifest = manifest,
                        Detail = $"{manifest.CreatedUtc.ToLocalTime():g} · {FileSizeFormatter.FromBytes(manifest.Bytes)} · {workspace}"
                    });
                }
                catch
                {
                    // skip unreadable packages
                }
            }
        }

        return list
            .OrderByDescending(b => b.Manifest.CreatedUtc)
            .ToList();
    }

    private static IEnumerable<string> DiscoverSubagentStores(string? transcriptDir, string storesRoot)
    {
        if (string.IsNullOrWhiteSpace(transcriptDir))
            yield break;

        var sub = Path.Combine(transcriptDir, "subagents");
        if (!Directory.Exists(sub))
            yield break;

        foreach (var file in Directory.EnumerateFiles(sub, "*.jsonl"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var store = Path.Combine(storesRoot, id);
            if (Directory.Exists(store))
                yield return store;
        }
    }

    private static IEnumerable<string> DiscoverWaypoints(CursorPaths paths, string composerId, ComposerCatalog catalog)
    {
        var checkpointRoot = Path.Combine(paths.Checkpoints, "checkpoints");
        if (!Directory.Exists(checkpointRoot))
            return [];
        if (!catalog.CheckpointIds.TryGetValue(composerId, out var ids))
            return [];

        return ids
            .Select(id => Path.Combine(checkpointRoot, id))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static ComposerCatalog TryCatalog(CursorPaths paths)
    {
        try
        {
            return SqliteStateStore.ReadCatalog(paths);
        }
        catch
        {
            return new ComposerCatalog();
        }
    }

    private static AgentBuilder Get(Dictionary<string, AgentBuilder> map, string id)
    {
        if (!map.TryGetValue(id, out var builder))
        {
            builder = new AgentBuilder { ComposerId = id };
            map[id] = builder;
        }

        return builder;
    }

    private static void ApplyWorkspace(AgentBuilder builder, CursorWorkspaceInfo? workspace)
    {
        if (workspace is null)
            return;
        builder.WorkspaceId ??= workspace.Id;
        builder.WorkspacePath ??= workspace.FolderPath;
        builder.WorkspaceLabel ??= workspace.Label;
        builder.ProjectSlug ??= workspace.Slug;
    }

    private static void Touch(AgentBuilder builder, string path)
    {
        try
        {
            var write = Directory.Exists(path)
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);
            if (write > builder.LastWriteUtc)
                builder.LastWriteUtc = write;
        }
        catch
        {
            // ignore
        }
    }

    private static string TitleFromTranscript(string transcriptDir)
    {
        var file = Path.Combine(transcriptDir, Path.GetFileName(transcriptDir) + ".jsonl");
        if (!File.Exists(file))
        {
            file = Directory.EnumerateFiles(transcriptDir, "*.jsonl").FirstOrDefault() ?? "";
            if (file.Length == 0)
                return Path.GetFileName(transcriptDir);
        }

        try
        {
            foreach (var line in File.ReadLines(file).Take(40))
            {
                var start = line.IndexOf("<user_query>", StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                    continue;
                start += "<user_query>".Length;
                var end = line.IndexOf("</user_query>", start, StringComparison.OrdinalIgnoreCase);
                var raw = end > start ? line[start..end] : line[start..];
                raw = raw.Replace("\\n", " ").Trim();
                if (raw.Length == 0)
                    continue;
                return raw.Length <= 90 ? raw : raw[..90].Trim() + "…";
            }
        }
        catch
        {
            // fall through
        }

        return Path.GetFileName(transcriptDir);
    }

    private static bool LooksLikeComposerId(string id) =>
        Uuid.IsMatch(id) || id.StartsWith("bc-", StringComparison.OrdinalIgnoreCase);

    private static AgentRecord ToRecord(AgentBuilder builder)
    {
        var files = 0;
        long bytes = 0;
        Measure(builder.TranscriptDir, ref files, ref bytes);
        Measure(builder.StoreDir, ref files, ref bytes);
        foreach (var extra in builder.SubagentStoreDirs)
            Measure(extra, ref files, ref bytes);
        foreach (var waypoint in builder.WaypointDirs)
            Measure(waypoint, ref files, ref bytes);

        return new AgentRecord
        {
            ComposerId = builder.ComposerId,
            Title = string.IsNullOrWhiteSpace(builder.Title) ? builder.ComposerId : builder.Title,
            WorkspaceId = builder.WorkspaceId,
            WorkspacePath = builder.WorkspacePath,
            WorkspaceLabel = builder.WorkspaceLabel ?? builder.ProjectSlug ?? "Unknown workspace",
            ProjectSlug = builder.ProjectSlug,
            TranscriptDir = builder.TranscriptDir,
            StoreDir = builder.StoreDir,
            SubagentStoreDirs = builder.SubagentStoreDirs,
            WaypointDirs = builder.WaypointDirs,
            LastWriteUtc = builder.LastWriteUtc == default ? DateTime.UtcNow : builder.LastWriteUtc,
            Bytes = bytes,
            Files = files,
            HasStore = !string.IsNullOrWhiteSpace(builder.StoreDir),
            HasWaypoints = builder.WaypointDirs.Count > 0,
            HeaderJson = builder.HeaderJson
        };
    }

    private static void Measure(string? path, ref int files, ref long bytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                files++;
                bytes += new FileInfo(file).Length;
            }
            catch
            {
                // skip
            }
        }
    }

    private sealed class AgentBuilder
    {
        public required string ComposerId { get; init; }
        public string? Title { get; set; }
        public string? WorkspaceId { get; set; }
        public string? WorkspacePath { get; set; }
        public string? WorkspaceLabel { get; set; }
        public string? ProjectSlug { get; set; }
        public string? TranscriptDir { get; set; }
        public string? StoreDir { get; set; }
        public List<string> SubagentStoreDirs { get; set; } = [];
        public List<string> WaypointDirs { get; set; } = [];
        public DateTime LastWriteUtc { get; set; }
        public string? HeaderJson { get; set; }
    }
}
