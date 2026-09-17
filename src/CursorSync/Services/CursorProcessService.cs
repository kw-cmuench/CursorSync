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
}
