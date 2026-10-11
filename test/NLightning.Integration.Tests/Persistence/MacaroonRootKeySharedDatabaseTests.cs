using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Node.Provisioning;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Enums;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Repositories;
using LndGrpc.Macaroons;

/// <summary>
/// NL-1389: the LND gRPC macaroon root keys in the node's database, encrypted with the node data key. Two processes
/// (two service providers with their own data directories) over one SQLite file: a root key id deleted by one is never
/// resurrected by a later one holding an older copy of the files, one without the files uses the stored root key 0
/// instead of making a new one, and the files of a node from before are moved in once.
/// </summary>
public sealed class MacaroonRootKeySharedDatabaseTests : IDisposable
{
    private static readonly byte[] s_seed = Enumerable.Repeat((byte)0x6d, 32).ToArray();
    private static readonly byte[] s_fileRootKey = Enumerable.Repeat((byte)0xa1, 32).ToArray();
    private static readonly byte[] s_fileKey7 = Enumerable.Repeat((byte)0xa7, 32).ToArray();

    private readonly SqliteFileTestDatabase _database = new();
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
                // Best effort
            }
        }

        _database.Dispose();
    }

    [Fact]
    public async Task Given_TheFilesOfANodeFromBefore_When_ItStarts_Then_TheyAreMovedIntoTheDatabaseEncrypted()
    {
        // Arrange: macaroons.key and macaroon-root-keys/7.key from before the upgrade
        var directory = DataDirectory(withFiles: true);
        await using var process = BuildProcess();
        var store = Store(process, directory);

        // Act
        var created = store.Initialize();

        // Assert: the same keys, now from the database
        Assert.False(created);
        Assert.True(store.UsesDatabase);
        Assert.Equal(s_fileRootKey, store.TryGet(0));
        Assert.Equal(s_fileKey7, store.TryGet(7));
        Assert.Equal([0ul, 7ul], store.ListIds());
        await using var context = _database.CreateContext();
        var rows = await context.MacaroonRootKeys.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(48, r.Ciphertext.Length));
        Assert.DoesNotContain(rows, r => r.Ciphertext.AsSpan(0, 32).SequenceEqual(s_fileRootKey)
                                      || r.Ciphertext.AsSpan(0, 32).SequenceEqual(s_fileKey7));
    }

    [Fact]
    public async Task Given_AnIdDeletedByOneProcess_When_AReplacementWithAnOlderDirectoryStarts_Then_ItStaysDeleted()
    {
        // Arrange: process A moved the files in and deleted id 7; B has an older copy of the directory (7.key in it)
        var directoryA = DataDirectory(withFiles: true);
        await using (var processA = BuildProcess())
        {
            var storeA = Store(processA, directoryA);
            storeA.Initialize();
            Assert.True(storeA.Delete(7));
        }

        var directoryB = DataDirectory(withFiles: true);
        await using var processB = BuildProcess();

        // Act
        var storeB = Store(processB, directoryB);
        storeB.Initialize();

        // Assert
        Assert.Null(storeB.TryGet(7));
        Assert.Equal([0ul], storeB.ListIds());
        Assert.Equal(s_fileRootKey, storeB.TryGet(0));
    }

    [Fact]
    public async Task Given_AReplacementWithoutTheFiles_When_ItStarts_Then_ItUsesTheStoredRootKeys()
    {
        // Arrange: process A made root key 0 and baked id 9 into the database
        byte[] rootKey;
        byte[] key9;
        await using (var processA = BuildProcess())
        {
            var storeA = Store(processA, DataDirectory(withFiles: false));
            Assert.True(storeA.Initialize());
            rootKey = storeA.TryGet(0)!;
            key9 = storeA.GetOrCreate(9);
        }

        var directoryB = DataDirectory(withFiles: false);
        await using var processB = BuildProcess();

        // Act: B may not make a root key 0 of its own
        var storeB = Store(processB, directoryB);
        var created = storeB.Initialize(allowGenerateDefault: false);

        // Assert: the same keys, and no key file written
        Assert.False(created);
        Assert.Equal(rootKey, storeB.TryGet(0));
        Assert.Equal(key9, storeB.TryGet(9));
        Assert.False(File.Exists(Path.Combine(directoryB, LndMacaroonFiles.RootKeyFileName)));
        Assert.False(Directory.Exists(Path.Combine(directoryB, "macaroon-root-keys")));
    }

    [Fact]
    public async Task Given_NoRootKeyAnywhere_When_GenerationIsRefused_Then_TheStartFailsAndNothingIsStored()
    {
        // Arrange
        await using var process = BuildProcess();
        var store = Store(process, DataDirectory(withFiles: false));

        // Act & Assert
        var refused = Assert.Throws<InvalidOperationException>(() => store.Initialize(allowGenerateDefault: false));
        Assert.Contains("RequireExistingRootKey", refused.Message);
        await using var context = _database.CreateContext();
        Assert.Equal(0, await context.MacaroonRootKeys.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AProvisionedRootKey_When_TheStoreStarts_Then_ItWinsAndTheDatabaseIsNotUsed()
    {
        // Arrange
        await using var process = BuildProcess();
        var provisioned = Enumerable.Repeat((byte)0x99, 32).ToArray();
        var store = Store(process, DataDirectory(withFiles: true),
                          new ProvisionedSecrets { LndGrpcMacaroonRootKey = provisioned });

        // Act
        store.Initialize();

        // Assert
        Assert.False(store.UsesDatabase);
        Assert.Equal(provisioned, store.TryGet(0));
        await using var context = _database.CreateContext();
        Assert.Equal(0, await context.MacaroonRootKeys.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AKeyManagerWithoutNodeDataEncryption_When_TheStoreStarts_Then_TheKeysStayFiles()
    {
        // Arrange: a key manager that cannot encrypt node data (as a VLS signer)
        var keys = new Mock<ISecureKeyManager>();
        keys.Setup(k => k.EncryptNodeData(It.IsAny<NodeDataPurpose>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(),
                                          It.IsAny<byte[]>()))
            .Throws<NotSupportedException>();
        await using var process = BuildProcess(keys.Object);
        var directory = DataDirectory(withFiles: true);
        var store = Store(process, directory);

        // Act
        store.Initialize();

        // Assert
        Assert.False(store.UsesDatabase);
        Assert.Equal(s_fileRootKey, store.TryGet(0));
        Assert.Equal(s_fileKey7, store.TryGet(7));
    }

    private static LndRootKeyStore Store(IServiceProvider process, string directory, ProvisionedSecrets? secrets = null) =>
        new(directory, null, secrets,
            new LndRootKeyDatabase(process.GetRequiredService<IServiceScopeFactory>(),
                                   process.GetRequiredService<ISecureKeyManager>()));

    private string DataDirectory(bool withFiles)
    {
        var directory = Path.Combine(Path.GetTempPath(), "nltg-lnd-grpc-" + Guid.NewGuid().ToString("N"));
        _directories.Add(directory);
        Directory.CreateDirectory(directory);
        if (!withFiles)
            return directory;

        File.WriteAllBytes(Path.Combine(directory, LndMacaroonFiles.RootKeyFileName), s_fileRootKey);
        Directory.CreateDirectory(Path.Combine(directory, "macaroon-root-keys"));
        File.WriteAllBytes(Path.Combine(directory, "macaroon-root-keys", "7.key"), s_fileKey7);
        return directory;
    }

    private ServiceProvider BuildProcess(ISecureKeyManager? keyManager = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(keyManager
                           ?? SecureKeyManager.FromSeed(s_seed.ToArray(), BitcoinNetwork.Regtest, _ => { }));
        services.AddRepositoriesInfrastructureServices();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(_database.CreateContext(),
                                                             NullLogger<UnitOfWork>.Instance, new Sha256(),
                                                             sp.GetRequiredService<Domain.Bitcoin.Interfaces
                                                                                      .IUtxoMemoryRepository>()));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}