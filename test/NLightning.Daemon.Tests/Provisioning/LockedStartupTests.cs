using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NLightning.Daemon.Contracts.Provisioning;
using NLightning.Daemon.Provisioning;
using NLightning.Domain.Protocol.ValueObjects;
using NLightning.Infrastructure.Bitcoin.Managers;
using NLightning.Infrastructure.Persistence.Contexts;
using NLightning.Infrastructure.Persistence.Enums;
using NLightning.Infrastructure.Persistence.Providers;

namespace NLightning.Daemon.Tests.Provisioning;

/// <summary>
/// The locked start (NL-1349): <see cref="LockedStartup"/> over the real node graph and a SQLite file database.
/// </summary>
public sealed class LockedStartupTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly LockedNodeFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task Given_AFreshDatabase_When_TheRightKeyIsDelivered_Then_TheNodeUnlocksAndEnrolls()
    {
        // Arrange
        var keyFile = _files.CreateKeyFile(Password, bip32: true, out var expectedNodeId);
        var provisioner = new QueueKeyProvisioner();
        var attempt = provisioner.Enqueue(keyFile, Password);
        var startup = _files.CreateStartup(provisioner, new ListLogger());

        // Act
        var unlocked = await startup.RunAsync(TestContext.Current.CancellationToken);

        // Assert: accepted, answered with the node id, enrolled, and no key material in the configuration directory
        Assert.NotNull(unlocked);
        using var keyManager = unlocked.KeyManager;
        Assert.Equal(expectedNodeId, Hex(keyManager.GetNodePubKey()));
        var response = await attempt.Response;
        Assert.True(response.Ok);
        Assert.Equal(KeyProvisioningProtocol.UnlockedState, response.State);
        Assert.Equal(expectedNodeId, response.NodeId);
        Assert.Equal(KeyProvisioningProtocol.UnlockedState, startup.Status().State);
        Assert.Equal(expectedNodeId, Hex(await _files.ReadEnrolledNodeKeyAsync()));
        Assert.False(File.Exists(SecureKeyManager.GetKeyFilePath(_files.ConfigPath)));
        _files.AssertNoSecretInConfigDirectory(Password);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_AV2OrV3KeyFile_When_Delivered_Then_ItUnlocksToItsOwnNodeId(bool bip32)
    {
        // Arrange: the same key file read from disk by a normal start gives the reference node id
        var keyFile = _files.CreateKeyFile(Password, bip32, out var expectedNodeId);
        var provisioner = new QueueKeyProvisioner();
        provisioner.Enqueue(keyFile, Password);
        var startup = _files.CreateStartup(provisioner, new ListLogger());

        // Act
        var unlocked = await startup.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        using var keyManager = unlocked!.KeyManager;
        Assert.Equal(expectedNodeId, Hex(keyManager.GetNodePubKey()));
        Assert.Equal(bip32 ? KeyDerivationScheme.Bip32 : KeyDerivationScheme.LegacyGenesisChainCode,
                     keyManager.DerivationScheme);
    }

    [Fact]
    public async Task Given_AWrongPassword_When_Delivered_Then_TheNodeStaysLockedAndNothingIsWritten()
    {
        // Arrange: a node enrolled once; then a wrong password, then the right one
        var keyFile = _files.CreateKeyFile(Password, bip32: true, out var nodeId);
        await _files.UnlockOnceAsync(keyFile, Password);
        var before = _files.SnapshotConfigDirectory();
        var provisioner = new QueueKeyProvisioner();
        var wrong = provisioner.Enqueue(keyFile, "not the password");
        var logger = new ListLogger();
        var startup = _files.CreateStartup(provisioner, logger);
        var run = startup.RunAsync(TestContext.Current.CancellationToken);

        // Act
        var response = await wrong.Response;

        // Assert: refused, still locked, the directory (database included) unchanged, no password in the log
        Assert.False(response.Ok);
        Assert.Equal(KeyProvisioningProtocol.LockedState, response.State);
        Assert.Contains("could not be opened", response.Error);
        Assert.Equal(KeyProvisioningProtocol.LockedState, startup.Status().State);
        Assert.False(run.IsCompleted);
        Assert.Equal(before, _files.SnapshotConfigDirectory());
        Assert.DoesNotContain(logger.Lines, line => line.Contains("not the password") || line.Contains(Password));

        // The right key still unlocks afterwards
        provisioner.Enqueue(keyFile, Password);
        var unlocked = await run;
        using var keyManager = unlocked!.KeyManager;
        Assert.Equal(nodeId, Hex(keyManager.GetNodePubKey()));
    }

    [Fact]
    public async Task Given_ADatabaseEnrolledToAnotherKey_When_AKeyIsDelivered_Then_ItIsRefusedAndNothingIsWritten()
    {
        // Arrange: the database belongs to key A; key B arrives
        var keyA = _files.CreateKeyFile(Password, bip32: true, out var nodeA);
        var keyB = _files.CreateKeyFile(Password, bip32: true, out _);
        await _files.UnlockOnceAsync(keyA, Password);
        var before = _files.SnapshotConfigDirectory();
        var provisioner = new QueueKeyProvisioner();
        var other = provisioner.Enqueue(keyB, Password);
        var startup = _files.CreateStartup(provisioner, new ListLogger());
        var run = startup.RunAsync(TestContext.Current.CancellationToken);

        // Act
        var response = await other.Response;

        // Assert
        Assert.False(response.Ok);
        Assert.Contains("enrolled to another node key", response.Error);
        Assert.False(run.IsCompleted);
        Assert.Equal(before, _files.SnapshotConfigDirectory());
        Assert.Equal(nodeA, Hex(await _files.ReadEnrolledNodeKeyAsync()));

        provisioner.Complete();
        Assert.Null(await run);
    }

    [Fact]
    public async Task Given_AKeyIndexFileOfAnotherKey_When_AKeyIsDelivered_Then_ItIsRefused()
    {
        // Arrange: key A's channel key index file is in the directory
        var keyA = _files.CreateKeyFile(Password, bip32: true, out _);
        var keyB = _files.CreateKeyFile(Password, bip32: true, out _);
        var unlockedA = await _files.UnlockOnceAsync(keyA, Password, keepKey: true);
        using (var keyManagerA = unlockedA!.KeyManager)
            keyManagerA.GetNextChannelKey(out _);
        var provisioner = new QueueKeyProvisioner();
        var other = provisioner.Enqueue(keyB, Password);
        var startup = _files.CreateStartup(provisioner, new ListLogger());
        var run = startup.RunAsync(TestContext.Current.CancellationToken);

        // Act
        var response = await other.Response;

        // Assert
        Assert.False(response.Ok);
        Assert.Contains("key index file belongs to another node key", response.Error);
        provisioner.Complete();
        Assert.Null(await run);
    }

    [Fact]
    public async Task Given_AnUnlockedNode_When_ItReservesChannelKeys_Then_OnlyThePublicIndexIsWrittenAndRestored()
    {
        // Arrange
        var keyFile = _files.CreateKeyFile(Password, bip32: true, out var nodeId);
        var unlocked = await _files.UnlockOnceAsync(keyFile, Password, keepKey: true);

        // Act: two reservations, then a new locked start with the same (unchanged) key file
        using (var keyManager = unlocked!.KeyManager)
        {
            keyManager.GetNextChannelKey(out _);
            keyManager.GetNextChannelKey(out var second);
            Assert.Equal(2U, second);
        }

        var restarted = await _files.UnlockOnceAsync(keyFile, Password, keepKey: true);

        // Assert: the index survives in the public index file, never in a key file
        using var restartedKey = restarted!.KeyManager;
        restartedKey.GetNextChannelKey(out var third);
        Assert.Equal(3U, third);
        var indexFile = KeyIndexFile.Open(KeyIndexFile.GetPath(_files.ConfigPath), "regtest");
        Assert.Equal(nodeId, indexFile.NodePublicKey);
        Assert.Equal(3U, indexFile.LastUsedIndex);
        _files.AssertNoSecretInConfigDirectory(Password);
    }

    [Fact]
    public async Task Given_DeliveredSecrets_When_TheKeyIsAccepted_Then_TheyReplaceTheConfigurationInMemory()
    {
        // Arrange: the configuration names a database that does not exist; the secrets bring the real one
        var keyFile = _files.CreateKeyFile(Password, bip32: true, out _);
        var provisioner = new QueueKeyProvisioner();
        provisioner.Enqueue(keyFile, Password, new KeyProvisioningSecrets
        {
            DatabaseConnectionString = _files.ConnectionString,
            BitcoinRpcUser = "rpc-user",
            BitcoinRpcPassword = "rpc-secret"
        });
        var startup = _files.CreateStartup(provisioner, new ListLogger(),
                                           connectionString: "Data Source=/nonexistent/dir/nltg.db");

        // Act
        var unlocked = await startup.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        using var keyManager = unlocked!.KeyManager;
        Assert.Equal(_files.ConnectionString, unlocked.Configuration["Database:ConnectionString"]);
        Assert.Equal("rpc-user", unlocked.Configuration["Bitcoin:RpcUser"]);
        Assert.Equal("rpc-secret", unlocked.Configuration["Bitcoin:RpcPassword"]);
        Assert.NotNull(await _files.ReadEnrolledNodeKeyAsync());
        _files.AssertNoSecretInConfigDirectory("rpc-secret");
    }

    [Fact]
    public async Task Given_AProvisionerThatEnds_When_NoKeyWasAccepted_Then_RunReturnsNull()
    {
        // Arrange
        var provisioner = new QueueKeyProvisioner();
        provisioner.Complete();
        var startup = _files.CreateStartup(provisioner, new ListLogger());

        // Act
        var unlocked = await startup.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(unlocked);
        Assert.Equal(KeyProvisioningProtocol.LockedState, startup.Status().State);
    }

    private static string Hex(Domain.Crypto.ValueObjects.CompactPubKey key) =>
        Convert.ToHexString((byte[])key).ToLowerInvariant();
}

