using Google.Protobuf;
using Grpc.Core;
using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using Routerrpc;

namespace NLightning.Integration.Tests.Docker.Onchain;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Fixtures;
using Utils;

/// <summary>
/// NL-316 / NL-322 Docker proof (ABCD wave 7, W7-B): LND david pays our invoice in two parts (<c>basic_mpp</c>), so we
/// hold the first one (our "hold": the set is incomplete, nothing is fulfilled); david force-closes the channel that
/// carries it; the second part arrives on another channel and completes the set. The part on david's commitment was
/// never fulfilled off chain and no fulfill of it is stored: our switch accepts it as final hop on chain (the
/// <c>FinalHopProcessor</c> checks), persists the preimage on its record and settles the invoice, and our
/// <c>RemoteCommitResolver</c> claims it with <c>&lt;sig&gt; &lt;preimage&gt;</c> before its <c>cltv_expiry</c>.
/// </summary>
/// <remarks>Run with <c>scripts/run-onchain.sh</c> (own process, own fixture), or the in-container runner with
/// <c>-class NLightning.Integration.Tests.Docker.Onchain.OnchainFinalHopTests</c>.</remarks>
[Collection(OnchainRegtestCollection.Name)]
public class OnchainFinalHopTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);
    private static readonly LightningMoney s_amount = LightningMoney.Satoshis(80_000);
    private static readonly LightningMoney s_firstPart = LightningMoney.Satoshis(30_000);
    private static readonly LightningMoney s_secondPart = LightningMoney.Satoshis(50_000);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public OnchainFinalHopTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "onchain-final-hop");
        await Node.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
            await _node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Given_HeldPartOfOurInvoice_When_DavidForceClosesAndTheSetCompletes_Then_WeClaimItOnChainAndTheInvoiceIsSettled()
    {
        // Arrange: two channels to david, each with a push so david can pay us over both
        var ct = TestContext.Current.CancellationToken;
        var david = _fixture.GetLndNode("david");
        var davidAddress = await Node.ConnectToAsync(david, ct);
        var first = await OpenUsableChannelAsync(david, davidAddress, ct);
        var second = await OpenUsableChannelAsync(david, davidAddress, ct);
        var firstLnd = await LndTestHelpers.GetChannelByPointAsync(david, first.ChannelPoint(), ct);
        var secondLnd = await LndTestHelpers.GetChannelByPointAsync(david, second.ChannelPoint(), ct);
        Assert.NotNull(firstLnd);
        Assert.NotNull(secondLnd);
        var invoice = await Node.Services.GetRequiredService<IInvoiceService>()
                                .CreateInvoiceAsync(s_amount, "nl-316 on-chain final hop", null, ct);

        // david sends the first part: we hold it (the set is incomplete), nothing is fulfilled
        var firstAttempt = await SendPartAsync(david, firstLnd.ChanId, invoice, s_firstPart, ct);
        var held = await Poll.ForAsync(() => IncomingHtlcs(first.ChannelId)
                                               .FirstOrDefault(h => h.State == HtlcState.RcvdAddAckRevocation),
                                       s_timeout, "the first part locked in and held", ct);
        Assert.Null(held.Removal);
        Assert.Null(held.KnownPreimage);
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(invoice, ct)).Status);
        Console.WriteLine($"Holding HTLC {held.Id} of {held.AmountMsat} msat, cltv_expiry {held.CltvExpiry}");

        // Act 1: david force-closes the channel that carries it; its commitment confirms
        var commitmentTxId = await ForceCloseAsync(david, first, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [Node], ct);
        var close = await WaitForCloseAsync(first.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
        Assert.Equal(commitmentTxId, new uint256((byte[])close.CommitmentTransactionId));

        // Nothing is claimed while the set is incomplete (the switch has not accepted the part)
        Assert.Null((await GetOutputAsync(first.ChannelId, OutputDescriptorKind.RemoteOfferedHtlc))
                  ?.ResolvingTransactionId);

        // Act 2: the second part completes the set over the other channel
        var secondAttempt = await SendPartAsync(david, secondLnd.ChanId, invoice, s_secondPart, ct);

        // Assert: the part on chain is accepted as final hop (its preimage on the record) and the invoice settles
        var settled = await Poll.ForAsync(async () =>
        {
            var stored = await GetInvoiceAsync(invoice, ct);
            return stored.Status == InvoiceStatus.Settled ? stored : null;
        }, s_timeout, "our invoice settled", ct);
        Assert.Equal(s_amount, settled.AmountReceived);
        Assert.Equal(invoice.Preimage, IncomingHtlcs(first.ChannelId).Single(h => h.Id == held.Id).KnownPreimage);
        var secondResult = await secondAttempt.WaitAsync(s_timeout, ct);
        Console.WriteLine($"Second part: {secondResult.Status} {secondResult.Failure?.Code}");
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Succeeded, secondResult.Status);

        // The next blocks: our preimage claim of the held part on david's commitment, confirmed before cltv_expiry
        var row = await Poll.ForAsync(async () =>
        {
            var output = await GetOutputAsync(first.ChannelId, OutputDescriptorKind.RemoteOfferedHtlc);
            if (output is { ResolvingTransactionId: not null })
                return output;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [Node], ct);
            return null;
        }, s_timeout, "our preimage claim of the held part saved", ct);
        Assert.Equal(held.Id, row.HtlcId);
        var claim = await MineUntilConfirmedAsync(row.ResolvingTransactionId!.Value, [david], ct);
        var confirmedAt = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
        Assert.True(confirmedAt < held.CltvExpiry, $"claimed at {confirmedAt}, expiry {held.CltvExpiry}");
        Assert.Equal(0U, claim.LockTime.Value);
        Assert.Equal(commitmentTxId, Assert.Single(claim.Inputs).PrevOut.Hash);
        Assert.Equal((byte[])invoice.Preimage, claim.Inputs[0].WitScript.Pushes.ElementAt(1));
        await Poll.UntilAsync(async () => (await GetOutputAsync(first.ChannelId,
                                                                OutputDescriptorKind.RemoteOfferedHtlc))?.State
                                          >= OutputResolutionState.Resolved,
                              s_timeout, "the held part's output resolved", ct);

        // david learns the preimage from our claim: its first attempt succeeds too
        var firstResult = await Poll.ForAsync(async () =>
        {
            if (firstAttempt.IsCompleted)
                return await firstAttempt;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [Node], ct);
            return null;
        }, s_timeout, "david's first attempt resolved", ct);
        Console.WriteLine($"First part: {firstResult.Status} {firstResult.Failure?.Code}");
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Succeeded, firstResult.Status);
        Assert.Equal(InvoiceStatus.Settled, (await GetInvoiceAsync(invoice, ct)).Status);
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(LNDNodeConnection peer,
                                                                                    string peerAddress,
                                                                                    CancellationToken ct)
    {
        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var channel = await Node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress, s_capacity)
        {
            PushAmount = s_push,
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {peer.LocalAlias}");

        await Poll.UntilAsync(async () =>
        {
            var ours = await Node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
                return true;

            // LND may want more confirmations than we do
            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [Node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [peer], [Node], ct);
        return channel;
    }

    /// <summary>
    /// One part of <paramref name="invoice"/> from <paramref name="lnd"/> over <paramref name="chanId"/>
    /// (<c>SendToRouteV2</c>; the MPP record carries our payment secret and the whole amount). Returns the attempt,
    /// which completes when the part is resolved. <c>BuildRoute</c> is retried while LND's router lacks the fresh
    /// private channel's edge (NL-319).
    /// </summary>
    private async Task<Task<HTLCAttempt>> SendPartAsync(LNDNodeConnection lnd, ulong chanId, InvoiceModel invoice,
                                                        LightningMoney part, CancellationToken ct)
    {
        var route = await Poll.ForAsync(async () =>
        {
            try
            {
                return await lnd.RouterClient.BuildRouteAsync(new BuildRouteRequest
                {
                    AmtMsat = (long)part.MilliSatoshi,
                    // Our final-hop check wants cltv_expiry >= height + min_final_cltv_expiry_delta when the part is
                    // accepted, which happens a few blocks later for the one on chain
                    FinalCltvDelta = invoice.MinFinalCltvExpiry + 20,
                    OutgoingChanId = chanId,
                    HopPubkeys = { ByteString.CopyFrom(Node.NodeId) }
                }, cancellationToken: ct);
            }
            catch (RpcException e)
            {
                Console.WriteLine($"BuildRoute over {chanId} failed ({e.Status.Detail}); retrying");
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                return null;
            }
        }, s_timeout, $"a route over {chanId}", ct);

        // BuildRoute cannot attach a payment address for a node outside LND's graph: set the MPP record here
        route.Route.Hops[^1].MppRecord = new MPPRecord
        {
            PaymentAddr = ByteString.CopyFrom((byte[])invoice.PaymentSecret),
            TotalAmtMsat = (long)s_amount.MilliSatoshi
        };
        return lnd.RouterClient.SendToRouteV2Async(new Routerrpc.SendToRouteRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])invoice.PaymentHash),
            Route = route.Route
        }, cancellationToken: ct).ResponseAsync;
    }

    /// <summary>LND <c>CloseChannel { force = true }</c>; returns the commitment's txid once it is broadcast.</summary>
    private static async Task<uint256> ForceCloseAsync(LNDNodeConnection lnd,
                                                       OpenChannelClientSubscriptionResponse channel,
                                                       CancellationToken ct)
    {
        var parts = channel.ChannelPoint().Split(':');
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_timeout);
        using var closeCall = lnd.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint { FundingTxidStr = parts[0], OutputIndex = uint.Parse(parts[1]) },
            Force = true
        }, cancellationToken: closeTimeout.Token);
        PendingUpdate? pending = null;
        while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
            pending = closeCall.ResponseStream.Current.ClosePending;
        Assert.NotNull(pending);

        // LND's txid bytes are in internal order
        var txId = new uint256(pending.Txid.ToByteArray());
        Console.WriteLine($"{lnd.LocalAlias} force-closed {channel.ChannelPoint()} with {txId}");
        return txId;
    }

    /// <summary>Mines one block at a time until <paramref name="txId"/> (internal order) is confirmed.</summary>
    private async Task<NBitcoin.Transaction> MineUntilConfirmedAsync(Domain.Bitcoin.ValueObjects.TxId txId,
                                                            IEnumerable<LNDNodeConnection> lndNodes,
                                                            CancellationToken ct)
    {
        var displayTxId = new uint256((byte[])txId);
        var lnd = lndNodes.ToList();
        return await Poll.ForAsync(async () =>
        {
            try
            {
                var info = await _fixture.Bitcoin.GetRawTransactionInfoAsync(displayTxId, ct);
                if (info.Confirmations >= 1)
                    return info.Transaction;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Not broadcast yet
            }

            await ChainSync.MineAndWaitAsync(_fixture, 1, lnd, [Node], ct);
            return null;
        }, s_timeout, $"transaction {displayTxId} confirmed", ct);
    }

    private async Task<ChannelCloseModel> WaitForCloseAsync(ChannelId channelId, CancellationToken ct) =>
        await Poll.ForAsync(async () =>
        {
            using var scope = Node.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                              .GetCloseAsync(channelId);
        }, s_timeout, $"the funding spend of {channelId} recorded", ct);

    private async Task<OutputResolutionModel?> GetOutputAsync(ChannelId channelId, OutputDescriptorKind kind)
    {
        using var scope = Node.Services.CreateScope();
        var outputs = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                                 .GetOutputsByChannelIdAsync(channelId);
        return outputs.FirstOrDefault(o => o.Descriptor == kind);
    }

    private async Task<InvoiceModel> GetInvoiceAsync(InvoiceModel invoice, CancellationToken ct) =>
        await Node.Services.GetRequiredService<IInvoiceService>().GetInvoiceAsync(invoice.PaymentHash, ct)
     ?? throw new InvalidOperationException("Our invoice is gone");

    private IReadOnlyList<Domain.Channels.Commitments.HtlcRecord> IncomingHtlcs(ChannelId channelId) =>
        Node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel) && channel.Commitments is { } c
            ? c.Htlcs.Values.Where(h => h.Direction == HtlcDirection.Incoming).ToList()
            : [];
}