namespace NLightning.Domain.Channels.Interfaces;

using Crypto.ValueObjects;
using Money;
using Payments.ValueObjects;
using Persistence.Interfaces;
using Protocol.Onion.Enums;
using Protocol.Onion.Models;
using Protocol.Onion.ValueObjects;
using Protocol.Tlv;
using ValueObjects;

/// <summary>
/// The only way code outside the channel layer (payments, the HTLC switch, IPC) changes a channel's HTLCs or feerate
/// (BOLT2 plan §3.10, N6-T2; implemented by <c>ChannelOperationsService</c>).
/// </summary>
/// <remarks>
/// Every method follows the same contract (I8, D3):
/// <list type="number">
///   <item>It takes the channel's lock (<see cref="IChannelLockProvider"/>). Callers must not hold any channel lock
///   when they call it: never two locks, never re-entrant. An HTLC switch handling an event of channel A therefore
///   calls this for channel B only after it released A's lock.</item>
///   <item>It checks the preconditions: HTLCs are enabled (<c>NodeOptions.HtlcsEnabled</c>), the channel is
///   <c>Open</c>, its peer is connected and <c>channel_reestablish</c> was processed, the channel is not
///   <c>Failed</c> and no <c>shutdown</c> was exchanged, plus the BOLT 2 sender rules of the commitment engine.
///   A precondition that fails throws <see cref="Exceptions.CommitmentRefusedException"/> (nothing persisted,
///   nothing sent; callers map it to a local failure such as <c>temporary_channel_failure</c>). An unknown channel
///   throws <see cref="KeyNotFoundException"/>.</item>
///   <item>It applies the update to the commitment engine and <b>persists</b> the resulting transition (and, for an
///   offer, the <see cref="HtlcOrigin"/>) in one <c>IUnitOfWork.SaveChangesAsync</c> before anything is sent.</item>
///   <item>Only after the save it enqueues the wire message on the peer's outbox, then asks the commit scheduler to
///   send a <c>commitment_signed</c> (debounced; never while one is unacknowledged). The returned task completes
///   when the update is persisted and queued, not when it is committed or resolved.</item>
/// </list>
/// Resolutions are reported through the channel domain events raised after the corresponding save
/// (<c>IChannelDomainEvent</c>, re-derived from the persisted state on startup): <c>IncomingHtlcLockedIn</c> once an
/// incoming add is irrevocably committed, <c>OutgoingHtlcFulfilled</c> as soon as the peer sends the preimage,
/// <c>OutgoingHtlcFailed</c> only once the peer's removal is irrevocably committed, and <c>OutgoingHtlcSettled</c>
/// when an outgoing HTLC is gone from both commitments.
/// </remarks>
public interface IChannelOperations
{
    /// <summary>
    /// Offers an HTLC to the channel's peer (<c>update_add_htlc</c>).
    /// </summary>
    /// <param name="channelId">The outgoing channel.</param>
    /// <param name="amount">The HTLC amount (<c>amount_msat</c>).</param>
    /// <param name="paymentHash">The payment hash.</param>
    /// <param name="cltvExpiry">The absolute <c>cltv_expiry</c>.</param>
    /// <param name="onion">The 1366-byte onion for the peer.</param>
    /// <param name="pathKey">The route-blinding path key to put in the <c>update_add_htlc</c> TLVs, if any.</param>
    /// <param name="origin">What the HTLC belongs to; persisted atomically with the add. Must be
    /// <see cref="HtlcOrigin.IsValid"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the channel lock; once the save started it completes.</param>
    /// <returns>The HTLC id assigned to the add. Recording it on the payment or circuit is a later save, so a crash
    /// can leave the HTLC live while its payment or circuit does not know the id yet (see
    /// <c>IForwardCircuitDbRepository</c> and <c>IPaymentDbRepository</c>).</returns>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is not valid (for example
    /// <c>default(HtlcOrigin)</c>). Nothing is persisted.</exception>
    Task<ulong> OfferHtlcAsync(ChannelId channelId, LightningMoney amount, Hash paymentHash, uint cltvExpiry,
                               OnionPacket onion, BlindedPathTlv? pathKey, HtlcOrigin origin,
                               CancellationToken cancellationToken = default);

    /// <summary>
    /// Fulfills an incoming HTLC (<c>update_fulfill_htlc</c>). The HTLC must be locked in, and
    /// SHA256(<paramref name="paymentPreimage"/>) must equal its payment hash. The preimage is persisted before the
    /// message is sent, so a crash can never lose it.
    /// </summary>
    Task FulfillHtlcAsync(ChannelId channelId, ulong htlcId, Secret paymentPreimage,
                          CancellationToken cancellationToken = default);

