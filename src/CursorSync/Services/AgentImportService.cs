using System.IO.Compression;
using System.Text.Json;
using CursorSync.Models;

namespace CursorSync.Services;

public static class AgentImportService
{
    public static AgentImportInspection Inspect(string path)
    {
        string? extracted = null;
        try
        {
            var root = OpenRoot(path, out extracted, out var openError);
            if (root is null)
                return Fail(path, openError ?? "That backup could not be opened.");

            return InspectRoot(path, root);
        }
        finally
        {
            BackupArchive.TryDeleteDirectory(extracted);
        }
    }

    public static AgentTransferResult Import(
        string path,
        IReadOnlyList<CursorWorkspaceInfo> localWorkspaces,
        CursorWorkspaceInfo? fallbackTarget,
        CursorPaths paths,
        bool includeTranscript,
        bool includeStore,
        bool includeWaypoints,
        bool forceTarget,
        IProgress<SyncProgress> progress,
        CancellationToken cancellationToken)
    {
        string? extracted = null;
        string? staged = null;
        try
        {
            var root = OpenRoot(path, out extracted, out var openError);
            if (root is null)
                return new AgentTransferResult { Success = false, Error = openError ?? "That backup could not be opened." };

            var inspection = InspectRoot(path, root);
            if (!inspection.CanImport)
            {
                var first = inspection.Report.Issues.FirstOrDefault(i => i.Severity == IntegritySeverity.Error);
                return new AgentTransferResult
                {
                    Success = false,
                    Error = first?.Message ?? "This backup did not pass the integrity check.",
                    Warnings = inspection.Report.Issues.Select(i => i.Message).ToList()
                };
            }

            progress.Report(new SyncProgress { Message = "Preparing agents for import…" });
            staged = Path.Combine(Path.GetTempPath(), "CursorSync", "import-" + Guid.NewGuid().ToString("n"));
            Materialize(inspection.Agents, staged, cancellationToken);

            var transfer = new AgentTransferService();
            return transfer.RestorePack(
                staged,
                localWorkspaces,
                fallbackTarget,
                paths,
                includeTranscript,
                includeStore,
                includeWaypoints,
                forceTarget,
                progress,
                cancellationToken);
        }
        finally
        {
            BackupArchive.TryDeleteDirectory(extracted);
            BackupArchive.TryDeleteDirectory(staged);
        }
    }

    private static string? OpenRoot(string path, out string? extracted, out string? error)
    {
        extracted = null;
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Choose a backup zip or folder first.";
            return null;
        }

        if (BackupArchive.IsZip(path))
        {
            if (!File.Exists(path))
            {
                error = "That zip file was not found.";
                return null;
            }

            try
            {
                using var zip = ZipFile.OpenRead(path);
                if (zip.Entries.Count == 0)
                {
                    error = "That zip file is empty.";
                    return null;
                }
            }
            catch (Exception ex)
            {
                error = "That zip file is not readable: " + UserFacingError.From(ex);
                return null;
            }

            extracted = BackupArchive.ExtractToTemp(path);
            return extracted;
        }

        if (!Directory.Exists(path))
        {
            error = "That folder was not found.";
            return null;
        }

