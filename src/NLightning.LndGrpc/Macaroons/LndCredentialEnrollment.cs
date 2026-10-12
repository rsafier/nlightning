using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NLightning.Domain.Signing;

namespace NLightning.LndGrpc.Macaroons;

/// <summary>Immutable node authority for administrative credential storage.</summary>
internal static class LndCredentialEnrollment
{
    internal const string FileName = "macaroons.enrollment";

    internal static void Bind(string directory, NodeSigningContext context)
    {
        context.Validate();
        RequireCanonicalPath(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var expected = Binding(context);
        if (File.Exists(path))
        {
            RequirePrivate(path);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("Administrative credentials belong to another immutable node enrollment.");
            return;
        }
        if (Directory.EnumerateFiles(directory, "*.macaroon").Any()
         || File.Exists(Path.Combine(directory, LndMacaroonFiles.RootKeyFileName))
         || Directory.Exists(Path.Combine(directory, "macaroon-root-keys"))
            && Directory.EnumerateFileSystemEntries(Path.Combine(directory, "macaroon-root-keys")).Any())
            throw new InvalidOperationException("Existing administrative credentials without enrollment cannot be adopted.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            LndMacaroonFiles.WriteExclusive(temporary, expected);
            File.Move(temporary, path, false);
            if (!OperatingSystem.IsWindows()) SyncDirectory(directory);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static byte[] EffectiveRootKey(byte[] storedKey, NodeSigningContext? context)
    {
        if (context is null) return storedKey;
        try { return HMACSHA256.HashData(storedKey, Binding(context)); }
        finally { CryptographicOperations.ZeroMemory(storedKey); }
    }

    private static byte[] Binding(NodeSigningContext context) => Encoding.UTF8.GetBytes(string.Join("\n",
        "NLightning.LndGrpc.MacaroonRoot/v1", context.NodeId, context.OwnerId, context.SignerId,
        NLightning.Domain.Protocol.ValueObjects.BitcoinNetwork.Resolve(context.Network).Name,
        context.NodePublicKey.ToString()));

    private static void RequireCanonicalPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new UnauthorizedAccessException("Administrative credential paths must not pass through symbolic links.");
    }

    private static void RequirePrivate(string path)
    {
        RequireCanonicalPath(path);
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                      | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(path) & forbidden) != 0)
            throw new UnauthorizedAccessException("Administrative credential enrollment must be readable only by its owner.");
    }

    private static void SyncDirectory(string directory)
    {
        var descriptor = UnixOpen(directory, 0);
        if (descriptor < 0) throw new IOException("Could not open administrative credential directory for durable sync.");
        try
        {
            if (UnixFsync(descriptor) != 0)
                throw new IOException("Could not sync administrative credential directory.");
        }
        finally { _ = UnixClose(descriptor); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int UnixFsync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int descriptor);
}