using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NLightning.Daemon.Hosting;

using Domain.Signing;

/// <summary>Preflight boundaries for separately composed native nodes and signer processes.</summary>
public sealed class HostedNodeIsolationManifest
{
    public IReadOnlyList<HostedNativeNodeEnrollment> Nodes { get; }

    public HostedNodeIsolationManifest(IEnumerable<HostedNativeNodeEnrollment> nodes)
    {
        Nodes = Array.AsReadOnly(nodes.Select(node => node with
        {
            PeerListeners = Array.AsReadOnly(node.PeerListeners.ToArray())
        }).ToArray());
        Validate();
    }

    internal HostedNativeNodeEnrollment Resolve(HostedNativeNodeEnrollment supplied)
    {
        var enrolled = Nodes.SingleOrDefault(node => node.Signer.Context.NodeId == supplied.Signer.Context.NodeId);
        if (enrolled is null || enrolled.Signer != supplied.Signer
         || enrolled.PrivateDatabasePath != supplied.PrivateDatabasePath
         || enrolled.NodeCredentialPath != supplied.NodeCredentialPath || enrolled.NodeIpcPath != supplied.NodeIpcPath
         || !enrolled.PeerListeners.SequenceEqual(supplied.PeerListeners))
            throw new ArgumentException("A launch does not match its immutable hosted enrollment.");
        return enrolled;
    }

    public void Validate()
    {
        ValidateSigners(Nodes.Select(node => node.Signer));
        var privatePaths = new HashSet<string>(StringComparer.Ordinal);
        var listeners = new List<HostedPeerListener>();
        foreach (var node in Nodes)
        {
            foreach (var path in SignerPaths(node.Signer).Concat(new[]
                     {
                         node.PrivateDatabasePath, node.PrivateDatabasePath + "-wal",
                         node.PrivateDatabasePath + "-shm", node.NodeCredentialPath, node.NodeIpcPath
                     }))
                AddPath(privatePaths, path);
            if (!node.PeerListeners.Any())
                throw new ArgumentException("Every hosted Lightning identity requires its own advertised listener.");
            foreach (var listener in node.PeerListeners)
            {
                listener.Validate();
                if (listeners.Any(previous => previous.Overlaps(listener)))
                    throw new ArgumentException("Hosted Lightning listeners overlap.");
                listeners.Add(listener);
            }
        }
    }

    public void ValidateProvisionedCredentials()
    {
        ValidateCredentialFiles(Nodes.SelectMany(node => new[]
            { node.Signer.CredentialPath, node.NodeCredentialPath }));
    }

    public static void ValidateSigners(IEnumerable<HostedSignerEnrollment> enrollments)
    {
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        var signers = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var enrollment in enrollments)
        {
            enrollment.Context.Validate();
            if (!nodes.Add(enrollment.Context.NodeId) || !owners.Add(enrollment.Context.OwnerId)
             || !signers.Add(enrollment.Context.SignerId)
             || !identities.Add(enrollment.Context.NodePublicKey.ToString()))
                throw new ArgumentException("Isolated hosted nodes require distinct node, owner, signer and Lightning identities.");
            foreach (var path in SignerPaths(enrollment)) AddPath(paths, path);
        }
    }

    public static void ValidateCredentialFiles(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            _ = NormalizePath(path);
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                              | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(path) & forbidden) != 0)
                    throw new UnauthorizedAccessException("Hosted credentials must be accessible only to their owner.");
            }
            var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(path).TrimEnd('\r', '\n'));
            try
            {
                if (bytes.Length < 32 || bytes.Any(value => value is < 33 or > 126))
                    throw new ArgumentException("Hosted credentials require at least 32 printable ASCII characters without spaces.");
                if (!seen.Add(Convert.ToHexString(SHA256.HashData(bytes))))
                    throw new ArgumentException("Hosted nodes must not reuse authentication credentials.");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    private static IEnumerable<string> SignerPaths(HostedSignerEnrollment signer)
    {
        yield return signer.StatePath;
        yield return signer.StatePath + ".enrollment";
        yield return signer.StatePath + ".key-index";
        yield return signer.StatePath + ".nonces";
        yield return signer.StatePath + ".swap-sessions";
        yield return (signer.KeyFilePath ?? signer.StatePath) + ".lock";
        yield return signer.SocketPath;
        yield return signer.CredentialPath;
        if (signer.KeyFilePath is { } keyFile) yield return keyFile;
        if (signer.PasswordFilePath is { } passwordFile) yield return passwordFile;
    }

    private static void AddPath(HashSet<string> paths, string path)
    {
        if (!paths.Add(NormalizePath(path)))
            throw new ArgumentException("Hosted private storage, sockets and credentials must use separate paths.");
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Hosted private resource paths must be absolute.");
        var normalized = Path.GetFullPath(path);
        for (var current = normalized; current is not null; current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new UnauthorizedAccessException("Hosted private resource paths must not pass through symbolic links.");
        }
        return normalized;
    }
}

public sealed record HostedSignerEnrollment(NodeSigningContext Context, string StatePath, string SocketPath,
                                            string CredentialPath, string? KeyFilePath = null,
                                            string? PasswordFilePath = null);

public sealed record HostedNativeNodeEnrollment(HostedSignerEnrollment Signer, string PrivateDatabasePath,
                                                string NodeCredentialPath, string NodeIpcPath,
                                                IReadOnlyList<HostedPeerListener> PeerListeners);

public sealed record HostedPeerListener(string Address, int Port)
{
    public void Validate()
    {
        if (!IPAddress.TryParse(Address, out _) || Port is < 1 or > 65535)
            throw new ArgumentException("Hosted peer listeners require a numeric IP address and a valid port.");
    }

    internal bool Overlaps(HostedPeerListener other)
    {
        if (Port != other.Port) return false;
        var address = IPAddress.Parse(Address);
        var peer = IPAddress.Parse(other.Address);
        return address.Equals(peer) || address.Equals(IPAddress.Any) || peer.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any) || peer.Equals(IPAddress.IPv6Any)
            || address.MapToIPv6().Equals(peer.MapToIPv6());
    }
}