        return path;
    }

    private static AgentImportInspection Fail(string path, string message) =>
        new()
        {
            SourcePath = path,
            KindLabel = "Unreadable",
            Summary = message,
            Report = new CursorIntegrityReport
            {
                Subject = "This backup",
                Issues = [Error("import.open", message)]
            }
        };

    private static AgentImportInspection InspectRoot(string originalPath, string root)
    {
        var issues = new List<IntegrityIssue>();
        var searchRoot = Unwrap(root);
        var kind = DetectKind(searchRoot);
        var agents = Discover(searchRoot, kind);

        if (agents.Count == 0)
        {
            issues.Add(Error(
                "import.empty",
                "No agent transcripts or memory stores were found. Choose a CursorSync backup, a copied ~/.cursor/projects folder, or a sync payload."));
        }

        var files = 0;
        long bytes = 0;
        foreach (var agent in agents)
        {
            if (issues.Count >= 40)
            {
                issues.Add(Warning("import.truncated", "Stopped after 40 findings. Fix these, then try again."));
                break;
            }

            InspectAgent(agent, issues, ref files, ref bytes);
        }

        if (agents.Count > 0 && issues.All(i => i.Severity != IntegritySeverity.Error))
            issues.Add(Info("import.ok", $"Found {agents.Count} agent{(agents.Count == 1 ? "" : "s")} that can be imported."));

        var kindLabel = KindLabel(kind);
        var report = new CursorIntegrityReport
        {
            Subject = "This backup",
            Issues = issues
        };
        return new AgentImportInspection
        {
            SourcePath = originalPath,
            Kind = kind,
            KindLabel = kindLabel,
            Summary = $"{agents.Count} agent{(agents.Count == 1 ? "" : "s")} · {kindLabel} · {FileSizeFormatter.FromBytes(bytes)}",
            Agents = agents,
            Report = report,
            Files = files,
            Bytes = bytes
        };
    }

    private static string Unwrap(string root)
    {
        if (Directory.Exists(Path.Combine(root, "payload"))
            && (Directory.Exists(Path.Combine(root, "payload", "transcripts"))
                || Directory.Exists(Path.Combine(root, "payload", "agentStores"))))
            return Path.Combine(root, "payload");

        try
        {
            var children = Directory.GetDirectories(root);
            if (children.Length == 1
                && !LooksLikeKnownRoot(root)
                && LooksLikeKnownRoot(children[0]))
                return children[0];
        }
        catch
        {
            // keep the original root
        }

        return root;
    }

    private static bool LooksLikeKnownRoot(string root) =>
        Directory.Exists(Path.Combine(root, "agents"))
        || File.Exists(Path.Combine(root, "manifest.json"))
        || Directory.Exists(Path.Combine(root, "projects"))
        || Directory.Exists(Path.Combine(root, "agent-transcripts"))
        || Directory.Exists(Path.Combine(root, "transcripts"))
        || Directory.Exists(Path.Combine(root, "payload"))
        || Directory.Exists(Path.Combine(root, "cursor_agent_stores"))
        || Directory.Exists(Path.Combine(root, "agentStores"));

    private static AgentImportKind DetectKind(string root)
    {
        if (Directory.Exists(Path.Combine(root, "agents")))
            return AgentImportKind.CursorSyncPack;
        if (File.Exists(Path.Combine(root, "manifest.json"))
            && (Directory.Exists(Path.Combine(root, "transcript")) || Directory.Exists(Path.Combine(root, "store"))))
            return AgentImportKind.CursorSyncAgent;
        if (Directory.Exists(Path.Combine(root, "transcripts", "projects"))
            || Directory.Exists(Path.Combine(root, "transcripts")))
            return AgentImportKind.HubPayload;
        if (Directory.Exists(Path.Combine(root, "projects")))
            return AgentImportKind.CursorHome;
        if (Directory.Exists(Path.Combine(root, "agent-transcripts"))
            || HasProjectChildren(root))
            return AgentImportKind.ProjectCache;
        if (Directory.Exists(Path.Combine(root, "cursor_agent_stores"))
            || Directory.Exists(Path.Combine(root, "agentStores")))
            return AgentImportKind.AgentStores;
        return AgentImportKind.Unknown;
    }

    private static bool HasProjectChildren(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Any(dir => Directory.Exists(Path.Combine(dir, "agent-transcripts")));
        }
        catch
        {
            return false;
        }
    }

    private static string KindLabel(AgentImportKind kind) => kind switch
    {
        AgentImportKind.CursorSyncPack => "CursorSync backup",
        AgentImportKind.CursorSyncAgent => "CursorSync agent backup",
        AgentImportKind.ProjectCache => "manual project cache",
        AgentImportKind.HubPayload => "sync payload",
        AgentImportKind.CursorHome => "Cursor home / projects copy",
        AgentImportKind.AgentStores => "agent memory stores",
        _ => "unrecognized folder"
    };

    private static List<AgentImportItem> Discover(string root, AgentImportKind kind)
    {
        var map = new Dictionary<string, ImportBuilder>(StringComparer.OrdinalIgnoreCase);
        switch (kind)
        {
            case AgentImportKind.CursorSyncPack:
                ScanPack(root, map);
                break;
            case AgentImportKind.CursorSyncAgent:
                ScanAgentFolder(root, map);
                break;
            case AgentImportKind.HubPayload:
                ScanProjects(Path.Combine(root, "transcripts", "projects"), map);
                ScanStores(root, map);
                break;
            case AgentImportKind.CursorHome:
                ScanProjects(Path.Combine(root, "projects"), map);
                ScanStores(root, map);
                break;
            case AgentImportKind.ProjectCache:
                if (Directory.Exists(Path.Combine(root, "agent-transcripts")))
                    ScanTranscripts(root, Path.GetFileName(root), map);
                else
                    ScanProjects(root, map);
                ScanStores(root, map);
                break;
            default:
                ScanPack(root, map);
                ScanAgentFolder(root, map);
                ScanProjects(Path.Combine(root, "projects"), map);
                ScanProjects(Path.Combine(root, "transcripts", "projects"), map);
                if (Directory.Exists(Path.Combine(root, "agent-transcripts")))
                    ScanTranscripts(root, Path.GetFileName(root), map);
                else
                    ScanProjects(root, map);
                ScanStores(root, map);
                ScanLooseJsonl(root, map);
                break;
        }

        return map.Values
            .Select(ToItem)
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanPack(string root, Dictionary<string, ImportBuilder> map)
    {
        var agentsRoot = Path.Combine(root, "agents");
        if (!Directory.Exists(agentsRoot))
            return;

        foreach (var dir in SafeDirs(agentsRoot))
            ScanAgentFolder(dir, map, Path.GetFileName(dir));
    }

    private static void ScanAgentFolder(string dir, Dictionary<string, ImportBuilder> map, string? fallbackId = null)
    {
        if (!Directory.Exists(dir))
            return;

        var manifest = TryReadManifest(Path.Combine(dir, "manifest.json"));
        var id = FirstNonEmpty(manifest?.ComposerId, fallbackId, Path.GetFileName(dir));
        if (!AgentCatalog.IsComposerId(id))
            return;

        var builder = Get(map, id);
        builder.Title ??= FirstNonEmpty(manifest?.Title);
        builder.HeaderJson ??= manifest?.HeaderJson;
        builder.SourceWorkspacePath ??= manifest?.SourceWorkspacePath;
        builder.SourceWorkspaceId ??= manifest?.SourceWorkspaceId;
        builder.SourceProjectSlug ??= manifest?.SourceProjectSlug;
        TakeDir(builder, Path.Combine(dir, "transcript"), isTranscript: true);
        TakeDir(builder, Path.Combine(dir, "store"), isTranscript: false);
        var sqlite = Path.Combine(dir, "sqlite");
        if (Directory.Exists(sqlite))
            builder.SqliteDir ??= sqlite;
        var waypoints = Path.Combine(dir, "waypoints");
        if (Directory.Exists(waypoints))
            builder.WaypointsDir ??= waypoints;
    }

    private static void ScanProjects(string projectsRoot, Dictionary<string, ImportBuilder> map)
    {
        if (!Directory.Exists(projectsRoot))
            return;

        foreach (var project in SafeDirs(projectsRoot))
            ScanTranscripts(project, Path.GetFileName(project), map);
    }

    private static void ScanTranscripts(string projectDir, string slug, Dictionary<string, ImportBuilder> map)
    {
        var transcripts = Path.Combine(projectDir, "agent-transcripts");
        if (!Directory.Exists(transcripts))
            return;

        foreach (var dir in SafeDirs(transcripts))
        {
            var id = Path.GetFileName(dir);
            if (!AgentCatalog.IsComposerId(id))
                continue;

            var builder = Get(map, id);
            builder.TranscriptDir ??= dir;
            builder.SourceProjectSlug ??= slug;
            builder.Title ??= FirstNonEmpty(AgentCatalog.GuessTitle(dir), id);
        }
    }

    private static void ScanStores(string root, Dictionary<string, ImportBuilder> map)
    {
        foreach (var stores in StoreRoots(root))
        {
            if (!Directory.Exists(stores))
                continue;

            foreach (var dir in SafeDirs(stores))
            {
                var id = Path.GetFileName(dir);
                if (!AgentCatalog.IsComposerId(id))
                    continue;

                var builder = Get(map, id);
                builder.StoreDir ??= dir;
                builder.Title ??= id;
            }
        }
    }

    private static void ScanLooseJsonl(string root, Dictionary<string, ImportBuilder> map)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!AgentCatalog.IsComposerId(id))
                continue;

            var builder = Get(map, id);
            builder.TranscriptFile ??= file;
            builder.Title ??= FirstNonEmpty(AgentCatalog.GuessTitle(root), id);
        }
    }

    private static IEnumerable<string> StoreRoots(string root)
    {
        yield return Path.Combine(root, "agentStores", "cursor_agent_stores");
        yield return Path.Combine(root, "agentStores");
        yield return Path.Combine(root, "cursor_agent_stores");
        yield return Path.Combine(root, "AgentStores", "cursor_agent_stores");
        yield return Path.Combine(root, "payload", "agentStores", "cursor_agent_stores");
        yield return Path.Combine(root, "payload", "agentStores");
    }

    private static void TakeDir(ImportBuilder builder, string dir, bool isTranscript)
    {
        if (!Directory.Exists(dir))
            return;
        if (isTranscript)
            builder.TranscriptDir ??= dir;
        else
            builder.StoreDir ??= dir;
    }

    private static void InspectAgent(AgentImportItem agent, List<IntegrityIssue> issues, ref int files, ref long bytes)
    {
        var hasData = false;
        if (!string.IsNullOrWhiteSpace(agent.TranscriptDir) && Directory.Exists(agent.TranscriptDir))
        {
            hasData = true;
            InspectTree(agent.TranscriptDir, agent.Title, issues, ref files, ref bytes, jsonl: true);
        }
        else if (!string.IsNullOrWhiteSpace(agent.TranscriptFile) && File.Exists(agent.TranscriptFile))
        {
            hasData = true;
            InspectFile(agent.TranscriptFile, agent.Title, issues, ref files, ref bytes, jsonl: true);
        }

        if (!string.IsNullOrWhiteSpace(agent.StoreDir) && Directory.Exists(agent.StoreDir))
        {
            hasData = true;
            InspectTree(agent.StoreDir, agent.Title + " store", issues, ref files, ref bytes, jsonl: false);
        }

        if (!string.IsNullOrWhiteSpace(agent.SqliteDir) && Directory.Exists(agent.SqliteDir))
            InspectTree(agent.SqliteDir, agent.Title + " sqlite", issues, ref files, ref bytes, jsonl: false);

        if (!hasData)
            issues.Add(Warning("import.emptyAgent:" + agent.ComposerId, $"Agent “{agent.Title}” has no transcript or memory files."));
    }

    private static void InspectTree(string dir, string label, List<IntegrityIssue> issues, ref int files, ref long bytes, bool jsonl)
    {
        IEnumerable<string> found;
        try
        {
            found = Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            }).Take(80);
        }
        catch (Exception ex)
        {
            issues.Add(Error("import.read:" + label, $"Could not read “{label}”: {UserFacingError.From(ex)}"));
            return;
        }

        var jsonChecked = 0;
        foreach (var file in found)
        {
            files++;
            try { bytes += new FileInfo(file).Length; }
            catch { }

            var ext = Path.GetExtension(file);
            if (jsonl && ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                InspectJsonl(file, label, issues);
                continue;
            }

            if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) && jsonChecked < 12)
            {
                jsonChecked++;
                InspectJson(file, label, issues);
            }
        }
    }

    private static void InspectFile(string file, string label, List<IntegrityIssue> issues, ref int files, ref long bytes, bool jsonl)
    {
        files++;
        try { bytes += new FileInfo(file).Length; }
        catch { }

        if (jsonl)
            InspectJsonl(file, label, issues);
        else
            InspectJson(file, label, issues);
    }

    private static void InspectJsonl(string file, string label, List<IntegrityIssue> issues)
    {
        try
        {
            var lines = 0;
            var ok = 0;
            foreach (var line in File.ReadLines(file).Take(80))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                lines++;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    _ = doc.RootElement.ValueKind;
                    ok++;
                }
                catch
                {
                    issues.Add(Error("import.jsonl:" + Path.GetFileName(file), $"Transcript “{label}” has invalid JSONL in {Path.GetFileName(file)}."));
                    return;
                }
            }

            if (lines == 0)
                issues.Add(Warning("import.jsonlEmpty:" + Path.GetFileName(file), $"Transcript “{label}” is empty ({Path.GetFileName(file)})."));
            else if (ok == 0)
                issues.Add(Error("import.jsonlNone:" + Path.GetFileName(file), $"Transcript “{label}” has no valid JSONL lines."));
        }
        catch (Exception ex)
        {
            issues.Add(Error("import.jsonlRead:" + Path.GetFileName(file), $"Transcript “{label}” could not be read: {UserFacingError.From(ex)}"));
        }
    }

    private static void InspectJson(string file, string label, List<IntegrityIssue> issues)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            _ = doc.RootElement.ValueKind;
        }
        catch (Exception ex)
        {
            issues.Add(Error("import.json:" + Path.GetFileName(file), $"{label} file {Path.GetFileName(file)} is not valid JSON. {UserFacingError.From(ex)}"));
        }
    }

    private static void Materialize(IReadOnlyList<AgentImportItem> agents, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(destination, "agents"));
        var files = 0;
        long bytes = 0;
        foreach (var agent in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = Path.Combine(destination, "agents", agent.ComposerId);
            Directory.CreateDirectory(dir);

            if (!string.IsNullOrWhiteSpace(agent.TranscriptDir) && Directory.Exists(agent.TranscriptDir))
                CopyTree(agent.TranscriptDir, Path.Combine(dir, "transcript"), ref files, ref bytes, cancellationToken);
            else if (!string.IsNullOrWhiteSpace(agent.TranscriptFile) && File.Exists(agent.TranscriptFile))
            {
                var transcriptDir = Path.Combine(dir, "transcript");
                Directory.CreateDirectory(transcriptDir);
                var dest = Path.Combine(transcriptDir, agent.ComposerId + ".jsonl");
                File.Copy(agent.TranscriptFile, dest, overwrite: true);
                files++;
            }

            if (!string.IsNullOrWhiteSpace(agent.StoreDir) && Directory.Exists(agent.StoreDir))
                CopyTree(agent.StoreDir, Path.Combine(dir, "store"), ref files, ref bytes, cancellationToken);
            if (!string.IsNullOrWhiteSpace(agent.SqliteDir) && Directory.Exists(agent.SqliteDir))
                CopyTree(agent.SqliteDir, Path.Combine(dir, "sqlite"), ref files, ref bytes, cancellationToken);
            if (!string.IsNullOrWhiteSpace(agent.WaypointsDir) && Directory.Exists(agent.WaypointsDir))
                CopyTree(agent.WaypointsDir, Path.Combine(dir, "waypoints"), ref files, ref bytes, cancellationToken);

            var manifest = new AgentBackupManifest
            {
                SchemaVersion = 1,
                Kind = "agent",
                ComposerId = agent.ComposerId,
                Title = agent.Title,
                SourceWorkspaceId = agent.SourceWorkspaceId,
                SourceWorkspacePath = agent.SourceWorkspacePath,
                SourceProjectSlug = agent.SourceProjectSlug,
                CreatedUtc = DateTime.UtcNow,
                IncludeTranscript = true,
                IncludeStore = true,
                IncludeWaypoints = !string.IsNullOrWhiteSpace(agent.WaypointsDir),
                HeaderJson = agent.HeaderJson
            };
            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, JsonUtil.Options));
        }

        var pack = new AgentBackupManifest
        {
            SchemaVersion = 2,
            Kind = "workspacePack",
            Title = $"{agents.Count} imported agent{(agents.Count == 1 ? "" : "s")}",
            CreatedUtc = DateTime.UtcNow,
            IncludeTranscript = true,
            IncludeStore = true,
            IncludeWaypoints = agents.Any(a => !string.IsNullOrWhiteSpace(a.WaypointsDir)),
            Files = files,
            Bytes = bytes,
            AgentCount = agents.Count
        };
        File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(pack, JsonUtil.Options));
    }

    private static void CopyTree(string source, string destination, ref int files, ref long bytes, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dest = Path.Combine(destination, Path.GetRelativePath(source, file));
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            File.Copy(file, dest, overwrite: true);
            files++;
            try { bytes += new FileInfo(dest).Length; }
            catch { }
        }
    }

    private static AgentBackupManifest? TryReadManifest(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AgentBackupManifest>(File.ReadAllText(path), JsonUtil.Options);
        }
        catch
        {
            return null;
        }
    }

    private static ImportBuilder Get(Dictionary<string, ImportBuilder> map, string id)
    {
        if (!map.TryGetValue(id, out var builder))
        {
            builder = new ImportBuilder { ComposerId = id };
            map[id] = builder;
        }

        return builder;
    }

    private static AgentImportItem ToItem(ImportBuilder builder) =>
        new()
        {
            ComposerId = builder.ComposerId,
            Title = FirstNonEmpty(builder.Title, builder.ComposerId) ?? builder.ComposerId,
            TranscriptDir = builder.TranscriptDir,
            TranscriptFile = builder.TranscriptFile,
            StoreDir = builder.StoreDir,
            SqliteDir = builder.SqliteDir,
            WaypointsDir = builder.WaypointsDir,
            SourceWorkspacePath = builder.SourceWorkspacePath,
            SourceWorkspaceId = builder.SourceWorkspaceId,
            SourceProjectSlug = builder.SourceProjectSlug,
            HeaderJson = builder.HeaderJson
        };

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch { return []; }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static IntegrityIssue Error(string code, string message) =>
        new() { Severity = IntegritySeverity.Error, Code = code, Message = message };

    private static IntegrityIssue Warning(string code, string message) =>
        new() { Severity = IntegritySeverity.Warning, Code = code, Message = message };

    private static IntegrityIssue Info(string code, string message) =>
        new() { Severity = IntegritySeverity.Info, Code = code, Message = message };

    private sealed class ImportBuilder
    {
        public required string ComposerId { get; init; }
        public string? Title { get; set; }
        public string? TranscriptDir { get; set; }
        public string? TranscriptFile { get; set; }
        public string? StoreDir { get; set; }
        public string? SqliteDir { get; set; }
        public string? WaypointsDir { get; set; }
        public string? SourceWorkspacePath { get; set; }
        public string? SourceWorkspaceId { get; set; }
        public string? SourceProjectSlug { get; set; }
        public string? HeaderJson { get; set; }
    }
}
