using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Fixtures;
using Utils;

/// <summary>
/// NL-379 against LND david: the on-chain reserve of anchors channels (<c>Node:Anchors</c>, 10,000 sat per channel) is
/// kept as fundee and as opener. With an empty wallet we refuse LND's anchors <c>open_channel</c> with an error LND
/// reports, and accept it once the wallet holds the reserve; as opener a funding that would leave less than the reserve
/// after its fee is refused before anything is locked or sent, and a smaller one opens and leaves the reserve in the
/// wallet.
/// </summary>
/// <remarks>Run with <c>ONCHAIN_SUITE=anchors scripts/run-onchain.sh</c>.</remarks>
[Collection(OnchainRegtestCollection.Name)]
[Trait("Category", AnchorsChannelTests.AnchorsCategory)]
public class AnchorsReserveTests : IAsyncLifetime
{
    private const string RefusalLogFragment = "Refusing anchors channel";

    private readonly AnchorsHarness _harness;

    public AnchorsReserveTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Given_AnEmptyWallet_When_LndOpensAnAnchorsChannelToUs_Then_WeRefuseItUntilTheReserveIsFunded()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        var node = await _harness.CreateNodeAsync("anchors-reserve-fundee", ct);
        await _harness.EnsureLndWalletFundedAsync(david, ct);
        await ChainSync.WaitAllAtTipAsync(_harness.Fixture, [david], [node], ct);
        await node.ConnectToAsync(david, ct);

        // Act 1: david opens an anchors channel to our empty wallet
        var refusal = await Assert.ThrowsAsync<RpcException>(async () => await OpenFromDavidAsync(david, node, ct));

        // Assert 1: we refused it for the reserve, LND saw our error, nothing was stored
        Console.WriteLine($"LND's open failed: {refusal.Status.Detail}");
        Assert.Contains("anchors reserve", refusal.Status.Detail);
        Assert.True(node.CountLogLines(RefusalLogFragment) >= 1);
        Assert.Empty((await node.ListChannelsAsync(ct)).Channels);

        // Act 2: the wallet gets more than one channel's reserve, david opens again
        await node.FundWalletAsync(LightningMoney.Satoshis(50_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_harness.Fixture, [david], [node], ct);
        await ReconnectAsync(node, david, ct);
        var point = await OpenFromDavidAsync(david, node, ct);

        // Assert 2: accepted as an anchors channel, which now counts toward our reserve
        Console.WriteLine($"david opened {Convert.ToHexString(point.FundingTxidBytes.ToByteArray())}:{point.OutputIndex}");
        var ours = await Poll.ForAsync(async () =>
        {
            var channels = await node.ListChannelsAsync(ct);
            return channels.Channels.Count == 1 ? channels.Channels[0] : null;
        }, AnchorsHarness.Timeout, "our end of david's channel", ct);
        Assert.True(AnchorsHarness.GetModel(node, ours.ChannelId).ChannelParams.OptionAnchorOutputs);
        var reserve = node.Services.GetRequiredService<IAnchorReserveService>();
        Assert.Equal(10_000, reserve.GetRequiredReserve().Satoshi);
    }

    [Fact]
    public async Task Given_AFundingThatWouldLeaveLessThanTheReserve_When_WeOpen_Then_ItIsRefusedAndASmallerOneOpens()
    {
        // Arrange: 1,005,000 sat; a 1,000,000 sat anchors channel would leave under 5,000 sat after its fee
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        var node = await _harness.CreateNodeAsync("anchors-reserve-opener", ct);
        await _harness.EnsureLndWalletFundedAsync(david, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(1_005_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_harness.Fixture, [david], [node], ct);
        var peerAddress = await node.ConnectToAsync(david, ct);
        var utxos = node.Services.GetRequiredService<IUtxoMemoryRepository>();

        // Act 1
        var refusal = await Assert.ThrowsAsync<ClientException>(
            () => node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress, AnchorsHarness.Capacity)
            {
                FeeRatePerKw = AnchorsHarness.EstimateFeeRatePerKw
            }, ct));

        // Assert 1: refused for the reserve before any output was locked
        Console.WriteLine($"Our open failed: {refusal.Message}");
        Assert.Contains("anchors reserve", refusal.Message);
        Assert.True(utxos.GetLockedBalance().IsZero);
        Assert.Empty((await node.ListChannelsAsync(ct)).Channels);

        // Act 2: 900,000 sat leaves about 105,000 sat of change, more than the reserve
        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress,
                                                                               LightningMoney.Satoshis(900_000))
        {
            FeeRatePerKw = AnchorsHarness.EstimateFeeRatePerKw
        }, ct);
        await ChainSync.MineAndWaitAsync(_harness.Fixture, 3, [david], [node], ct);

        // Assert 2: an anchors channel, and the confirmed change backs its reserve
        Assert.True(AnchorsHarness.GetModel(node, channel.ChannelId).ChannelParams.OptionAnchorOutputs);
        var status = await node.Services.GetRequiredService<IAnchorReserveService>().GetStatusAsync(ct);
        Console.WriteLine($"Reserve {status.RequiredReserve.Satoshi} sat, available {status.AvailableBalance.Satoshi} "
                        + $"sat, {status.AnchorsChannelCount} anchors channel(s)");
        Assert.Equal(10_000, status.RequiredReserve.Satoshi);
        Assert.False(status.IsBelowReserve);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeNodesAsync(["david"]);

    private static async Task<ChannelPoint> OpenFromDavidAsync(LndNodeConnection david, NLightningTestNode node,
                                                               CancellationToken ct) =>
        await david.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom((byte[])node.NodeId),
            LocalFundingAmount = 300_000,
            Private = true,
            CommitmentType = CommitmentType.Anchors
        }, cancellationToken: ct).ResponseAsync.WaitAsync(AnchorsHarness.Timeout, ct);

    /// <summary>
    /// Our <c>error</c> for the refused temporary channel closes the connection: wait for that (up to 10 s), then
    /// connect again.
    /// </summary>
    private static async Task ReconnectAsync(NLightningTestNode node, LndNodeConnection david, CancellationToken ct)
    {
        var davidId = new CompactPubKey(david.LocalNodePubKeyBytes);
        for (var i = 0; i < 20 && node.PeerManager.GetPeer(davidId) is not null; i++)
            await Task.Delay(500, ct);

        if (node.PeerManager.GetPeer(davidId) is null)
            await node.ConnectToAsync(david, ct);
    }
}