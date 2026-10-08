using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace NLightning.Signer;

using Domain.Protocol.Interfaces;
using Infrastructure.RemoteSigning;

internal sealed record SignerWalletAuthorityConfiguration(string Mode, NativeSignerBinding Binding,
    NativeSignerExecution Execution, string PostgreSqlConnectionFile, string WriterCredentialFile,
    string CoreCredentialsFile, NativeCoreChainEvidenceOptions Core,
    NativeWalletKeyLocator[] Derivations);

internal sealed record SignerCoreCredentials(string Username, string Password);
internal sealed record SignerWalletAuthorityRuntime(NativeAuthorizedSignerExecutor Executor,
    INativeSignerWriterCredentialVerifier WriterCredentials, AuthenticatedNativeCoreChainEvidence Evidence);

/// <summary>Administrator-installed, withdrawal-only authority composition. It never provisions owner authority.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class SignerWalletAuthorityProfile
{
    private readonly string _connectionString;
    private readonly string _writerCredential;
    private readonly SignerCoreCredentials _coreCredentials;
    public SignerWalletAuthorityConfiguration Configuration { get; }
    public string Fingerprint { get; }

    private SignerWalletAuthorityProfile(SignerWalletAuthorityConfiguration configuration,
        string connectionString, string writerCredential, SignerCoreCredentials coreCredentials, string fingerprint)
    {
        Configuration = configuration;
        _connectionString = connectionString;
        _writerCredential = writerCredential;
        _coreCredentials = coreCredentials;
        Fingerprint = fingerprint;
    }

    private static readonly JsonSerializerOptions s_json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static SignerWalletAuthorityProfile Load(string path, SignerDaemonOptions options,
        NativeSignerBinding binding)
    {
        var encoded = ReadPrivate(path, 64 * 1024);
        var configuration = JsonSerializer.Deserialize<SignerWalletAuthorityConfiguration>(encoded, s_json)
            ?? throw new InvalidDataException("Wallet authority profile is missing.");
        if (configuration.Mode != "NativeWalletAuthorityV1")
            throw new InvalidDataException("Wallet authority requires the explicit restricted validation profile.");
        new SignerAuthorityConfiguration(configuration.Binding, configuration.Execution).Validate(binding);
        if (configuration.Core is null || configuration.Derivations is null
         || configuration.Derivations.Length is 0 or > 4096)
            throw new InvalidDataException("Wallet authority evidence and bounded installed derivations are required.");
        var paths = new[] { path, configuration.PostgreSqlConnectionFile, configuration.WriterCredentialFile,
            configuration.CoreCredentialsFile };
        paths = paths.Select(p => Path.IsPathFullyQualified(p) ? Path.GetFullPath(p) : p).ToArray();
        var workerPaths = new[] { options.SocketPath, options.KeyFilePath, options.StateFilePath,
            options.AuthTokenFilePath, options.PasswordFilePath, options.StateFilePath + ".key-index",
            options.StateFilePath + ".nonces", options.StateFilePath + ".swap-sessions",
            options.StateFilePath + ".enrollment", options.StateFilePath + ".authority-profile",
            (options.KeyFilePath ?? options.StateFilePath) + ".lock" };
        workerPaths = workerPaths.Select(p => p is null ? null : Path.GetFullPath(p)).ToArray();
        if (paths.Any(p => !Path.IsPathFullyQualified(p))
         || paths.Distinct(StringComparer.Ordinal).Count() != paths.Length
         || paths.Any(p => workerPaths.Contains(p, StringComparer.Ordinal)))
            throw new InvalidDataException("Authority profile and secrets require separate absolute administrator-installed paths.");
        var database = ReadPrivate(configuration.PostgreSqlConnectionFile, 16 * 1024);
        var writer = ReadPrivate(configuration.WriterCredentialFile, 4096);
        var core = ReadPrivate(configuration.CoreCredentialsFile, 16 * 1024);
        var connectionString = System.Text.Encoding.UTF8.GetString(database).TrimEnd('\r', '\n');
        ValidatePostgreSql(connectionString);
        var writerCredential = System.Text.Encoding.UTF8.GetString(writer).TrimEnd('\r', '\n');
        _ = new NativeSignerWriterCredential(binding, configuration.Execution, writerCredential);
        var credentials = JsonSerializer.Deserialize<SignerCoreCredentials>(core, s_json)
            ?? throw new InvalidDataException("Authenticated Core credentials are missing.");
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        var installedIdentity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            configuration.Mode,
            configuration.Binding,
            configuration.Core,
            configuration.Derivations,
            AuthorityHost = settings.Host,
            AuthorityPort = settings.Port,
            AuthorityDatabase = settings.Database,
            AuthorityUser = settings.Username,
            AuthorityRootCertificate = settings.RootCertificate,
            CoreUser = credentials.Username
        });
        return new SignerWalletAuthorityProfile(configuration, connectionString, writerCredential, credentials,
            Convert.ToHexString(SHA256.HashData(installedIdentity)));
    }

    internal static void ValidatePostgreSql(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (settings.SslMode != SslMode.VerifyFull || string.IsNullOrWhiteSpace(settings.Host)
         || string.IsNullOrWhiteSpace(settings.Database) || string.IsNullOrWhiteSpace(settings.Username)
         || string.IsNullOrWhiteSpace(settings.Password) || settings.PersistSecurityInfo || settings.IncludeErrorDetail
         || settings.Timeout is <= 0 or > 30 || settings.CommandTimeout is <= 0 or > 60)
            throw new InvalidDataException("Authority requires authenticated PostgreSQL with verified TLS and bounded timeouts.");
    }

    public SignerWalletAuthorityRuntime CreateRuntime(ISecureKeyManager keys, DurableSignerState journal,
        NativeSignerSafetyCheckpointSet checkpoints)
    {
        var configuration = Configuration;
        var registry = new NativeSignerWalletScriptRegistry(configuration.Binding, keys, configuration.Derivations);
        var evidence = new AuthenticatedNativeCoreChainEvidence(configuration.Binding, configuration.Core,
            registry, _coreCredentials.Username, _coreCredentials.Password);
        try
        {
            var authority = new NativeSignerAuthority(() => new NpgsqlConnection(_connectionString));
            var executor = CreateExecutor(configuration, authority, journal, checkpoints, registry, evidence);
            return new SignerWalletAuthorityRuntime(executor,
                new NativeSignerWriterCredential(configuration.Binding, configuration.Execution, _writerCredential), evidence);
        }
        catch { evidence.Dispose(); throw; }
    }

    internal static NativeAuthorizedSignerExecutor CreateExecutor(SignerWalletAuthorityConfiguration configuration,
        NativeSignerAuthority authority, DurableSignerState journal, NativeSignerSafetyCheckpointSet checkpoints,
        INativeSignerWalletDerivationRegistry registry, IAuthenticatedNativeChainEvidence evidence) =>
        SignerAuthorityBootstrap.Create(new SignerAuthorityConfiguration(configuration.Binding, configuration.Execution),
            configuration.Binding, authority, journal, checkpoints, evidence,
            installed => new NativeWalletSignerRequestValidator(authority, registry, installed));

    public static void RequireInstalled(string statePath, SignerWalletAuthorityProfile? profile, bool manifest)
    {
        var marker = statePath + ".authority-profile";
        if (!File.Exists(marker) && new FileInfo(marker).LinkTarget is null) return;
        SignerFiles.RequirePrivate(marker);
        if (manifest) return;
        if (profile is null || !File.ReadAllBytes(marker).AsSpan().SequenceEqual(Convert.FromHexString(profile.Fingerprint)))
            throw new InvalidOperationException("Installed authority profile cannot be omitted or replaced.");
    }

    public void InstallMarker(string statePath)
    {
        RequireInstalled(statePath, this, manifest: false);
        var path = statePath + ".authority-profile";
        if (File.Exists(path)) return;
        using (var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        {
            stream.Write(Convert.FromHexString(Fingerprint));
            stream.Flush(true);
        }
        SignerFiles.SyncParentDirectory(path);
    }

    private static byte[] ReadPrivate(string path, int limit)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Administrator-installed paths must be absolute.");
        SignerFiles.RequirePrivate(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > limit) throw new InvalidDataException("Installed authority file size is invalid.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}