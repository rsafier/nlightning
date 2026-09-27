namespace NLightning.Domain.Channels.Splicing.Interfaces;

using Crypto.ValueObjects;
using Models;
using Node.Options;
using Persistence.Interfaces;
using Protocol.Interfaces;
using Protocol.Messages;
using ValueObjects;

/// <summary>
/// Runs splices (BOLT 2 "Channel Splicing"; splicing plan §3.5, lane SP1-D): the operator's splice-in/splice-out and
/// the peer's <c>splice_init</c>/<c>splice_ack</c>. Implemented by the Application <c>SpliceService</c>, a singleton,
/// registered by <c>AddSpliceServices()</c>.
/// </summary>
/// <remarks>
/// <para>The splice transaction is built by the interactive-tx driver (<c>IInteractiveTxDriver</c>, Application) with
/// the splice as its host: the Application <c>SpliceNegotiationHost : IInteractiveTxHost</c> (purpose
/// <c>InteractiveTxPurpose.Splice</c>) supplies the <c>SharedFundingSpec</c> (the current funding as shared input, the
/// new funding output), the splice <c>commitment_signed</c> (SP-CS-01), the shared-input signature (SP-SIG-01, through
/// <c>ILightningSigner.SignSpliceSharedInput</c>) and the completion (pending funding, broadcast row, quiescence ended,
/// SP-Q-01). The <c>tx_*</c> messages keep flowing through the existing interactive-tx handlers.</para>
/// <para>Lock discipline as <see cref="Quiescence.IQuiescenceService"/>: <see cref="StartAsync"/> takes the channel's
/// lock itself (never call it while holding one); the <c>Handle*</c> members run under the lock the
/// <c>ChannelManager</c> holds for the message, stage their writes on the given unit of work and return the replies in
/// wire order (persist before send, SP-I7). Splicing requires both <c>option_quiesce</c> and <c>option_splice</c>
/// negotiated (D14).</para>
/// </remarks>
public interface ISpliceService
{
    /// <summary>
    /// Starts a splice we initiate (SP-S-01/02): checks the rules and our balance or wallet, requests quiescence
    /// (<c>QuiescencePurpose.Splice</c>), then sends <c>splice_init</c> under the lock and drives the negotiation.
    /// </summary>
    /// <returns>Completes once the negotiation is signed or ended, with where it got.</returns>
    /// <exception cref="InvalidOperationException">A rule refuses the splice (not negotiated, not <c>Open</c>,
    /// <c>shutdown</c> sent, a splice unlocked, amount above our balance or the wallet).</exception>
    /// <exception cref="KeyNotFoundException">Unknown channel.</exception>
    Task<SpliceResult> StartAsync(SpliceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Under the lock: the peer's <c>splice_init</c> (SP-R-01). Accepts with contribution 0 (D10) and a new funding
    /// key: <c>splice_ack</c>, then the driver starts as non-initiator; rejects with <c>tx_abort</c>.
    /// </summary>
    /// <exception cref="Exceptions.ChannelWarningException">A rule the spec answers with "warning and close"
    /// (not quiescent, sender not the quiescence initiator, a splice in progress or unlocked, <c>shutdown</c>
    /// received, a splice-out above the sender's balance).</exception>
    Task<IReadOnlyList<IChannelMessage>> HandleSpliceInitAsync(SpliceInitMessage message,
                                                               FeatureOptions negotiatedFeatures,
                                                               CompactPubKey peerPubKey, IUnitOfWork unitOfWork,
                                                               CancellationToken cancellationToken = default);

    /// <summary>
    /// Under the lock: the peer's <c>splice_ack</c> to our <c>splice_init</c> (SP-R-02): the driver starts as
    /// initiator with the shared input and output; rejects with <c>tx_abort</c>.
    /// </summary>
    /// <exception cref="Exceptions.ChannelWarningException">No <c>splice_init</c> of ours is waiting, or a splice-out
    /// above the sender's balance.</exception>
    Task<IReadOnlyList<IChannelMessage>> HandleSpliceAckAsync(SpliceAckMessage message,
                                                              FeatureOptions negotiatedFeatures,
                                                              CompactPubKey peerPubKey, IUnitOfWork unitOfWork,
                                                              CancellationToken cancellationToken = default);

    /// <summary>The negotiation in progress (or last signed) on <paramref name="channelId"/>, or null.</summary>
    SpliceNegotiationModel? GetNegotiation(ChannelId channelId);
}