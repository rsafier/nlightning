using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Application.Channels.DualFunding;
using Daemon.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Fixtures;
using Utils;

/// <summary>
/// RBF of a dual-funded open against Eclair 0.14.3 (NL-554, lane dfrbf's <c>bumpopen</c>, NL-528, NL-530): (a) we
/// bump the open we started (a plain <c>openchannel</c>, v2 since NL-551) and Eclair follows the replacement; (b) Eclair
/// bumps its own open to us (<c>rbfopen</c>) and we follow; (c) we bump Eclair's open as its accepter, which Eclair
/// accepts (unlike CLN v26.06.8, NL-530: Eclair has no initiator-only rule for <c>tx_init_rbf</c>). Each replacement
/// leaves the first attempt out of bitcoind's mempool, confirms, and the channel carries payments both ways.
/// </summary>
/// <remarks>
/// Eclair accepts a peer's RBF only <c>remote-rbf-limits.attempt-delta-blocks</c> (3) blocks after the previous
/// attempt, so (a) and (c) mine empty blocks (<c>generateblock</c> without transactions) first; our own rule for a
/// peer's RBF (one new block, NL-520) gets one empty block in (b). Run with <c>scripts/run-interop.sh eclair Release
/// -class NLightning.Integration.Tests.Docker.Interop.Eclair.EclairDualFundRbfTests</c>.
/// </remarks>
[Collection(EclairInteropCollection.Name)]
[Trait("Category", EclairInteropCollection.Category)]
public sealed class EclairDualFundRbfTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 10 * 60 * 1_000;

    /// <summary>Eclair's <c>remote-rbf-limits.attempt-delta-blocks</c>.</summary>
    private const int EclairRbfDeltaBlocks = 3;

    /// <summary>Our bump of an open: twice the test node's 10 sat/vB estimate, below Eclair's 10x tolerance.</summary>
    private const uint OurBumpFeeRatePerKw = 5_000;

    /// <summary>Eclair's first funding feerate in (b) and (c), and its bump in (b).</summary>
    private const long EclairOpenSatByte = 5;

    private const long EclairBumpSatByte = 10;

    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(90);

    private readonly EclairFixture _fixture;
    private EclairChannelSession? _session;

    public EclairDualFundRbfTests(EclairFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_session is null)
            return;

        if (DockerDiagnostics.CurrentTestFailed)
        {
            Console.WriteLine($"[eclair] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
            await DockerDiagnostics.DumpContainerLogsAsync([EclairFixture.EclairContainerName], 400);
        }

        await _session.DisposeAsync();
    }

    /// <summary>
    /// (a) Our plain <c>openchannel</c> of 1M sat to Eclair is a v2 open whose funding is published unconfirmed; after
    /// Eclair's three-block delta, our <c>bumpopen</c> at 5,000 sat/kw replaces it: Eclair's channel moves to the
    /// replacement, the first attempt leaves the mempool, the replacement confirms and payments flow both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurUnconfirmedDualFundedOpen_When_WeBumpIt_Then_EclairFollowsAndTheReplacementConfirms()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = _session = await EclairChannelSession.CreateConnectedAsync(
                                     _fixture, "nltg-eclair-df-rbf-a", LightningMoney.Satoshis(2_500_000), ct);
        var opened = await HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                         session, new OpenChannelClientRequest(_fixture.EclairAddress,
                                                               LightningMoney.Satoshis(1_000_000)), ct);
        session.ChannelId = opened.ChannelId;
        Assert.NotNull(opened.FundingTxId);
        var first = Display(opened.FundingTxId.Value);
        Assert.Equal(ChannelVersion.V2, Channel(session).Version);
        await WaitInMempoolAsync(first, ct);
        await WaitEclairFundingAsync(session, first, ct);
        await MineEmptyBlocksAsync(session, EclairRbfDeltaBlocks, ct);

        // Act
        var bumped = await HandleAsync<BumpOpenClientRequest, BumpOpenClientResponse>(
                         session, new BumpOpenClientRequest(session.ChannelId, OurBumpFeeRatePerKw), ct);

        // Assert: Eclair follows the replacement, which replaced the first attempt and confirms
        var replacement = Display(bumped.FundingTxId);
        Console.WriteLine($"[proof] our bumpopen replaced {first} with {replacement}");
        Assert.NotEqual(first, replacement);
        await AssertReplacedAsync(first, replacement, ct);
        await WaitEclairFundingAsync(session, replacement, ct);
        Assert.Equal(replacement, Display(Channel(session).FundingOutput!.TransactionId!.Value));
        await session.MineUntilUsableAsync(ct);
        Assert.Equal(replacement, Display(Channel(session).FundingOutput!.TransactionId!.Value));
        Assert.Equal(replacement, EclairFundingTxId(await session.GetEclairChannelAsync(ct)));
        Assert.Equal(LightningMoney.Satoshis(1_000_000), Channel(session).FundingOutput!.Amount);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(40_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(15_000), ct);
    }

    /// <summary>
    /// (b) Eclair opens 1M sat to us (<c>open_channel2</c> at 5 sat/vB, we contribute nothing); after one empty block
    /// Eclair's <c>rbfopen</c> at 10 sat/vB replaces the funding: we follow the replacement, the first attempt leaves the
    /// mempool, the replacement confirms and payments flow both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_EclairsUnconfirmedDualFundedOpen_When_EclairBumpsIt_Then_WeFollowAndTheReplacementConfirms()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = _session = await EclairChannelSession.CreateConnectedAsync(
                                     _fixture, "nltg-eclair-df-rbf-b", LightningMoney.Satoshis(500_000), ct);
        var first = await EclairOpensAsync(session, 1_000_000, ct);
        await MineEmptyBlocksAsync(session, 1, ct);

        // Act
        var answer = await session.Eclair.RbfOpenAsync(session.ChannelIdHex, EclairBumpSatByte, 20_000, ct);
        Console.WriteLine($"[eclair] rbfopen at {EclairBumpSatByte} sat/vB: {answer?.ToJsonString()}");

        // Assert: Eclair's channel and ours on the replacement
        var replacement = await Poll.ForAsync(async () =>
        {
            var txId = EclairFundingTxId(await session.GetEclairChannelAsync(ct));
            return txId != first ? txId : null;
        }, s_stepTimeout, "Eclair on its replacement funding", ct);
        Console.WriteLine($"[proof] Eclair's rbfopen replaced {first} with {replacement}");
        await Poll.UntilAsync(() => Task.FromResult(Channel(session).FundingOutput?.TransactionId is { } txId
                                                 && Display(txId) == replacement),
                              s_stepTimeout, "our channel on Eclair's replacement", ct);
        await AssertReplacedAsync(first, replacement, ct);
        await session.MineUntilUsableAsync(ct);
        var ours = Channel(session);
        Assert.Equal(replacement, Display(ours.FundingOutput!.TransactionId!.Value));
        Assert.False(ours.IsInitiator);
        Assert.Equal(LightningMoney.Zero, ours.LocalBalance);
        Assert.Equal(LightningMoney.Satoshis(1_000_000), ours.FundingOutput.Amount);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(30_000), ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(10_000), ct);
    }

    /// <summary>
    /// (c) Eclair opens 500k sat to us (<c>open_channel2</c> at 5 sat/vB) and we contribute 200k sat; after Eclair's
    /// three-block delta, our <c>bumpopen</c> as the accepter (we are the interactive-tx initiator of the new attempt and
    /// pay its shared fields) replaces the funding at 5,000 sat/kw. Eclair accepts it (no initiator-only rule, unlike
    /// CLN v26.06.8, NL-530) and follows; both shares are in the confirmed replacement and payments flow both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_EclairsUnconfirmedDualFundedOpen_When_WeBumpItAsTheAccepter_Then_EclairFollows()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = _session = await EclairChannelSession.CreateConnectedAsync(
                                     _fixture, "nltg-eclair-df-rbf-c", LightningMoney.Satoshis(1_000_000), ct,
                                     configureNode: n => n.ConfigureServices = services =>
                                         services.Configure<DualFundingOptions>(o => o.AcceptContributionSat = 200_000));
        var first = await EclairOpensAsync(session, 500_000, ct);
        Assert.Equal(LightningMoney.Satoshis(700_000), Channel(session).FundingOutput!.Amount);
        await MineEmptyBlocksAsync(session, EclairRbfDeltaBlocks, ct);

        // Act
        var bumped = await HandleAsync<BumpOpenClientRequest, BumpOpenClientResponse>(
                         session, new BumpOpenClientRequest(session.ChannelId, OurBumpFeeRatePerKw), ct);

        // Assert
        var replacement = Display(bumped.FundingTxId);
        Console.WriteLine($"[proof] our bumpopen as the accepter replaced {first} with {replacement}");
        Assert.NotEqual(first, replacement);
        await AssertReplacedAsync(first, replacement, ct);
        await WaitEclairFundingAsync(session, replacement, ct);
        await session.MineUntilUsableAsync(ct);
        var ours = Channel(session);
        Assert.Equal(replacement, Display(ours.FundingOutput!.TransactionId!.Value));
        Assert.Equal(replacement, EclairFundingTxId(await session.GetEclairChannelAsync(ct)));
        Assert.False(ours.IsInitiator);
        Assert.Equal(LightningMoney.Satoshis(200_000), ours.LocalBalance);
        Assert.Equal(LightningMoney.Satoshis(700_000), ours.FundingOutput.Amount);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(30_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
    }

    /// <summary>
    /// Eclair opens <paramref name="capacitySat"/> to the session's node at <see cref="EclairOpenSatByte"/> from a
    /// funded wallet; returns the first funding txid once it is in the mempool and our channel is on it.
    /// </summary>
    private async Task<string> EclairOpensAsync(EclairChannelSession session, long capacitySat, CancellationToken ct)
    {
        await _fixture.FundEclairWalletAsync(LightningMoney.Satoshis((ulong)capacitySat * 2), [session.Node], ct);
        var answer = await session.Eclair.OpenAsync(session.Node.NodeIdHex, capacitySat, ct,
                                                    fundingFeerateSatByte: EclairOpenSatByte);
        Console.WriteLine($"[eclair] Eclair opened to {session.Node.Name}: {answer}");
        session.ChannelId = EclairChannelSession.ParseOpenedChannelId(answer);
        var first = EclairChannelSession.ParseOpenedFundingTxId(answer);
        await WaitInMempoolAsync(first, ct);
        await Poll.UntilAsync(() => Task.FromResult(TryChannel(session)?.FundingOutput?.TransactionId is { } txId
                                                 && Display(txId) == first),
                              s_stepTimeout, "our channel on Eclair's first funding", ct);
        Assert.Equal(ChannelVersion.V2, Channel(session).Version);
        return first;
    }

    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(EclairChannelSession session,
                                                                         TRequest request, CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }

    private async Task WaitEclairFundingAsync(EclairChannelSession session, string txId, CancellationToken ct) =>
        await Poll.UntilAsync(async () => EclairFundingTxId(await session.GetEclairChannelAsync(ct)) == txId,
                              s_stepTimeout, $"Eclair's channel on funding {txId}", ct);

    private async Task WaitInMempoolAsync(string txId, CancellationToken ct) =>
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct)).Contains(uint256.Parse(txId)),
                              s_stepTimeout, $"{txId} in bitcoind's mempool", ct);

    private async Task AssertReplacedAsync(string replaced, string replacement, CancellationToken ct) =>
        await Poll.UntilAsync(async () =>
        {
            var mempool = await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct);
            return mempool.Contains(uint256.Parse(replacement)) && !mempool.Contains(uint256.Parse(replaced));
        }, s_stepTimeout, $"{replacement} replaced {replaced} in bitcoind's mempool", ct);

    /// <summary>
    /// <paramref name="count"/> empty blocks (<c>generateblock</c> without transactions): the funding stays unconfirmed
    /// while the RBF rules' block counts move.
    /// </summary>
    private async Task MineEmptyBlocksAsync(EclairChannelSession session, int count, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            var address = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
            await _fixture.Bitcoin.Rpc.SendCommandAsync("generateblock", ct, address.ToString(), Array.Empty<string>());
        }

        await _fixture.WaitAllAtTipAsync([session.Node], ct);
    }

    private static string? EclairFundingTxId(JsonNode eclair) => EclairJson.FundingTxId(eclair);

    /// <summary>The txid as bitcoind and Eclair print it.</summary>
    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();

    private static ChannelModel? TryChannel(EclairChannelSession session) =>
        session.Node.Services.GetRequiredService<IChannelMemoryRepository>().TryGetChannel(session.ChannelId,
                                                                                          out var channel)
            ? channel
            : null;

    private static ChannelModel Channel(EclairChannelSession session) =>
        TryChannel(session) ?? throw new InvalidOperationException($"{session.Node.Name} has no channel "
                                                                 + session.ChannelId);
}