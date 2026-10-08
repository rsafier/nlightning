using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NBitcoin;
using NLightning.Domain.Crypto.KeyRing;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Persistence.Interfaces;
using NLightning.Infrastructure.Bitcoin.KeyRing;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.RemoteSigning.Tests;

public sealed class RemoteKeyRingPublicTests(SignerDaemonFixture daemon) : IClassFixture<SignerDaemonFixture>
{
    [Theory]
    [InlineData(21, 0)]
    [InlineData(99, 7)]
    [InlineData(42060, 12)]
    public void PublicDerivationMatchesTheNativeKeyWithoutExportingItsPrivateMaterial(int family, int index)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var keys = new RemoteSecureKeyManager(connection);
        var privateBytes = (byte[])daemon.LocalKeys.GetKeyRingKeyAtIndex(family, index);
        try
        {
            var expected = ExtKey.CreateFromBytes(privateBytes).Neuter().PubKey.ToBytes();
            Assert.Equal(expected, (byte[])keys.GetKeyRingPublicKey(family, index));
            Assert.Throws<NotSupportedException>(() => keys.GetKeyRingKeyAtIndex(family, index));
        }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(21, -1)]
    public void ReservedFamiliesAndNegativeIndexesAreRefusedByTheActualSigner(int family, int index)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var keys = new RemoteSecureKeyManager(connection);
        Assert.Throws<ArgumentException>(() => keys.GetKeyRingPublicKey(family, index));
        Assert.Equal(daemon.LocalKeys.GetKeyRingPublicKey(21, 0), keys.GetKeyRingPublicKey(21, 0));
    }

    [Fact]
    public async Task PublicDerivationSurvivesSignerRestart()
    {
        CompactPubKey before;
        using (var connection = new RemoteSignerConnection(daemon.Options()))
            before = new RemoteSecureKeyManager(connection).GetKeyRingPublicKey(99, 17);
        await daemon.RestartAsync();
        using var restarted = new RemoteSignerConnection(daemon.Options());
        Assert.Equal(before, new RemoteSecureKeyManager(restarted).GetKeyRingPublicKey(99, 17));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyRingAllocationUsesOnlyPublicDerivationAndRequiresItsDatabaseSave(bool failSave)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var keys = new RemoteSecureKeyManager(connection);
        var records = new Mock<IKeyRingDbRepository>(MockBehavior.Strict);
        records.Setup(r => r.LastIndexAsync(21)).ReturnsAsync((int?)null);
        KeyRingKey? added = null;
        records.Setup(r => r.Add(It.IsAny<KeyRingKey>())).Callback<KeyRingKey>(key => added = key);
        var uow = new Mock<IUnitOfWork>(MockBehavior.Strict);
        uow.SetupGet(u => u.KeyRingDbRepository).Returns(records.Object);
        uow.Setup(u => u.Dispose());
        var saved = false;
        uow.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            Assert.NotNull(added);
            saved = true;
            return failSave ? Task.FromException(new IOException("Injected save failure.")) : Task.CompletedTask;
        });
        using var services = new ServiceCollection().AddScoped(_ => uow.Object).BuildServiceProvider();
        using var ring = new KeyRingService(keys, services.GetRequiredService<IServiceScopeFactory>(),
                                          Options.Create(new KeyRingOptions()));
        if (failSave)
            await Assert.ThrowsAsync<IOException>(() => ring.DeriveNextAsync(21, TestContext.Current.CancellationToken));
        else
        {
            var issued = await ring.DeriveNextAsync(21, TestContext.Current.CancellationToken);
            Assert.True(saved);
            Assert.Equal(new KeyRingLocator(21, 0), issued.Locator);
            Assert.Equal(keys.GetKeyRingPublicKey(21, 0), issued.PublicKey);
            Assert.Same(added, issued);
        }
        uow.Verify(u => u.SaveChangesAsync(), Times.Once);
    }
}