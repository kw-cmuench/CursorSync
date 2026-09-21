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
                    StorageDir = dir,
                    FolderExists = Directory.Exists(folderPath),
                    IsReady = File.Exists(Path.Combine(dir, "state.vscdb"))
                });
            }
            catch
            {
                // skip unreadable workspace folders
            }
        }

        return list
            .GroupBy(w => NormalizeFolder(w.FolderPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(w => w.IsReady)
                .ThenBy(w => w.Label, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(w => w.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static CursorWorkspaceInfo? FindByFolder(IEnumerable<CursorWorkspaceInfo> workspaces, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        var full = NormalizeFolder(folder);
        if (string.IsNullOrWhiteSpace(full))
            return null;

        return workspaces.FirstOrDefault(w => SameFolder(w.FolderPath, full));
    }

    public static bool SameFolder(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(NormalizeFolder(left), NormalizeFolder(right), StringComparison.OrdinalIgnoreCase);
    }

    public static string ToProjectSlug(string folder)
    {
        var full = NormalizeFolder(folder);
        if (string.IsNullOrWhiteSpace(full))
            return "";
        var builder = new StringBuilder(full.Length);
        foreach (var c in full)
        {
            if (c is ':')
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

    public static string ToFileUri(string? folder)
    {
        var full = NormalizeFolder(folder);
        if (string.IsNullOrWhiteSpace(full))
            return "";

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var unc = full.TrimStart('\\').Replace('\\', '/');
            return "file://" + EncodeUriPath(unc);
        }

        var unix = full.Replace('\\', '/');
        if (unix.Length >= 2 && unix[1] == ':')
        {
            var drive = char.ToLowerInvariant(unix[0]);
            return "file:///" + drive + "%3A" + EncodeUriPath(unix[2..]);
        }

        return "file:///" + EncodeUriPath(unix.TrimStart('/'));
    }

    public static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return "";

        var path = StripFilePrefix(folder.Trim()).Replace('/', '\\');
        path = StripDriveSlashPrefix(path);

        try
        {
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception)
        {
            return path.TrimEnd('\\');
        }
    }

    public static string? TryFolderPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = DecodeFileUri(uri);
            return string.IsNullOrWhiteSpace(decoded) ? null : NormalizeFolder(decoded);
        }

        if (uri.Contains("://", StringComparison.Ordinal))
            return null;

        if (Path.IsPathRooted(uri))
            return NormalizeFolder(uri);

        return null;
    }

    private static string DecodeFileUri(string uri)
    {
        var body = StripFilePrefix(uri);
        try
        {
            body = Uri.UnescapeDataString(body);
        }
        catch
        {
            // keep the original body if it is not a valid escape sequence
        }

        body = body.Replace('/', '\\');
        if (body.StartsWith("localhost\\", StringComparison.OrdinalIgnoreCase))
            body = body["localhost\\".Length..];

        body = StripDriveSlashPrefix(body);
        if (!LooksLikeDrive(body) && !body.StartsWith(@"\\", StringComparison.Ordinal) && body.Contains('\\'))
            body = @"\\" + body.TrimStart('\\');

        return body;
    }

    private static string StripFilePrefix(string value)
    {
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return value[7..];
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return value[5..];
        return value;
    }

    private static string StripDriveSlashPrefix(string path)
    {
        if (path.Length >= 4 && path[0] == '\\' && LooksLikeDrive(path[1..]))
            return path[1..];
        return path;
    }

    private static bool LooksLikeDrive(string path) =>
        path.Length >= 2
        && char.IsLetter(path[0])
        && path[1] == ':'
        && (path.Length == 2 || path[2] is '\\' or '/');

    public static string EncodeUriPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.None);
        for (var i = 0; i < parts.Length; i++)
            parts[i] = Uri.EscapeDataString(parts[i]);
        return string.Join("/", parts);
    }

    public static CursorWorkspaceInfo GetExisting(CursorPaths paths, string folder)
    {
        var full = NormalizeFolder(folder);
        if (string.IsNullOrWhiteSpace(full) || !Directory.Exists(full))
            throw new DirectoryNotFoundException("That folder does not exist.");

        var existing = FindByFolder(List(paths), full);
        if (existing is not null)
            return existing;

        throw new InvalidOperationException(
            "Cursor does not know that folder yet. Open it in Cursor with File → Open Folder, wait until the window loads, then refresh. CursorSync will not invent a workspace ID.");
    }

    public static void EnsureProjectTranscripts(CursorPaths paths, string folder)
    {
        var slug = ToProjectSlug(folder);
        if (string.IsNullOrWhiteSpace(slug))
            return;
        Directory.CreateDirectory(Path.Combine(paths.Projects, slug, "agent-transcripts"));
    }

    public static CursorWorkspaceInfo Retarget(CursorPaths paths, CursorWorkspaceInfo workspace, string newFolder)
    {
        var full = NormalizeFolder(newFolder);
        if (string.IsNullOrWhiteSpace(full) || !Directory.Exists(full))
            throw new DirectoryNotFoundException("That folder does not exist.");

        var clash = FindByFolder(List(paths), full);
        if (clash is not null && !string.Equals(clash.Id, workspace.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"“{clash.Label}” already points at that folder.");

        var uri = ToFileUri(full);
        File.WriteAllText(
            Path.Combine(workspace.StorageDir, "workspace.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["folder"] = uri }, JsonUtil.Options));

        var oldSlug = workspace.Slug;
        var newSlug = ToProjectSlug(full);
        if (!string.Equals(oldSlug, newSlug, StringComparison.OrdinalIgnoreCase))
        {
            var oldProjects = Path.Combine(paths.Projects, oldSlug);
            var newProjects = Path.Combine(paths.Projects, newSlug);
            if (Directory.Exists(oldProjects) && !Directory.Exists(newProjects))
                Directory.Move(oldProjects, newProjects);
            else
                Directory.CreateDirectory(Path.Combine(paths.Projects, newSlug, "agent-transcripts"));
        }

        return new CursorWorkspaceInfo
        {
            Id = workspace.Id,
            FolderPath = full,
            FolderUri = uri,
            Slug = newSlug,
            Label = Path.GetFileName(full.TrimEnd('\\', '/')),
            StorageDir = workspace.StorageDir,
            FolderExists = true,
            IsReady = File.Exists(Path.Combine(workspace.StorageDir, "state.vscdb"))
        };
    }

    public static bool TryBuildRenamePath(string folder, string newName, out string destination, out string error)
    {
        destination = "";
        error = "";
        var full = NormalizeFolder(folder);
        if (string.IsNullOrWhiteSpace(full) || !Directory.Exists(full))
        {
            error = "That workspace folder is missing on disk.";
            return false;
        }

        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(parent))
        {
            error = "This folder cannot be renamed.";
            return false;
        }

        var name = SanitizeFileName(newName);
        if (name is null)
        {
            error = "Enter a valid folder name. It cannot contain \\ / : * ? \" < > |";
            return false;
        }

        destination = NormalizeFolder(Path.Combine(parent, name));
        if (string.Equals(destination, full, StringComparison.OrdinalIgnoreCase))
        {
            error = "That is already this workspace’s name.";
            return false;
        }

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            error = "A folder with that name already exists.";
            return false;
        }

        return true;
    }

    public static CursorWorkspaceInfo RenameFolder(CursorPaths paths, CursorWorkspaceInfo workspace, string destination)
    {
        var source = NormalizeFolder(workspace.FolderPath);
        var dest = NormalizeFolder(destination);
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            throw new DirectoryNotFoundException("That workspace folder is missing on disk.");
        if (string.IsNullOrWhiteSpace(dest))
            throw new InvalidOperationException("The new folder name is blank.");
        if (Directory.Exists(dest) || File.Exists(dest))
            throw new IOException("A folder with that name already exists.");

        Directory.Move(source, dest);
        try
        {
            return Retarget(paths, workspace, dest);
        }
        catch
        {
            TryMoveFolder(dest, source);
            throw;
        }
    }

    public static void TryMoveFolder(string from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return;
        if (!Directory.Exists(from) || Directory.Exists(to))
            return;
        try { Directory.Move(from, to); }
        catch { }
    }

    public static string? SanitizeFileName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        if (name.EndsWith(' ') || name.EndsWith('.'))
            return null;

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        return reserved.Contains(name) ? null : name;
    }

    public static void DeleteStorage(CursorPaths paths, CursorWorkspaceInfo workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.Id)
            || workspace.Id is "." or ".."
            || workspace.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("That workspace ID is not safe to delete.");

        if (string.IsNullOrWhiteSpace(workspace.StorageDir) || !Directory.Exists(workspace.StorageDir))
            return;

        var root = Path.GetFullPath(paths.WorkspaceStorage);
        var dir = Path.GetFullPath(workspace.StorageDir);
        if (!IsStrictSubfolder(root, dir))
            throw new InvalidOperationException("Refusing to delete a folder outside Cursor's workspaceStorage.");
        if (!string.Equals(Path.GetFileName(dir), workspace.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Workspace folder name does not match its ID.");

        Directory.Delete(dir, recursive: true);
    }

    public static bool TryBuildProjectCacheName(string value, out string slug, out string error)
    {
        slug = "";
        error = "";
        var name = SanitizeFileName(value);
        if (name is null)
        {
            error = "Enter a valid project cache name. It cannot contain \\ / : * ? \" < > |";
            return false;
        }

        slug = name;
        return true;
    }

    public static void RenameProjectCache(CursorPaths paths, string oldSlug, string newSlug)
    {
        var fromName = RequireCacheSlug(oldSlug, "That project cache name is not safe to rename.");
        var toName = SanitizeFileName(newSlug) ?? throw new InvalidOperationException("Enter a valid project cache name.");
        var root = Path.GetFullPath(paths.Projects);
        var from = Path.GetFullPath(Path.Combine(root, fromName));
        var to = Path.GetFullPath(Path.Combine(root, toName));
        if (!IsStrictSubfolder(root, from) || !IsStrictSubfolder(root, to))
            throw new InvalidOperationException("Refusing to rename a folder outside Cursor’s projects cache.");
        if (!Directory.Exists(from))
            throw new DirectoryNotFoundException("That project cache folder is missing.");
        if (Directory.Exists(to) || File.Exists(to))
            throw new IOException("A project cache with that name already exists.");

        Directory.Move(from, to);
    }

    public static void DeleteProjectCache(CursorPaths paths, string slug)
    {
        var name = RequireCacheSlug(slug, "That project cache name is not safe to delete.");
        if (string.IsNullOrWhiteSpace(paths.Projects) || !Directory.Exists(paths.Projects))
            return;

        var root = Path.GetFullPath(paths.Projects);
        var dir = Path.GetFullPath(Path.Combine(root, name));
        if (!IsStrictSubfolder(root, dir))
            throw new InvalidOperationException("Refusing to delete a folder outside Cursor’s projects cache.");
        if (!string.Equals(Path.GetFileName(dir), name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Project folder name does not match its slug.");
        if (!Directory.Exists(dir))
            return;

        Directory.Delete(dir, recursive: true);
    }

    private static string RequireCacheSlug(string slug, string error)
    {
        var name = slug?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException(error);
        return name;
    }

    private static bool IsStrictSubfolder(string root, string path)
    {
        var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return full.Length > prefix.Length && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> SuggestSplitFolders(CursorWorkspaceInfo parent)
    {
        if (!Directory.Exists(parent.FolderPath))
            return [];

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".vscode", ".cursor", "node_modules", "bin", "obj", "dist", "out", ".idea"
        };

        return Directory.EnumerateDirectories(parent.FolderPath)
            .Where(dir => !skip.Contains(Path.GetFileName(dir)))
            .Where(dir => !Path.GetFileName(dir).StartsWith('.'))
            .OrderBy(dir => Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}
