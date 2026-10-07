using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Remote;

using Channels.Services;
using Domain.Accounting.Constants;
using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Payments.Models;

/// <summary>
/// NL-1182 on the peer's commitment: a forward held for the HTLC interceptor while its incoming channel closed on chain
/// and settled by it keeps the interceptor's preimage on the incoming HTLC's record;
/// <see cref="Application.Onchain.Resolvers.RemoteCommitResolver"/> claims the HTLC with <c>&lt;sig&gt; &lt;preimage&gt;</c>
/// before its <c>cltv_expiry</c>, with the settle's <c>InterceptedHtlcSettled</c> event and without it (the feed's
/// gate, NL-619). A final hop's mark of an open invoice, an HTLC failed off chain and a preimage of another hash are
/// never claimed.
/// </summary>
public sealed class RemoteInterceptorClaimTests : IDisposable
{
    private const uint Cltv = 600;
    private const ulong AmountMsat = 30_000_000;

    private static readonly Secret s_preimage = RealSigningCommitmentPair.Preimage(4);

    private readonly RemoteResolutionTestContext _context = new();

    private RealSigningCommitmentPair Pair => _context.Pair;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AForwardTheInterceptorSettled_When_ThePeerCommitmentConfirms_Then_ClaimedWithItsPreimage(
        bool withSettleEvent)
    {
        // Arrange: no invoice for the hash (a forward), the interceptor's preimage on the incoming record
        var id = Pair.Add(Pair.Bob, AmountMsat, s_preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(RemoteFinalHopClaimTests.WithKnownPreimage(Pair.Alice.State, id, s_preimage));
        if (withSettleEvent)
            _context.Store.AccountingEventKeys.Add(
                AccountingEventKeys.InterceptedHtlcSettled(_context.Channel.ChannelId, id));
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert: claimed at once, revealing the interceptor's preimage, deadline cltv_expiry, script-valid
        var row = _context.HtlcRow(id);
        Assert.Equal(OutputDescriptorKind.RemoteOfferedHtlc, row.Descriptor);
        Assert.Equal(OutputResolutionState.Broadcast, row.State);
        Assert.Equal(Cltv, row.DeadlineHeight);
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.Equal(row.ResolvingTransactionId, claim.TransactionId);
        var tx = Transaction.Load(claim.RawTransaction, Network.Main);
        Assert.Equal((byte[])s_preimage, tx.Inputs[0].WitScript.Pushes.ElementAt(1));
        Assert.True(_context.Verifies(claim, out var error), error.ToString());
        // The switch is still handed the HTLC every round (no invoice, no circuit); it sees the preimage on the record
        // and offers nothing again (HtlcSwitch.TryInterceptOnChain)
        Assert.All(_context.SwitchEvents.OfType<IncomingHtlcLockedIn>(), e => Assert.Equal(id, e.Htlc.Id));

        // It confirms before the expiry
        await _context.MineAsync(claim, RemoteResolutionTestContext.CloseHeight + 1);
        Assert.Equal(OutputResolutionState.Resolved, _context.HtlcRow(id).State);
    }

    [Fact]
    public async Task Given_AMarkWithoutTheSettleEventOnAnHtlcOfAnOpenInvoice_When_Resolved_Then_NotClaimed()
    {
        // Arrange: without the interceptor's event a mark on an HTLC of one of our invoices is a final hop's, and the
        // invoice is still Open (NL-323)
        var id = Pair.Add(Pair.Bob, AmountMsat, s_preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(RemoteFinalHopClaimTests.WithKnownPreimage(Pair.Alice.State, id, s_preimage));
        var hash = RealSigningCommitmentPair.Hash(s_preimage);
        _context.Store.Invoices[hash] = new InvoiceModel(hash, s_preimage,
                                                         new Secret(Enumerable.Repeat((byte)0x53, 32).ToArray()),
                                                         LightningMoney.MilliSatoshis(AmountMsat), "final hop",
                                                         "lnbcrt-test", DateTimeOffset.UtcNow, 3_600, 18);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.Equal(OutputResolutionState.Waiting, _context.HtlcRow(id).State);
    }

    [Fact]
    public async Task Given_AnHtlcWeFailedOffChainWithTheSettleEvent_When_ItsCommitmentConfirms_Then_NeverClaimed()
    {
        // Arrange: a fail removal is never claimed, whatever the record and the feed say
        var id = Pair.Add(Pair.Bob, AmountMsat, s_preimage, Cltv);
        Pair.Settle(Pair.Bob);
        Pair.Fail(Pair.Alice, id);
        _context.UseSnapshot(RemoteFinalHopClaimTests.WithKnownPreimage(Pair.Alice.State, id, s_preimage));
        _context.Store.AccountingEventKeys.Add(
            AccountingEventKeys.InterceptedHtlcSettled(_context.Channel.ChannelId, id));
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + 1);

        // Assert
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
    }

    [Fact]
    public async Task Given_TheSettleEventButARecordPreimageOfAnotherHash_When_Resolved_Then_NeverClaimed()
    {
        // Arrange
        var id = Pair.Add(Pair.Bob, AmountMsat, s_preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(RemoteFinalHopClaimTests.WithKnownPreimage(Pair.Alice.State, id,
                                                                        RealSigningCommitmentPair.Preimage(9)));
        _context.Store.AccountingEventKeys.Add(
            AccountingEventKeys.InterceptedHtlcSettled(_context.Channel.ChannelId, id));
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(Cltv);

        // Assert
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.Equal(OutputResolutionState.Ignored, _context.HtlcRow(id).State);
    }

    public void Dispose() => _context.Dispose();
}