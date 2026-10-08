using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using NLightning.Cashu.PaymentProcessor;
using NLightning.LnBackend;
using NLightning.LndGrpc;
using NLightning.LndGrpc.Macaroons;

namespace NLightning.Daemon.Hosting;

/// <summary>Checks administrative listeners and credential authority before any hosted process starts.</summary>
internal static class HostedAdministrativeApiIsolation
{
    internal static void Validate(IReadOnlyList<HostedDaemonLaunch> launches)
    {
        if (launches.Select(launch => launch.Enrollment.Signer.Context.OwnerId).Distinct().Count() < 2) return;
        var listeners = launches.SelectMany(launch => launch.Enrollment.PeerListeners).ToList();
        var resources = new List<(string Path, bool Directory, string Owner)>();
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var clientAuthorities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var launch in launches)
        {
            var enrollment = launch.Enrollment;
            var owner = enrollment.Signer.Context.OwnerId;
            foreach (var file in new[] { enrollment.PrivateDatabasePath, enrollment.NodeCredentialPath,
                         enrollment.NodeIpcPath, enrollment.Signer.StatePath, enrollment.Signer.CredentialPath,
                         enrollment.Signer.SocketPath, enrollment.Signer.KeyFilePath, enrollment.Signer.PasswordFilePath })
                if (file is not null) AddResource(file, false, owner);
        }
        foreach (var launch in launches)
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(launch.ConfigurationFilePath, false).Build();
            var owner = launch.Enrollment.Signer.Context.OwnerId;
            var lnd = configuration.GetSection(LndGrpcOptions.SectionName).Get<LndGrpcOptions>() ?? new();
            if (lnd.Enabled)
            {
                if (lnd.AllowNoMacaroons)
                    throw new ArgumentException("Hosted owners require authenticated LND administrative requests.");
                AddListener(lnd.ListenAddress, lnd.Port);
                var directory = lnd.ResolveDataDirectory(Path.GetDirectoryName(launch.ConfigurationFilePath)!);
                AddResource(directory, true, owner);
                foreach (var file in ExistingRootKeys(directory))
                    AddAuthority(roots, SHA256.HashData(File.ReadAllBytes(file)), owner, "macaroon roots");
            }
            var cashu = configuration.GetSection(CashuPaymentProcessorOptions.SectionName)
                                     .Get<CashuPaymentProcessorOptions>() ?? new();
            if (cashu.Enabled)
            {
                AddListener(cashu.ListenAddress, cashu.Port);
                AddClientAuthority(cashu.TlsDirectory, cashu.AllowInsecureLoopback, owner);
            }
            var backend = configuration.GetSection(LnBackendOptions.SectionName).Get<LnBackendOptions>() ?? new();
            if (backend.Enabled)
            {
                AddListener(backend.ListenAddress, backend.Port);
                AddClientAuthority(backend.TlsDirectory, backend.AllowInsecureLoopback, owner);
            }
        }

        void AddListener(string address, int port)
        {
            if (port == 0) return;
            var listener = new HostedPeerListener(address, port);
            listener.Validate();
            if (listeners.Any(existing => existing.Overlaps(listener)))
                throw new ArgumentException("Hosted administrative and Lightning listeners overlap.");
            listeners.Add(listener);
        }

        void AddClientAuthority(string? directory, bool allowInsecure, string owner)
        {
            if (allowInsecure || string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Hosted owners require mutual TLS for Cashu and hold-backend administration.");
            AddResource(directory, true, owner);
            var path = Path.Combine(directory, "ca.pem");
            if (!File.Exists(path))
                throw new ArgumentException("Hosted administrative mutual TLS requires an installed client CA.");
            RequireCanonicalPath(path);
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
            AddAuthority(clientAuthorities, SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()), owner,
                "client certificate authorities");
        }

        void AddResource(string path, bool directory, string owner)
        {
            var normalized = RequireCanonicalPath(path);
            if (resources.Any(existing => existing.Owner != owner
                && (existing.Path == normalized
                    || existing.Directory && IsInside(normalized, existing.Path)
                    || directory && IsInside(existing.Path, normalized))))
                throw new ArgumentException("Hosted administrative credential storage overlaps another owner's private resources.");
            resources.Add((normalized, directory, owner));
        }
    }

    private static IEnumerable<string> ExistingRootKeys(string directory)
    {
        var root = Path.Combine(directory, LndMacaroonFiles.RootKeyFileName);
        if (File.Exists(root)) { RequireCanonicalPath(root); yield return root; }
        var additional = Path.Combine(directory, "macaroon-root-keys");
        if (!Directory.Exists(additional)) yield break;
        RequireCanonicalPath(additional);
        foreach (var file in Directory.EnumerateFiles(additional, "*.key"))
        { RequireCanonicalPath(file); yield return file; }
    }

    private static void AddAuthority(Dictionary<string, string> seen, byte[] fingerprint, string owner, string kind)
    {
        var key = Convert.ToHexString(fingerprint);
        if (seen.TryGetValue(key, out var existing) && existing != owner)
            throw new ArgumentException($"Hosted owners must not reuse administrative {kind}.");
        seen[key] = owner;
    }

    private static bool IsInside(string path, string directory) =>
        path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal);

    private static string RequireCanonicalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Hosted administrative credential paths must be absolute.");
        var normalized = Path.GetFullPath(path);
        for (var current = normalized; current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new UnauthorizedAccessException("Hosted administrative credential paths must not pass through symbolic links.");
        return normalized;
    }
}