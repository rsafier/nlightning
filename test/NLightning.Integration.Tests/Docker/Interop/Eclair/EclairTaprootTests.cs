using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Abcd;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Messages;
using Fixtures;
using Onchain.Anchors;
using Utils;

/// <summary>
/// Taproot plan T6 against Eclair 0.14.3 (wave t03, lane ECL; NL-877): simple taproot channels
/// (<c>option_simple_taproot</c>, bits 80/81), which Eclair advertises Optional by default and prefers for unannounced
/// channels, with our node on the experimental gate (<c>Features:AllowExperimentalFeatures</c>,
/// <c>Features:OptionSimpleTaproot=Optional</c>). Both opens are dual-funded (<c>open_channel2</c>: both sides offer
/// <c>option_dual_fund</c>, and Eclair refuses a v1 open then, NL-557), so the interactive-tx taproot TLVs of BOLTs
/// PR #1324 (<c>tx_complete</c> <c>commit_nonces</c>) carry the first commitment's nonces. (a) Eclair opens a private
/// taproot channel to us (<c>open channelType=simple_taproot_channel</c>): payments both ways, our restart and
/// Eclair's restart each followed by <c>channel_reestablish</c> with the type-22 nonces and payments both ways, then
/// Eclair's cooperative close (<c>closing_complete</c>/<c>closing_sig</c>, MuSig2). (b) We open one to Eclair
/// (<c>openchannel --channel-type taproot</c>) from a wallet holding only P2TR outputs, so our taproot wallet input is in
/// the funding transaction (sent with its <c>prevtx</c>: Eclair 0.14.3 reads <c>prevtx_details</c>, 1111, but only for
/// a splice of a taproot channel, and never sends it; NL-957); payments both ways, then our cooperative close. (c) A
/// splice in and a splice out of a taproot channel, skipped until splicing a taproot channel lands (NL-965).
/// </summary>
/// <remarks>Run with <c>scripts/run-cluster.sh -n 1 --suite eclair --class
/// NLightning.Integration.Tests.Docker.Interop.Eclair.EclairTaprootTests</c>.</remarks>
[Collection(EclairInteropCollection.Name)]
[Trait("Category", EclairInteropCollection.Category)]
public sealed class EclairTaprootTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 15 * 60 * 1_000;

    /// <summary>Eclair's name of the taproot channel type in its <c>open</c> API.</summary>
    private const string EclairTaprootChannelType = "simple_taproot_channel";

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(120);

    private readonly EclairFixture _fixture;
    private EclairChannelSession? _session;

    public EclairTaprootTests(EclairFixture fixture, ITestOutputHelper output)
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_session is null)
            return;

        if (TestDiagnostics.CurrentTestFailed)
        {
            Console.WriteLine($"[eclair] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
            foreach (var line in _session.Node.NodeLog.TakeLast(400))
                Console.WriteLine(line);
            await _fixture.DumpEclairLogAsync(400);
        }

        await _session.DisposeAsync();
    }

    /// <summary>
    /// (a) Eclair opens a private taproot channel to us (dual-funded, Eclair the only contributor): a taproot channel on
    /// both ends with a P2TR funding output; payments both ways; our node restarts and Eclair restarts, each time we send
    /// <c>channel_reestablish</c> on the new connection, the channel is usable again with the same balance and
    /// payments flow both ways; Eclair's <c>close</c> runs <c>option_simple_close</c> (we sign its
    /// <c>closing_complete</c>), the key-path closing transaction confirms, both ends list the channel closed and our
    /// wallet holds our balance (less the fee if ours is the confirmed transaction).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_EclairOpensAPrivateTaprootChannel_When_PaymentsRestartsAndEclairCloses_Then_AllWork()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act 1: Eclair's open_channel2 with the taproot channel type
        var session = _session = await EclairChannelSession.BuildEclairFundedAsync(
                                     _fixture, "nltg-tap-eclair", s_capacity, ct, EnableTaproot,
                                     channelType: EclairTaprootChannelType);

        // Assert 1
        await AssertTaprootChannelAsync(session, weAreInitiator: false, ct);

        // Act 2: payments both ways (Eclair funded the channel, so it pays first)
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(200_000), ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(30_000), ct);

        // Act 3: our restart
        var mark = session.Sent.Mark;
        var before = await session.GetOurChannelAsync(ct);
        await session.Node.StopAsync();
        await session.StartNodeAsync(ct);

        // Assert 3
        await AssertReestablishedAsync(session, mark, before.LocalBalance, ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(12_000), ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(7_000), ct);

        // Act 4: Eclair's restart
        mark = session.Sent.Mark;
        before = await session.GetOurChannelAsync(ct);
        await _fixture.RestartEclairAsync(ct);

        // Assert 4
        await AssertReestablishedAsync(session, mark, before.LocalBalance, ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(9_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(3_000), ct);

        // Act 5: Eclair closes cooperatively
        var ours = await session.GetOurChannelAsync(ct);
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);
        var answer = await session.Eclair.CloseAsync(session.ChannelIdHex, ct);
        Console.WriteLine($"[eclair] close: {answer?.ToJsonString()}");

        // Assert 5
        await Poll.UntilAsync(() => Task.FromResult(
                                  session.Node.CountLogLines("Signed the peer's closing transaction") >= 1),
                              s_stepTimeout, "we signed Eclair's closing_complete", ct);
        await AssertClosedAsync(session, ours.LocalBalance, walletBefore, ct);
    }

    /// <summary>
    /// (b) We open a private taproot channel to Eclair (<c>openchannel --channel-type taproot</c>, v2 because
    /// <c>option_dual_fund</c> is negotiated) from a wallet holding only P2TR outputs: the funding transaction spends our
    /// taproot wallet output (a one-element key-path witness) and pays a P2TR funding output; payments both ways; our
    /// <c>closechannel</c> runs <c>option_simple_close</c> both ways and the key-path closing transaction confirms.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_WeOpenAPrivateTaprootChannelFromATaprootWallet_When_PaymentsAndWeClose_Then_AllWork()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = _session = await EclairChannelSession.CreateConnectedAsync(
                                     _fixture, "nltg-tap-we", LightningMoney.Satoshis(2_500_000), ct, EnableTaproot,
                                     addressType: AddressType.P2Tr);

        // Act 1: openchannel --channel-type taproot
        OpenChannelClientResponse opened;
        using (var scope = session.Node.Services.CreateScope())
        {
            var handler = scope.ServiceProvider
                               .GetRequiredService<IClientCommandHandler<OpenChannelClientRequest,
                                    OpenChannelClientResponse>>();
            opened = await handler.HandleAsync(new OpenChannelClientRequest(session.EclairAddress, s_capacity)
            {
                IsSimpleTaproot = true
            }, ct);
        }

        session.ChannelId = opened.ChannelId;
        Console.WriteLine($"[nltg] opened {opened.ChannelId} to Eclair");
        await session.MineUntilUsableAsync(ct);

        // Assert 1: a taproot channel funded by our P2TR wallet output
        var fundingTx = await AssertTaprootChannelAsync(session, weAreInitiator: true, ct);
        var keyPathInputs = fundingTx.Inputs.Count(i => i.WitScript.PushCount == 1
                                                     && i.WitScript[0].Length is 64 or 65);
        Console.WriteLine($"[proof] funding {fundingTx.GetHash()}: {fundingTx.Inputs.Count} input(s), "
                        + $"{keyPathInputs} taproot key-path");
        Assert.Equal(fundingTx.Inputs.Count, keyPathInputs);

        // Act 2: payments both ways
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(300_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(40_000), ct);

        // Act 3: our closechannel
        var ours = await session.GetOurChannelAsync(ct);
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);
        CloseChannelClientResponse closed;
        using (var scope = session.Node.Services.CreateScope())
        {
            var handler = scope.ServiceProvider
                               .GetRequiredService<IClientCommandHandler<CloseChannelClientRequest,
                                    CloseChannelClientResponse>>();
            closed = await handler.HandleAsync(new CloseChannelClientRequest(session.ChannelId)
            {
                WaitSeconds = (uint)s_stepTimeout.TotalSeconds
            }, ct);
        }

        // Assert 3
        Console.WriteLine($"[nltg] closechannel: {closed.State}, closing tx {closed.ClosingTxId}");
        Assert.Equal(ChannelState.Closing, closed.State);
        await Poll.UntilAsync(() => Task.FromResult(
                                  session.Node.CountLogLines("The peer signed our closing transaction") >= 1
                               && session.Node.CountLogLines("Signed the peer's closing transaction") >= 1),
                              s_stepTimeout, "closing_complete signed both ways", ct);
        await AssertClosedAsync(session, ours.LocalBalance, walletBefore, ct);
    }

    /// <summary>
    /// (c) A splice in (100,000 sat from our wallet) and then a splice out (50,000 sat to an address of bitcoind's
    /// wallet) of the taproot channel Eclair opened to us: each splice locks on both ends (the shared taproot input
    /// signed with MuSig2, <c>tx_complete</c> <c>funding_nonce</c>), the capacity follows, and payments flow both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs, Skip = "until lane SPL lands (NL-965)")]
    public async Task Given_ATaprootChannel_When_WeSpliceInAndOut_Then_BothLockAndPaymentsFlow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = _session = await EclairChannelSession.BuildEclairFundedAsync(
                                     _fixture, "nltg-tap-splice", s_capacity, ct, EnableTaproot,
                                     channelType: EclairTaprootChannelType);
        await AssertTaprootChannelAsync(session, weAreInitiator: false, ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(200_000), ct);

        // Act 1: splice in
        var spliceIn = await HandleAsync<SpliceInClientRequest, SpliceClientResponse>(
                           session, new SpliceInClientRequest(session.ChannelId, 100_000), ct);
        Console.WriteLine($"[nltg] splicein: {spliceIn.State}, txid {spliceIn.SpliceTxId}, capacity "
                        + $"{spliceIn.NewCapacitySat}, reason {spliceIn.FailureReason}");

        // Assert 1
        Assert.NotNull(spliceIn.SpliceTxId);
        Assert.Equal(1_100_000UL, spliceIn.NewCapacitySat);
        await MineUntilSplicedAsync(session, new uint256((byte[])spliceIn.SpliceTxId.Value), 1_100_000, ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);

        // Act 2: splice out to bitcoind's wallet
        var address = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
        var spliceOut = await HandleAsync<SpliceOutClientRequest, SpliceClientResponse>(
                            session, new SpliceOutClientRequest(session.ChannelId, 50_000)
                            {
                                Address = address.ToString()
                            }, ct);
        Console.WriteLine($"[nltg] spliceout: {spliceOut.State}, txid {spliceOut.SpliceTxId}, capacity "
                        + $"{spliceOut.NewCapacitySat}, reason {spliceOut.FailureReason}");

        // Assert 2
        Assert.NotNull(spliceOut.SpliceTxId);
        Assert.NotNull(spliceOut.NewCapacitySat);
        var spliceOutTxId = new uint256((byte[])spliceOut.SpliceTxId.Value);
        await MineUntilSplicedAsync(session, spliceOutTxId, (long)spliceOut.NewCapacitySat.Value, ct);
        var spliceOutTx = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(spliceOutTxId, true, ct);
        Assert.Contains(spliceOutTx.Outputs, o => o.ScriptPubKey == address.ScriptPubKey
                                               && o.Value == Money.Satoshis(50_000));
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
    }

    private static void EnableTaproot(Domain.Node.Options.NodeOptions options)
    {
        options.Features.AllowExperimentalFeatures = true;
        options.Features.OptionSimpleTaproot = FeatureSupport.Optional;
    }

    private static ChannelModel Model(EclairChannelSession session) =>
        session.Node.ChannelMemoryRepository.TryGetChannel(session.ChannelId, out var channel)
            ? channel
            : throw new InvalidOperationException($"{session.Node.Name} has no channel {session.ChannelId}");

    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(EclairChannelSession session,
                                                                         TRequest request, CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }

    /// <summary>
    /// A private, dual-funded simple taproot channel at our end, a taproot commitment format at Eclair's and a P2TR
    /// funding output of the capacity on chain; returns the funding transaction.
    /// </summary>
    private async Task<Transaction> AssertTaprootChannelAsync(EclairChannelSession session, bool weAreInitiator,
                                                              CancellationToken ct)
    {
        var model = Model(session);
        var theirs = await session.GetEclairChannelAsync(ct);
        var commitments = theirs["data"]?["commitments"];
        Console.WriteLine($"[eclair] channel: {EclairChannelSession.DescribeEclair(theirs)}; params "
                        + $"{commitments?["params"]?.ToJsonString()}");
        Assert.Equal("NORMAL", theirs["state"]!.GetValue<string>());
        Assert.True(model.ChannelParams.OptionSimpleTaproot, "not a simple taproot channel at our end");
        Assert.False(model.ChannelParams.AnnounceChannel);
        Assert.Equal(ChannelVersion.V2, model.Version);
        Assert.Equal(weAreInitiator, (await session.GetOurChannelAsync(ct)).IsInitiator);
        Assert.Contains("taproot", commitments?.ToJsonString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var funding = model.FundingOutput!;
        var fundingTx = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(
                            new uint256((byte[])funding.TransactionId!.Value), true, ct);
        var fundingOutput = fundingTx.Outputs[(int)funding.Index!.Value];
        Assert.Equal((long)s_capacity.Satoshi, fundingOutput.Value.Satoshi);
        Assert.True(fundingOutput.ScriptPubKey.IsScriptType(ScriptType.Taproot),
                    $"funding output {fundingOutput.ScriptPubKey} is not P2TR");
        return fundingTx;
    }

    /// <summary>
    /// We sent <c>channel_reestablish</c> on a new connection (Eclair fails a taproot channel whose reestablish lacks
    /// the type-22 nonces), the channel is usable on both ends, our balance is unchanged and no data loss was seen.
    /// </summary>
    private static async Task AssertReestablishedAsync(EclairChannelSession session, long mark,
                                                       LightningMoney balanceBefore, CancellationToken ct)
    {
        await Poll.UntilAsync(() => session.Sent.CountSent<ChannelReestablishMessage>(session.ChannelId, mark) > 0,
                              EclairChannelSession.UsableTimeout, "we sent channel_reestablish on a new connection",
                              ct);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await session.GetOurChannelAsync(ct);
        Assert.Equal(balanceBefore, after.LocalBalance);
        Assert.False(after.DataLossDetected);
        Assert.Equal(0, session.Node.CountLogLines("Failing channel"));
    }

    /// <summary>
    /// A BOLT 3 simple-close transaction of the funding output in the mempool (version 2, sequence 0xFFFFFFFD) whose only
    /// input is a MuSig2 key-path spend (one 64-byte witness element); after 6 blocks both ends list the channel closed
    /// and our wallet holds our balance, less the fee when we paid it.
    /// </summary>
    private async Task AssertClosedAsync(EclairChannelSession session, LightningMoney ourBalance,
                                         LightningMoney walletBefore, CancellationToken ct)
    {
        var funding = Model(session).FundingOutput!;
        var fundingOutPoint = new OutPoint(new uint256((byte[])funding.TransactionId!.Value), funding.Index!.Value);
        var closingTx = await Poll.ForAsync(async () =>
        {
            foreach (var txid in await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct))
            {
                // Both closing transactions spend the funding output: the one listed may be replaced before it is read
                try
                {
                    var mempoolTx = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(txid, true, ct);
                    if (mempoolTx.Inputs.Any(i => i.PrevOut == fundingOutPoint))
                        return mempoolTx;
                }
                catch (NBitcoin.RPC.RPCException)
                {
                    // Replaced meanwhile
                }
            }

            return null;
        }, s_stepTimeout, "a closing transaction in the mempool", ct);
        var input = Assert.Single(closingTx.Inputs);
        Console.WriteLine($"[proof] closing transaction {closingTx.GetHash()}: version {closingTx.Version}, sequence "
                        + $"{(uint)input.Sequence:X8}, witness {input.WitScript.PushCount} element(s), outputs "
                        + string.Join(", ", closingTx.Outputs.Select(o => o.Value.Satoshi)));
        Assert.Equal(2U, closingTx.Version);
        Assert.Equal(0xFFFFFFFDU, (uint)input.Sequence);
        Assert.Equal(1, input.WitScript.PushCount);
        Assert.Equal(64, input.WitScript[0].Length);
        Assert.Equal(0, session.Node.CountLogLines("closing_signed for channel"));

        await _fixture.MineAndWaitAsync(6, [session.Node], ct);
        await Poll.UntilAsync(async () =>
        {
            var ours = (await session.Node.ListChannelsAsync(ct)).Channels
                                                                   .FirstOrDefault(c => c.ChannelId
                                                                                     == session.ChannelId);
            return ours is null || ours.State == ChannelState.Closed;
        }, s_stepTimeout, "our channel is Closed", ct);
        await Poll.UntilAsync(async () =>
        {
            var channel = await session.Eclair.ChannelAsync(session.ChannelIdHex, ct);
            if (channel?["state"]?.GetValue<string>() == "CLOSED")
                return true;

            var closed = await session.Eclair.ClosedChannelsAsync(session.Node.NodeIdHex, ct);
            return closed.Any(c => c?["channelId"]?.GetValue<string>() == session.ChannelIdHex);
        }, s_stepTimeout, "Eclair lists the channel closed", ct);

        LightningMoney received = LightningMoney.Zero;
        await Poll.UntilAsync(() =>
        {
            received = AnchorsHarness.WalletBalance(session.Node) - walletBefore;
            return Task.FromResult(received.Satoshi + 10_000 >= ourBalance.Satoshi);
        }, s_stepTimeout, "our wallet holds our closing output", ct);
        Console.WriteLine($"[proof] our balance {ourBalance.Satoshi} sat, wallet +{received.Satoshi} sat");
        Assert.True(received <= ourBalance,
                    $"our wallet got {received.Satoshi} sat for a balance of {ourBalance.Satoshi} sat");
    }

    /// <summary>
    /// Mines one block at a time until our channel's funding is <paramref name="spliceTxId"/> with
    /// <paramref name="capacitySat"/> and the channel is usable on both ends again.
    /// </summary>
    private async Task MineUntilSplicedAsync(EclairChannelSession session, uint256 spliceTxId, long capacitySat,
                                             CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + s_stepTimeout;
        while (true)
        {
            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            var ours = await session.GetOurChannelAsync(ct);
            if (ours.FundingTxId is { } funding && new uint256((byte[])funding) == spliceTxId
                                                && ours.Capacity.Satoshi == capacitySat)
                break;

            Assert.True(DateTime.UtcNow < deadline, $"the splice {spliceTxId} did not lock: {ours.Describe()}");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        Console.WriteLine($"[proof] spliced: {await session.DescribeAsync(ct)}");
    }
}