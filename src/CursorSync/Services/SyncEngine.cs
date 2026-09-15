using CursorSync.Models;

namespace CursorSync.Services;

public sealed class SyncEngine
{
    public async Task<SyncResult> RunAsync(
        SyncKind kind,
        AppSettings settings,
        IReadOnlyList<CategoryDefinition> categories,
        IProgress<SyncProgress> progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.HubPath))
            return Fail("Choose a sync folder first. Use a OneDrive, Dropbox, or network path both machines can see.");

        var hub = settings.HubPath.Trim();
        Directory.CreateDirectory(hub);
        Directory.CreateDirectory(Path.Combine(hub, "payload"));

        var paths = CursorPaths.FromSettings(settings);
        if (kind is SyncKind.Push or SyncKind.TwoWay)
            ExtensionListWriter.Ensure(paths);

        var log = new List<string>();
        var warnings = new List<string>();
        var copied = 0;
        var skipped = 0;
        long bytes = 0;

        void Report(string message, double? fraction = null) =>
            progress.Report(new SyncProgress
            {
                Message = message,
                FilesCopied = copied,
                BytesCopied = bytes,
                Fraction = fraction
            });

        var pairs = new List<(CategoryDefinition Category, FilePair Pair, DateTime? OtherWriteUtc)>();

        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report($"Scanning {category.Title}…");

            foreach (var entry in category.Resolve(paths))
            {
                switch (kind)
                {
                    case SyncKind.Push:
                        foreach (var pair in FileInventory.Enumerate(entry, sourceIsLocal: true, hub))
                            pairs.Add((category, pair, null));
                        break;

                    case SyncKind.Pull:
                        foreach (var pair in FileInventory.Enumerate(entry, sourceIsLocal: false, hub))
                            pairs.Add((category, pair, null));
                        break;

                    case SyncKind.TwoWay:
                        CollectTwoWay(entry, hub, settings.ConflictPolicy, pairs, category);
                        break;
                }
            }
        }

        if (kind == SyncKind.Pull && settings.BackupBeforePull)
        {
            Report("Creating a local backup…");
            var backup = new BackupService();
            var backupPath = await Task.Run(() => backup.Backup(paths, categories, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (backupPath is not null)
            {
                log.Add($"Backup saved to {backupPath}");
                Report($"Backup saved to {backupPath}");
            }
        }

        var total = Math.Max(pairs.Count, 1);
        var index = 0;

        foreach (var (category, pair, _) in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            try
            {
                var destDir = Path.GetDirectoryName(pair.DestinationPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                if (File.Exists(pair.DestinationPath))
                {
                    var destTime = File.GetLastWriteTimeUtc(pair.DestinationPath);
                    if (destTime == pair.SourceWriteUtc && new FileInfo(pair.DestinationPath).Length == new FileInfo(pair.SourcePath).Length)
                    {
                        skipped++;
                        continue;
                    }
                }

                await CopyFileAsync(pair.SourcePath, pair.DestinationPath, cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(pair.DestinationPath, pair.SourceWriteUtc);

                if (kind == SyncKind.Pull &&
                    category.Id == "chats" &&
                    string.Equals(Path.GetFileName(pair.DestinationPath), "workspace.json", StringComparison.OrdinalIgnoreCase))
                {
                    PathRewriter.RewriteWorkspaceFile(pair.DestinationPath, settings);
                }

                copied++;
                bytes += new FileInfo(pair.DestinationPath).Length;
                log.Add($"{category.Title}: {Path.GetFileName(pair.DestinationPath)}");
                Report($"Copying {Path.GetFileName(pair.SourcePath)}", index / (double)total);
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(pair.SourcePath)}: {ex.Message}");
                skipped++;
            }
        }

        var manifestStore = new HubManifestStore();
        manifestStore.Update(hub, settings.MachineName, kind, categories.Select(c => c.Id).ToList());

        if (copied == 0 && pairs.Count == 0)
            warnings.Add("Nothing to copy. The selected categories were empty on the source side.");

        Report("Done", 1);

        return new SyncResult
        {
            Success = true,
            FilesCopied = copied,
            BytesCopied = bytes,
            FilesSkipped = skipped,
            Categories = categories.Select(c => c.Id).ToList(),
            Warnings = warnings,
            Log = log.TakeLast(80).ToList()
        };
    }

    private static void CollectTwoWay(
        SyncEntry entry,
        string hub,
        ConflictPolicy policy,
        List<(CategoryDefinition, FilePair, DateTime?)> pairs,
        CategoryDefinition category)
    {
        var localFiles = FileInventory.Enumerate(entry, sourceIsLocal: true, hub)
            .ToDictionary(p => Normalize(p.DestinationPath), p => p, StringComparer.OrdinalIgnoreCase);

        var hubFiles = FileInventory.Enumerate(entry, sourceIsLocal: false, hub)
            .ToDictionary(p => Normalize(p.SourcePath), p => p, StringComparer.OrdinalIgnoreCase);

        var keys = localFiles.Keys.Union(hubFiles.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            localFiles.TryGetValue(key, out var local);
            hubFiles.TryGetValue(key, out var remote);

            if (local.SourcePath is null && remote.SourcePath is not null)
            {
                pairs.Add((category, remote, null));
                continue;
            }

            if (remote.SourcePath is null && local.SourcePath is not null)
            {
                pairs.Add((category, local, null));
                continue;
            }

            var winner = PickWinner(local, remote, policy);
            if (winner is not null)
                pairs.Add((category, winner.Value, null));
        }
    }

    private static FilePair? PickWinner(FilePair local, FilePair remote, ConflictPolicy policy)
    {
        return policy switch
        {
            ConflictPolicy.PreferLocal => NeedsCopy(local.SourcePath, remote.SourcePath) ? local : null,
            ConflictPolicy.PreferHub => NeedsCopy(remote.SourcePath, local.SourcePath) ? remote : null,
            _ => local.SourceWriteUtc >= remote.SourceWriteUtc
                ? NeedsCopy(local.SourcePath, remote.SourcePath) ? local : null
                : NeedsCopy(remote.SourcePath, local.SourcePath) ? remote : null
        };
    }

    private static bool NeedsCopy(string source, string destination)
    {
        if (!File.Exists(destination))
            return true;

        var srcInfo = new FileInfo(source);
        var dstInfo = new FileInfo(destination);
        return srcInfo.LastWriteTimeUtc != dstInfo.LastWriteTimeUtc || srcInfo.Length != dstInfo.Length;
    }

    private static string Normalize(string path) => path.Replace('/', '\\').ToLowerInvariant();

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 80;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, useAsync: true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static SyncResult Fail(string error) => new()
    {
        Success = false,
        Error = error
    };
}
