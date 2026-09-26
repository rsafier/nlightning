using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Docker;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Client.Requests;
using Domain.Enums;
using Domain.Money;
using Fixtures;
using Mock;
using TestCollections;
using Utils;

[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class ChannelOpeningFlowTests : IAsyncLifetime
{
    private readonly LightningRegtestNetworkFixture _lightningRegtestNetworkFixture;
    private readonly NLightningTestNode _node;

    public ChannelOpeningFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _lightningRegtestNetworkFixture = fixture;
        Console.SetOut(new TestOutputWriter(output));

        var port = PortPoolUtil.GetAvailablePortAsync().GetAwaiter().GetResult();
        Assert.True(port > 0);
        _node = new NLightningTestNode(fixture, $"nlightning_channel_test_{Guid.NewGuid()}.db",
                                       new FakeSecureKeyManager(), port, options => options.ToSelfDelay = 240);
    }

    public async ValueTask InitializeAsync()
    {
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenSingleP2WPKHInput_WhenHandleAsyncIsCalled_ChannelOpensCorrectly()
    {
        await FundAndOpenAsync([(LightningMoney.FromUnit(1, LightningMoneyUnit.Btc), AddressType.P2Wpkh, false)],
                               LightningMoney.Satoshis(1_000_000));
    }

    [Fact]
    public async Task GivenMultipleP2WPKHInput_WhenHandleAsyncIsCalled_ChannelOpensCorrectly()
    {
        await FundAndOpenAsync([
                                   (LightningMoney.Satoshis(1_100_000), AddressType.P2Wpkh, false),
                                   (LightningMoney.Satoshis(1_100_000), AddressType.P2Wpkh, true)
                               ], LightningMoney.Satoshis(2_100_000));
    }

    [Fact]
    public async Task GivenSingleP2TRInput_WhenHandleAsyncIsCalled_ChannelOpensCorrectly()
    {
        await FundAndOpenAsync([(LightningMoney.FromUnit(1, LightningMoneyUnit.Btc), AddressType.P2Tr, false)],
                               LightningMoney.Satoshis(1_000_000));
    }

    [Fact]
    public async Task GivenMultipleP2TRInput_WhenHandleAsyncIsCalled_ChannelOpensCorrectly()
    {
        await FundAndOpenAsync([
                                   (LightningMoney.Satoshis(1_100_000), AddressType.P2Tr, false),
                                   (LightningMoney.Satoshis(1_100_000), AddressType.P2Tr, true)
                               ], LightningMoney.Satoshis(2_000_000));
    }

    [Fact]
    public async Task GivenMixedInput_WhenHandleAsyncIsCalled_ChannelOpensCorrectly()
    {
        await FundAndOpenAsync([
                                   (LightningMoney.Satoshis(1_100_000), AddressType.P2Wpkh, false),
                                   (LightningMoney.Satoshis(1_100_000), AddressType.P2Tr, false)
                               ], LightningMoney.Satoshis(2_000_000));
    }

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
        _node.DeleteFiles();
        PortPoolUtil.ReleasePort(_node.Port);
        GC.SuppressFinalize(this);
    }

    private async Task FundAndOpenAsync(
        IReadOnlyList<(LightningMoney Amount, AddressType Type, bool IsChange)> deposits,
        LightningMoney fundingAmount)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _lightningRegtestNetworkFixture.Builder!.LNDNodePool!.ReadyNodes
                                                   .First(x => x.LocalAlias == "alice");
        Assert.NotNull(alice);

        foreach (var deposit in deposits)
            await _node.FundWalletAsync(deposit.Amount, deposit.Type, ct, deposit.IsChange);

        var utxoRepository = _node.Services.GetRequiredService<IUtxoMemoryRepository>();
        var balance = utxoRepository.GetConfirmedBalance(_node.BlockchainMonitor.LastProcessedBlockHeight);
        Assert.True(balance > LightningMoney.Zero);

        var aliceAddress = await _node.ConnectToAsync(alice, ct);

        // Act
        var opened = await _node.OpenChannelAsync(new OpenChannelClientRequest(aliceAddress, fundingAmount)
        {
            FeeRatePerKw = LightningMoney.Satoshis(10000)
        }, ct);

        // Assert
        Assert.True(opened.ChannelState is ChannelState.ReadyForThem or ChannelState.ReadyForUs or ChannelState.Open);
        Assert.True(_node.ChannelMemoryRepository.TryGetChannel(opened.ChannelId, out _),
                    "Expected the channel to be registered");
    }
}