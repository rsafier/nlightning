using System.Text.Json;

namespace NLightning.Infrastructure.Bitcoin.Services;

/// <summary>
/// Reads and writes the fee rate cache file. A write goes to a uniquely named owner-only temporary file next to the
/// target, flushed to disk and renamed over it, so a reader, a crash or a second writer (another instance on the same
/// file) sees a whole file, the old one or a new one, never a partial one.
/// </summary>
internal static class FeeRateCacheFile
{
    /// <summary>The largest file read: an entry is about 300 bytes, so anything bigger is not ours.</summary>
    internal const int MaxFileBytes = 16 * 1024;

    /// <summary>Writes <paramref name="entry"/> to <paramref name="path"/> atomically, owner-only on Unix.</summary>
    public static void Write(string path, FeeRateCacheEntry entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, FeeRateCacheJsonContext.Default.FeeRateCacheEntry);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // Best effort: the write already failed, its error is the one reported
            }

            throw;
        }
    }

    /// <summary>
    /// Reads the entry at <paramref name="path"/>: null with a null <paramref name="problem"/> when there is no file,
    /// null with the reason when it cannot be used as an entry (too big, not JSON, not this format).
    /// </summary>
    public static FeeRateCacheEntry? TryRead(string path, out string? problem)
    {
        problem = null;
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaxFileBytes)
            {
                problem = $"it is {stream.Length} bytes, more than {MaxFileBytes}";
                return null;
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            var entry = JsonSerializer.Deserialize(bytes, FeeRateCacheJsonContext.Default.FeeRateCacheEntry);
            if (entry is null)
            {
                problem = "it holds no entry";
                return null;
            }

            if (entry.Version != FeeRateCacheEntry.CurrentVersion)
            {
                problem = $"its version {entry.Version} is not {FeeRateCacheEntry.CurrentVersion}";
                return null;
            }

            return entry;
        }
        catch (JsonException e)
        {
            problem = $"it is not a fee cache entry ({e.Message})";
            return null;
        }
    }
}