using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Provisioning;

using Contracts.Provisioning;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Domain.Signing;
using Extensions;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Node;

/// <summary>The key a locked start accepted, and the configuration the node starts with.</summary>
/// <param name="KeyManager">The node key, in memory only.</param>
/// <param name="Configuration">The node configuration with the delivered secrets (NL-1352) laid over it.</param>
public sealed record UnlockedNode(SecureKeyManager KeyManager, IConfiguration Configuration);

/// <summary>
/// The locked start (NL-1349, <c>docs/agents/LOCKED_START.md</c>): waits for key material from an
/// <see cref="IKeyProvisioner"/> and accepts the first key that opens and matches the node's identity. For each
/// attempt it
/// <list type="number">
/// <item>opens the material in memory (an encrypted key file: <see cref="SecureKeyManager.FromKeyFileContent"/>;
/// nothing is written, a version 1 file is not upgraded);</item>
/// <item>refuses a key whose node public key differs from the key index file's (<see cref="KeyIndexFile"/>) or from
/// the database's signing enrollment (<see cref="NodeSigningEnrollmentStore.IsEnrolledToAnotherAsync"/>, read-only);
/// </item>
/// <item>runs the configured migrations and the signing enrollment validation of a normal start (a fresh database
/// enrolls, a database from before enrollment is adopted when its channels are this key's, NL-1340);</item>
/// </list>
/// on a throwaway service provider of the node graph, never started. A refused key leaves the node locked and is
/// answered with the reason; the accepted one is answered with the node id and handed to the caller, which builds and
/// runs the host exactly as a normal start does.
/// </summary>
public sealed class LockedStartup(
    string network,
    string configPath,
    IConfiguration configuration,
    IKeyProvisioner provisioner,
    ILogger logger,
    TimeSpan failureDelay,
    Action<IServiceCollection, IConfiguration, ISecureKeyManager>? addNodeServices = null)
{
    /// <summary>The migration that creates <c>NodeSigningEnrollments</c> (its id per provider ends with it).</summary>
    private const string EnrollmentMigrationSuffix = "_AddNodeSigningEnrollment";

    private volatile string? _nodeId;

    /// <summary>The answer to a status request.</summary>
    public KeyProvisioningResponse Status() => new()
    {
        Ok = true,
        State = _nodeId is null ? KeyProvisioningProtocol.LockedState : KeyProvisioningProtocol.UnlockedState,
        Network = network,
        NodeId = _nodeId
    };

    /// <summary>Waits for an accepted key; null when the provisioner delivers nothing more.</summary>
    public async Task<UnlockedNode?> RunAsync(CancellationToken cancellationToken)
    {
        await provisioner.StartAsync(cancellationToken);
        logger.LogInformation("Node is locked: waiting for its key on {Endpoint}", provisioner.Description);

        while (true)
        {
            using var attempt = await provisioner.NextAttemptAsync(cancellationToken);
            if (attempt is null)
            {
                logger.LogError("The key provisioner ended without delivering an accepted key");
                return null;
            }

            var (unlocked, error) = await TryUnlockAsync(attempt, cancellationToken);
            if (unlocked is not null)
            {
                _nodeId = Convert.ToHexString((byte[])unlocked.KeyManager.GetNodePubKey()).ToLowerInvariant();
                logger.LogInformation("Key accepted for node {NodeId}; starting the node", _nodeId);
                await TryRespondAsync(attempt, Status(), cancellationToken);
                return unlocked;
            }

            logger.LogWarning("Key refused: {Reason}", error);
            await TryRespondAsync(attempt, new KeyProvisioningResponse
            {
                Ok = false,
                State = KeyProvisioningProtocol.LockedState,
                Network = network,
                Error = error
            }, cancellationToken);
            await Task.Delay(failureDelay, cancellationToken);
        }
    }

    private async Task<(UnlockedNode?, string?)> TryUnlockAsync(KeyProvisioningAttempt attempt,
                                                               CancellationToken cancellationToken)
    {
        var nodeConfiguration = ApplySecrets(configuration, attempt.Secrets);

        KeyIndexFile indexFile;
        try
        {
            indexFile = KeyIndexFile.Open(KeyIndexFile.GetPath(configPath), network);
        }
        catch (InvalidDataException e)
        {
            return (null, e.Message);
        }

        SecureKeyManager keyManager;
        try
        {
            keyManager = OpenMaterial(attempt.Material, indexFile);
        }
        catch (Exception e)
        {
            // Our own messages (wrong password: "Decryption failed.", another network, a malformed file) hold no
            // secret; anything else is reported by type only
            var reason = e is System.Security.Cryptography.CryptographicException
                             or System.Runtime.Serialization.SerializationException or FormatException
                             or ArgumentException or NotSupportedException
                      || e.GetType() == typeof(Exception)
                             ? e.Message
                             : e.GetType().Name;
            return (null, $"The key could not be opened: {reason}");
        }

        try
        {
            if (!indexFile.BelongsTo(keyManager.GetNodePubKey()))
                throw new KeyRefusedException(
                    "The key does not match this node: its key index file belongs to another node key.");

            await CheckIdentityAsync(nodeConfiguration, keyManager, cancellationToken);
            return (new UnlockedNode(keyManager, nodeConfiguration), null);
        }
        catch (Exception e) when (e is KeyRefusedException or InvalidOperationException)
        {
            keyManager.Dispose();
            return (null, e.Message);
        }
        catch
        {
            keyManager.Dispose();
            throw;
        }
    }

    private SecureKeyManager OpenMaterial(ProvisionedKeyMaterial material, KeyIndexFile indexFile)
    {
        switch (material)
        {
            case EncryptedKeyFileMaterial keyFile:
                SecureKeyManager? keyManager = null;
                var json = System.Text.Encoding.UTF8.GetString(keyFile.KeyFile);
                keyManager = SecureKeyManager.FromKeyFileContent(
                    json, new BitcoinNetwork(network), keyFile.Password,
                    index => indexFile.Persist(keyManager!.GetNodePubKey(), index), indexFile.LastUsedIndex);
                return keyManager;
            default:
                throw new NotSupportedException($"Unsupported key material {material.GetType().Name}.");
        }
    }

    private async Task CheckIdentityAsync(IConfiguration nodeConfiguration, SecureKeyManager keyManager,
                                          CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        // The check's own lines, and the framework's warnings and errors only (no SQL at Information)
        services.AddLogging(builder => builder.AddProvider(new ForwardingLoggerProvider(logger))
                                              .AddFilter("Microsoft", LogLevel.Warning)
                                              .AddFilter("System", LogLevel.Warning));
        (addNodeServices ?? DefaultAddNodeServices)(services, nodeConfiguration, keyManager);
        await using var provider = services.BuildServiceProvider();

        // 1. Read-only: a database enrolled to another key is refused before anything is written. EF reads its
        // migration history only with dynamic code (a NativeAOT build never runs the node, NL-708)
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<NodeSigningContext>();
            var store = scope.ServiceProvider.GetRequiredService<NodeSigningEnrollmentStore>();
            var database = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();

            // Only a database whose schema has the enrollment table can hold one; a new database, or one from before
            // enrollment, is decided by the checks below
            var applied = await database.Database.GetAppliedMigrationsAsync(cancellationToken);
            if (applied.Any(id => id.EndsWith(EnrollmentMigrationSuffix, StringComparison.Ordinal))
             && await store.IsEnrolledToAnotherAsync(context, cancellationToken))
                throw new KeyRefusedException("The key does not match this node: the database is enrolled to another "
                                            + "node key.");
        }

        // 2. As a normal start: migrations when configured, then the enrollment (enroll, adopt or validate)
        await DatabaseExtensions.MigrateDatabaseIfConfiguredAsync(provider);
        await NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(provider, cancellationToken);
    }

    private static void DefaultAddNodeServices(IServiceCollection services, IConfiguration nodeConfiguration,
                                               ISecureKeyManager keyManager) =>
        services.AddNltgNodeServices(nodeConfiguration, keyManager);

    /// <summary>Lays the delivered secrets over the configuration, in memory only (NL-1352).</summary>
    public static IConfiguration ApplySecrets(IConfiguration configuration, KeyProvisioningSecrets? secrets)
    {
        if (secrets is null)
            return configuration;

        var values = new Dictionary<string, string?>();
        if (secrets.DatabaseConnectionString is { Length: > 0 } connectionString)
            values["Database:ConnectionString"] = connectionString;
        if (secrets.BitcoinRpcUser is { Length: > 0 } rpcUser)
            values["Bitcoin:RpcUser"] = rpcUser;
        if (secrets.BitcoinRpcPassword is { Length: > 0 } rpcPassword)
            values["Bitcoin:RpcPassword"] = rpcPassword;
        if (values.Count == 0)
            return configuration;

        return new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(values).Build();
    }

    private static async Task TryRespondAsync(KeyProvisioningAttempt attempt, KeyProvisioningResponse response,
                                              CancellationToken cancellationToken)
    {
        try
        {
            await attempt.RespondAsync(response, cancellationToken);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client left; the outcome stands
        }
    }

    private sealed class KeyRefusedException(string message) : Exception(message);

    private sealed class ForwardingLoggerProvider(ILogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }
}