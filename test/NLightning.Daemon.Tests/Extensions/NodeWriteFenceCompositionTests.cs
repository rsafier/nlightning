using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Daemon.Tests.Extensions;

using Daemon.Extensions;
using Domain.Bitcoin.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Node.Fencing;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-1341: the node composition registers no node write fence (a standard node behaves as before), and one a host
/// registers reaches the unit of work, the signer and the chain service.
/// </summary>
public class NodeWriteFenceCompositionTests
{
    [Fact]
    public void Given_NodeServices_When_Composed_Then_NoFenceIsRegisteredAndTheSignerSigns()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(), CreateSecureKeyManager());
        using var provider = services.BuildServiceProvider();

        // Act
        var signature = provider.GetRequiredService<ILightningSigner>().SignNodeMessage(CreateHash());

        // Assert
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(INodeWriteFence));
        Assert.Null(provider.GetService<INodeWriteFence>());
        Assert.Equal(64, ((byte[])signature).Length);
    }

    [Fact]
    public async Task Given_ARegisteredRefusingFence_When_TheNodeSignsSavesAndBroadcasts_Then_EachIsRefused()
    {
        // Arrange
        var fence = new RefusingFence();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(), CreateSecureKeyManager());
        services.AddSingleton<INodeWriteFence>(fence);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Act / Assert: nothing below reaches a key, the database or bitcoind
        Assert.Throws<NodeFencedException>(() =>
            provider.GetRequiredService<ILightningSigner>().SignNodeMessage(CreateHash()));
        await Assert.ThrowsAsync<NodeFencedException>(() =>
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
        await Assert.ThrowsAsync<NodeFencedException>(() =>
            provider.GetRequiredService<IBitcoinChainService>().SendTransactionAsync(Network.RegTest.CreateTransaction()));
        Assert.Equal(1, fence.Saves);
        Assert.Equal([NodeEffect.Sign, NodeEffect.Broadcast], fence.Effects);
    }

    private static Hash CreateHash() => new(Enumerable.Repeat((byte)0x42, 32).ToArray());

    // Node key 1 (public key G), a fresh copy per call as the real key manager hands out
    private static ISecureKeyManager CreateSecureKeyManager()
    {
        var privateKey = new byte[32];
        privateKey[^1] = 1;
        CompactPubKey publicKey = Convert.FromHexString(
            "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetNodePubKey()).Returns(publicKey);
        keyManager.Setup(k => k.GetNodeKeyPair()).Returns(() => new CryptoKeyPair(privateKey.ToArray(), publicKey));
        return keyManager.Object;
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:Network"] = "regtest",
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = "Data Source=:memory:",
            ["Bitcoin:RpcEndpoint"] = "http://127.0.0.1:1",
            ["Bitcoin:RpcUser"] = "user",
            ["Bitcoin:RpcPassword"] = "password",
            ["Bitcoin:Notifications"] = "Poll"
        }).Build();

    private sealed class RefusingFence : INodeWriteFence
    {
        public int Saves { get; private set; }

        public List<NodeEffect> Effects { get; } = [];

        public ValueTask CheckSaveAsync(DbConnection connection, DbTransaction transaction,
                                        CancellationToken cancellationToken)
        {
            Saves++;
            throw new NodeFencedException("fenced");
        }

        public ValueTask CheckEffectAsync(NodeEffect effect, CancellationToken cancellationToken)
        {
            Effects.Add(effect);
            throw new NodeFencedException("fenced");
        }
    }
}