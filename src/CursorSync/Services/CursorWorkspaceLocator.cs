using System.Text;
using System.Text.Json;
using CursorSync.Models;

namespace CursorSync.Services;

public static class CursorWorkspaceLocator
{
    public static IReadOnlyList<CursorWorkspaceInfo> List(CursorPaths paths)
    {
        var list = new List<CursorWorkspaceInfo>();
        if (!Directory.Exists(paths.WorkspaceStorage))
            return list;

        foreach (var dir in Directory.EnumerateDirectories(paths.WorkspaceStorage))
        {
            var file = Path.Combine(dir, "workspace.json");
            if (!File.Exists(file))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var uri = root.TryGetProperty("folder", out var folder)
                    ? folder.GetString()
                    : root.TryGetProperty("workspace", out var workspace) ? workspace.GetString() : null;
                if (string.IsNullOrWhiteSpace(uri))
                    continue;

                var folderPath = TryFolderPath(uri);
                if (string.IsNullOrWhiteSpace(folderPath))
                    continue;

                var id = Path.GetFileName(dir);
                list.Add(new CursorWorkspaceInfo
                {
                    Id = id,
                    FolderPath = folderPath,
                    FolderUri = uri,
                    Slug = ToProjectSlug(folderPath),
                    Label = Path.GetFileName(folderPath.TrimEnd('\\', '/')),
                    StorageDir = dir
                });
            }
            catch
            {
                // skip unreadable workspace folders
            }
        }

        return list
            .OrderBy(w => w.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static CursorWorkspaceInfo? FindByFolder(IEnumerable<CursorWorkspaceInfo> workspaces, string folder)
    {
        var full = NormalizeFolder(folder);
        return workspaces.FirstOrDefault(w =>
            string.Equals(NormalizeFolder(w.FolderPath), full, StringComparison.OrdinalIgnoreCase));
    }

    public static string ToProjectSlug(string folder)
    {
        var full = NormalizeFolder(folder);
        var builder = new StringBuilder(full.Length);
        foreach (var c in full)
        {
            if (c is ':' )
                continue;
            if (c is '\\' or '/')
            {
                builder.Append('-');
                continue;
            }

            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
                builder.Append(c);
        }

        return builder.ToString();
    }

    public static string NormalizeFolder(string folder) =>
        Path.GetFullPath(folder).TrimEnd('\\', '/');

    public static string? TryFolderPath(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var parsed = new Uri(uri);
                if (parsed.IsFile)
                    return NormalizeFolder(parsed.LocalPath);
            }
            catch
            {
                return null;
            }
        }

        if (Path.IsPathRooted(uri))
            return NormalizeFolder(uri);

        return null;
    }
}
