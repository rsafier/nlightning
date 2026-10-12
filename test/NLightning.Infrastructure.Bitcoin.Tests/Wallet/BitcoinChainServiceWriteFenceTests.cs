using NBitcoin;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet.Models;
using Domain.Node.Fencing;

/// <summary>
/// NL-1341: every publication of <see cref="Bitcoin.Wallet.BitcoinChainService"/> asks the node write fence first. A
/// refusal sends nothing to bitcoind; a fence that allows it changes nothing.
/// </summary>
public class BitcoinChainServiceWriteFenceTests
{
    [Fact]
    public async Task Given_ARefusingFence_When_ATransactionIsSent_Then_ItIsNotPublished()
    {
        // Arrange
        var fence = new FakeNodeWriteFence { Refuse = true };
        using var node = new FakeRpcNode(_ => Result(new uint256(1).ToString()));
        var service = node.CreateService(writeFence: fence);

        // Act / Assert
        await Assert.ThrowsAsync<NodeFencedException>(() => service.SendTransactionAsync(CreateTransaction(1)));
        Assert.Equal(0, node.Calls("sendrawtransaction"));
        Assert.Equal([NodeEffect.Broadcast], fence.Effects);
    }

    [Fact]
    public async Task Given_AFenceThatAllowsIt_When_ATransactionIsSent_Then_ItIsPublished()
    {
        // Arrange
        var fence = new FakeNodeWriteFence();
        var transaction = CreateTransaction(1);
        using var node = new FakeRpcNode(_ => Result(transaction.GetHash().ToString()));
        var service = node.CreateService(writeFence: fence);

        // Act
        var txId = await service.SendTransactionAsync(transaction);

        // Assert
        Assert.Equal(transaction.GetHash(), txId);
        Assert.Equal(1, node.Calls("sendrawtransaction"));
        Assert.Equal([NodeEffect.Broadcast], fence.Effects);
    }

    [Fact]
    public async Task Given_ARefusingFence_When_APackageIsSubmitted_Then_ItFailsWithoutReachingBitcoind()
    {
        // Arrange
        var fence = new FakeNodeWriteFence { Refuse = true };
        using var node = new FakeRpcNode(_ => Result("{}"));
        var service = node.CreateService(writeFence: fence);

        // Act
        var result = await service.SubmitPackageAsync(CreateTransaction(1), CreateTransaction(2));

        // Assert: a failed call (tried again later), not a verdict of bitcoind
        Assert.Equal(PackageSubmitStatus.Failed, result.Status);
        Assert.Equal(0, node.Calls("submitpackage"));
    }

    [Fact]
    public async Task Given_ARefusingFence_When_ARawPackageIsSubmitted_Then_ItIsNotPublished()
    {
        // Arrange
        var fence = new FakeNodeWriteFence { Refuse = true };
        using var node = new FakeRpcNode(_ => Result("{}"));
        var service = node.CreateService(writeFence: fence);

        // Act / Assert
        await Assert.ThrowsAsync<NodeFencedException>(() =>
            service.SubmitRawPackageAsync([CreateTransaction(1), CreateTransaction(2)], null));
        Assert.Equal(0, node.Calls("submitpackage"));
    }

    private static string Result(string value) =>
        value.StartsWith('{') ? $$"""{"result":{{value}},"error":null,"id":1}"""
                              : $$"""{"result":"{{value}}","error":null,"id":1}""";

    private static Transaction CreateTransaction(byte seed)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat(seed, 32).ToArray()), 0));
        transaction.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
        return transaction;
    }
}