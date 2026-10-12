using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace NLightning.Signer;

/// <summary>Local prototype file ownership boundary; the transport stays independent of the key file.</summary>
[UnsupportedOSPlatform("windows")]
internal static class SignerFiles
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                               | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OtherPermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                                                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
                                                | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static void PrepareDirectoryFor(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var info = new DirectoryInfo(directory);
        if (info.LinkTarget is not null)
            throw new IOException("Signer directories cannot be symbolic links.");
        if (!info.Exists)
            Directory.CreateDirectory(directory, PrivateDirectory);
        RequirePrivate(directory);
    }

    public static void RequirePrivate(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException("Signer files cannot be symbolic links.");
        if ((File.GetUnixFileMode(path) & OtherPermissions) != 0)
            throw new IOException($"Signer path must have owner-only permissions: {path}");
    }

    public static FileStream LockKeyFile(string keyFile)
    {
        var lockPath = keyFile + ".lock";
        if (File.Exists(lockPath) || new FileInfo(lockPath).LinkTarget is not null)
            RequirePrivate(lockPath);
        return new FileStream(lockPath, new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            UnixCreateMode = PrivateFile
        });
    }

    public static async Task<string> ReadPasswordAsync(SignerDaemonOptions options)
    {
        string? password;
        if (options.PasswordStdin)
            password = await Console.In.ReadLineAsync();
        else
        {
            RequirePrivate(options.PasswordFilePath!);
            // Permit the terminal newline of a one-line file, without silently trimming meaningful spaces.
            password = (await File.ReadAllTextAsync(options.PasswordFilePath!)).TrimEnd('\r', '\n');
        }

        if (string.IsNullOrEmpty(password) || password.Contains('\n') || password.Contains('\r'))
            throw new ArgumentException("Password must be a nonempty single line.");
        return password;
    }

    public static async Task<byte[]> ReadSeedAsync()
    {
        // Read one bounded ASCII hex line without constructing an immutable managed secret string.
        var encoded = new byte[66];
        var single = new byte[1];
        var seed = new byte[32];
        try
        {
            var input = Console.OpenStandardInput();
            var length = 0;
            while (await input.ReadAsync(single) != 0)
            {
                if (single[0] == '\n')
                    break;
                if (length == encoded.Length)
                    throw new ArgumentException("Seed must be exactly 64 hexadecimal characters.");
                encoded[length++] = single[0];
            }
            if (length > 0 && encoded[length - 1] == '\r')
                length--;
            if (length != 64)
                throw new ArgumentException("Seed must be exactly 64 hexadecimal characters.");
            for (var i = 0; i < seed.Length; i++)
                seed[i] = (byte)((Hex(encoded[i * 2]) << 4) | Hex(encoded[i * 2 + 1]));
            return seed;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(seed);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(single);
        }
    }

    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => throw new ArgumentException("Seed must contain only hexadecimal characters.")
    };

    public static void SyncParentDirectory(string path)
    {
        var descriptor = UnixOpen(Path.GetDirectoryName(path)!, 0);
        if (descriptor < 0)
            throw new IOException("Could not open signer state directory for durable sync.");
        try
        {
            if (UnixFsync(descriptor) != 0)
                throw new IOException("Could not sync signer state directory.");
        }
        finally
        {
            _ = UnixClose(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int UnixFsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int descriptor);
}