using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Daemon.Extensions;
using Daemon.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Persistence.Interfaces;
using Fixtures;
using Onchain.Anchors;
using TestCollections;
using Utils;

/// <summary>
/// The on-chain <c>withdraw</c> (ClientCommand 25) against LND david, through the daemon's client handler: (a) our
/// wallet (a P2WPKH and a P2TR output) pays a new LND address, bitcoind accepts and mines the transaction, LND lists
/// the output and our <c>BroadcastTransactions</c> row is confirmed; (b) with an anchors channel open, "all" sends
/// everything but the anchors reserve, which comes back as change and stays in the wallet, and a further withdrawal
/// that would spend into it is refused.
/// </summary>
/// <remarks>
/// The node registers the withdraw services itself (<see cref="WithdrawIpcServiceExtensions.AddWithdrawIpcServices"/>
/// through <see cref="NLightningTestNode.ConfigureServices"/>; the registration is idempotent, so it holds once the
/// daemon's composition calls it too). Run with the in-container runner (NL-276):
/// <c>-class NLightning.Integration.Tests.Docker.WithdrawFlowTests</c>.
/// </remarks>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class WithdrawFlowTests : IAsyncLifetime
{
    /// <summary>LND's default per-channel anchors reserve, also ours (<c>Node:Anchors:ReservePerChannel</c>).</summary>
    private const long ReserveSat = 10_000;

    private readonly AnchorsHarness _harness;
    private NLightningTestNode? _node;

    public WithdrawFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await _harness.CreateNodeAsync("withdraw", TestContext.Current.CancellationToken,
                                               beforeStart: node => node.ConfigureServices =
                                                                        services => services.AddWithdrawIpcServices());
    }

    [Fact]
    public async Task Given_AFundedWallet_When_WithdrawingToAnLndAddress_Then_LndReceivesTheFundsOnChain()
    {
        // Arrange: 400,000 sat P2WPKH and 300,000 sat P2TR, so 600,000 sat needs both
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        await Node.FundWalletAsync(LightningMoney.Satoshis(400_000), Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        await Node.FundWalletAsync(LightningMoney.Satoshis(300_000), Domain.Bitcoin.Enums.AddressType.P2Tr, ct);
        var address = await NewLndAddressAsync(david, ct);

        // Act
        var response = await WithdrawAsync(new WithdrawClientRequest(address, 600_000, 5), ct);

        // Assert: in bitcoind's mempool, at 5 sat/vB, two inputs, change back to us
        Console.WriteLine($"Withdrawal {Display(response.TxId)}: {response.AmountSat} sat, fee {response.FeeSat} sat, "
                        + $"change {response.ChangeSat} sat, weight {response.Weight}");
        Assert.True(response.Published);
        Assert.Equal(600_000, response.AmountSat);
        Assert.Equal(2, response.InputCount);
        Assert.Equal(1_250, response.FeeRatePerKw);
        Assert.Equal(700_000, response.AmountSat + response.FeeSat + response.ChangeSat);
        Assert.True(response.FeeSat * 1000 >= 1_250L * response.Weight, "below the requested fee rate");
        var txId = new uint256((byte[])response.TxId);
        Assert.Contains(txId, await _harness.Fixture.Bitcoin.GetRawMempoolAsync(ct));
        var mempoolTx = await _harness.Fixture.Bitcoin.GetRawTransactionAsync(txId, true, ct);
        Assert.All(mempoolTx.Inputs, input => Assert.Equal(0xFFFFFFFDu, (uint)input.Sequence));

        // Mined: LND lists the output, our row is Confirmed and our change is in the wallet
        await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [Node], ct);
        var utxo = await Poll.ForAsync(async () => (await david.LightningClient.ListUnspentAsync(
                                                        new ListUnspentRequest { MinConfs = 1, MaxConfs = 1_000 },
                                                        cancellationToken: ct))
                                                  .Utxos.FirstOrDefault(u => u.Address == address),
                                       AnchorsHarness.Timeout, "LND lists the withdrawn output", ct);
        Assert.Equal(600_000, utxo.AmountSat);
        Assert.Equal(txId.ToString(), utxo.Outpoint.TxidStr);
        await Poll.UntilAsync(async () => await GetBroadcastStateAsync(response.TxId) == BroadcastState.Confirmed,
                              AnchorsHarness.Timeout, "our withdrawal row confirmed", ct);
        Assert.Equal(LightningMoney.Satoshis(response.ChangeSat), AnchorsHarness.WalletBalance(Node));
    }

    [Fact]
    public async Task Given_AnAnchorsChannel_When_WithdrawingAll_Then_TheReserveStaysInTheWallet()
    {
        // Arrange: one anchors channel to david (the harness funds 2,000,000 sat; 1,000,000 go to the channel)
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(Node, david, null, ct);
        Assert.True(AnchorsHarness.GetModel(Node, channel.ChannelId).ChannelParams.OptionAnchorOutputs);
        await WaitSpendableAsync(david, LightningMoney.Satoshis(900_000), ct);
        var before = AnchorsHarness.WalletBalance(Node);
        var address = await NewLndAddressAsync(david, ct);

        // Act
        var response = await WithdrawAsync(new WithdrawClientRequest(address, null, null), ct);

        // Assert: everything but the fee and the reserve went out, the reserve came back as change
        Console.WriteLine($"Withdrew all: {response.AmountSat} sat of {before}, fee {response.FeeSat} sat, change "
                        + $"{response.ChangeSat} sat, reserve {response.AnchorReserveSat} sat");
        Assert.True(response.Published);
        Assert.Equal(ReserveSat, response.AnchorReserveSat);
        Assert.Equal(ReserveSat, response.ChangeSat);
        Assert.Equal(before.Satoshi, response.AmountSat + response.FeeSat + response.ChangeSat);
        await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [Node], ct);
        var utxo = await Poll.ForAsync(async () => (await david.LightningClient.ListUnspentAsync(
                                                        new ListUnspentRequest { MinConfs = 1, MaxConfs = 1_000 },
                                                        cancellationToken: ct))
                                                  .Utxos.FirstOrDefault(u => u.Address == address),
                                       AnchorsHarness.Timeout, "LND lists the withdrawn output", ct);
        Assert.Equal(response.AmountSat, utxo.AmountSat);
        Assert.Equal(LightningMoney.Satoshis(ReserveSat), AnchorsHarness.WalletBalance(Node));

        // Once the change backs the reserve (three blocks), spending any of it is refused
        await ChainSync.MineAndWaitAsync(_harness.Fixture, 3, [david], [Node], ct);
        var refusal = await Assert.ThrowsAsync<ClientException>(
                          () => WithdrawAsync(new WithdrawClientRequest(address, 1_000, null), ct));
        Console.WriteLine($"Refused: {refusal.ErrorCode}: {refusal.Message}");
        Assert.Equal(ErrorCodes.NotEnoughBalance, refusal.ErrorCode);
        Assert.Contains("anchors reserve", refusal.Message);
        Assert.Equal(LightningMoney.Satoshis(ReserveSat), AnchorsHarness.WalletBalance(Node));

        // The channel is untouched
        var ours = await Node.GetChannelAsync(channel.ChannelId, ct);
        Assert.True(ours.IsUsable(), "the channel is no longer usable");
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync(["david"]);
        GC.SuppressFinalize(this);
    }

    private async Task<WithdrawClientResponse> WithdrawAsync(WithdrawClientRequest request, CancellationToken ct)
    {
        using var scope = Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<WithdrawClientRequest, WithdrawClientResponse>>();
        return await handler.HandleAsync(request, ct);
    }

    private async Task<BroadcastState?> GetBroadcastStateAsync(TxId txId)
    {
        using var scope = Node.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return (await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId))?.State;
    }

    /// <summary>Mines until the reserve-backing balance (three confirmations, nothing locked) reaches the amount.</summary>
    private Task WaitSpendableAsync(LNDNodeConnection david, LightningMoney amount, CancellationToken ct) =>
        Poll.UntilAsync(async () =>
        {
            var utxos = Node.Services.GetRequiredService<IUtxoMemoryRepository>();
            var available = utxos.GetAvailableConfirmedBalance(Node.BlockchainMonitor.LastProcessedBlockHeight,
                                                               new HashSet<(TxId, uint)>());
            if (available >= amount)
                return true;

            await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [Node], ct);
            return false;
        }, AnchorsHarness.Timeout, $"{amount} spendable", ct);

    private static async Task<string> NewLndAddressAsync(LNDNodeConnection lnd, CancellationToken ct) =>
        (await lnd.LightningClient.NewAddressAsync(new NewAddressRequest { Type = AddressType.WitnessPubkeyHash },
                                                   cancellationToken: ct)).Address;

    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();
}