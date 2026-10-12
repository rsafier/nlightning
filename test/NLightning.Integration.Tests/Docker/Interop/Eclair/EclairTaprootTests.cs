using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
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
/// a splice of a taproot channel, and never sends it; NL-957); payments both ways, then our cooperative close. (c) Our
/// <c>bumpopen</c> of our taproot open and (d) Eclair's <c>rbfopen</c> of its taproot open (NL-970), each followed by
/// the other end, confirmed, used and closed. Splicing a taproot channel with Eclair is
/// <see cref="EclairTaprootSpliceTests"/> (suite <c>eclair2</c>).
/// </summary>
/// <remarks>Run with <c>scripts/run-cluster.sh -n 1 --suite eclair --class
/// NLightning.Integration.Tests.Docker.Interop.Eclair.EclairTaprootTests</c>.</remarks>
[Collection(EclairInteropCollection.Name)]
[Trait("Category", EclairInteropCollection.Category)]
public sealed class EclairTaprootTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 15 * 60 * 1_000;

    /// <summary>Eclair's name of the taproot channel type in its <c>open</c> API.</summary>
    internal const string EclairTaprootChannelType = "simple_taproot_channel";

    /// <summary>Eclair's <c>remote-rbf-limits.attempt-delta-blocks</c>.</summary>
    private const int EclairRbfDeltaBlocks = 3;

    /// <summary>Our bump of an open: twice the test node's 10 sat/vB estimate, below Eclair's 10x tolerance.</summary>
    private const uint OurBumpFeeRatePerKw = 5_000;

    /// <summary>Eclair's first funding feerate in (d), and its bump.</summary>
    private const long EclairOpenSatByte = 5;

    private const long EclairBumpSatByte = 10;

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
        await AssertTaprootChannelAsync(_fixture, session, weAreInitiator: false, s_capacity, ct);

        // Act 2: payments both ways (Eclair funded the channel, so it pays first)
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(200_000), ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(30_000), ct);

        // Act 3: our restart
        var reestablished = CountReestablished(session);
        var before = await session.GetOurChannelAsync(ct);
        await session.Node.StopAsync();
        await session.StartNodeAsync(ct);

        // Assert 3
        await AssertReestablishedAsync(session, reestablished, before.LocalBalance, ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(12_000), ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(7_000), ct);

        // Act 4: Eclair's restart
        reestablished = CountReestablished(session);
        before = await session.GetOurChannelAsync(ct);
        await _fixture.RestartEclairAsync(ct);

        // Assert 4
        await AssertReestablishedAsync(session, reestablished, before.LocalBalance, ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(9_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(3_000), ct);

        // Act 5: Eclair closes cooperatively
        var ours = await session.GetOurChannelAsync(ct);
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);
        var answer = await session.Eclair.CloseAsync(session.ChannelIdHex, ct);
        Console.WriteLine($"[eclair] close: {answer?.ToJsonString()}");

        // Assert 5
        await Poll.UntilAsync(() => Task.FromResult(
                                  session.Node.CountLogLines("Signed the peer's taproot closing transaction") >= 1),
                              s_stepTimeout, "we signed Eclair's closing_complete", ct);
        await AssertClosedAsync(_fixture, session, ours.LocalBalance, walletBefore, ct);
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
        var fundingTx = await AssertTaprootChannelAsync(_fixture, session, weAreInitiator: true, s_capacity, ct);
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
                                  session.Node.CountLogLines("The peer signed our taproot closing transaction") >= 1
                               && session.Node.CountLogLines("Signed the peer's taproot closing transaction") >= 1),
                              s_stepTimeout, "closing_complete signed both ways", ct);
        await AssertClosedAsync(_fixture, session, ours.LocalBalance, walletBefore, ct);
    }

    /// <summary>
    /// (c) RBF of our taproot dual-funded open (NL-970): our <c>openchannel --channel-type taproot</c> of 1M sat to
    /// Eclair is published unconfirmed; after Eclair's three-block delta our <c>bumpopen</c> at 5,000 sat/kw replaces it
    /// (a new MuSig2 commitment 0 per attempt, the <c>tx_complete</c> nonces bound to the new txid); Eclair follows, the
    /// first attempt leaves the mempool, the replacement confirms as a P2TR funding, payments flow both ways and our
    /// cooperative close confirms.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurUnconfirmedTaprootOpen_When_WeBumpIt_Then_EclairFollowsTheReplacementAndItCloses()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = _session = await EclairChannelSession.CreateConnectedAsync(
                                     _fixture, "nltg-tap-rbf-a", LightningMoney.Satoshis(2_500_000), ct, EnableTaproot);
        var opened = await HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                         session, new OpenChannelClientRequest(session.EclairAddress, s_capacity)
                         {
                             IsSimpleTaproot = true
                         }, ct);
        session.ChannelId = opened.ChannelId;
        Assert.NotNull(opened.FundingTxId);
        var first = Display(opened.FundingTxId.Value);
        Assert.True(Model(session).ChannelParams.OptionSimpleTaproot);
        await WaitInMempoolAsync(first, ct);
        await WaitEclairFundingAsync(session, first, ct);
        await MineEmptyBlocksAsync(session, EclairRbfDeltaBlocks, ct);

        // Act
        var bumped = await HandleAsync<BumpOpenClientRequest, BumpOpenClientResponse>(
                         session, new BumpOpenClientRequest(session.ChannelId, OurBumpFeeRatePerKw), ct);

        // Assert: Eclair follows the replacement, which replaced the first attempt and confirms
        var replacement = Display(bumped.FundingTxId);
        Console.WriteLine($"[proof] our taproot bumpopen replaced {first} with {replacement}");
        Assert.NotEqual(first, replacement);
        await AssertReplacedAsync(first, replacement, ct);
        await WaitEclairFundingAsync(session, replacement, ct);
        await session.MineUntilUsableAsync(ct);
        var fundingTx = await AssertTaprootChannelAsync(_fixture, session, weAreInitiator: true, s_capacity, ct);
        Assert.Equal(replacement, fundingTx.GetHash().ToString());
        Assert.Equal(replacement, EclairJson.FundingTxId(await session.GetEclairChannelAsync(ct)));
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(40_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(15_000), ct);
        await WeCloseAsync(_fixture, session, ct);
    }

    /// <summary>
    /// (d) Eclair's RBF of its taproot dual-funded open to us (NL-970): Eclair opens 1M sat
    /// (<c>simple_taproot_channel</c> at 5 sat/vB, we contribute nothing); after one empty block its <c>rbfopen</c> at
    /// 10 sat/vB replaces the funding; we follow (our partial signature of its new commitment 0 stored with the attempt),
    /// the first attempt leaves the mempool, the replacement confirms, payments flow both ways and Eclair's cooperative
    /// close confirms.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_EclairsUnconfirmedTaprootOpen_When_EclairBumpsIt_Then_WeFollowTheReplacementAndItCloses()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = _session = await EclairChannelSession.CreateConnectedAsync(
                                     _fixture, "nltg-tap-rbf-b", LightningMoney.Satoshis(500_000), ct, EnableTaproot);
        await _fixture.FundEclairWalletAsync(LightningMoney.Satoshis(s_capacity.Satoshi * 2), [session.Node], ct);
        var answer = await session.Eclair.OpenAsync(session.Node.NodeIdHex, (long)s_capacity.Satoshi, ct,
                                                    channelType: EclairTaprootChannelType,
                                                    fundingFeerateSatByte: EclairOpenSatByte);
        Console.WriteLine($"[eclair] Eclair opened to {session.Node.Name}: {answer}");
        session.ChannelId = EclairChannelSession.ParseOpenedChannelId(answer);
        var first = EclairChannelSession.ParseOpenedFundingTxId(answer);
        await WaitInMempoolAsync(first, ct);
        await Poll.UntilAsync(() => Task.FromResult(TryModel(session)?.FundingOutput?.TransactionId is { } txId
                                                 && Display(txId) == first),
                              s_stepTimeout, "our channel on Eclair's first funding", ct);
        Assert.True(Model(session).ChannelParams.OptionSimpleTaproot);
        await MineEmptyBlocksAsync(session, 1, ct);

        // Act
        var rbf = await session.Eclair.RbfOpenAsync(session.ChannelIdHex, EclairBumpSatByte, 20_000, ct);
        Console.WriteLine($"[eclair] rbfopen at {EclairBumpSatByte} sat/vB: {rbf?.ToJsonString()}");

        // Assert
        var replacement = await Poll.ForAsync(async () =>
        {
            var txId = EclairJson.FundingTxId(await session.GetEclairChannelAsync(ct));
            return txId != first ? txId : null;
        }, s_stepTimeout, "Eclair on its replacement funding", ct);
        Console.WriteLine($"[proof] Eclair's taproot rbfopen replaced {first} with {replacement}");
        await Poll.UntilAsync(() => Task.FromResult(TryModel(session)?.FundingOutput?.TransactionId is { } txId
                                                 && Display(txId) == replacement),
                              s_stepTimeout, "our channel on Eclair's replacement", ct);
        await AssertReplacedAsync(first, replacement, ct);
        await session.MineUntilUsableAsync(ct);
        var fundingTx = await AssertTaprootChannelAsync(_fixture, session, weAreInitiator: false, s_capacity, ct);
        Assert.Equal(replacement, fundingTx.GetHash().ToString());
        Assert.Equal(LightningMoney.Zero, Model(session).LocalBalance);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(30_000), ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(10_000), ct);
        await EclairClosesAsync(_fixture, session, ct);
    }

    /// <summary>Our node on the experimental gate with <c>option_simple_taproot</c> Optional.</summary>
    internal static void EnableTaproot(Domain.Node.Options.NodeOptions options)
    {
        options.Features.AllowExperimentalFeatures = true;
        options.Features.OptionSimpleTaproot = FeatureSupport.Optional;
    }

    internal static ChannelModel? TryModel(EclairChannelSession session) =>
        session.Node.ChannelMemoryRepository.TryGetChannel(session.ChannelId, out var channel) ? channel : null;

    internal static ChannelModel Model(EclairChannelSession session) =>
        TryModel(session)
     ?? throw new InvalidOperationException($"{session.Node.Name} has no channel {session.ChannelId}");

    internal static async Task<TResponse> HandleAsync<TRequest, TResponse>(EclairChannelSession session,
                                                                          TRequest request, CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }

    /// <summary>The txid as bitcoind and Eclair print it.</summary>
    internal static string Display(TxId txId) => new uint256((byte[])txId).ToString();

    /// <summary>
    /// A private, dual-funded simple taproot channel at our end, a taproot commitment format at Eclair's and a P2TR
    /// funding output of <paramref name="capacity"/> on chain; returns the funding transaction.
    /// </summary>
    internal static async Task<Transaction> AssertTaprootChannelAsync(EclairFixture fixture,
                                                                      EclairChannelSession session,
                                                                      bool weAreInitiator, LightningMoney capacity,
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
        var fundingTx = await fixture.Bitcoin.Rpc.GetRawTransactionAsync(
                            new uint256((byte[])funding.TransactionId!.Value), true, ct);
        var fundingOutput = fundingTx.Outputs[(int)funding.Index!.Value];
        Assert.Equal((long)capacity.Satoshi, fundingOutput.Value.Satoshi);
        Assert.True(fundingOutput.ScriptPubKey.IsScriptType(ScriptType.Taproot),
                    $"funding output {fundingOutput.ScriptPubKey} is not P2TR");
        return fundingTx;
    }

    /// <summary>The times our log says the channel was reestablished (the node keeps its log across a restart).</summary>
    internal static int CountReestablished(EclairChannelSession session) =>
        session.Node.CountLogLines($"Channel {session.ChannelId} reestablished with peer");

    /// <summary>
    /// The channel was reestablished again on a new connection (we processed Eclair's <c>channel_reestablish</c>,
    /// and Eclair fails a taproot channel whose reestablish lacks the type-22 nonces), the channel is usable on both
    /// ends, our balance is unchanged and no data loss was seen.
    /// </summary>
    internal static async Task AssertReestablishedAsync(EclairChannelSession session, int reestablishedBefore,
                                                        LightningMoney balanceBefore, CancellationToken ct)
    {
        await Poll.UntilAsync(() => Task.FromResult(CountReestablished(session) > reestablishedBefore),
                              EclairChannelSession.UsableTimeout, "the channel reestablished on a new connection",
                              ct);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await session.GetOurChannelAsync(ct);
        Assert.Equal(balanceBefore, after.LocalBalance);
        Assert.False(after.DataLossDetected);
        Assert.Equal(0, session.Node.CountLogLines("Failing channel"));
    }

    /// <summary>
    /// Our <c>closechannel</c> runs <c>option_simple_close</c> both ways and the key-path closing transaction
    /// confirms (<see cref="AssertClosedAsync"/>).
    /// </summary>
    internal static async Task WeCloseAsync(EclairFixture fixture, EclairChannelSession session, CancellationToken ct)
    {
        var ours = await session.GetOurChannelAsync(ct);
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);
        var signedBefore = session.Node.CountLogLines("The peer signed our taproot closing transaction");
        var theirsBefore = session.Node.CountLogLines("Signed the peer's taproot closing transaction");
        var closed = await HandleAsync<CloseChannelClientRequest, CloseChannelClientResponse>(
                         session, new CloseChannelClientRequest(session.ChannelId)
                         {
                             WaitSeconds = (uint)s_stepTimeout.TotalSeconds
                         }, ct);
        Console.WriteLine($"[nltg] closechannel: {closed.State}, closing tx {closed.ClosingTxId}");
        Assert.Equal(ChannelState.Closing, closed.State);
        await Poll.UntilAsync(() => Task.FromResult(
                                  session.Node.CountLogLines("The peer signed our taproot closing transaction")
                                > signedBefore
                               && session.Node.CountLogLines("Signed the peer's taproot closing transaction")
                                > theirsBefore),
                              s_stepTimeout, "closing_complete signed both ways", ct);
        await AssertClosedAsync(fixture, session, ours.LocalBalance, walletBefore, ct);
    }

    /// <summary>
    /// Eclair's <c>close</c> runs <c>option_simple_close</c> (we sign its <c>closing_complete</c>) and the key-path
    /// closing transaction confirms (<see cref="AssertClosedAsync"/>).
    /// </summary>
    internal static async Task EclairClosesAsync(EclairFixture fixture, EclairChannelSession session,
                                                 CancellationToken ct)
    {
        var ours = await session.GetOurChannelAsync(ct);
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);
        var signedBefore = session.Node.CountLogLines("Signed the peer's taproot closing transaction");
        var answer = await session.Eclair.CloseAsync(session.ChannelIdHex, ct);
        Console.WriteLine($"[eclair] close: {answer?.ToJsonString()}");
        await Poll.UntilAsync(() => Task.FromResult(
                                  session.Node.CountLogLines("Signed the peer's taproot closing transaction")
                                > signedBefore),
                              s_stepTimeout, "we signed Eclair's closing_complete", ct);
        await AssertClosedAsync(fixture, session, ours.LocalBalance, walletBefore, ct);
    }

    /// <summary>
    /// A BOLT 3 simple-close transaction of the funding output in the mempool (version 2, sequence 0xFFFFFFFD) whose only
    /// input is a MuSig2 key-path spend (one 64-byte witness element); after 6 blocks both ends list the channel closed
    /// and our wallet holds our balance, less the fee when we paid it.
    /// </summary>
    internal static async Task AssertClosedAsync(EclairFixture fixture, EclairChannelSession session,
                                                 LightningMoney ourBalance, LightningMoney walletBefore,
                                                 CancellationToken ct)
    {
        var funding = Model(session).FundingOutput!;
        var fundingOutPoint = new OutPoint(new uint256((byte[])funding.TransactionId!.Value), funding.Index!.Value);
        var closingTx = await Poll.ForAsync(async () =>
        {
            foreach (var txid in await fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct))
            {
                // Both closing transactions spend the funding output: the one listed may be replaced before it is read
                try
                {
                    var mempoolTx = await fixture.Bitcoin.Rpc.GetRawTransactionAsync(txid, true, ct);
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

        await fixture.MineAndWaitAsync(6, [session.Node], ct);
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

        var received = LightningMoney.Zero;
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
    /// <paramref name="count"/> empty blocks (<c>generateblock</c> without transactions): the unconfirmed funding or
    /// splice stays unconfirmed while the RBF rules' block counts move.
    /// </summary>
    internal static async Task MineEmptyBlocksAsync(EclairFixture fixture, EclairChannelSession session, int count,
                                                    CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            var address = await fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
            await fixture.Bitcoin.Rpc.SendCommandAsync("generateblock", ct, address.ToString(), Array.Empty<string>());
        }

        await fixture.WaitAllAtTipAsync([session.Node], ct);
    }

    private Task MineEmptyBlocksAsync(EclairChannelSession session, int count, CancellationToken ct) =>
        MineEmptyBlocksAsync(_fixture, session, count, ct);

    private async Task WaitEclairFundingAsync(EclairChannelSession session, string txId, CancellationToken ct) =>
        await Poll.UntilAsync(async () => EclairJson.FundingTxId(await session.GetEclairChannelAsync(ct)) == txId,
                              s_stepTimeout, $"Eclair's channel on funding {txId}", ct);

    private async Task WaitInMempoolAsync(string txId, CancellationToken ct) =>
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct))
                                 .Contains(uint256.Parse(txId)),
                              s_stepTimeout, $"{txId} in bitcoind's mempool", ct);

    private async Task AssertReplacedAsync(string replaced, string replacement, CancellationToken ct) =>
        await Poll.UntilAsync(async () =>
        {
            var mempool = await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct);
            return mempool.Contains(uint256.Parse(replacement)) && !mempool.Contains(uint256.Parse(replaced));
        }, s_stepTimeout, $"{replacement} replaced {replaced} in bitcoind's mempool", ct);
}