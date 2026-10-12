using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace NLightning.Infrastructure.Transport.Tor;

/// <summary>
/// The onion service's private key file: one line, <c>ED25519-V3:&lt;base64&gt;</c> as Tor's <c>ADD_ONION</c> returns
/// it, owner-only (0600) outside Windows, written atomically (a temporary file renamed over).
/// </summary>
/// <remarks>
/// A key readable by group or others is reported by <see cref="HasGroupOrOtherPermissions"/>, which the service logs as
/// a warning at every start (as for <c>--password-file</c>, SECURITY_REVIEW SR-06; NL-584). The temporary file is
/// always created new (a stale one from a crash is deleted first), so it never keeps another file's mode.
/// </remarks>
internal static class TorOnionKeyFile
{
    private const string KeyPrefix = "ED25519-V3:";

    /// <summary>
    /// The saved key blob; null when the file does not exist.
    /// </summary>
    /// <exception cref="TorOnionKeyFileException">The file exists but cannot be read or holds no v3 key.</exception>
    public static string? Read(string path)
    {
        if (!File.Exists(path))
            return null;

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.ASCII).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new TorOnionKeyFileException($"Could not read the onion service key {path}: {e.Message}", e);
        }

        if (!text.StartsWith(KeyPrefix, StringComparison.Ordinal) || text.Length == KeyPrefix.Length
                                                                  || text.Any(char.IsWhiteSpace)
                                                                  || !IsBase64(text[KeyPrefix.Length..]))
            throw new TorOnionKeyFileException($"The onion service key {path} is not an ED25519-V3 key; fix or move "
                                             + "it away (a new key is a new onion address)");

        return text;
    }

    /// <summary>
    /// True when the file at <paramref name="path"/> grants any permission to its group or to others (never on
    /// Windows).
    /// </summary>
    public static bool HasGroupOrOtherPermissions(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return false;

        const UnixFileMode groupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                                        | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & groupOrOther) != 0;
    }

    /// <summary>
    /// Saves <paramref name="keyBlob"/>, never over an existing file.
    /// </summary>
    public static void Write(string path, string keyBlob)
    {
        if (File.Exists(path))
            throw new TorOnionKeyFileException($"The onion service key {path} already exists");

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // A stale temporary file (a crash between create and rename) would keep its own mode with FileMode.Create:
        // delete it, and create the new one exclusively so UnixCreateMode applies (NL-584)
        var temporary = path + ".tmp";
        File.Delete(temporary);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(Encoding.ASCII.GetBytes(keyBlob + "\n"));
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: false);
    }

    private static bool IsBase64(string text)
    {
        var buffer = new byte[text.Length];
        return Convert.TryFromBase64String(text, buffer, out _);
    }
}

/// <summary>
/// The onion service's key file is unusable; the service is not started rather than moved to a new address.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class TorOnionKeyFileException : Exception
{
    public TorOnionKeyFileException(string message) : base(message) { }
    public TorOnionKeyFileException(string message, Exception innerException) : base(message, innerException) { }
}