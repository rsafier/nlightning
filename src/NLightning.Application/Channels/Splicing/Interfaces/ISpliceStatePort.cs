namespace NLightning.Application.Channels.Splicing.Interfaces;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Crypto.ValueObjects;
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
/// <para>The registered implementation is <see cref="EngineSpliceStatePort"/>; the splice harness replaces it with an
/// in-memory stand-in.</para>
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
    /// must be remembered is staged on <paramref name="unitOfWork"/> (the interactive-tx row is the driver's). A simple
    /// taproot channel (NL-965) signs a MuSig2 partial signature against <paramref name="remoteNonce"/>, the peer's
    /// verification nonce of that commitment (its <c>tx_complete</c> <c>commit_nonces</c>, or the
    /// <c>current_commit_nonce</c> of its <c>channel_reestablish</c> for a retransmission).
    /// </summary>
    Task<CommitmentSignedMessage> SignSpliceCommitmentAsync(ChannelModel channel, ChannelFunding funding,
                                                            IUnitOfWork unitOfWork,
                                                            CancellationToken cancellationToken,
                                                            MusigPublicNonce? remoteNonce = null);

    /// <summary>
    /// SP-CS-02: verifies the peer's <c>commitment_signed</c> for our commitment spending <paramref name="funding"/> at
    /// our current commitment number (no <c>revoke_and_ack</c>) and stages it (SP-I2: every active funding
    /// broadcastable).
    /// </summary>
    /// <exception cref="Exceptions.SpliceCommitmentException">A signature is missing or invalid.</exception>
    /// <remarks>A simple taproot channel (NL-965) takes <paramref name="remoteNextNonce"/>, the peer's verification
    /// nonce of its next commitment on the new funding (its <c>tx_complete</c> <c>commit_nonces</c>, or its
    /// <c>channel_reestablish</c> map), with the funding: the next batch signs that funding against it.</remarks>
    Task ReceiveSpliceCommitmentAsync(ChannelModel channel, ChannelFunding funding, CommitmentSignedMessage message,
                                      IUnitOfWork unitOfWork, CancellationToken cancellationToken,
                                      MusigPublicNonce? remoteNextNonce = null);

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

    /// <summary>
    /// The set without the pending funding <paramref name="fundingTxId"/>: a splice whose negotiation ended with
    /// <c>tx_abort</c> after the commitment step (the peer's <c>commitment_signed</c> may already have made it pending,
    /// SP-CS-02), before any <c>tx_signatures</c> of ours. The funding comes back
    /// <see cref="Domain.Channels.Splicing.Enums.ChannelFundingStatus.Discarded"/> in <c>Retired</c>; a funding that is not
    /// pending leaves the set unchanged with nothing retired.
    /// </summary>
    (FundingSet Next, IReadOnlyList<ChannelFunding> Retired) Discard(FundingSet fundings, TxId fundingTxId);

    /// <summary>
    /// Stages <paramref name="next"/> (and the fundings that left it) on <paramref name="unitOfWork"/>: a lock when
    /// <paramref name="next"/>'s current funding is another one, otherwise the pending fundings' flags and the discarded
    /// ones.
    /// </summary>
    Task StageFundingsAsync(ChannelModel channel, FundingSet next, IReadOnlyList<ChannelFunding> retired,
                            IUnitOfWork unitOfWork, CancellationToken cancellationToken);

    /// <summary>
    /// After the save of <see cref="StageFundingsAsync"/>: the fundings in memory (engine, signer: a new pending funding
    /// registered, a lock applied with <c>LockFunding</c>).
    /// </summary>
    void ApplyFundings(ChannelModel channel, FundingSet next, IReadOnlyList<ChannelFunding> retired);
}