using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// <see cref="AnchorReserveService.GetStatusAsync"/> takes the admission gate, so a spend that reserved its inputs and
/// then checks the reserve (the on-chain withdraw) can never read the status between an anchors accept's check and its
/// admission: it would count neither the outputs the accept saw nor the channel it admits.
/// </summary>
public class AnchorReserveServiceGateTests
{
    private const uint Height = 200;

    [Fact]
    public async Task Given_AnAcceptBetweenItsCheckAndItsAdmission_When_TheStatusIsRead_Then_ItWaitsAndCountsTheChannel()
    {
        // Arrange: the accept's check blocks inside its read of the pending broadcasts, holding the gate
        var utxos = new FakeWalletUtxoRepository();
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
        utxos.Add(new UtxoModel(new TxId(RandomUtils.GetBytes(32)), 0, LightningMoney.Satoshis(50_000), 100,
                                new WalletAddressModel(AddressType.P2Wpkh, 0, false, address)));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var broadcasts = new Mock<IBroadcastTransactionDbRepository>();
        broadcasts.Setup(b => b.GetPendingAsync()).Returns(async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await proceed.Task;
            }

            return (IReadOnlyList<BroadcastTransactionModel>)[];
        });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.BroadcastTransactionDbRepository).Returns(broadcasts.Object);
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var channelMemory = new Mock<IChannelMemoryRepository>();
        channelMemory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.Setup(m => m.LastProcessedBlockHeight).Returns(Height);
        var reserve = new AnchorReserveService(utxos, channelMemory.Object, monitor.Object, scopeFactory,
                                               Microsoft.Extensions.Options.Options.Create(
                                                   new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest }),
                                               NullLogger<AnchorReserveService>.Instance);

        var accept = reserve.EnsureCanAcceptAnchorsChannelAsync(CreateAnchorsChannel(),
                                                                 TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        var status = reserve.GetStatusAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var completedWhileAdmitting = status.IsCompleted;
        proceed.SetResult();
        await accept;
        var result = await status;

        // Assert
        Assert.False(completedWhileAdmitting);
        Assert.Equal(1, result.AnchorsChannelCount);
        Assert.Equal(10_000, result.RequiredReserve.Satoshi);
    }

    private static ChannelModel CreateAnchorsChannel()
    {
        var pubKey = new CompactPubKey(new Key().PubKey.ToBytes());
        var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
                                     LightningMoney.Satoshis(1), 30, LightningMoney.Satoshis(100_000), 144, null);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, true, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, pubKey, pubKey, pubKey, pubKey, pubKey, pubKey);
        return new ChannelModel(channelParams, new ChannelId(RandomUtils.GetBytes(32)), null, null, false, null, null,
                                LightningMoney.Satoshis(100_000), keySet, 0, 0, LightningMoney.Zero, null, 0, pubKey, 0,
                                ChannelState.V1Opening, ChannelVersion.V1);
    }
}