    /// <summary>
    /// Fulfills an incoming HTLC like <see cref="FulfillHtlcAsync(ChannelId, ulong, Secret, CancellationToken)"/> and
    /// commits <paramref name="stageWithFulfill"/>'s writes in the <b>same</b> save as the fulfill (for example the
    /// final hop's invoice moving to <c>Settled</c>, NL-253), so neither can be persisted without the other.
    /// </summary>
    /// <param name="channelId">The incoming channel.</param>
    /// <param name="htlcId">The peer's id of the HTLC.</param>
    /// <param name="paymentPreimage">The preimage.</param>
    /// <param name="stageWithFulfill">Called under the channel's lock with the unit of work of the fulfill, after the
    /// transition is staged and before the save. It must only stage writes (never save). If it throws, nothing is
    /// persisted or sent and the exception propagates.</param>
    /// <param name="cancellationToken">Cancels waiting for the channel lock; once the save started it completes.</param>
    Task FulfillHtlcAsync(ChannelId channelId, ulong htlcId, Secret paymentPreimage,
                          Func<IUnitOfWork, Task> stageWithFulfill, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fulfills an incoming HTLC like <see cref="FulfillHtlcAsync(ChannelId, ulong, Secret, CancellationToken)"/> and
    /// sends <paramref name="attribution"/> with it: the <c>attribution_data</c> TLV and, when there is one, the
    /// <c>fulfillment_payload</c> TLV (BOLT 2/4, <c>option_attribution_data</c>). Both are persisted with the fulfill,
    /// so a retransmission after a reconnection sends them again.
    /// </summary>
    /// <param name="channelId">The incoming channel.</param>
    /// <param name="htlcId">The peer's id of the HTLC.</param>
    /// <param name="paymentPreimage">The preimage.</param>
    /// <param name="attribution">What <c>IAttributionDataService.CreateFulfillment</c> (final node) or
    /// <c>WrapFulfillment</c> (intermediate node) returned.</param>
    /// <param name="stageWithFulfill">As in
    /// <see cref="FulfillHtlcAsync(ChannelId, ulong, Secret, Func{IUnitOfWork, Task}, CancellationToken)"/>; null for
    /// none.</param>
    /// <param name="cancellationToken">Cancels waiting for the channel lock; once the save started it completes.</param>
    /// <exception cref="ArgumentException">The <c>fulfillment_payload</c> is longer than 32768 bytes (BOLT 2: the peer
    /// would fail the channel). Nothing is persisted.</exception>
    Task FulfillHtlcAsync(ChannelId channelId, ulong htlcId, Secret paymentPreimage,
                          AttributedFulfillment attribution, Func<IUnitOfWork, Task>? stageWithFulfill = null,
                          CancellationToken cancellationToken = default);

    /// <summary>
    /// Fails an incoming HTLC (<c>update_fail_htlc</c>) with an already encrypted error onion
    /// (<c>IFailureOnionService</c>, created or wrapped with the HTLC's stored shared secret).
    /// </summary>
    Task FailHtlcAsync(ChannelId channelId, ulong htlcId, ReadOnlyMemory<byte> reason,
                       CancellationToken cancellationToken = default);

    /// <summary>
    /// Fails an incoming HTLC (<c>update_fail_htlc</c>) with a return packet and its <c>attribution_data</c>
    /// (BOLT 4 attributable failures): what <c>IAttributionDataService.CreateErrorPacket</c> (erring node) or
    /// <c>WrapErrorPacket</c> (intermediate node) returned. The attribution is persisted with the failure, so a
    /// retransmission after a reconnection sends it again.
    /// </summary>
    Task FailHtlcAsync(ChannelId channelId, ulong htlcId, AttributedErrorPacket errorPacket,
                       CancellationToken cancellationToken = default);

    /// <summary>
    /// This node's BOLT 4 hold time for an incoming HTLC, to report in the <c>attribution_data</c> of its removal:
    /// the time since its <c>update_add_htlc</c> was received and persisted
    /// (<see cref="IChannelStateDbRepository.GetHtlcAddedAtAsync"/>), in units of 100 ms (rounded down). Zero ("no
    /// timing information", which BOLT 4 allows) when the receipt time is unknown (an HTLC received before migration
    /// <c>AddAttributionData</c>, or no row). Reads only; takes no channel lock.
    /// </summary>
    /// <param name="channelId">The incoming channel.</param>
    /// <param name="htlcId">The peer's id of the HTLC.</param>
    /// <param name="cancellationToken">Unused by the read; kept for symmetry.</param>
    Task<uint> GetHoldTimeAsync(ChannelId channelId, ulong htlcId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fails an incoming HTLC whose onion we could not parse (<c>update_fail_malformed_htlc</c>).
    /// <paramref name="failureCode"/> must have the BADONION bit set and <paramref name="sha256OfOnion"/> is
    /// SHA256 of the onion we received.
    /// </summary>
    Task FailMalformedHtlcAsync(ChannelId channelId, ulong htlcId, FailureCode failureCode, Hash sha256OfOnion,
                                CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends <c>update_fee</c> (funder only).
    /// </summary>
    Task UpdateFeeAsync(ChannelId channelId, uint feeratePerKw, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the shared secret of an incoming HTLC's onion, so a later failure can be wrapped for the origin even
    /// after a restart (ONION M4-T5). Call it after the peel and before the HTLC is forwarded or failed; it is
    /// persisted but sends nothing.
    /// </summary>
    Task RecordOnionSecretAsync(ChannelId channelId, ulong htlcId, Secret sharedSecret,
                                CancellationToken cancellationToken = default);
}