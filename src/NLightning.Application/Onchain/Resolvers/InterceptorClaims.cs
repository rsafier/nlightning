namespace NLightning.Application.Onchain.Resolvers;

using Domain.Accounting.Constants;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Payments.Trampoline;

/// <summary>
/// The HTLC interceptor side of the preimage claims of the local and remote commitment resolvers (NL-1182, LND's on-chain
/// interception): a forward whose incoming channel went on chain before it was forwarded is held for the interceptor,
/// and its settle persists the preimage on the incoming HTLC's record (<see cref="HtlcRecord.KnownPreimage"/>) with the
/// <c>InterceptedHtlcSettled</c> accounting event in the same save. The resolvers claim the HTLC with that preimage.
/// </summary>
internal static class InterceptorClaims
{
    /// <summary>
    /// The preimage the interceptor's settle persisted on the incoming HTLC <paramref name="record"/> of
    /// <paramref name="channelId"/>, checked against its payment hash; null otherwise (an outgoing HTLC, one failed off
    /// chain, a final hop's mark of one of our invoices, a trampoline relay part).
    /// </summary>
    /// <remarks>
    /// The settle's own <c>InterceptedHtlcSettled</c> event proves it. Without the event (the accounting feed's gate
    /// drops live events until a cutover succeeds, NL-619), a <c>KnownPreimage</c> on an incoming HTLC is the
    /// interceptor's when the hash is none of our invoices (a final hop's mark always has its invoice, keysend
    /// included, and is claimed only once that invoice is settled, <see cref="FinalHopClaims"/>) and the HTLC is no
    /// trampoline relay part (<see cref="TrampolineRelayClaims"/>).
    /// </remarks>
    public static async Task<byte[]?> GetSettledPreimageAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                             HtlcRecord? record)
    {
        if (record is not { Direction: HtlcDirection.Incoming, KnownPreimage: { } known }
         || record.Removal is { IsFulfill: false }
         || !Hashes(known, record.PaymentHash))
            return null;

        if (unitOfWork.AccountingEventDbRepository is { } events
         && await events.ExistsAsync(AccountingEventKeys.InterceptedHtlcSettled(channelId, record.Id)))
            return known;

        if (await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(record.PaymentHash) is not null
         || await TrampolineRelayReads.GetPartAsync(unitOfWork, channelId, record.Id) is not null)
            return null;

        return known;
    }

    private static bool Hashes(Secret preimage, Hash paymentHash) =>
        System.Security.Cryptography.SHA256.HashData((byte[])preimage).AsSpan().SequenceEqual((byte[])paymentHash);
}