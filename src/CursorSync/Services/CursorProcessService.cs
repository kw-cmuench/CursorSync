using System.Diagnostics;
using CursorSync.Models;

namespace CursorSync.Services;

public static class CursorProcessService
{
    public static CursorStatus GetStatus(CursorPaths paths)
    {
        var processes = GetCursorProcesses();
        return new CursorStatus
        {
            UserFolderFound = Directory.Exists(paths.UserFolder),
            HomeFolderFound = Directory.Exists(paths.HomeCursor),
            IsRunning = processes.Count > 0,
            ProcessCount = processes.Count,
            UserFolder = paths.UserFolder,
            HomeFolder = paths.HomeCursor
        };
    }

    public static IReadOnlyList<Process> GetCursorProcesses()
    {
        return Process.GetProcessesByName("Cursor");
    }

    public static async Task<bool> TryCloseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var processes = GetCursorProcesses();
        foreach (var process in processes)
        {
            try
            {
                process.CloseMainWindow();
            }
            catch
            {
                // ignored
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetCursorProcesses().Count == 0)
                return true;
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        return GetCursorProcesses().Count == 0;
    }

    public static bool TryOpenFolder(string folder, out string? error)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            error = "That folder does not exist.";
            return false;
        }

        var arguments = "\"" + folder.TrimEnd('\\') + "\"";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "Programs", "cursor", "Cursor.exe"),
            Path.Combine(local, "Programs", "Cursor", "Cursor.exe"),
            Path.Combine(local, "cursor", "Cursor.exe")
        };

        foreach (var exe in candidates)
        {
            if (!File.Exists(exe))
                continue;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false
            });
            error = null;
            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cursor",
                Arguments = arguments,
                UseShellExecute = true
            });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not start Cursor: " + ex.Message;
            return false;
        }
    }
}
