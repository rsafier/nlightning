namespace NLightning.Application.Channels.Backup;

/// <summary>
/// Atomic writes of the backup file: the bytes go to a temporary file in the same directory (owner-only on Unix),
/// flushed to disk, then renamed over the target, so a reader (or a crash) sees the old file or the new one, never a
/// partial one. A symlinked target is followed, so the real file is replaced.
/// </summary>
public static class ChannelBackupFile
{
    /// <summary>Writes <paramref name="contents"/> to <paramref name="path"/> atomically.</summary>
    public static async Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> contents,
                                                  CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var targetPath = ResolveFinalPath(Path.GetFullPath(path));
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, CreateTempFileOptions()))
            {
                await stream.WriteAsync(contents, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            CopyUnixFileMode(targetPath, tempPath);
            File.Move(tempPath, targetPath, true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Moves an unreadable backup file aside (<c>&lt;file&gt;.&lt;UTC time&gt;.unreadable</c>).</summary>
    /// <returns>Where it went.</returns>
    public static string MoveAside(string path, DateTimeOffset now)
    {
        var target = $"{path}.{now.UtcDateTime:yyyyMMddHHmmss}.unreadable";
        File.Move(path, target, false);
        return target;
    }

    private static string ResolveFinalPath(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is null)
            return path;

        return info.ResolveLinkTarget(true)?.FullName ?? path;
    }

    private static FileStreamOptions CreateTempFileOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        return options;
    }

    private static void CopyUnixFileMode(string sourcePath, string destinationPath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(sourcePath))
            return;

        File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort: the original exception matters more
        }
    }
}