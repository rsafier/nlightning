using Serilog;

namespace NLightning.Daemon.Utilities;

/// <summary>
/// Owner-only files and directories for the node's secrets (the configuration with the bitcoind RPC password, the key
/// file, the cookie, the database and the logs all live in the configuration directory). Unix only: on Windows the
/// user profile's ACL applies and these helpers do nothing.
/// </summary>
public static class FilePermissionUtils
{
    private const UnixFileMode OwnerOnlyDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerOnlyFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode GroupOrOtherMode = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                                                                         | UnixFileMode.GroupExecute
                                                                         | UnixFileMode.OtherRead
                                                                         | UnixFileMode.OtherWrite
                                                                         | UnixFileMode.OtherExecute;

    /// <summary>
    /// Creates <paramref name="path"/> (and missing parents) with mode 0700 on Unix. An existing directory keeps its
    /// mode.
    /// </summary>
    public static void CreateOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        // Directory.CreateDirectory(path, mode) applies the mode to the last directory only: create the missing
        // parents (e.g. ~/.nltg) one by one, top down
        var missing = new Stack<string>();
        for (var current = Path.GetFullPath(path);
             !string.IsNullOrEmpty(current) && !Directory.Exists(current);
             current = Path.GetDirectoryName(current))
            missing.Push(current);

        while (missing.TryPop(out var directory))
            Directory.CreateDirectory(directory, OwnerOnlyDirectoryMode);
    }

    /// <summary>
    /// Writes a new file with mode 0600 on Unix (never readable by others, not even briefly).
    /// </summary>
    /// <exception cref="IOException">The file already exists.</exception>
    public static void WriteNewOwnerOnlyFile(string path, string contents)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnlyFileMode;

        using var writer = new StreamWriter(path, options);
        writer.Write(contents);
    }

    /// <summary>
    /// True when <paramref name="path"/> (a file or directory) has any group or other permission bit on Unix.
    /// </summary>
    public static bool IsAccessibleByOthers(string path)
    {
        if (OperatingSystem.IsWindows() || (!File.Exists(path) && !Directory.Exists(path)))
            return false;

        return (File.GetUnixFileMode(path) & GroupOrOtherMode) != 0;
    }

    /// <summary>
    /// Logs a warning when <paramref name="path"/> is accessible by other users. Never changes the mode: the operator
    /// may share it on purpose (a group-readable config for a monitoring user).
    /// </summary>
    /// <returns>True when it warned.</returns>
    public static bool WarnIfAccessibleByOthers(string path, string description, ILogger logger)
    {
        if (OperatingSystem.IsWindows() || !IsAccessibleByOthers(path))
            return false;

        logger.Warning("{Description} {Path} is accessible by other users (mode {Mode}); it holds secrets, restrict "
                     + "it with chmod {Suggested}", description, path, FormatMode(File.GetUnixFileMode(path)),
                       Directory.Exists(path) ? "700" : "600");
        return true;
    }

    private static string FormatMode(UnixFileMode mode) => Convert.ToString((int)mode, 8).PadLeft(3, '0');
}