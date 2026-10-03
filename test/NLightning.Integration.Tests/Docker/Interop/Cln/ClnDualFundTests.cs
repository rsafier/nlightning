using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Abcd;
using Application.Channels.DualFunding;
using Application.InteractiveTx;
using Domain.Bitcoin.Enums;
using Domain.Channels.DualFunding;
using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.ValueObjects;
using Fixtures;
using Fixtures.Cln;
using Utils;

/// <summary>
/// Proof DF of the splicing plan (<c>docs/agents/SPLICING_PLAN.md</c> "Optional wave DF", NL-037): BOLT 2 "Channel
/// Establishment v2" (<c>open_channel2</c>/<c>accept_channel2</c>, <c>option_dual_fund</c> 28/29) against Core
/// Lightning v26.06.8: CLN opens a dual-funded channel to us with our contribution, we open one to CLN and CLN matches
/// ours (<c>--funder-policy=match</c>), and we RBF our unconfirmed open (CLN may change its contribution in its
/// <c>tx_ack_rbf</c>, NL-521) through the daemon's <c>bumpopen</c>. Lane dfrbf (RBF on by default, NL-528): CLN RBFs
/// its own open to us (<c>openchannel_bump</c>/<c>openchannel_update</c>/<c>openchannel_signed</c> over the inputs
/// it reserved for the first attempt) and we follow the replacement; and after our bump the <b>first</b> attempt is
/// mined instead (<c>generateblock</c> with its raw transaction from our broadcast row), and both nodes follow it.
/// NL-530: our <c>bumpopen</c> as the accepter of CLN's open is refused by CLN v26.06.8 with
/// <c>tx_abort</c> "Only the channel initiator is allowed to initiate RBF" (BOLT 2 lets the accepter send
/// <c>tx_init_rbf</c>, and the recipient "MAY fail the negotiation for any reason"); the open stays on its first
/// funding and confirms. Each channel is used for payments both ways.
/// </summary>
/// <remarks>
/// <para>CLN v26.06.8 advertises <c>option_dual_fund</c> only with <c>--experimental-dual-fund</c> (checked with
/// <c>lightningd --help</c> on the pinned image), so every test runs a second CLN, <c>nltg-cln-df</c>, on the fixture's
/// bitcoind (<see cref="ClnFixture.StartClnAsync"/>, a pod in the collection's run namespace) with that option and the
/// funder plugin's <c>match</c> policy at 100 %. The fixture's own CLN is left alone. Our node runs with <c>Features:AllowExperimentalFeatures</c> and <c>DualFund = Optional</c> (the feature
/// stays experimental until this proof is accepted, plan DF3) and registers the dual-funding services itself through
/// <see cref="NLightningTestNode.ConfigureServices"/> until the integrator adds <c>AddDualFundingServices()</c> to the
/// node composition.</para>
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnDualFundTests(ClnFixture fixture) : IAsyncLifetime
{
    private const string ClnName = "nltg-cln-df";

    private static readonly TimeSpan s_usableTimeout = TimeSpan.FromMinutes(3);

    private readonly List<NLightningTestNode> _nodes = [];
    private ExtraClnNode? _clnNode;

    private ClnClient _cln = null!;
    private string ClnAddress => _clnNode!.Address;
    private CompactPubKey ClnPubKey => Convert.FromHexString(_clnNode!.NodeId);

    public async ValueTask InitializeAsync()
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _clnNode = await fixture.StartClnAsync(new ClnNodeSpec(ClnName)
        {
            // The fixture CLN's flags without --ignore-fee-limits=false, plus dual funding and the funder plugin
            EnforceFeeLimits = false,
            ExtraArgs =
            [
                "--experimental-dual-fund",
                // As opener CLN v26.06.8's lightningd tells dualopend the funding is locked at its own
                // funding-confirms (1 on regtest) while dualopend asserts the accepter's minimum_depth (ours: 3) is
                // reached, and dies (openingd/dualopend.c handle_funding_depth): keep the two equal
                "--funding-confirms=3", "--funder-lease-requests-only=false", "--funder-policy=match",
                "--funder-policy-mod=100", "--funder-min-their-funding=10000sat"
            ]
        }, CancellationToken.None);
        _cln = _clnNode.Client;
        try
        {
            // CLN's wallet: two confirmed outputs, so it can contribute and still fund its own open
            for (var i = 0; i < 2; i++)
                await FundClnAsync(LightningMoney.Satoshis(1_500_000), CancellationToken.None);
        }
        catch
        {
            // A failed setup (e.g. the fixture's bitcoind gone) must not leave nltg-cln-df running
            await _clnNode.DisposeAsync();
            _clnNode = null;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
            await node.DisposeAsync();

        if (_clnNode is null)
            return;

        try
        {
            Console.WriteLine("[cln-df] UNUSUAL/BROKEN: "
                            + await _cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 40, "unusual"));
            if (TestDiagnostics.CurrentTestFailed)
            {
                // The channel's daemons (dualopend, channeld; not onchaind) and the funder plugin's decisions
                var entries = (await _cln.CallAsync("getlog", CancellationToken.None, ("level", "debug")))["log"]!
                   .AsArray();
                var log = entries.Select(e => $"{e?["time"]} {e?["source"]}: {e?["log"]}")
                                 .Where(l => l.Contains("chan#", StringComparison.Ordinal)
                                          && !l.Contains("onchaind", StringComparison.Ordinal))
                                 .Take(250);
                Console.WriteLine("[cln-df] channel log:" + Environment.NewLine + string.Join(Environment.NewLine, log));
                Console.WriteLine("[cln-df] funder log: "
                                + await _cln.GetLogLinesAsync("funder", CancellationToken.None, 40, "debug"));
            }
        }
        catch
        {
            // Best effort
        }

        await _clnNode.DisposeAsync();
    }

    [Fact]
    public async Task Given_ClnOpensDualFunded_When_WeContribute_Then_TheChannelIsNormalAndCarriesPaymentsBothWays()
    {
        // Arrange: our node contributes 200,000 sat to a peer's v2 open
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("df-accepter", 200_000, ct);

        // Act: CLN opens 500,000 sat to us (fundchannel uses open_channel2 when both offer option_dual_fund)
        var opened = await _cln.CallAsync("fundchannel", ct, ("id", node.NodeIdHex), ("amount", "500000"),
                                          ("announce", "false"));
        var channelIdHex = opened["channel_id"]!.GetValue<string>();
        Console.WriteLine($"[cln-df] CLN fundchannel: {opened.ToJsonString()}");
        var channelId = new ChannelId(Convert.FromHexString(channelIdHex));
        await MineUntilUsableAsync(node, channelId, ct);

        // Assert: the v2 channel id from both revocation basepoints, both contributions in the channel
        var ours = Channel(node, channelId);
        Assert.Equal(ChannelVersion.V2, ours.Version);
        Assert.False(ours.IsInitiator);
        AssertV2ChannelId(ours);
        Assert.Equal(LightningMoney.Satoshis(200_000), ours.LocalBalance);
        Assert.Equal(LightningMoney.Satoshis(700_000), ours.FundingOutput!.Amount);
        var theirs = await _cln.GetPeerChannelAsync(node.NodeIdHex, channelIdHex, ct);
        Assert.Equal("CHANNELD_NORMAL", theirs!["state"]!.GetValue<string>());
        Assert.Equal(700_000_000L, theirs["total_msat"]!.GetValue<long>());
        Assert.Equal("local", theirs["opener"]!.GetValue<string>());

        // Act & Assert: payments both ways
        await PayBothWaysAsync(node, ct);
    }

    [Fact]
    public async Task Given_WeOpenDualFunded_When_ClnMatches_Then_TheChannelIsNormalAndCarriesPaymentsBothWays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("df-opener", 0, ct);

        // Act: openchannel --dual-fund through the daemon's client handler, 400,000 sat of ours
        var channelId = await OpenThroughClientAsync(node, LightningMoney.Satoshis(400_000), ct);
        await MineUntilUsableAsync(node, channelId, ct);

        // Assert: CLN matched our 400,000 sat (funder-policy=match, 100 %)
        var ours = Channel(node, channelId);
        Assert.Equal(ChannelVersion.V2, ours.Version);
        Assert.True(ours.IsInitiator);
        AssertV2ChannelId(ours);
        Assert.Equal(LightningMoney.Satoshis(400_000), ours.LocalBalance);
        Assert.Equal(LightningMoney.Satoshis(800_000), ours.FundingOutput!.Amount);
        var theirs = await _cln.GetPeerChannelAsync(node.NodeIdHex, channelId.ToString(), ct);
        Assert.Equal("CHANNELD_NORMAL", theirs!["state"]!.GetValue<string>());
        Assert.Equal("remote", theirs["opener"]!.GetValue<string>());
        Assert.Equal(800_000_000L, theirs["total_msat"]!.GetValue<long>());

        await PayBothWaysAsync(node, ct);
    }

    /// <summary>
    /// NL-776: CLN v26.06.8 puts a P2TR script in <c>accept_channel2</c> and <c>shutdown</c> of a dual-funded channel,
    /// a form BOLT 2 allows only with <c>option_shutdown_anysegwit</c>; with our default features (the option
    /// advertised) we keep it at the open, accept CLN's <c>shutdown</c>, and the cooperative close confirms.
    /// </summary>
    [Fact]
    public async Task Given_OurDualFundedChannel_When_WeCloseCooperatively_Then_ClnsP2TrScriptIsPaidAndBothEndsClose()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("df-close", 0, ct);
        var channelId = await OpenThroughClientAsync(node, LightningMoney.Satoshis(400_000), ct);
        await MineUntilUsableAsync(node, channelId, ct);

        // Act
        CloseChannelClientResponse closed;
        using (var scope = node.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<
                Daemon.Interfaces.IClientCommandHandler<CloseChannelClientRequest, CloseChannelClientResponse>>();
            closed = await handler.HandleAsync(new CloseChannelClientRequest(channelId)
            {
                WaitSeconds = 90
            }, ct);
        }

        // Assert: CLN's shutdown script is a segwit v1 program (P2TR) and the closing transaction pays it
        Assert.Equal(ChannelState.Closing, closed.State);
        Assert.NotNull(closed.ClosingTxId);
        var remoteScript = (byte[])Channel(node, channelId).RemoteShutdownScript!.Value;
        Console.WriteLine($"[cln-df] CLN's shutdown script {Convert.ToHexStringLower(remoteScript)}");
        Assert.Equal(0x51, remoteScript[0]);
        await WaitInMempoolAsync(closed.ClosingTxId.Value, ct);
        await MineAndWaitAsync(node, 6, ct);
        await Poll.UntilAsync(async () =>
        {
            var ours = (await node.ListChannelsAsync(ct)).Channels.FirstOrDefault(c => c.ChannelId == channelId);
            return ours is null || ours.State == ChannelState.Closed;
        }, TimeSpan.FromSeconds(90), "our channel is Closed", ct);
        await Poll.UntilAsync(async () =>
        {
            var state = (await _cln.GetPeerChannelAsync(node.NodeIdHex, channelId.ToString(), ct))?["state"]
                          ?.GetValue<string>();
            return state is "ONCHAIN" or "CLOSED";
        }, TimeSpan.FromSeconds(90), "CLN sees the mutual close on chain", ct);
    }

    [Fact]
    public async Task Given_OurUnconfirmedDualFundedOpen_When_WeBumpIt_Then_ClnFollowsTheReplacementAndItConfirms()
    {
        // Arrange: our open, not mined. CLN's funder matches only a funding feerate inside its own acceptable range
        // (plugins/funder.c: "their feerate ... is out of range", from CLN's estimates; 253..2,530 sat/kw on the idle
        // regtest, higher once earlier classes of a full run mined fee-paying transactions), so both feerates are
        // chosen inside the range CLN reports now. Whether CLN contributes to each attempt is still its own decision
        // (NL-521: in a wave d13 full run it matched only the RBF, and BOLT 2 lets it change its contribution in
        // tx_ack_rbf), so the channel is checked against what CLN says it put in.
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("df-rbf", 0, ct);
        var service = node.Services.GetRequiredService<IDualFundedOpenService>();
        var (minimum, maximum) = await GetClnAcceptableFeerateRangeAsync(ct);
        var openFeerate = Math.Clamp(1_000u, minimum, maximum);
        var bumpFeerate = Math.Max(Math.Min(2 * openFeerate, maximum),
                                   (uint)InteractiveTxDriver.GetMinimumRbfFeeratePerKw(openFeerate));
        Console.WriteLine($"[cln-df] CLN accepts {minimum}..{maximum} sat/kw: open at {openFeerate}, bump at "
                        + $"{bumpFeerate}");
        var first = await service.OpenAsync(new DualFundedOpenRequest(ClnPubKey, LightningMoney.Satoshis(300_000),
                                                                      openFeerate), ct);
        Assert.True(first.FailureReason is null, first.FailureReason);
        await WaitInMempoolAsync(first.FundingTxId!.Value, ct);
        var firstCapacity = Channel(node, first.ChannelId).FundingOutput!.Amount;

        // Act: bumpopen through the daemon's client handler (RBF is on by default since lane dfrbf)
        var bumped = await BumpThroughClientAsync(node, first.ChannelId, bumpFeerate, ct);

        // Assert: a new funding transaction that CLN follows, in the mempool instead of the first
        Assert.NotEqual(first.FundingTxId, bumped.FundingTxId);
        await WaitInMempoolAsync(bumped.FundingTxId, ct);
        var theirs = await _cln.GetPeerChannelAsync(node.NodeIdHex, first.ChannelId.ToString(), ct);
        Console.WriteLine($"[cln-df] CLN after the RBF: {theirs?.ToJsonString()}");
        Assert.Equal(TxIdDisplay(bumped.FundingTxId), theirs!["funding_txid"]!.GetValue<string>());

        // ...with the capacity CLN reports: our 300,000 sat plus whatever CLN put into the replacement
        var capacity = LightningMoney.MilliSatoshis(theirs["total_msat"]!.GetValue<ulong>());
        var clnShare = LightningMoney.MilliSatoshis(capacity.MilliSatoshi - 300_000_000);
        Console.WriteLine($"[cln-df] CLN contributed {firstCapacity.Satoshi - 300_000} sat to the open and "
                        + $"{clnShare.Satoshi} sat to the RBF");
        var ours = Channel(node, first.ChannelId);
        Assert.Equal(capacity, ours.FundingOutput!.Amount);
        Assert.Equal(LightningMoney.Satoshis(300_000), ours.LocalBalance);
        Assert.Equal(clnShare, ours.RemoteBalance);
        if (bumpFeerate >= minimum && bumpFeerate <= maximum)
            Assert.Equal(LightningMoney.Satoshis(600_000), capacity);

        // Act & Assert: it confirms and the channel works
        await MineUntilUsableAsync(node, first.ChannelId, ct);
        Assert.Equal(bumped.FundingTxId, Channel(node, first.ChannelId).FundingOutput!.TransactionId);
        Assert.Equal(capacity, Channel(node, first.ChannelId).FundingOutput!.Amount);
        await PayBothWaysAsync(node, ct);
    }

    [Fact]
    public async Task Given_ClnsUnconfirmedDualFundedOpen_When_ClnBumpsIt_Then_WeFollowTheReplacementAndItConfirms()
    {
        // Arrange: CLN opens 500,000 sat to us at a feerate inside its own range, we contribute 200,000 sat
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("df-rbf-accepter", 200_000, ct);
        var (minimum, maximum) = await GetClnAcceptableFeerateRangeAsync(ct);
        var openFeerate = Math.Clamp(1_000u, minimum, maximum);
        var bumpFeerate = Math.Max(Math.Min(2 * openFeerate, maximum),
                                   (uint)InteractiveTxDriver.GetMinimumRbfFeeratePerKw(openFeerate));
        var opened = await _cln.CallAsync("fundchannel", ct, ("id", node.NodeIdHex), ("amount", "500000"),
                                          ("announce", "false"), ("feerate", $"{openFeerate}perkw"));
        Console.WriteLine($"[cln-df] CLN fundchannel: {opened.ToJsonString()}");
        var channelIdHex = opened["channel_id"]!.GetValue<string>();
        var channelId = new ChannelId(Convert.FromHexString(channelIdHex));
        var firstTxIdHex = opened["txid"]!.GetValue<string>();
        await Poll.UntilAsync(() => Task.FromResult(TryGetChannel(node, channelId)?.FundingOutput?.TransactionId
                                                                  is { } txId && TxIdDisplay(txId) == firstTxIdHex),
                              TimeSpan.FromSeconds(30), "our channel on CLN's first funding", ct);

        // Act: CLN's RBF of its open over the inputs it reserved for the first attempt (IT-RBF-01)
        var reserved = (await _cln.CallAsync("listfunds", ct))["outputs"]!.AsArray()
                                                                     .Where(o => o?["reserved"]?.GetValue<bool>() == true)
                                                                     .Select(o => $"\"{o!["txid"]!.GetValue<string>()}:"
                                                                                + $"{o["output"]!.GetValue<int>()}\"")
                                                                     .ToList();
        Assert.NotEmpty(reserved);
        var initial = await _cln.CallAsync("utxopsbt", ct, ("satoshi", 500_000), ("feerate", $"{bumpFeerate}perkw"),
                                           ("startweight", 42 + 172), ("utxos", $"[{string.Join(',', reserved)}]"),
                                           ("reservedok", "true"), ("min_witness_weight", 110),
                                           ("excess_as_change", "true"));
        var update = await _cln.CallAsync("openchannel_bump", ct, ("channel_id", channelIdHex), ("amount", 500_000),
                                          ("initialpsbt", initial["psbt"]!.GetValue<string>()),
                                          ("funding_feerate", $"{bumpFeerate}perkw"));
        for (var round = 0; update["commitments_secured"]?.GetValue<bool>() != true; round++)
        {
            Assert.True(round < 10, $"CLN's RBF did not secure the commitments: {update.ToJsonString()}");
            update = await _cln.CallAsync("openchannel_update", ct, ("channel_id", channelIdHex),
                                          ("psbt", update["psbt"]!.GetValue<string>()));
        }

        var signed = await _cln.CallAsync("signpsbt", ct, ("psbt", update["psbt"]!.GetValue<string>()));
        var sent = await _cln.CallAsync("openchannel_signed", ct, ("channel_id", channelIdHex),
                                        ("signed_psbt", signed["signed_psbt"]!.GetValue<string>()));
        Console.WriteLine($"[cln-df] CLN openchannel_signed: {sent.ToJsonString()}");
        var bumpedTxIdHex = sent["txid"]!.GetValue<string>();

        // Assert: we followed the replacement (our contribution rebuilt at the new feerate), in the mempool
        Assert.NotEqual(firstTxIdHex, bumpedTxIdHex);
        await Poll.UntilAsync(() => Task.FromResult(TryGetChannel(node, channelId)?.FundingOutput?.TransactionId
                                                                  is { } txId && TxIdDisplay(txId) == bumpedTxIdHex),
                              TimeSpan.FromSeconds(60), "our channel on CLN's replacement", ct);
        var dualFund = node.Services.GetRequiredService<DualFundedOpenService>();
        Assert.Equal(2, dualFund.GetSignedFundingTxIds(channelId).Count);
        var ours = Channel(node, channelId);
        Assert.False(ours.IsInitiator);
        Assert.Equal(LightningMoney.Satoshis(200_000), ours.LocalBalance);
        Assert.Equal(LightningMoney.Satoshis(700_000), ours.FundingOutput!.Amount);

        // ...and it confirms and carries payments
        await MineUntilUsableAsync(node, channelId, ct);
        Assert.Equal(bumpedTxIdHex, TxIdDisplay(Channel(node, channelId).FundingOutput!.TransactionId!.Value));
        await PayBothWaysAsync(node, ct);
    }

    [Fact]
    public async Task Given_ClnsUnconfirmedDualFundedOpen_When_WeBumpItAsTheAccepter_Then_ClnRefusesAndTheOpenConfirms()
    {
        // Arrange: CLN opens 500,000 sat to us, we contribute 200,000 sat
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("df-rbf-accepter-bump", 200_000, ct);
        var (minimum, maximum) = await GetClnAcceptableFeerateRangeAsync(ct);
        var openFeerate = Math.Clamp(1_000u, minimum, maximum);
        var bumpFeerate = Math.Max(Math.Min(2 * openFeerate, maximum),
                                   (uint)InteractiveTxDriver.GetMinimumRbfFeeratePerKw(openFeerate));
        var opened = await _cln.CallAsync("fundchannel", ct, ("id", node.NodeIdHex), ("amount", "500000"),
                                          ("announce", "false"), ("feerate", $"{openFeerate}perkw"));
        var channelId = new ChannelId(Convert.FromHexString(opened["channel_id"]!.GetValue<string>()));
        var firstTxIdHex = opened["txid"]!.GetValue<string>();
        await Poll.UntilAsync(() => Task.FromResult(TryGetChannel(node, channelId)?.FundingOutput?.TransactionId
                                                                  is { } txId && TxIdDisplay(txId) == firstTxIdHex),
                              TimeSpan.FromSeconds(30), "our channel on CLN's first funding", ct);

        // Act: NL-530, our bumpopen as the accepter (BOLT 2 "Fee bumping": the sender of tx_init_rbf "MAY be either the
        // initiator or the accepter")
        var refusal = await Assert.ThrowsAsync<Domain.Client.Exceptions.ClientException>(
                          () => BumpThroughClientAsync(node, channelId, bumpFeerate, ct));
        Console.WriteLine($"[cln-df] our accepter bump: {refusal.Message}");

        // Assert: CLN v26.06.8 refuses an RBF from the accepter ("Only the channel initiator is allowed to initiate
        // RBF", dualopend's tx_init_rbf handler); we stay on the first funding, and so does CLN
        var clnLog = await _cln.GetLogLinesAsync("initiator is allowed to initiate RBF", ct, 5);
        Console.WriteLine($"[cln-df] CLN on our tx_init_rbf: {clnLog}");
        Assert.Contains("Only the channel initiator is allowed to initiate RBF", clnLog);
        Assert.Equal(firstTxIdHex, TxIdDisplay(Channel(node, channelId).FundingOutput!.TransactionId!.Value));
        Assert.Single(node.Services.GetRequiredService<DualFundedOpenService>().GetSignedFundingTxIds(channelId));

        // ...and the open confirms and carries payments both ways (reconnected when CLN dropped the connection)
        await Poll.UntilAsync(async () => node.IsConnectedTo(ClnPubKey) && await _cln.IsConnectedAsync(node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(60), "we and CLN connected again", ct);
        await MineUntilUsableAsync(node, channelId, ct);
        Assert.Equal(firstTxIdHex, TxIdDisplay(Channel(node, channelId).FundingOutput!.TransactionId!.Value));
        Assert.Equal(LightningMoney.Satoshis(200_000), Channel(node, channelId).LocalBalance);
        await PayBothWaysAsync(node, ct);
    }

    [Fact]
    public async Task Given_OurBumpedDualFundedOpen_When_TheFirstAttemptIsMined_Then_BothNodesFollowIt()
    {
        // Arrange: our open and our bump (NL-528: any signed attempt may be the one mined)
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("df-rbf-first", 0, ct);
        var service = node.Services.GetRequiredService<IDualFundedOpenService>();
        var (minimum, maximum) = await GetClnAcceptableFeerateRangeAsync(ct);
        var openFeerate = Math.Clamp(1_000u, minimum, maximum);
        var bumpFeerate = Math.Max(Math.Min(2 * openFeerate, maximum),
                                   (uint)InteractiveTxDriver.GetMinimumRbfFeeratePerKw(openFeerate));
        var first = await service.OpenAsync(new DualFundedOpenRequest(ClnPubKey, LightningMoney.Satoshis(300_000),
                                                                      openFeerate), ct);
        Assert.True(first.FailureReason is null, first.FailureReason);
        await WaitInMempoolAsync(first.FundingTxId!.Value, ct);
        var firstCapacity = Channel(node, first.ChannelId).FundingOutput!.Amount;
        var bumped = await BumpThroughClientAsync(node, first.ChannelId, bumpFeerate, ct);
        await WaitInMempoolAsync(bumped.FundingTxId, ct);

        // Act: a block with the first attempt (from our broadcast row), not the replacement in the mempool
        BroadcastTransactionRow firstRow;
        using (var scope = node.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<Domain.Persistence.Interfaces.IUnitOfWork>()
                                 .BroadcastTransactionDbRepository.GetByTransactionIdAsync(first.FundingTxId.Value);
            firstRow = new BroadcastTransactionRow(Convert.ToHexString(row!.RawTransaction).ToLowerInvariant());
        }

        var address = await fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
        await fixture.Bitcoin.Rpc.SendCommandAsync("generateblock", ct, address.ToString(),
                                                   new[] { firstRow.RawHex });
        await MineUntilUsableAsync(node, first.ChannelId, ct);

        // Assert: both on the first attempt, with its capacity, and the channel works
        var ours = Channel(node, first.ChannelId);
        Assert.Equal(first.FundingTxId, ours.FundingOutput!.TransactionId);
        Assert.Equal(firstCapacity, ours.FundingOutput.Amount);
        Assert.Equal(LightningMoney.Satoshis(300_000), ours.LocalBalance);
        var theirs = await _cln.GetPeerChannelAsync(node.NodeIdHex, first.ChannelId.ToString(), ct);
        Console.WriteLine($"[cln-df] CLN after the first attempt was mined: {theirs?.ToJsonString()}");
        Assert.Equal(TxIdDisplay(first.FundingTxId.Value), theirs!["funding_txid"]!.GetValue<string>());
        Assert.Equal((long)firstCapacity.MilliSatoshi, theirs["total_msat"]!.GetValue<long>());
        await PayBothWaysAsync(node, ct);
    }

    private sealed record BroadcastTransactionRow(string RawHex);

    private static async Task<(ChannelId ChannelId, Domain.Bitcoin.ValueObjects.TxId FundingTxId)>
        BumpThroughClientAsync(NLightningTestNode node, ChannelId channelId, uint feeratePerKw, CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<
            Daemon.Interfaces.IClientCommandHandler<BumpOpenClientRequest,
                Domain.Client.Responses.BumpOpenClientResponse>>();
        var response = await handler.HandleAsync(new BumpOpenClientRequest(channelId, feeratePerKw), ct);
        return (response.ChannelId, response.FundingTxId);
    }

    private static Domain.Channels.Models.ChannelModel? TryGetChannel(NLightningTestNode node, ChannelId channelId) =>
        node.Services.GetRequiredService<Domain.Channels.Interfaces.IChannelMemoryRepository>()
            .TryGetChannel(channelId, out var channel)
            ? channel
            : null;

    /// <summary>
    /// CLN's <c>feerates perkw</c> <c>min_acceptable</c>/<c>max_acceptable</c>: its funder plugin contributes only to a
    /// funding feerate inside them (<c>feerate_our_min</c>/<c>feerate_our_max</c> of the <c>openchannel2</c> and
    /// <c>rbf_channel</c> hooks).
    /// </summary>
    private async Task<(uint Min, uint Max)> GetClnAcceptableFeerateRangeAsync(CancellationToken ct)
    {
        var perKw = (await _cln.CallAsync("feerates", ct, ("style", "perkw")))["perkw"]!;
        return (perKw["min_acceptable"]!.GetValue<uint>(), perKw["max_acceptable"]!.GetValue<uint>());
    }

    private async Task<NLightningTestNode> CreateNodeAsync(string name, long acceptContributionSat,
                                                           CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(fixture.Bitcoin, name, configureNodeOptions: o =>
        {
            o.Features.AllowExperimentalFeatures = true;
            o.Features.DualFund = FeatureSupport.Optional;
        });
        _nodes.Add(node);
        node.ConfigureServices = services =>
        {
            services.AddDualFundingServices();
            services.Configure<DualFundingOptions>(o =>
            {
                o.AcceptContributionSat = acceptContributionSat;
            });
        };

        await node.StartAsync(ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(1_000_000), AddressType.P2Wpkh, ct);
        await MineAndWaitAsync(node, 6, ct);
        await node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(ClnAddress)).WaitAsync(ct);
        await Poll.UntilAsync(async () => node.IsConnectedTo(ClnPubKey)
                                       && await _cln.IsConnectedAsync(node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(30), $"{name} and CLN connected", ct);
        return node;
    }

    private static async Task<ChannelId> OpenThroughClientAsync(NLightningTestNode node, LightningMoney amount,
                                                                CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<
            Daemon.Interfaces.IClientCommandHandler<OpenChannelClientRequest,
                Domain.Client.Responses.OpenChannelClientResponse>>();
        var response = await handler.HandleAsync(new OpenChannelClientRequest(string.Empty, amount)
        {
            NodeInfo = Convert.ToHexString((byte[])node.PeerManager.ListPeers().Single().NodeId),
            IsDualFunded = true
        }, ct);
        return response.ChannelId;
    }

    private static Domain.Channels.Models.ChannelModel Channel(NLightningTestNode node, ChannelId channelId) =>
        node.Services.GetRequiredService<Domain.Channels.Interfaces.IChannelMemoryRepository>()
            .TryGetChannel(channelId, out var channel)
            ? channel
            : throw new InvalidOperationException($"{node.Name} has no channel {channelId}");

    private static void AssertV2ChannelId(Domain.Channels.Models.ChannelModel channel)
    {
        using var sha256 = new Infrastructure.Crypto.Hashes.Sha256();
        Assert.Equal(ChannelIdV2.Derive(sha256, channel.LocalKeySet.RevocationCompactBasepoint,
                                        channel.RemoteKeySet!.RevocationCompactBasepoint), channel.ChannelId);
    }

    private async Task PayBothWaysAsync(NLightningTestNode node, CancellationToken ct)
    {
        // We pay CLN's invoice first (CLN may have contributed nothing to our open), then CLN pays ours
        var label = $"df-{Guid.NewGuid():N}";
        var theirInvoice = await _cln.CallAsync("invoice", ct, ("amount_msat", "30000000"), ("label", label),
                                                ("description", "df pays cln"));
        var payment = await node.PayInvoiceAsync(theirInvoice["bolt11"]!.GetValue<string>(), ct);
        Assert.Equal(Domain.Payments.Enums.PaymentStatus.Succeeded, payment.Status);

        var ourInvoice = await node.CreateInvoiceAsync(LightningMoney.Satoshis(15_000), "cln pays df", ct);
        var paid = await _cln.CallAsync("xpay", ct, ("invstring", ourInvoice.Bolt11!));
        Assert.Equal(15_000_000L, paid["amount_msat"]!.GetValue<long>());
    }

    private async Task MineUntilUsableAsync(NLightningTestNode node, ChannelId channelId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + s_usableTimeout;
        while (true)
        {
            var ours = node.Services.GetRequiredService<Domain.Channels.Interfaces.IChannelMemoryRepository>()
                           .TryGetChannel(channelId, out var channel)
                           ? channel.State
                           : ChannelState.None;
            var theirs = (await _cln.GetPeerChannelAsync(node.NodeIdHex, channelId.ToString(), ct))?["state"]
                           ?.GetValue<string>();
            var usable = ours == ChannelState.Open && theirs == "CHANNELD_NORMAL"
                      && (await node.GetChannelAsync(channelId, ct)).IsUsable();
            if (usable)
                return;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Channel {channelId} not usable in time: ours {ours}, CLN {theirs}");

            await MineAndWaitAsync(node, 1, ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task MineAndWaitAsync(NLightningTestNode node, int blocks, CancellationToken ct)
    {
        await fixture.MineAndWaitAsync(blocks, [node], ct);
        var tip = await fixture.Bitcoin.Rpc.GetBlockCountAsync(ct);
        await Poll.UntilAsync(async () => (await _cln.GetInfoAsync(ct))["blockheight"]!.GetValue<long>() == tip,
                              TimeSpan.FromSeconds(60), "the dual-funding CLN at the tip", ct);
    }

    private async Task WaitInMempoolAsync(Domain.Bitcoin.ValueObjects.TxId txId, CancellationToken ct)
    {
        var display = NBitcoin.uint256.Parse(TxIdDisplay(txId));
        await Poll.UntilAsync(async () => (await fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct)).Contains(display),
                              TimeSpan.FromSeconds(60), $"{TxIdDisplay(txId)} in the mempool", ct);
    }

    /// <summary>The txid as bitcoind and CLN print it (reversed byte order).</summary>
    private static string TxIdDisplay(Domain.Bitcoin.ValueObjects.TxId txId) =>
        Convert.ToHexStringLower(((byte[])txId).Reverse().ToArray());

    private async Task FundClnAsync(LightningMoney amount, CancellationToken ct)
    {
        var address = (await _cln.CallAsync("newaddr", ct, ("addresstype", "bech32")))["bech32"]!.GetValue<string>();

        // At 1 sat/vB (bitcoind's wallet default pays far more): the fixture's bitcoind is shared with the other CLN
        // classes, and every fee-paying transaction this class gets mined raises bitcoind's estimate, which CLN's
        // min_acceptable feerate follows; the lane dfrbf proofs pushed it above our 2,500 sat/kw opens, failing the
        // classes that ran after this one in a full run
        var sent = await fixture.Bitcoin.Rpc.SendCommandAsync(
                       "sendtoaddress", ct, address,
                       NBitcoin.Money.Satoshis(amount.Satoshi).ToDecimal(NBitcoin.MoneyUnit.BTC), "", "", false,
                       true, null, "unset", null, 1);
        var txId = NBitcoin.uint256.Parse(sent.Result.ToString());
        await fixture.MineAsync(6, ct);
        await Poll.UntilAsync(async () =>
        {
            var outputs = (await _cln.CallAsync("listfunds", ct))["outputs"]!.AsArray();
            return outputs.Any(o => o?["txid"]?.GetValue<string>() == txId.ToString()
                                 && o["status"]?.GetValue<string>() == "confirmed");
        }, TimeSpan.FromSeconds(60), "the dual-funding CLN sees its deposit confirmed", ct);
    }
}