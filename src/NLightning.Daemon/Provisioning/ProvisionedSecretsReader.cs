using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace NLightning.Daemon.Provisioning;

using Contracts.Provisioning;
using Domain.Node.Options;
using Domain.Node.Provisioning;
using LndGrpc;
using LndGrpc.Tls;

/// <summary>
/// Checks the file secrets of an unlock request (NL-1352) and turns them into the node's
/// <see cref="ProvisionedSecrets"/>. A malformed value refuses the key with a reason that names the field, never its
/// content, so the node stays locked. With <c>Node:Startup:RequireProvisionedSecrets</c> it also refuses a request that
/// leaves a secret the node would generate (LND gRPC TLS pair and macaroon root key, Tor onion service key) with neither
/// a delivered value nor an existing file.
/// </summary>
public static class ProvisionedSecretsReader
{
    private const string OnionKeyPrefix = "ED25519-V3:";

    /// <summary>The length of Tor's expanded ed25519 secret key (<c>ADD_ONION ED25519-V3</c>).</summary>
    private const int OnionKeyLength = 64;

    private const int MacaroonRootKeyLength = 32;

    /// <exception cref="InvalidDataException">A value is malformed, or a required secret is missing.</exception>
    public static ProvisionedSecrets Read(KeyProvisioningSecrets? secrets, bool returnLndGrpcMacaroons,
                                          bool requireProvisioned, IConfiguration configuration, string configPath)
    {
        var provisioned = new ProvisionedSecrets
        {
            LndGrpcMacaroonRootKey = ReadRootKey(secrets?.LndGrpcMacaroonRootKey),
            LndGrpcTls = ReadTls("lndGrpcTls", secrets?.LndGrpcTls),
            TorOnionServiceKey = ReadOnionKey(secrets?.TorOnionServiceKey),
            CashuProcessorTls = ReadTls("cashuProcessorTls", secrets?.CashuProcessorTls),
            LnBackendTls = ReadTls("lnBackendTls", secrets?.LnBackendTls),
            RefuseGeneration = requireProvisioned
        };

        if (returnLndGrpcMacaroons && provisioned.LndGrpcMacaroonRootKey is null)
            throw new InvalidDataException("returnLndGrpcMacaroons needs secrets.lndGrpcMacaroonRootKey.");

        if (requireProvisioned)
            CheckRequired(provisioned, configuration, configPath);

        return provisioned;
    }

    private static byte[]? ReadRootKey(string? value)
    {
        if (value is null)
            return null;

        var buffer = new byte[value.Length];
        try
        {
            if (!Convert.TryFromBase64String(value.Trim(), buffer, out var length) || length != MacaroonRootKeyLength)
                throw new InvalidDataException("secrets.lndGrpcMacaroonRootKey is not base64 of 32 bytes.");

            return buffer.AsSpan(0, MacaroonRootKeyLength).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static string? ReadOnionKey(string? value)
    {
        if (value is null)
            return null;

        var text = value.Trim();
        if (!text.StartsWith(OnionKeyPrefix, StringComparison.Ordinal))
            throw new InvalidDataException($"secrets.torOnionServiceKey is not an {OnionKeyPrefix}<base64> key.");

        var blob = text[OnionKeyPrefix.Length..];
        var buffer = new byte[blob.Length];
        try
        {
            if (blob.Any(char.IsWhiteSpace) || !Convert.TryFromBase64String(blob, buffer, out var length)
                                            || length != OnionKeyLength)
                throw new InvalidDataException("secrets.torOnionServiceKey is not a 64-byte ed25519 key in base64.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        return text;
    }

    private static ProvisionedTls? ReadTls(string name, KeyProvisioningTls? tls)
    {
        if (tls is null)
            return null;
        if (string.IsNullOrWhiteSpace(tls.Certificate) || string.IsNullOrWhiteSpace(tls.Key))
            throw new InvalidDataException($"secrets.{name} needs both certificate and key.");

        var certificatePem = ReadCertificate($"secrets.{name}.certificate", tls.Certificate);
        var keyPem = ReadKey($"secrets.{name}.key", tls.Key);
        var clientCaPem = string.IsNullOrWhiteSpace(tls.ClientCa)
                              ? null
                              : ReadCertificate($"secrets.{name}.clientCa", tls.ClientCa);
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem, keyPem);
            if (!certificate.HasPrivateKey)
                throw new InvalidDataException($"secrets.{name}.key is not the certificate's private key.");
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException($"secrets.{name}.key does not match secrets.{name}.certificate, or its "
                                         + "algorithm is not supported.");
        }

        return new ProvisionedTls(certificatePem, keyPem, clientCaPem);
    }

    private static string ReadCertificate(string name, string value)
    {
        try
        {
            using var certificate = IsPem(value)
                                        ? X509Certificate2.CreateFromPem(value)
                                        : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value.Trim()));
            return certificate.ExportCertificatePem();
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            throw new InvalidDataException($"{name} is not an X.509 certificate in PEM or base64 DER.");
        }
    }

    private static string ReadKey(string name, string value)
    {
        if (IsPem(value))
        {
            // Checked against the certificate by the caller (any PEM label X509Certificate2.CreateFromPem reads)
            if (!PemEncoding.TryFind(value, out _))
                throw new InvalidDataException($"{name} is not a private key in PEM or base64 PKCS#8 DER.");
            return value;
        }

        byte[] der;
        try
        {
            der = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"{name} is not a private key in PEM or base64 PKCS#8 DER.");
        }

        try
        {
            return new string(PemEncoding.Write("PRIVATE KEY", der));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
    }

    private static bool IsPem(string value) => value.Contains("-----BEGIN ", StringComparison.Ordinal);

    /// <summary>Refuses a request that leaves a generated secret with neither a delivered value nor a file.</summary>
    private static void CheckRequired(ProvisionedSecrets secrets, IConfiguration configuration, string configPath)
    {
        var missing = new List<string>();
        var lnd = configuration.GetSection(LndGrpcOptions.SectionName).Get<LndGrpcOptions>() ?? new LndGrpcOptions();
        if (lnd.Enabled)
        {
            var directory = lnd.ResolveDataDirectory(configPath);
            if (secrets.LndGrpcTls is null
             && (!File.Exists(Path.Combine(directory, LndTlsFiles.CertificateFileName))
              || !File.Exists(Path.Combine(directory, LndTlsFiles.KeyFileName))))
                missing.Add("lndGrpcTls");
            // The macaroon root key needs no file: a locked start signs locally, so the root keys live in the node's
            // database (NL-1389), and a start that finds neither a stored nor a delivered root key 0 fails closed
            // there (it may not generate one)
        }

        var tor = configuration.GetSection("Node:Tor").Get<TorOptions>() ?? new TorOptions();
        if (tor.IsOnionServiceEnabled && secrets.TorOnionServiceKey is null
                                      && !File.Exists(tor.OnionServiceKeyFile))
            missing.Add("torOnionServiceKey");

        if (missing.Count > 0)
            throw new InvalidDataException("Node:Startup:RequireProvisionedSecrets: deliver "
                                         + string.Join(", ", missing.Select(m => $"secrets.{m}"))
                                         + " (no file holds it and the node may not generate it).");
    }
}