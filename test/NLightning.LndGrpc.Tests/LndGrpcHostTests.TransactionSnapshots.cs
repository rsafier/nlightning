using Microsoft.Extensions.DependencyInjection;
using Moq;
using NBitcoin;

namespace NLightning.LndGrpc.Tests;

using Domain.Bitcoin.Events;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Services;

public sealed partial class LndGrpcHostTests
{
    [Fact]
    public void Given_AQueuedConfirmation_When_TheTipRewindsBeforeMapping_Then_ItsCommittedConfirmationRemainsIntact()
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(1), 0));
        transaction.Outputs.Add(Money.Satoshis(40_000), new Key().PubKey.WitHash.ScriptPubKey);
        var snapshot = new WalletTransactionEventArgs(transaction.ToHex(), 40_000, 0, 111,
            new uint256(42).ToString(), DateTimeOffset.UnixEpoch.AddSeconds(123), "", [0u], [], transaction.GetHash().ToString());
        var monitor = Mock.Get(_services!.GetRequiredService<IBlockchainMonitor>());
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(110);

        var mapped = _services!.GetRequiredService<LightningService>().ToTransactionEvent(snapshot);

        Assert.Equal(1, mapped.NumConfirmations);
        Assert.Equal(111, mapped.BlockHeight);
        Assert.Equal(snapshot.BlockHash, mapped.BlockHash);
        Assert.Equal(snapshot.AmountSat, mapped.Amount);
    }
}