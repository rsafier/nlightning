using System.Runtime.InteropServices;

namespace NLightning.Application.Channels.Backup;

/// <summary>
/// Atomic writes of the backup file: the bytes go to a temporary file in the same directory (owner-only on Unix),
/// flushed to disk, then renamed over the target, so a reader (or a crash) sees the old file or the new one, never a
/// partial one. A symlinked target is followed, so the real file is replaced. On Linux and macOS the directory is
/// fsynced after the rename too, so the rename itself survives a power loss (best effort: a failed directory sync is
/// ignored; Windows has no directory sync).
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

        if (!string.IsNullOrEmpty(directory))
            TrySyncDirectory(directory);
    }

    /// <summary>
    /// Moves a backup file aside (<c>&lt;file&gt;.&lt;UTC time&gt;.&lt;reason&gt;</c>, with <c>-2</c>, <c>-3</c>, ...
    /// before the reason when that name is taken), never overwriting another file.
    /// </summary>
    /// <returns>Where it went.</returns>
    public static string MoveAside(string path, DateTimeOffset now, string reason = "unreadable")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var stem = $"{path}.{now.UtcDateTime:yyyyMMddHHmmss}";
        var target = $"{stem}.{reason}";
        for (var attempt = 2; File.Exists(target); attempt++)
            target = $"{stem}-{attempt}.{reason}";

        File.Move(path, target, false);
        if (Path.GetDirectoryName(Path.GetFullPath(target)) is { Length: > 0 } directory)
            TrySyncDirectory(directory);

        return target;
    }

    /// <summary>fsync of a directory on Linux and macOS, so a rename in it is durable; best effort.</summary>
    internal static bool TrySyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;

        try
        {
            var fd = Open(directory, 0 /* O_RDONLY, the same value on Linux and macOS */);
            if (fd < 0)
                return false;

            try
            {
                return FSync(fd) == 0;
            }
            finally
            {
                _ = Close(fd);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int FSync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

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