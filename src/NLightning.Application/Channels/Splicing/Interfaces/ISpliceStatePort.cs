namespace NLightning.Application.Channels.Splicing.Interfaces;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// The channel-state half of a splice that the negotiation (lane SP1-D) drives but does not own (splicing plan §3.3,
/// §3.8): the channel's fundings (<see cref="FundingSet"/>), the splice commitment step of the engine (lane SP1-B,
/// SP1-B-T3), the per-funding signer (lane SP1-C, SP1-C-T1/T2) and the <c>ChannelFundings</c> rows (lane SP1-C,
/// migration <c>AddSpliceFundings</c>).
/// </summary>
/// <remarks>
/// <para>Every member is called under the channel's lock. The <c>Stage*</c>/<c>Sign*</c>/<c>Receive*</c> members only
/// stage writes on the given unit of work; the caller saves, then calls the matching <c>On*Saved</c>/<c>Apply*</c>
/// member (persist, then memory, then send; SP-I7).</para>
/// <para>Until lanes SP1-B and SP1-C land, the registered default is <see cref="UnavailableSpliceStatePort"/>, which
/// reports a channel that was never spliced and refuses everything else: the integrator replaces it with the adapter
/// over the engine and the signer.</para>
/// </remarks>
public interface ISpliceStatePort
{
    /// <summary>The channel's fundings: the current one and the pending splices.</summary>
    /// <exception cref="InvalidOperationException">The channel's funding outpoint is not known.</exception>
    FundingSet GetFundings(ChannelModel channel);

    /// <summary>
    /// SP-CS-01: our <c>commitment_signed</c> for the peer's commitment spending the new funding
    /// <paramref name="funding"/> (its outpoint, capacity, keys and the contributions as balance deltas), at the peer's
    /// current commitment number, with the current feerate and signatures for every pending HTLC, and
    /// <c>funding_txid</c> set (SP-OP-02). The funding is registered with the signer (<c>RegisterFunding</c>); what
    /// must be remembered is staged on <paramref name="unitOfWork"/> (the interactive-tx row is the driver's).
    /// </summary>
    Task<CommitmentSignedMessage> SignSpliceCommitmentAsync(ChannelModel channel, ChannelFunding funding,
                                                            IUnitOfWork unitOfWork,
                                                            CancellationToken cancellationToken);

    /// <summary>
    /// SP-CS-02: verifies the peer's <c>commitment_signed</c> for our commitment spending <paramref name="funding"/> at
    /// our current commitment number (no <c>revoke_and_ack</c>) and stages it (SP-I2: every active funding
    /// broadcastable).
    /// </summary>
    /// <exception cref="Exceptions.SpliceCommitmentException">A signature is missing or invalid.</exception>
    Task ReceiveSpliceCommitmentAsync(ChannelModel channel, ChannelFunding funding, CommitmentSignedMessage message,
                                      IUnitOfWork unitOfWork, CancellationToken cancellationToken);

    /// <summary>
    /// After the save of <see cref="ReceiveSpliceCommitmentAsync"/>: invariant SP-I1 (<c>MarkSpliceCommitmentPersisted</c>,
    /// before any <c>shared_input_signature</c> is made) and the memory state.
    /// </summary>
    void OnSpliceCommitmentSaved(ChannelModel channel, ChannelFunding funding);

    /// <summary>The set with <paramref name="funding"/> added as pending (<see cref="FundingSet.AddPending"/>).</summary>
    FundingSet AddPending(FundingSet fundings, ChannelFunding funding);

    /// <summary>
    /// The set once <paramref name="fundingTxId"/> is locked both ways (<see cref="FundingSet.Lock"/>): it becomes the
    /// current funding with its deltas folded; the former current one and its siblings leave the set.
    /// </summary>
    (FundingSet Next, IReadOnlyList<ChannelFunding> Retired) Lock(FundingSet fundings, TxId fundingTxId);

    /// <summary>Stages <paramref name="next"/> (and the fundings that left it) on <paramref name="unitOfWork"/>.</summary>
    Task StageFundingsAsync(ChannelModel channel, FundingSet next, IReadOnlyList<ChannelFunding> retired,
                            IUnitOfWork unitOfWork, CancellationToken cancellationToken);

    /// <summary>
    /// After the save of <see cref="StageFundingsAsync"/>: the fundings in memory (engine, signer: a new pending funding
    /// registered, a lock applied with <c>LockFunding</c>).
    /// </summary>
    void ApplyFundings(ChannelModel channel, FundingSet next, IReadOnlyList<ChannelFunding> retired);
}