namespace NLightning.Domain.Bitcoin.Interfaces;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Transactions.Models;
using ValueObjects;

/// <summary>
/// Interface for transaction signing services that can be implemented either locally 
/// or delegated to external services like VLS (Validating Lightning Signer)
/// </summary>
public interface ILightningSigner
{
    /// <summary>
    /// Generate a new channel key set and return the channel key index
    /// </summary>
    uint CreateNewChannel(out ChannelBasepoints basepoints, out CompactPubKey firstPerCommitmentPoint);

    /// <summary>
    /// Generate or retrieve channel basepoints for a channel
    /// </summary>
    ChannelBasepoints GetChannelBasepoints(uint channelKeyIndex);

    /// <summary>
    /// Generate or retrieve channel basepoints for a channel
    /// </summary>
    ChannelBasepoints GetChannelBasepoints(ChannelId channelId);

    /// <summary>
    /// Get the node's public key
    /// </summary>
    CompactPubKey GetNodePublicKey();

    /// <summary>
    /// Generate the per-commitment point of one of our commitment transactions.
    /// </summary>
    /// <param name="channelKeyIndex">The channel key index.</param>
    /// <param name="commitmentNumber">
    /// The commitment number (0 for the first commitment), <b>not</b> a BOLT 3 index: the signer derives the secret at
    /// index <c>2^48 - 1 - commitmentNumber</c> (<see cref="Protocol.Models.PerCommitmentIndex"/>).
    /// </param>
    CompactPubKey GetPerCommitmentPoint(uint channelKeyIndex, ulong commitmentNumber);

    /// <summary>
    /// Generate the per-commitment point of one of our commitment transactions.
    /// </summary>
    /// <param name="channelId">The registered channel.</param>
    /// <param name="commitmentNumber">
    /// The commitment number (0 for the first commitment), <b>not</b> a BOLT 3 index: the signer derives the secret at
    /// index <c>2^48 - 1 - commitmentNumber</c> (<see cref="Protocol.Models.PerCommitmentIndex"/>).
    /// </param>
    CompactPubKey GetPerCommitmentPoint(ChannelId channelId, ulong commitmentNumber);

    /// <summary>
    /// Store channel information needed for signing
    /// </summary>
    void RegisterChannel(ChannelId channelId, ChannelSigningInfo signingInfo);

    /// <summary>
    /// Reveal the per-commitment secret of one of our commitment transactions, for <c>revoke_and_ack</c> or
    /// <c>channel_reestablish</c>.
    /// </summary>
    /// <remarks>
    /// Guard (NL-189, BOLT2 plan invariant I3): the secret of commitment <c>n</c> is released only when
    /// <c>n &lt; LocalCommitmentNumber</c>, i.e. when commitment <c>n</c> has been superseded by a newer local commitment
    /// that was persisted with the peer's signatures and reported through <see cref="AdvanceLocalCommitment"/>.
    /// Revealing the secret of our current commitment would let the peer take every output of it.
    /// </remarks>
    /// <param name="channelId">The registered channel.</param>
    /// <param name="commitmentNumber">The commitment number (not a BOLT 3 index).</param>
    /// <exception cref="Exceptions.SignerException">
    /// The channel is not registered, or commitment <paramref name="commitmentNumber"/> is not revoked yet.
    /// </exception>
    Secret RevealPerCommitmentSecret(ChannelId channelId, ulong commitmentNumber);

    /// <summary>
    /// Tell the signer that local commitment <paramref name="newLocalCommitmentNumber"/> (with the peer's commitment
    /// and HTLC signatures) is now persisted, so every older local commitment may be revoked. Call it only
    /// <b>after</b> the save succeeded.
    /// </summary>
    /// <exception cref="Exceptions.SignerException">
    /// The channel is not registered, or the number is lower than the current one (numbers never go back).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The number does not fit in 48 bits.</exception>
    void AdvanceLocalCommitment(ChannelId channelId, ulong newLocalCommitmentNumber);

    /// <summary>
    /// Sign the counterparty's HTLC transactions for a <c>commitment_signed</c> we send: the HTLC transactions of the
    /// <b>remote</b> commitment, in commitment output order.
    /// </summary>
    /// <remarks>
    /// Each signature uses our HTLC key for that commitment,
    /// <c>htlc_basepoint_secret + SHA256(remote_per_commitment_point || htlc_basepoint)</c>, with
    /// <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> under option_anchors and <c>SIGHASH_ALL</c> otherwise. Signatures are
    /// low-S. The returned list has the order of <paramref name="htlcTransactions"/>.
    /// </remarks>
    /// <param name="channelId">The registered channel.</param>
    /// <param name="htlcTransactions">The remote commitment's HTLC transactions, each with the remote point.</param>
    IReadOnlyList<CompactSignature> SignRemoteHtlcTransactions(ChannelId channelId,
                                                               IReadOnlyList<HtlcSigningContext> htlcTransactions);

    /// <summary>
    /// Verify the HTLC signatures of a received <c>commitment_signed</c>: one per HTLC transaction of our <b>local</b>
    /// commitment, in the same order.
    /// </summary>
    /// <remarks>
    /// Each signature must be a low-S signature by the peer's HTLC key for that commitment,
    /// <c>remote_htlc_basepoint + SHA256(local_per_commitment_point || remote_htlc_basepoint) * G</c>, over
    /// <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> under option_anchors and <c>SIGHASH_ALL</c> otherwise.
    /// </remarks>
    /// <exception cref="Exceptions.SignerException">
    /// The counts differ, a signature does not parse, is high-S or does not verify, or the channel is not registered
    /// or has no remote HTLC basepoint.
    /// </exception>
    void ValidateLocalHtlcSignatures(ChannelId channelId, IReadOnlyList<HtlcSigningContext> htlcTransactions,
                                     IReadOnlyList<CompactSignature> signatures);

    /// <summary>
    /// Sign one HTLC transaction of our own (local) commitment, for broadcast together with the peer's signature: our
    /// local HTLC key for that commitment, always <c>SIGHASH_ALL</c>.
    /// </summary>
    CompactSignature SignLocalHtlcTransaction(ChannelId channelId, HtlcSigningContext htlcTransaction);

    /// <summary>
    /// Sign a general transaction using the wallet signing context
    /// </summary>
    bool SignWalletTransaction(SignedTransaction unsignedTransaction);

    /// <summary>
    /// Sign a funding transaction using the wallet signing context  and validating using the channel context
    /// </summary>
    bool SignFundingTransaction(ChannelId channelId, SignedTransaction unsignedTransaction);

    /// <summary>
    /// Sign a transaction using the channel's signing context
    /// </summary>
    CompactSignature SignChannelTransaction(ChannelId channelId, SignedTransaction unsignedTransaction);

    /// <summary>
    /// Verify a signature against a transaction
    /// </summary>
    void ValidateSignature(ChannelId channelId, CompactSignature signature, SignedTransaction unsignedTransaction);
}