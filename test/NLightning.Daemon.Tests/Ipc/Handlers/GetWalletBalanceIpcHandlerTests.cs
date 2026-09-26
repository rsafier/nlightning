using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Ipc.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Client.Enums;
using Domain.Money;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Responses;

/// <summary>
/// NL-379: walletbalance reports the anchors reserve and what a channel funding may still spend.
/// </summary>
public class GetWalletBalanceIpcHandlerTests
{
    public GetWalletBalanceIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = NLightningMessagePackOptions.Options;
    }

    [Fact]
    public async Task Given_AnchorsChannels_When_WalletBalance_Then_TheReserveAndSpendableBalanceCrossTheWire()
    {
        // Arrange
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.Setup(m => m.LastProcessedBlockHeight).Returns(200u);
        var utxos = new Mock<IUtxoMemoryRepository>();
        utxos.Setup(u => u.GetConfirmedBalance(200u)).Returns(LightningMoney.Satoshis(85_000));
        utxos.Setup(u => u.GetUnconfirmedBalance(200u)).Returns(LightningMoney.Satoshis(1_000));
        var reserve = new Mock<IAnchorReserveService>();
        reserve.Setup(r => r.GetStatusAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AnchorReserveStatus(2, LightningMoney.Satoshis(20_000),
                                                     LightningMoney.Satoshis(80_000),
                                                     LightningMoney.Satoshis(60_000)));
        var handler = new GetWalletBalanceIpcHandler(monitor.Object, NullLogger<GetWalletBalanceIpcHandler>.Instance,
                                                     utxos.Object, reserve.Object);

        // Act
        var envelope = await handler.HandleAsync(new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.WalletBalance,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = []
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, envelope.Kind);
        var response = MessagePackSerializer.Deserialize<WalletBalanceIpcResponse>(
            envelope.Payload, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(85_000, response.ConfirmedBalance.Satoshi);
        Assert.Equal(1_000, response.UnconfirmedBalance.Satoshi);
        Assert.Equal(20_000, response.AnchorReserve.Satoshi);
        Assert.Equal(2, response.AnchorsChannelCount);
        Assert.Equal(80_000, response.AvailableBalance.Satoshi);
        Assert.Equal(60_000, response.SpendableBalance.Satoshi);
    }
}