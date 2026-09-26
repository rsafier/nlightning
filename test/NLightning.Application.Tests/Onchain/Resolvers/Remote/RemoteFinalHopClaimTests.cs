using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Remote;

using Channels.Services;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;

/// <summary>
/// NL-316 and NL-322 on the peer's commitment (B5-RMT-RO-01 with B5-LCL-RO-02): an HTLC the peer offered that pays one
/// of our invoices and was not fulfilled before the close is handed to the HTLC switch as a final-hop candidate every
/// round while the invoice is <c>Open</c>; once the switch accepted it (the preimage persisted on the HTLC's record) it
/// is claimed with <c>&lt;sig&gt; &lt;preimage&gt;</c> before its <c>cltv_expiry</c>. An invoice preimage alone is never
/// used.
/// </summary>
public sealed class RemoteFinalHopClaimTests : IDisposable
{
    private const uint Cltv = 600;
    private const ulong AmountMsat = 30_000_000;

    private static readonly ChannelId s_downstreamChannelId = new(Enumerable.Repeat((byte)0x72, 32).ToArray());

    private readonly RemoteResolutionTestContext _context = new();

    private RealSigningCommitmentPair Pair => _context.Pair;

    [Fact]
    public async Task Given_PeerHtlcForOurOpenInvoice_When_PeerCommitmentConfirms_Then_TheSwitchDecidesEveryRoundAndNothingIsClaimed()
    {
        // Arrange: Bob paid our invoice; the HTLC is locked in, our switch has not fulfilled it when Bob force-closes
        var preimage = RealSigningCommitmentPair.Preimage(3);
        var id = Pair.Add(Pair.Bob, AmountMsat, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot();
        AddInvoice(preimage);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + 1);

        // Assert: one final-hop decision per round for that HTLC, no claim without the switch's acceptance
        var decisions = _context.SwitchEvents.OfType<IncomingHtlcLockedIn>().ToList();
        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, d =>
        {
            Assert.Equal(_context.Channel.ChannelId, d.ChannelId);
            Assert.Equal(id, d.Htlc.Id);
            Assert.Equal(HtlcDirection.Incoming, d.Htlc.Direction);
        });
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.Equal(OutputResolutionState.Waiting, _context.HtlcRow(id).State);
    }

    [Fact]
    public async Task Given_PeerHtlcTheSwitchAcceptedAsFinalHop_When_Resolved_Then_ClaimedWithItsPreimageBeforeCltv()
    {
        // Arrange: the switch accepted the HTLC on chain: the preimage is on its record, the invoice settled
        var preimage = RealSigningCommitmentPair.Preimage(3);
        var id = Pair.Add(Pair.Bob, AmountMsat, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(WithKnownPreimage(Pair.Alice.State, id, preimage));
        var invoice = AddInvoice(preimage);
        invoice.Accept(LightningMoney.MilliSatoshis(AmountMsat));
        invoice.Settle(DateTimeOffset.UtcNow);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert: claimed at once (deadline cltv_expiry), and the switch is not asked again
        var claim = AssertPreimageClaim(id, preimage);
        Assert.DoesNotContain(_context.SwitchEvents, e => e is IncomingHtlcLockedIn);

        // It confirms before the expiry
        await _context.MineAsync(claim, RemoteResolutionTestContext.CloseHeight + 1);
        Assert.Equal(OutputResolutionState.Resolved, _context.HtlcRow(id).State);
    }

    [Fact]
    public async Task Given_TheSwitchAcceptsBetweenTwoRounds_When_TheNextBlockComes_Then_Claimed()
    {
        // Arrange: first round with the invoice Open, nothing accepted yet
        var preimage = RealSigningCommitmentPair.Preimage(3);
        var id = Pair.Add(Pair.Bob, AmountMsat, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot();
        var invoice = AddInvoice(preimage);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        Assert.Single(_context.SwitchEvents.OfType<IncomingHtlcLockedIn>());

        // Act: the switch accepts it (the preimage on the record, the invoice settled in the same save)
        _context.UseSnapshot(WithKnownPreimage(Pair.Alice.State, id, preimage));
        invoice.Accept(LightningMoney.MilliSatoshis(AmountMsat));
        invoice.Settle(DateTimeOffset.UtcNow);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + 1);

        // Assert
        AssertPreimageClaim(id, preimage);
        Assert.Single(_context.SwitchEvents.OfType<IncomingHtlcLockedIn>());
    }

    [Fact]
    public async Task Given_ARecordPreimageOfAnotherHash_When_Resolved_Then_NeverClaimed()
    {
        // Arrange: whatever the record holds, only a preimage of the HTLC's own hash may be revealed
        var preimage = RealSigningCommitmentPair.Preimage(3);
        var id = Pair.Add(Pair.Bob, AmountMsat, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(WithKnownPreimage(Pair.Alice.State, id, RealSigningCommitmentPair.Preimage(9)));
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(Cltv);

        // Assert
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.Equal(OutputResolutionState.Ignored, _context.HtlcRow(id).State);
    }

    [Fact]
    public async Task Given_AMarkOnAnHtlcOfAnOpenInvoice_When_Resolved_Then_NotClaimedAndTheSwitchIsAskedAgain()
    {
        // Arrange (NL-323): the switch marked this part of a set that then became incomplete (or it stopped before
        // the settle): the invoice is still Open, so the mark commits to nothing
        var preimage = RealSigningCommitmentPair.Preimage(3);
        var id = Pair.Add(Pair.Bob, AmountMsat, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(WithKnownPreimage(Pair.Alice.State, id, preimage));
        AddInvoice(preimage);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + 1);

        // Assert: no claim; the switch decides again every round
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.Equal(2, _context.SwitchEvents.OfType<IncomingHtlcLockedIn>().Count());
        Assert.Equal(OutputResolutionState.Waiting, _context.HtlcRow(id).State);
    }

    [Fact]
    public async Task Given_AMarkOnAnHtlcWeFailedOffChain_When_ItsCommitmentConfirms_Then_NeverClaimed()
    {
        // Arrange (NL-323): the part kept its mark through our off-chain fail (the engine keeps KnownPreimage on the
        // removal); the invoice was settled later by another set, and the peer's commitment still holds the HTLC
        var preimage = RealSigningCommitmentPair.Preimage(3);
        var id = Pair.Add(Pair.Bob, AmountMsat, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        Pair.Fail(Pair.Alice, id);
        _context.UseSnapshot(WithKnownPreimage(Pair.Alice.State, id, preimage));
        var invoice = AddInvoice(preimage);
        invoice.Accept(LightningMoney.MilliSatoshis(AmountMsat));
        invoice.Settle(DateTimeOffset.UtcNow);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + 1);

        // Assert
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("settled")]
    [InlineData("canceled")]
    [InlineData("forward")]
    [InlineData("expired")]
    public async Task Given_APeerHtlcThatCannotPayAnOpenInvoice_When_Resolved_Then_TheSwitchIsNotAsked(string why)
    {
        // Arrange (B5-LCL-RO-02): only an HTLC that may still pay one of our Open invoices is a final-hop candidate
        var preimage = RealSigningCommitmentPair.Preimage(3);
        var id = Pair.Add(Pair.Bob, AmountMsat, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot();
        var tip = RemoteResolutionTestContext.CloseHeight;
        switch (why)
        {
            case "settled":
                var settled = AddInvoice(preimage);
                settled.Accept(LightningMoney.MilliSatoshis(AmountMsat));
                settled.Settle(DateTimeOffset.UtcNow);
                break;
            case "canceled":
                AddInvoice(preimage).Cancel();
                break;
            case "forward":
                // Our invoice's hash, but the onion made us forward it (a preimage-extraction attempt)
                AddInvoice(preimage);
                _context.Store.Origins[(s_downstreamChannelId, new HtlcKey(HtlcDirection.Outgoing, 0))] =
                    HtlcOrigin.Forwarded(_context.Channel.ChannelId, id);
                break;
            case "expired":
                AddInvoice(preimage);
                tip = Cltv;
                break;
        }

        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(tip);

        // Assert
        Assert.DoesNotContain(_context.SwitchEvents, e => e is IncomingHtlcLockedIn);
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
    }

    public void Dispose() => _context.Dispose();

    private InvoiceModel AddInvoice(Secret preimage)
    {
        var invoice = new InvoiceModel(RealSigningCommitmentPair.Hash(preimage), preimage,
                                       new Secret(Enumerable.Repeat((byte)0x53, 32).ToArray()),
                                       LightningMoney.MilliSatoshis(AmountMsat), "final hop", "lnbcrt-test",
                                       DateTimeOffset.UtcNow, 3_600, 18);
        _context.Store.Invoices[invoice.PaymentHash] = invoice;
        return invoice;
    }

    /// <summary>The snapshot with the preimage the switch persists on an incoming HTLC it accepted as final hop.</summary>
    internal static ChannelCommitments WithKnownPreimage(ChannelCommitments state, ulong htlcId, Secret preimage) =>
        WithRecord(state, htlcId, r => r with { KnownPreimage = preimage });

    /// <summary>The snapshot with the incoming HTLC's record changed by <paramref name="change"/>.</summary>
    internal static ChannelCommitments WithRecord(ChannelCommitments state, ulong htlcId,
                                                  Func<HtlcRecord, HtlcRecord> change)
    {
        var updated = change(state.GetHtlc(HtlcDirection.Incoming, htlcId)!);
        return ChannelCommitments.Restore(state.ChannelId, state.Params, state.LocalBalanceMsat, state.RemoteBalanceMsat,
                                          state.Htlcs.SetItem(updated.Key, updated).Values, state.FeeUpdates,
                                          state.LocalNextHtlcId, state.RemoteNextHtlcId, state.LocalCommit,
                                          state.RemoteCommit, state.RemoteNextCommit,
                                          state.RemoteNextPerCommitmentPoint);
    }

    private BroadcastTransactionModel AssertPreimageClaim(ulong id, Secret preimage)
    {
        var row = _context.HtlcRow(id);
        Assert.Equal(OutputDescriptorKind.RemoteOfferedHtlc, row.Descriptor);
        Assert.Equal(OutputResolutionState.Broadcast, row.State);
        Assert.Equal(Cltv, row.DeadlineHeight);
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.Equal(row.ResolvingTransactionId, claim.TransactionId);
        var tx = Transaction.Load(claim.RawTransaction, Network.Main);
        Assert.Equal(0U, tx.LockTime.Value);
        Assert.Equal((byte[])preimage, tx.Inputs[0].WitScript.Pushes.ElementAt(1));
        Assert.True(_context.Verifies(claim, out var error), error.ToString());
        return claim;
    }
}