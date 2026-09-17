using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CursorSync.Models;
using Microsoft.Data.Sqlite;

namespace CursorSync.Services;

public static class SqliteStateStore
{
    private static readonly Regex ContentHash = new(@"composer\.content\.[0-9a-fA-F]{16,}", RegexOptions.Compiled);

    public static ComposerCatalog ReadCatalog(CursorPaths paths)
    {
        var catalog = new ComposerCatalog();
        using var global = OpenSnapshot(paths.StateDb);
        if (global is null)
            return catalog;

        catalog.Headers = ReadHeaders(global);
        using var cmd = global.CreateCommand();
        cmd.CommandText = "SELECT key FROM cursorDiskKV WHERE key LIKE 'checkpointId:%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var parts = key.Split(':', 3);
            if (parts.Length != 3)
                continue;
            if (!catalog.CheckpointIds.TryGetValue(parts[1], out var list))
            {
                list = [];
                catalog.CheckpointIds[parts[1]] = list;
            }
            list.Add(parts[2]);
        }

        return catalog;
    }

    public static AgentSqliteSlice CaptureComposer(CursorPaths paths, string composerId, CancellationToken cancellationToken)
    {
        var slice = new AgentSqliteSlice();
        using var global = OpenSnapshot(paths.StateDb);
        if (global is null)
            return slice;

        cancellationToken.ThrowIfCancellationRequested();
        CaptureKeyedRows(global, "cursorDiskKV", composerId, slice.CursorDiskKv);
        CaptureContentBlobs(global, slice.CursorDiskKv);
        CaptureHeader(global, composerId, slice.ItemTable);
        return slice;
    }

    public static void RegisterComposer(
        CursorPaths paths,
        CursorWorkspaceInfo target,
        string composerId,
        string title,
        string? headerJson,
        AgentSqliteSlice slice,
        CancellationToken cancellationToken)
    {
        using var global = OpenWritable(paths.StateDb)
            ?? throw new InvalidOperationException("Could not open Cursor's global chat database. Close Cursor and try again.");

        cancellationToken.ThrowIfCancellationRequested();
        using var tx = global.BeginTransaction();
        foreach (var (key, value) in slice.CursorDiskKv)
            Upsert(global, tx, "cursorDiskKV", key, value);
        UpsertComposerHeader(global, tx, target, composerId, title, headerJson);
        tx.Commit();

        var workspaceDb = Path.Combine(target.StorageDir, "state.vscdb");
        if (!File.Exists(workspaceDb))
            return;

        using var workspace = OpenWritable(workspaceDb);
        if (workspace is null)
            return;

        using var workspaceTx = workspace.BeginTransaction();
        AddSelectedComposer(workspace, workspaceTx, composerId);
        workspaceTx.Commit();
    }

    private static Dictionary<string, (string Title, string? WorkspaceId, string? WorkspacePath, string HeaderJson)> ReadHeaders(SqliteConnection connection)
    {
        var map = new Dictionary<string, (string Title, string? WorkspaceId, string? WorkspacePath, string HeaderJson)>(StringComparer.OrdinalIgnoreCase);
        var json = ReadItem(connection, "composer.composerHeaders");
        if (string.IsNullOrWhiteSpace(json))
            return map;

        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root?["allComposers"] is not JsonArray composers)
                return map;

            foreach (var node in composers.OfType<JsonObject>())
            {
                var id = node["composerId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var title = node["name"]?.GetValue<string>() ?? "Untitled agent";
                string? workspaceId = null;
                string? workspacePath = null;
                if (node["workspaceIdentifier"] is JsonObject ident)
                {
                    workspaceId = ident["id"]?.GetValue<string>();
                    workspacePath = ident["uri"]?["fsPath"]?.GetValue<string>()
                        ?? CursorWorkspaceLocator.TryFolderPath(ident["uri"]?["external"]?.GetValue<string>() ?? "");
                }

                map[id] = (title, workspaceId, workspacePath, node.ToJsonString());
            }
        }
        catch
        {
            return map;
        }

        return map;
    }

    private static void CaptureKeyedRows(SqliteConnection connection, string table, string composerId, Dictionary<string, string> target)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT key, value FROM {table}
            WHERE key = $exact
               OR key LIKE $prefix1
               OR key LIKE $prefix2
               OR key LIKE $prefix3
            """;
        cmd.Parameters.AddWithValue("$exact", $"composerData:{composerId}");
        cmd.Parameters.AddWithValue("$prefix1", $"bubbleId:{composerId}:%");
        cmd.Parameters.AddWithValue("$prefix2", $"checkpointId:{composerId}:%");
        cmd.Parameters.AddWithValue("$prefix3", $"messageRequestContext:{composerId}:%");

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            target[key] = ReadText(reader, 1);
        }
    }

    private static void CaptureContentBlobs(SqliteConnection connection, Dictionary<string, string> kv)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in kv.Values)
        {
            foreach (Match match in ContentHash.Matches(value))
                hashes.Add(match.Value);
        }

        if (hashes.Count == 0)
            return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM cursorDiskKV WHERE key = $key";
        var param = cmd.Parameters.Add("$key", SqliteType.Text);
        foreach (var hash in hashes)
        {
            param.Value = hash;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                kv[hash] = ReadText(reader, 1);
        }
    }

    private static void CaptureHeader(SqliteConnection connection, string composerId, Dictionary<string, string> itemTable)
    {
        var json = ReadItem(connection, "composer.composerHeaders");
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root?["allComposers"] is not JsonArray composers)
                return;

            var match = composers.OfType<JsonObject>()
                .FirstOrDefault(n => string.Equals(n["composerId"]?.GetValue<string>(), composerId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                itemTable["composerHeader"] = match.ToJsonString();
        }
        catch
        {
            // header is optional
        }
    }

    private static void UpsertComposerHeader(
        SqliteConnection connection,
        SqliteTransaction tx,
        CursorWorkspaceInfo target,
        string composerId,
        string title,
        string? headerJson)
    {
        var json = ReadItem(connection, "composer.composerHeaders", tx) ?? """{"allComposers":[]}""";
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        if (root["allComposers"] is not JsonArray composers)
        {
            composers = [];
            root["allComposers"] = composers;
        }

        for (var i = composers.Count - 1; i >= 0; i--)
        {
            if (string.Equals(composers[i]?["composerId"]?.GetValue<string>(), composerId, StringComparison.OrdinalIgnoreCase))
                composers.RemoveAt(i);
        }

        var header = TryParseObject(headerJson) ?? new JsonObject();
        header["composerId"] = composerId;
        if (string.IsNullOrWhiteSpace(header["name"]?.GetValue<string>()))
            header["name"] = title;
        header["lastUpdatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        header["workspaceIdentifier"] = new JsonObject
        {
            ["id"] = target.Id,
            ["uri"] = new JsonObject
            {
                ["fsPath"] = target.FolderPath,
                ["scheme"] = "file",
                ["external"] = target.FolderUri
            }
        };
        composers.Add(header);
        Upsert(connection, tx, "ItemTable", "composer.composerHeaders", root.ToJsonString());
    }

    private static void AddSelectedComposer(SqliteConnection connection, SqliteTransaction tx, string composerId)
    {
        var json = ReadItem(connection, "composer.composerData", tx) ?? """{"selectedComposerIds":[]}""";
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        if (root["selectedComposerIds"] is not JsonArray selected)
        {
            selected = [];
            root["selectedComposerIds"] = selected;
        }

        var exists = selected.Any(n => string.Equals(n?.GetValue<string>(), composerId, StringComparison.OrdinalIgnoreCase));
        if (!exists)
            selected.Add(composerId);

        root["hasMigratedComposerData"] = true;
        Upsert(connection, tx, "ItemTable", "composer.composerData", root.ToJsonString());
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction tx, string table, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table} WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = $"INSERT INTO {table}(key, value) VALUES($key, $value)";
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.Add("$value", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(value);
        insert.ExecuteNonQuery();
    }

    private static string? ReadItem(SqliteConnection connection, string key, SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1";
        cmd.Parameters.AddWithValue("$key", key);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadText(reader, 0) : null;
    }

    private static string ReadText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return "";

        if (reader.GetFieldType(ordinal) == typeof(string))
            return reader.GetString(ordinal);

        var bytes = reader.GetFieldValue<byte[]>(ordinal);
        return Encoding.UTF8.GetString(bytes);
    }

    private static JsonObject? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static SqliteConnection? OpenSnapshot(string dbPath)
    {
        if (!File.Exists(dbPath))
            return null;

        try
        {
            return Open($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
        }
        catch
        {
            return null;
        }
    }

    private static SqliteConnection? OpenWritable(string dbPath)
    {
        if (!File.Exists(dbPath))
            return null;
        return Open($"Data Source={dbPath};Cache=Shared");
    }

    private static SqliteConnection? Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=8000;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}
