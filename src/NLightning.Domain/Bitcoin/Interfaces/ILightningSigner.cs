namespace NLightning.Domain.Bitcoin.Interfaces;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Onchain.Models;
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
    /// Sign a 32-byte message hash with the node key (the key behind <see cref="GetNodePublicKey"/>), as BOLT 7
    /// gossip requires: <c>channel_update</c>, <c>node_announcement</c> and the node signatures of
    /// <c>channel_announcement</c>/<c>announcement_signatures</c>.
    /// </summary>
    /// <remarks>
    /// The caller computes the hash (BOLT 7: the double-SHA256 of the message after the signature, e.g.
    /// <see cref="Protocol.Payloads.ChannelUpdatePayload.GetSignatureHash"/>); the signer signs those 32 bytes as they
    /// are (no further hashing). The signature is deterministic (RFC 6979), low-S, as a 64-byte compact
    /// <c>r || s</c>.
    /// </remarks>
    CompactSignature SignNodeMessage(Hash messageHash);

    /// <summary>
    /// Verify a BOLT 7 node signature: <paramref name="signature"/> (64-byte compact) over the 32-byte
    /// <paramref name="messageHash"/> by <paramref name="nodeId"/>.
    /// </summary>
    /// <remarks>
    /// A high-S signature is accepted (normalized first), because ECDSA signatures are malleable and BOLT 7 expects
    /// relayed messages with <c>-s</c>. Returns <c>false</c> (never throws) for a signature or node id that does not
    /// parse.
    /// </remarks>
    bool VerifyNodeMessage(Hash messageHash, CompactSignature signature, CompactPubKey nodeId);

    /// <summary>
    /// Sign our half of a public channel's <c>channel_announcement</c> (BOLT 7, for <c>announcement_signatures</c>):
    /// the node signature with the node key and the bitcoin signature with the channel's funding key, both over the
    /// double-SHA256 of <paramref name="unsignedAnnouncement"/>.
    /// </summary>
    /// <remarks>
    /// No blind hash signing: the signer parses the announcement and refuses (with a
    /// <see cref="Exceptions.SignerException"/>) unless it names our chain, <paramref name="shortChannelId"/> (which
    /// must also be the channel's real short channel id when the signer knows it, and point at the channel's funding
    /// output index), our node id and the peer's (when known) as <c>node_id_1</c>/<c>node_id_2</c> in ascending order,
    /// and the channel's funding keys as the matching <c>bitcoin_key_1</c>/<c>bitcoin_key_2</c>. Refused as well after
    /// data loss and for a private channel (<see cref="ChannelSigningInfo.AnnounceChannel"/> false: BOLT 7 forbids
    /// <c>announcement_signatures</c> without <c>announce_channel</c>). Signatures are RFC 6979, low-S, 64-byte
    /// compact.
    /// </remarks>
    /// <param name="channelId">The registered (or persisted) channel.</param>
    /// <param name="unsignedAnnouncement">
    /// The announcement's signed data: every byte of the <c>channel_announcement</c> payload after the four signatures
    /// (from <c>len</c>/<c>features</c> to the end, including unknown trailing bytes), i.e. the payload bytes from
    /// offset 256.
    /// </param>
    /// <param name="shortChannelId">The short channel id the caller announces.</param>
    ChannelAnnouncementSignatures SignChannelAnnouncement(ChannelId channelId, ReadOnlyMemory<byte> unsignedAnnouncement,
                                                          ShortChannelId shortChannelId);

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
    /// Store channel information needed for signing. The revocation guard only moves forward, and the sticky marks
    /// are only ever added: <see cref="ChannelSigningInfo.DataLossDetected"/> applies <see cref="MarkDataLoss"/> and
    /// <see cref="ChannelSigningInfo.BroadcastSignedCommitmentNumber"/> applies <see cref="MarkBroadcastSigned"/>
    /// (invariant S1 across restarts). Registering a channel again refreshes what may have become known since (the
    /// real short channel id, the peer's node id and htlc_basepoint), but never its keys or funding outpoint: a
    /// registration with other keys or another funding outpoint under a registered id throws a
    /// <see cref="Exceptions.SignerException"/> and changes nothing (no guard, no mark).
    /// </summary>
    /// <remarks>
    /// Registration is optional for a persisted channel (NL-067): a signer built with an
    /// <see cref="IChannelSigningInfoSource"/> loads and registers a channel it does not know from the database the
    /// first time it is asked about it (with its local commitment number, data-loss flag and broadcast mark).
    /// </remarks>
    void RegisterChannel(ChannelId channelId, ChannelSigningInfo signingInfo);

    /// <summary>
    /// Reveal the per-commitment secret of one of our commitment transactions, for <c>revoke_and_ack</c> or
    /// <c>channel_reestablish</c>.
    /// </summary>
    /// <remarks>
    /// Guard (NL-189, BOLT2 plan invariant I3): the secret of commitment <c>n</c> is released only when
    /// <c>n &lt; LocalCommitmentNumber</c>, i.e. when commitment <c>n</c> has been superseded by a newer local commitment
    /// that was persisted with the peer's signatures and reported through <see cref="AdvanceLocalCommitment"/>.
    /// Revealing the secret of our current commitment would let the peer take every output of it. Invariant S1: it is
    /// also refused for <c>n &gt;=</c> a commitment signed for broadcast (<see cref="MarkBroadcastSigned"/>).
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
    /// Fully sign our latest local commitment transaction for broadcast (fail the channel, BOLT2 plan N9-T4): checks
    /// the peer's <paramref name="remoteSignature"/>, adds ours and returns the transaction with its 2-of-2 witness
    /// (<c>0 &lt;sig1&gt; &lt;sig2&gt; &lt;funding script&gt;</c>, signatures in funding-script key order).
    /// </summary>
    /// <remarks>
    /// Guards (invariants I4 and I12): refuses a commitment older than the signer's current local commitment number
    /// (it is revoked: broadcasting it lets the peer take every output), and refuses everything after
    /// <see cref="MarkDataLoss"/> (the peer holds a newer state; broadcasting ours would be a revoked broadcast).
    /// Invariant S1: on success it records the number with <see cref="MarkBroadcastSigned"/> before returning, and once
    /// a number is recorded any other number is refused. The checks, the signature and the mark are atomic per channel
    /// with <see cref="AdvanceLocalCommitment"/>, <see cref="RevealPerCommitmentSecret"/> and
    /// <see cref="MarkBroadcastSigned"/>: none of them interleaves with a broadcast signing of the same channel, so a
    /// commitment is never revoked between the revocation check and the mark, whatever lock the caller holds.
    /// </remarks>
    /// <param name="channelId">The registered channel.</param>
    /// <param name="commitmentNumber">The number of the local commitment <paramref name="unsignedCommitment"/> is.</param>
    /// <param name="unsignedCommitment">The unsigned commitment transaction (built for the local side).</param>
    /// <param name="remoteSignature">The peer's signature of that commitment (from its <c>commitment_signed</c>).</param>
    /// <returns>The fully signed transaction, ready to publish.</returns>
    /// <exception cref="Exceptions.SignerException">The channel is not registered, data loss was detected, the
    /// commitment is revoked, or the peer's signature does not verify.</exception>
    SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, ulong commitmentNumber,
                                                      SignedTransaction unsignedCommitment,
                                                      CompactSignature remoteSignature);

    /// <summary>
    /// Tell the signer that <c>channel_reestablish</c> proved we lost data on this channel (B2-RE-23): from now on it
    /// refuses to sign anything for the channel (commitment and HTLC signatures for new updates, and our own commitment
    /// for broadcast; invariant I12). Sticky: it is never cleared, and a registration with
    /// <see cref="ChannelSigningInfo.DataLossDetected"/> sets it too.
    /// </summary>
    void MarkDataLoss(ChannelId channelId);

    /// <summary>
    /// Invariant S1 (BOLT 5 plan §3.5): record that our local commitment <paramref name="commitmentNumber"/> of the
    /// channel was signed for broadcast. <see cref="SignLocalCommitmentForBroadcast"/> records it itself before it
    /// returns; call this at channel registration (before the first connection) for every channel whose persisted
    /// state holds a broadcast of that commitment, so the guard survives restarts.
    /// </summary>
    /// <remarks>
    /// From then on, for the life of the channel, the signer refuses to release the per-commitment secret of that
    /// commitment or any later one (<see cref="RevealPerCommitmentSecret"/>, so no <c>revoke_and_ack</c> can revoke the
    /// commitment that may be on chain), refuses <see cref="AdvanceLocalCommitment"/> past it, refuses any other number
    /// in <see cref="SignLocalCommitmentForBroadcast"/> (the same number may be signed again), and refuses new
    /// commitment and HTLC signatures (<see cref="SignChannelTransaction"/>, <see cref="SignRemoteHtlcTransactions"/>).
    /// Sweep and HTLC-transaction signatures for the on-chain resolution stay allowed. Sticky: marking a lower number
    /// keeps the lower one, and nothing clears it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The number does not fit in 48 bits.</exception>
    void MarkBroadcastSigned(ChannelId channelId, ulong commitmentNumber);

    /// <summary>
    /// The local commitment number the channel was signed for broadcast at (<see cref="MarkBroadcastSigned"/>), if any.
    /// </summary>
    bool TryGetBroadcastSignedCommitment(ChannelId channelId, out ulong commitmentNumber);

    /// <summary>
    /// Sign one input of a sweep, claim or penalty (BOLT 5 plan §3.5, O3-T1): <c>SIGHASH_ALL</c>, BIP 143, low-S,
    /// RFC 6979, with the channel key <see cref="SweepSigningContext.KeyKind"/> names:
    /// <see cref="Onchain.Enums.SweepKeyKind.DelayedPayment"/> (<c>delayed_payment_basepoint_secret</c> tweaked by our point),
    /// <see cref="Onchain.Enums.SweepKeyKind.Payment"/> (<c>payment_basepoint_secret</c>, static_remotekey),
    /// <see cref="Onchain.Enums.SweepKeyKind.HtlcRemotePoint"/> (<c>htlc_basepoint_secret</c> tweaked by the peer's point) or
    /// <see cref="Onchain.Enums.SweepKeyKind.Revocation"/> (<c>revocationprivkey</c> from our revocation basepoint secret and the
    /// peer's revealed per-commitment secret).
    /// </summary>
    /// <remarks>
    /// The derived public key (or its HASH160, as in the HTLC scripts' revocation branch) must appear in the witness
    /// script, so a wrong key kind, point or secret is refused instead of producing a useless signature. For
    /// <see cref="Onchain.Enums.SweepKeyKind.Revocation"/> a point given along the secret must equal <c>secret * G</c>; the caller
    /// takes the secret from the peer's shachain, which holds secrets of revoked commitments only. Not blocked by data
    /// loss or by a broadcast mark (S1): these signatures only move channel outputs that are already on chain to us.
    /// </remarks>
    /// <returns>The 64-byte compact signature; the transaction builder appends the sighash byte.</returns>
    /// <exception cref="Exceptions.SignerException">The channel is not registered, the context is incomplete for its
    /// key kind, the transaction does not parse or has no such input, or the key is not in the script.</exception>
    CompactSignature SignSweepInput(ChannelId channelId, SweepSigningContext context);

    /// <summary>
    /// Signs the wallet's inputs of a transaction (BOLT 5 plan O7-T1: the fee inputs of a CPFP child or of an anchor
    /// HTLC transaction), in place in <see cref="SignedTransaction.RawTxBytes"/>, with <c>SIGHASH_ALL</c>.
    /// </summary>
    /// <remarks>
    /// A wallet input is one that spends an output of the wallet's UTXO set. Only outputs reserved through
    /// <c>IFeeInputSelector</c> are signed: a wallet input that is not reserved, or is locked to a channel funding (see
    /// <see cref="SignFundingTransaction"/>), fails the call before anything is signed. P2WPKH and P2TR (key path) inputs
    /// are supported; keys are derived from the output's wallet address index and never leave the signer. Other inputs
    /// are left untouched, for their own signer. Every signature is checked with the script interpreter before it is
    /// returned. Equivalent to the overload with no other spent outputs, so a P2TR wallet input needs every input to be
    /// the wallet's.
    /// </remarks>
    /// <returns>True when every wallet input is signed; false when the transaction has no wallet input.</returns>
    /// <exception cref="Exceptions.SignerException">A wallet input cannot be signed (not reserved, locked to a channel,
    /// unsupported type, P2TR without every spent output), or the transaction does not parse.</exception>
    bool SignWalletTransaction(SignedTransaction unsignedTransaction);

    /// <summary>
    /// <see cref="SignWalletTransaction(SignedTransaction)"/> with the outputs the transaction spends that are not the
    /// wallet's, which a P2TR (BIP 341) signature commits to.
    /// </summary>
    bool SignWalletTransaction(SignedTransaction unsignedTransaction,
                               IReadOnlyList<Wallet.Models.SpentOutput> otherSpentOutputs);

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