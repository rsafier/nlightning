namespace NLightning.Application.Onchain.Resolvers;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Payments.Enums;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// The final-hop side of the preimage claims of the local and remote commitment resolvers (NL-316, NL-322): an HTLC
/// the peer offered that pays one of our invoices is claimed on chain only with the preimage the HTLC switch persisted
/// on its record after accepting it as our final hop, once its invoice is settled (B5-LCL-RO-02), and the switch is
/// asked to decide while it has not.
/// </summary>
/// <remarks>
/// The HTLC switch persists the preimage on an incoming HTLC's record (<see cref="HtlcRecord.KnownPreimage"/>) only
/// after <c>FinalHopProcessor</c> accepted the HTLC and its whole set is complete: a part whose channel is closing on
/// chain is committed that way instead of being fulfilled, and every part of a set it settles is committed before the
/// settle. The settle is the commit point: a mark is used only once the invoice is <c>Settled</c> with that preimage
/// (see <see cref="GetAcceptedPreimageAsync"/>). An HTLC whose onion made us an intermediate hop never gets it, so an
/// invoice's preimage is never revealed for it.
/// </remarks>
internal static class FinalHopClaims
{
    /// <summary>
    /// The preimage the switch persisted on the incoming HTLC <paramref name="record"/> when it accepted it as our
    /// final hop, checked against its payment hash; null otherwise.
    /// </summary>
    /// <remarks>
    /// The invoice's <c>Settled</c> save is the commit point of a set (NL-322/NL-323): the switch marks the other parts
    /// before that save, so a mark alone does not mean the set was committed to (the set may have become incomplete, or
    /// we stopped in between). The preimage is returned only when the invoice of the hash is <c>Settled</c> with that
    /// preimage and the HTLC was not failed off chain (no fail removal in its record), so an incomplete, unsettled set
    /// is never claimed on chain and an HTLC we failed is never claimed (BOLT 4: fulfill the entire set or fail it).
    /// </remarks>
    public static async Task<byte[]?> GetAcceptedPreimageAsync(IUnitOfWork unitOfWork, HtlcRecord? record)
    {
        if (record is not { Direction: HtlcDirection.Incoming, KnownPreimage: { } known }
         || record.Removal is { IsFulfill: false }
         || !Hashes(known, record.PaymentHash))
            return null;

        var invoice = await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(record.PaymentHash);
        return invoice is { Status: InvoiceStatus.Settled } && invoice.Preimage == known ? (byte[])known : null;
    }

    /// <summary>
    /// The switch event that makes it decide on an incoming HTLC of a channel closing on chain: an
    /// <see cref="IncomingHtlcLockedIn"/> for an HTLC irrevocably committed by the peer, without our removal, not
    /// expired at <paramref name="height"/>, that is not a forward (no outgoing HTLC carries its origin, no circuit),
    /// whose payment hash is one of our <c>Open</c> invoices. The switch accepts it with the final-hop checks and
    /// persists the preimage (and settles the invoice), or leaves it to time out. Null when there is nothing to decide.
    /// </summary>
    /// <remarks>Raised every round while the invoice stays <c>Open</c> (the switch is idempotent): after a restart, or
    /// when the switch was away, the next round asks again.</remarks>
    public static async Task<IncomingHtlcLockedIn?> GetFinalHopDecisionAsync(IUnitOfWork unitOfWork,
                                                                            ChannelId channelId, HtlcRecord? record,
                                                                            uint height)
    {
        if (record is not { Direction: HtlcDirection.Incoming, State: HtlcState.RcvdAddAckRevocation, Removal: null }
         || height >= record.CltvExpiry)
            return null;

        var forwards = await unitOfWork.ChannelStateDbRepository.FindHtlcsByOriginAsync(
                           HtlcOrigin.Forwarded(channelId, record.Id));
        if (forwards.Count > 0
         || await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(channelId, record.Id) is not null)
            return null;

        var invoice = await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(record.PaymentHash);
        return invoice is { Status: InvoiceStatus.Open } ? new IncomingHtlcLockedIn(channelId, record) : null;
    }

    private static bool Hashes(Secret preimage, Hash paymentHash) =>
        System.Security.Cryptography.SHA256.HashData((byte[])preimage).AsSpan().SequenceEqual((byte[])paymentHash);
}