/// <summary>A configuration directory, an operator directory with key files, and a SQLite node database.</summary>
internal sealed class LockedNodeFiles : IDisposable
{
    private readonly string _root;

    public LockedNodeFiles()
    {
        // Short: the provisioning socket lives under it
        _root = Path.Combine(Path.GetTempPath(), "lk" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)));
        ConfigPath = Path.Combine(_root, "c");
        OperatorPath = Path.Combine(_root, "o");
        Directory.CreateDirectory(ConfigPath);
        Directory.CreateDirectory(OperatorPath);
        ConnectionString = $"Data Source={Path.Combine(ConfigPath, "nltg.db")};Pooling=False";
    }

    public string ConfigPath { get; }
    public string OperatorPath { get; }
    public string ConnectionString { get; }

    public byte[] CreateKeyFile(string password, bool bip32, out string nodeId)
    {
        var path = Path.Combine(OperatorPath, $"{Guid.NewGuid():N}.key.json");
        var network = new BitcoinNetwork("regtest");
        using (var created = bip32
                                 ? SecureKeyManager.CreateNew(network, path, 0)
                                 : new SecureKeyManager(RandomNumberGenerator.GetBytes(32), network, path, 0))
            created.SaveToFile(password);

        // The node id a normal start reads from this file
        using (var reference = SecureKeyManager.FromFilePath(path, network, password))
            nodeId = Convert.ToHexString((byte[])reference.GetNodePubKey()).ToLowerInvariant();
        return File.ReadAllBytes(path);
    }

    public IConfiguration Configuration(string? connectionString = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:Network"] = "regtest",
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = connectionString ?? ConnectionString,
            ["Database:RunMigrations"] = "true"
        }).Build();

    public LockedStartup CreateStartup(IKeyProvisioner provisioner, ILogger logger, string? connectionString = null) =>
        new("regtest", ConfigPath, Configuration(connectionString), provisioner, logger, TimeSpan.Zero);

    public async Task<UnlockedNode?> UnlockOnceAsync(byte[] keyFile, string password, bool keepKey = false)
    {
        var provisioner = new QueueKeyProvisioner();
        provisioner.Enqueue(keyFile, password);
        var unlocked = await CreateStartup(provisioner, new ListLogger()).RunAsync(CancellationToken.None);
        Assert.NotNull(unlocked);
        if (keepKey)
            return unlocked;

        unlocked.KeyManager.Dispose();
        return null;
    }

    public async Task<byte[]?> ReadEnrolledNodeKeyAsync()
    {
        var options = new DbContextOptionsBuilder<NLightningDbContext>().UseSqlite(ConnectionString).Options;
        await using var database = new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite));
        var enrolled = await database.NodeSigningEnrollments.AsNoTracking().SingleOrDefaultAsync();
        return enrolled?.NodePublicKey;
    }

    /// <summary>Every file of the configuration directory with its content hash.</summary>
    public IReadOnlyDictionary<string, string> SnapshotConfigDirectory()
    {
        return Directory.EnumerateFiles(ConfigPath, "*", SearchOption.AllDirectories)
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .ToDictionary(p => Path.GetRelativePath(ConfigPath, p),
                                      p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    }

    /// <summary>No file of the configuration directory holds the secret or an extended private key.</summary>
    public void AssertNoSecretInConfigDirectory(string secret)
    {
        foreach (var path in Directory.EnumerateFiles(ConfigPath, "*", SearchOption.AllDirectories))
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain("tprv", text);
            Assert.DoesNotContain("xprv", text);
            Assert.DoesNotContain("encryptedExtKey", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A provisioner fed by the test.</summary>
internal sealed class QueueKeyProvisioner : IKeyProvisioner
{
    private readonly System.Threading.Channels.Channel<KeyProvisioningAttempt> _attempts =
        System.Threading.Channels.Channel.CreateUnbounded<KeyProvisioningAttempt>();

    public string Description => "test queue";

    public sealed record Pending(Task<KeyProvisioningResponse> Response);

    public Pending Enqueue(byte[] keyFile, string password, KeyProvisioningSecrets? secrets = null)
    {
        var response = new TaskCompletionSource<KeyProvisioningResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _attempts.Writer.TryWrite(new KeyProvisioningAttempt(
                                      new EncryptedKeyFileMaterial(keyFile.ToArray(), password), secrets,
                                      (answer, _) =>
                                      {
                                          response.TrySetResult(answer);
                                          return Task.CompletedTask;
                                      }));
        return new Pending(response.Task);
    }

    public void Complete() => _attempts.Writer.TryComplete();

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<KeyProvisioningAttempt?> NextAttemptAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _attempts.Reader.ReadAsync(cancellationToken);
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Collects every formatted log line (with its exception text).</summary>
internal sealed class ListLogger : ILogger
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
                return _lines.ToArray();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
            _lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}