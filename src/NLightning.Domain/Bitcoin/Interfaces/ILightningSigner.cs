namespace NLightning.Domain.Bitcoin.Interfaces;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;
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
    /// Fresh MuSig2 nonces for the <c>channel_announcement_2</c> session of a public simple taproot channel (taproot
    /// gossip, BOLTs PR #1059, NL-878): one for our node key, one for our funding key. The secret nonces stay in the
    /// signer, in memory only, bound to <paramref name="unsignedAnnouncement"/>'s message, and are consumed by
    /// <see cref="SignChannelAnnouncement2"/>; a new call (or <see cref="DiscardChannelAnnouncement2Nonces"/>) drops the
    /// previous pair, so no nonce ever signs twice. The announcement is checked as <see cref="SignChannelAnnouncement2"/>
    /// checks it.
    /// </summary>
    /// <exception cref="Exceptions.SignerException">The announcement is not one this channel may sign.</exception>
    ChannelAnnouncement2Nonces CreateChannelAnnouncement2Nonces(ChannelId channelId,
                                                                Protocol.Payloads.ChannelAnnouncement2Payload
                                                                    unsignedAnnouncement) =>
        throw new NotImplementedException("Taproot gossip T7");

    /// <summary>
    /// Our two partial signatures (node key, funding key) of <paramref name="unsignedAnnouncement"/>'s MuSig2 session
    /// with the peer's nonces: the key aggregate <c>KeyAgg(KeySort(node_id_1, node_id_2, bitcoin_key_1,
    /// bitcoin_key_2))</c>, the aggregate of all four nonces and the message <c>MsgHash("channel_announcement_2",
    /// "signature", m)</c>. Consumes the nonces of <see cref="CreateChannelAnnouncement2Nonces"/> (for the same
    /// announcement), so it signs at most once per pair. Refused for a private channel, a channel that is not simple
    /// taproot, another chain, scid, outpoint, capacity or key set, and after data loss.
    /// </summary>
    /// <exception cref="Exceptions.SignerException">Refused, or no live nonce pair for this announcement.</exception>
    ChannelAnnouncement2PartialSignatures SignChannelAnnouncement2(
        ChannelId channelId, Protocol.Payloads.ChannelAnnouncement2Payload unsignedAnnouncement,
        MusigPublicNonce remoteNodeNonce, MusigPublicNonce remoteBitcoinNonce) =>
        throw new NotImplementedException("Taproot gossip T7");

    /// <summary>Drops the channel's live <c>channel_announcement_2</c> nonce pair, if any (a closed connection).</summary>
    void DiscardChannelAnnouncement2Nonces(ChannelId channelId)
    {
    }

    /// <summary>
    /// A BIP 340 signature of <paramref name="messageHash"/> with the node key (taproot gossip's
    /// <c>channel_update_2</c> and <c>node_announcement_2</c>, verified against the x-only node id), fresh auxiliary
    /// randomness.
    /// </summary>
    CompactSignature SignNodeMessageBip340(Hash messageHash) => throw new NotImplementedException("Taproot gossip T7");

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
    /// Forget everything the signer keeps for a channel: its signing info, its local commitment number, its
    /// broadcast-signed mark (invariant S1), its data-loss flag and its pending/retired splice fundings. A no-op for a
    /// channel that is not registered.
    /// </summary>
    /// <remarks>
    /// Only for channels that are discarded before they were ever established (the cleanup of a failed
    /// <c>accept_channel</c>, NL-221) or whose on-chain resolution is done: unregistering also drops the channel's
    /// sticky guards, so a channel that may still hold a broadcast-signed commitment must never be unregistered. The
    /// per-channel commitment lock object stays behind, so concurrent callers never hold different locks for the same
    /// channel id.
    /// </remarks>
    void UnregisterChannel(ChannelId channelId);

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
    /// are left untouched, for their own signer. The key derived for a wallet input must give the scriptPubKey of the
    /// output's recorded wallet address, else the call fails; every signature is then checked with the script
    /// interpreter before it is returned (for P2TR against the caller's other spent outputs, which that check cannot
    /// validate: a wrong one gives an invalid transaction, never a loss). This overload accepts inputs of any fee
    /// reservation: prefer the one that takes the reservation id. Equivalent to the overload with no other spent
    /// outputs, so a P2TR wallet input needs every input to be the wallet's.
    /// </remarks>
    /// <returns>True when every wallet input is signed; false when the transaction has no wallet input.</returns>
    /// <exception cref="Exceptions.SignerException">A wallet input cannot be signed (not reserved, locked to a channel,
    /// unsupported type, no wallet address or a derived key that does not match it, P2TR without every spent output),
    /// or the transaction does not parse.</exception>
    bool SignWalletTransaction(SignedTransaction unsignedTransaction);

    /// <summary>
    /// <see cref="SignWalletTransaction(SignedTransaction)"/> with the outputs the transaction spends that are not the
    /// wallet's, which a P2TR (BIP 341) signature commits to.
    /// </summary>
    bool SignWalletTransaction(SignedTransaction unsignedTransaction,
                               IReadOnlyList<Wallet.Models.SpentOutput> otherSpentOutputs);

    /// <summary>
    /// <see cref="SignWalletTransaction(SignedTransaction, IReadOnlyList{Wallet.Models.SpentOutput})"/> for the spend of
    /// one fee reservation: every wallet input must belong to the reservation <paramref name="reservationId"/>
    /// (<c>FeeInputReservation.Id</c>), else the call fails before anything is signed.
    /// </summary>
    bool SignWalletTransaction(SignedTransaction unsignedTransaction, Guid reservationId,
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

    /// <summary>
    /// Sign the input of <paramref name="unsignedTransaction"/> that spends our anchor output (BOLT 3
    /// <c>to_local_anchor</c> of our commitment, or the anchor keyed to our funding key on the peer's): BOLT 5 plan
    /// O7-T2, the CPFP child. <c>SIGHASH_ALL</c>, BIP 143, RFC 6979, low-S, with our <b>funding</b> key over the script
    /// code <c>&lt;local_funding_pubkey&gt; OP_CHECKSIG OP_IFDUP OP_NOTIF OP_16 OP_CHECKSEQUENCEVERIFY OP_ENDIF</c>.
    /// </summary>
    /// <remarks>
    /// The signer builds the script code itself from the channel's local funding pubkey, so the signature is only valid
    /// for an anchor input (the sighash commits to that script and to <paramref name="amount"/>), never for the funding
    /// output. Only the BOLT 3 anchor amount (330 sat) is signed. Not blocked by data loss or by a broadcast mark (S1):
    /// the anchor only moves 330 sat that are already on chain, and a child fee-bumps a commitment that is already
    /// published.
    /// </remarks>
    /// <param name="channelId">The registered (or persisted) channel.</param>
    /// <param name="unsignedTransaction">The child transaction (witnesses are ignored).</param>
    /// <param name="inputIndex">The index of the anchor input.</param>
    /// <param name="amount">The anchor's value (330 sat).</param>
    /// <returns>The 64-byte compact signature; the transaction builder appends the sighash byte.</returns>
    /// <exception cref="Exceptions.SignerException">The channel is not registered, the transaction does not parse or
    /// has no such input, or the amount is not 330 sat.</exception>
    CompactSignature SignAnchorInput(ChannelId channelId, SignedTransaction unsignedTransaction, int inputIndex,
                                     LightningMoney amount);

    /// <summary>
    /// Simple taproot channels (NL-966): the key-path signature of the input of <paramref name="unsignedTransaction"/>
    /// that spends one of our taproot anchors (BIP 340, <c>SIGHASH_DEFAULT</c>, BIP 341 sighash over every spent
    /// output): our <c>to_local_anchor</c> on our commitment (internal key our <c>local_delayedpubkey</c> at
    /// <paramref name="ourPerCommitmentPoint"/>) or, with a null point, our <c>to_remote_anchor</c> on the peer's
    /// commitment (internal key our payment basepoint). The key is tweaked with the anchor's tapscript root
    /// (<c>OP_16 OP_CHECKSEQUENCEVERIFY</c>).
    /// </summary>
    /// <remarks>
    /// The signer builds the anchor output from the derived key and refuses unless the spent output of
    /// <paramref name="inputIndex"/> is exactly that 330-sat output, so the key never signs anything else. Not blocked
    /// by data loss or by a broadcast mark (S1), as <see cref="SignAnchorInput"/>.
    /// </remarks>
    /// <param name="channelId">The registered simple taproot channel.</param>
    /// <param name="unsignedTransaction">The child transaction (witnesses are ignored).</param>
    /// <param name="inputIndex">The index of the anchor input.</param>
    /// <param name="ourPerCommitmentPoint">Our per-commitment point of our commitment, or null for the peer's.</param>
    /// <param name="spentOutputs">Every output the transaction spends, in input order.</param>
    /// <returns>The 64-byte BIP 340 signature (no sighash byte).</returns>
    /// <exception cref="Exceptions.SignerException">The channel is not a registered taproot channel, the transaction
    /// does not parse or the spent output is not our anchor.</exception>
    CompactSignature SignTaprootAnchorInput(ChannelId channelId, SignedTransaction unsignedTransaction, int inputIndex,
                                            CompactPubKey? ourPerCommitmentPoint,
                                            IReadOnlyList<Wallet.Models.SpentOutput> spentOutputs) =>
        throw new NotImplementedException("Simple taproot anchors (NL-966)");

    #region Splicing (splicing plan SP1-0; implemented by lane SP1-C in LocalLightningSigner.Splicing.cs)

    /// <summary>
    /// Simple taproot channels (bolt-simple-taproot.md, D-T4): our public verification nonce for our local commitment
    /// <paramref name="localCommitmentNumber"/>, from the counter scheme: BIP 327 <c>NonceGen</c> with <c>rand'</c> the
    /// commitment's leaf of the MuSig2 shachain whose root is <c>HMAC("taproot-rev-root" || funding_txid,
    /// sha256(shachain_root))</c>. Nothing is stored: the same inputs give the same nonce, and the signer re-derives the
    /// secret half only to sign that commitment for broadcast.
    /// </summary>
    /// <param name="channelKeyIndex">The channel's key index (usable before the channel id exists: open/accept).</param>
    /// <param name="fundingTxId">The funding the commitment spends, the nonce's context. Null only for commitment 0 of a
    /// v1 open, whose nonce is sent in <c>open_channel</c>/<c>accept_channel</c> before the funding txid is known (a v1
    /// open has one funding transaction). Commitment 0 of a dual-funded open's attempt passes that attempt's txid: the
    /// attempts are different transactions and must not share a nonce (NL-972). The channel id overload picks the
    /// context itself (<c>ChannelSigningInfo.IsDualFunded</c>, splices always bound).</param>
    /// <param name="localCommitmentNumber">Our local commitment number the nonce verifies.</param>
    MusigPublicNonce GetLocalVerificationNonce(uint channelKeyIndex, TxId? fundingTxId, ulong localCommitmentNumber) =>
        throw new NotImplementedException("Taproot wave t02 lane SIG");

    /// <summary>
    /// <see cref="GetLocalVerificationNonce(uint, TxId?, ulong)"/> for a registered channel. Commitment 0 uses the
    /// context without a txid only for a channel opened with v1 (<see cref="ChannelSigningInfo.IsDualFunded"/> false); a
    /// dual-funded channel's commitment 0 is bound to <paramref name="fundingTxId"/> (null: the current funding), as the
    /// key index overload derives it before registration when given that txid (a dual-funded open's <c>tx_complete</c>
    /// <c>commit_nonces</c>, NL-972).
    /// </summary>
    MusigPublicNonce GetLocalVerificationNonce(ChannelId channelId, TxId? fundingTxId, ulong localCommitmentNumber) =>
        throw new NotImplementedException("Taproot wave t02 lane SIG");

    /// <summary>
    /// Simple taproot channels: our MuSig2 partial signature of the peer's commitment <paramref name="unsignedCommitment"/>
    /// (key-path spend of the funding output, <c>SIGHASH_DEFAULT</c>) with a fresh just-in-time signing nonce, combined
    /// with the peer's verification nonce <paramref name="remoteVerificationNonce"/>. The secret nonce is drawn from fresh
    /// randomness, used once and never stored, so a re-sign (retransmission) always gets a new nonce.
    /// </summary>
    /// <param name="channelId">The registered channel.</param>
    /// <param name="fundingTxId">The funding the commitment spends; null for the channel's current funding.</param>
    /// <param name="unsignedCommitment">The peer's unsigned commitment transaction.</param>
    /// <param name="remoteVerificationNonce">The peer's latest <c>next_local_nonce</c>(s) entry for that funding.</param>
    /// <returns>The <c>partial_signature_with_nonce</c> payload (our partial signature and our signing nonce).</returns>
    /// <exception cref="Exceptions.SignerException">The channel is not a registered taproot channel, data loss was
    /// detected, or the nonce does not parse.</exception>
    MusigPartialSignatureWithNonce SignRemoteCommitmentPartial(ChannelId channelId, TxId? fundingTxId,
                                                               SignedTransaction unsignedCommitment,
                                                               MusigPublicNonce remoteVerificationNonce) =>
        throw new NotImplementedException("Taproot wave t02 lane SIG");

    /// <summary>
    /// Simple taproot channels: checks the peer's partial signature of our local commitment
    /// <paramref name="localCommitmentNumber"/> (<c>PartialSigVerifyInternal</c>), with the aggregate nonce built from
    /// our verification nonce for that number (<see cref="GetLocalVerificationNonce(ChannelId, TxId?, ulong)"/>) and
    /// the peer's signing nonce carried in <paramref name="remoteSignature"/>.
    /// </summary>
    /// <exception cref="Exceptions.SignerException">The partial signature does not verify or the channel is not a
    /// registered taproot channel.</exception>
    void ValidateLocalCommitmentPartialSignature(ChannelId channelId, TxId? fundingTxId, ulong localCommitmentNumber,
                                                 MusigPartialSignatureWithNonce remoteSignature,
                                                 SignedTransaction unsignedCommitment) =>
        throw new NotImplementedException("Taproot wave t02 lane SIG");

    /// <summary>
    /// <see cref="SignLocalCommitmentForBroadcast(ChannelId, ulong, SignedTransaction, CompactSignature)"/> for a simple
    /// taproot channel: re-derives our verification secret nonce for <paramref name="commitmentNumber"/>, signs our
    /// half, aggregates it with the peer's stored partial signature and returns the transaction with its one-element
    /// key-path witness. The same guards (I4, I12, S1, SP-I4) apply.
    /// </summary>
    SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, TxId? fundingTxId, ulong commitmentNumber,
                                                      SignedTransaction unsignedCommitment,
                                                      MusigPartialSignatureWithNonce remoteSignature) =>
        throw new NotImplementedException("Taproot wave t02 lane SIG");

    /// <summary>
    /// Simple taproot channels, <c>option_simple_close</c> (bolt-simple-taproot.md §RBF Cooperative Close): a new
    /// closee nonce of ours, the <c>shutdown_nonce</c> of our <c>shutdown</c> or the <c>next_closee_nonce</c> of our
    /// <c>closing_sig</c>. A just-in-time nonce from fresh randomness and our funding key; its secret half stays in the
    /// signer's memory, keyed by the channel and the public nonce, until
    /// <see cref="SignClosingAsClosee"/> consumes it or <see cref="ForgetClosingNonces"/> drops it (never persisted: a
    /// restart starts a new shutdown with new nonces).
    /// </summary>
    /// <exception cref="Exceptions.SignerException">The channel is not a registered taproot channel, data loss was
    /// detected, or a local commitment was signed for broadcast.</exception>
    MusigPublicNonce CreateClosingNonce(ChannelId channelId) =>
        throw new NotImplementedException("Taproot wave t02 lane SIG");

    /// <summary>
    /// Simple taproot channels: our <c>closing_complete</c> partial signature as the closer, of
    /// <paramref name="unsignedClosing"/> (its single input spends the current funding output by key path,
    /// <c>SIGHASH_DEFAULT</c>), with a fresh just-in-time closer nonce and the peer's closee nonce
    /// <paramref name="remoteCloseeNonce"/> (its <c>shutdown_nonce</c>, or the <c>next_closee_nonce</c> of its last
    /// <c>closing_sig</c>). Nothing is kept: the result holds what <see cref="AggregateClosingSignature"/> needs.
    /// </summary>
    /// <returns>Our partial signature and our closer nonce (the <c>partial_sig_with_nonce</c> payload).</returns>
    /// <exception cref="Exceptions.SignerException">As <see cref="CreateClosingNonce"/>, or the transaction does not
    /// spend the current funding output, or the nonce does not parse.</exception>
    MusigPartialSignatureWithNonce SignClosingAsCloser(ChannelId channelId, SignedTransaction unsignedClosing,
                                                       MusigPublicNonce remoteCloseeNonce) =>
        throw new NotImplementedException("Taproot wave t02 lane SIG");

    /// <summary>
    /// Simple taproot channels: our <c>closing_sig</c> partial signature as the closee. Checks the closer's partial
    /// signature <paramref name="remoteCloserSignature"/> first (with our closee nonce
    /// <paramref name="localCloseeNonce"/>), then signs with the secret half of <paramref name="localCloseeNonce"/>,
    /// which is consumed: a second call with it throws, so every <c>closing_sig</c> needs a new closee nonce
    /// (<see cref="CreateClosingNonce"/>, sent as <c>next_closee_nonce</c>).
    /// </summary>
    /// <exception cref="Exceptions.SignerException">As <see cref="SignClosingAsCloser"/>, the closee nonce is not one
    /// of ours (or was used or forgotten), or the closer's partial signature does not verify (the nonce is then kept).
    /// </exception>
    MusigPartialSignature SignClosingAsClosee(ChannelId channelId, SignedTransaction unsignedClosing,
                                              MusigPublicNonce localCloseeNonce,
                                              MusigPartialSignatureWithNonce remoteCloserSignature) =>
        throw new NotImplementedException("Taproot wave t02 lane SIG");

    /// <summary>
    /// Simple taproot channels: checks the peer's partial signature of <paramref name="unsignedClosing"/>
    /// (<c>PartialSigVerifyInternal</c> with the peer's funding key), made with <paramref name="remoteNonce"/> in a
    /// session whose other nonce is our <paramref name="localNonce"/>: as the closer, the peer's <c>closing_sig</c>
    /// with its closee nonce and our closer nonce; as the closee, the peer's <c>closing_complete</c> with its closer
    /// nonce and our closee nonce.
    /// </summary>
    /// <exception cref="Exceptions.SignerException">The partial signature does not verify, a nonce does not parse, or
    /// the channel is not a registered taproot channel.</exception>
    void ValidateClosingPartialSignature(ChannelId channelId, SignedTransaction unsignedClosing,
                                         MusigPartialSignature remoteSignature, MusigPublicNonce remoteNonce,
                                         MusigPublicNonce localNonce) =>
        throw new NotImplementedException("Taproot wave t02 lane SIG");

    /// <summary>
    /// Simple taproot channels: the signed closing transaction. Verifies the peer's partial signature, aggregates it
    /// with ours (<c>PartialSigAgg</c>), checks the BIP 340 signature against the funding output key and returns
    /// <paramref name="unsignedClosing"/> with its one-element key-path witness (64 bytes, <c>SIGHASH_DEFAULT</c>).
    /// Every input is public: our partial signature and nonce as <see cref="SignClosingAsCloser"/> or
    /// <see cref="SignClosingAsClosee"/> (with our closee nonce) gave them, and the peer's.
    /// </summary>
    /// <exception cref="Exceptions.SignerException">A partial signature does not verify, the aggregate does not verify,
    /// or the channel is not a registered taproot channel.</exception>
    SignedTransaction AggregateClosingSignature(ChannelId channelId, SignedTransaction unsignedClosing,
                                                MusigPartialSignature localSignature, MusigPublicNonce localNonce,
                                                MusigPartialSignature remoteSignature,
                                                MusigPublicNonce remoteNonce) =>
        throw new NotImplementedException("Taproot wave t02 lane SIG");

    /// <summary>
    /// Drops (and zeroes) every closee nonce secret the signer keeps for the channel: when its close negotiation ends,
    /// is restarted by a reconnection, or the channel is unregistered. Never throws.
    /// </summary>
    void ForgetClosingNonces(ChannelId channelId)
    {
    }

    /// <summary>
    /// Splicing a simple taproot channel (NL-965, BOLTs PR #1324 <c>commit_nonces</c>): our verification nonce for our
    /// local commitment <paramref name="localCommitmentNumber"/> on the splice funding <paramref name="fundingTxId"/>,
    /// whose local funding key is our rotated key <paramref name="fundingKeyIndex"/>, before that funding is registered
    /// (the <c>tx_complete</c> of its negotiation). Always bound to the txid: once registered, the channel id overload
    /// derives the same nonce.
    /// </summary>
    /// <exception cref="Exceptions.SignerException">The channel is not a registered taproot channel, or
    /// <paramref name="fundingKeyIndex"/> is 0 (a taproot splice always rotates its funding key).</exception>
    MusigPublicNonce GetLocalVerificationNonce(ChannelId channelId, uint fundingKeyIndex, TxId fundingTxId,
                                               ulong localCommitmentNumber) =>
        throw new NotImplementedException("Taproot wave t03 lane SPL");

    /// <summary>
    /// Splicing a simple taproot channel (NL-965, BOLTs PR #1324 <c>funding_nonce</c>): a new signing nonce of ours for
    /// the shared input, the channel's current funding output spent by MuSig2 key path. A just-in-time nonce (fresh
    /// randomness and our current funding key); its secret half stays in the signer's memory until
    /// <see cref="SignSpliceSharedInputPartial"/> consumes it (single use, never stored: a restart before that forgets
    /// it, and so does the negotiation, which is not stored before our <c>commitment_signed</c>).
    /// </summary>
    /// <exception cref="Exceptions.SignerException">The channel is not a registered taproot channel, data loss was
    /// detected, or a local commitment was signed for broadcast.</exception>
    MusigPublicNonce CreateSpliceFundingNonce(ChannelId channelId) =>
        throw new NotImplementedException("Taproot wave t03 lane SPL");

    /// <summary>
    /// Splicing a simple taproot channel (NL-965, BOLTs PR #1324 <c>shared_input_partial_signature</c>): our MuSig2
    /// partial signature of input <paramref name="sharedInputIndex"/> of the splice transaction, which spends the
    /// channel's current funding output by key path (BIP 341 <c>SIGHASH_DEFAULT</c> over every spent output,
    /// <paramref name="spentOutputs"/> in input order), with our nonce <paramref name="localFundingNonce"/> (from
    /// <see cref="CreateSpliceFundingNonce"/>, consumed) and the peer's <c>funding_nonce</c>.
    /// </summary>
    /// <remarks>
    /// Signed at the commitment step, before our <c>commitment_signed</c> (as Eclair 0.14.3 does): the secret nonce cannot
    /// be stored (D-T4), so the partial signature is, in the save that precedes our <c>commitment_signed</c>. It is only
    /// released in <c>tx_signatures</c> after the peer's <c>commitment_signed</c> on the new funding was saved (the
    /// interactive-tx driver's IT-SIG-03 order, SP-I1); the guards of <see cref="SignSpliceSharedInput"/> apply
    /// otherwise (the registered pending funding output, data loss, S1).
    /// </remarks>
    /// <exception cref="Exceptions.SignerException">A guard refused, the nonce is not a live one of ours, or a nonce
    /// does not parse.</exception>
    MusigPartialSignatureWithNonce SignSpliceSharedInputPartial(ChannelId channelId, TxId newFundingTxId,
                                                                SignedTransaction unsignedSpliceTransaction,
                                                                int sharedInputIndex,
                                                                IReadOnlyList<Wallet.Models.SpentOutput> spentOutputs,
                                                                MusigPublicNonce localFundingNonce,
                                                                MusigPublicNonce remoteFundingNonce) =>
        throw new NotImplementedException("Taproot wave t03 lane SPL");

    /// <summary>
    /// Splicing a simple taproot channel: checks the peer's <c>shared_input_partial_signature</c> (SP-SIG-01), aggregates
    /// it with ours and returns the 64-byte BIP 340 key-path signature of the shared input (<c>SIGHASH_DEFAULT</c>),
    /// verified against the current funding output key.
    /// </summary>
    /// <exception cref="Exceptions.SignerException">A partial signature does not verify, a nonce does not parse, or the
    /// aggregate does not verify.</exception>
    byte[] AggregateSpliceSharedInputSignature(ChannelId channelId, SignedTransaction unsignedSpliceTransaction,
                                               int sharedInputIndex,
                                               IReadOnlyList<Wallet.Models.SpentOutput> spentOutputs,
                                               MusigPartialSignatureWithNonce localSignature,
                                               MusigPartialSignatureWithNonce remoteSignature) =>
        throw new NotImplementedException("Taproot wave t03 lane SPL");

    /// <summary>
    /// Our funding public key number <paramref name="fundingKeyIndex"/> of the channel (splicing plan D5): index 0 is
    /// the channel's original funding key; each splice rotates to a new index, derived deterministically from the
    /// channel's keys so a static channel backup restores it.
    /// </summary>
    CompactPubKey GetFundingPubKey(ChannelId channelId, uint fundingKeyIndex) =>
        throw new NotImplementedException("Lane SP1-C (SP1-C-T1)");

    /// <summary>
    /// Registers a pending splice funding of a registered channel (its outpoint, capacity, both funding keys and our
    /// key index), so commitments can be signed and verified for it (SP-OP-01). Registering the same funding again is
    /// a no-op; other keys for a registered funding txid throw a <see cref="Exceptions.SignerException"/>.
    /// </summary>
    void RegisterFunding(ChannelId channelId, Channels.Splicing.ChannelFunding funding) =>
        throw new NotImplementedException("Lane SP1-C (SP1-C-T1)");

    /// <summary>
    /// Invariant SP-I1: records that our local commitment <paramref name="localCommitmentNumber"/> spending the pending
    /// funding <paramref name="fundingTxId"/>, with the peer's verified commitment and HTLC signatures, is persisted.
    /// Call it only after the save succeeded. <see cref="SignSpliceSharedInput"/> refuses until it was called.
    /// </summary>
    void MarkSpliceCommitmentPersisted(ChannelId channelId, TxId fundingTxId, ulong localCommitmentNumber) =>
        throw new NotImplementedException("Lane SP1-C (SP1-C-T2)");

    /// <summary>
    /// Our <c>shared_input_signature</c> (SP-SIG-01): the ECDSA signature (<c>SIGHASH_ALL</c>, BIP 143, low-S, RFC
    /// 6979) of the input <paramref name="sharedInputIndex"/> of <paramref name="unsignedSpliceTransaction"/> that spends
    /// the channel's current funding output, with the current funding key.
    /// </summary>
    /// <remarks>
    /// SP-I1: refused (<see cref="Exceptions.SignerException"/>) unless <see cref="MarkSpliceCommitmentPersisted"/>
    /// recorded a persisted commitment for <paramref name="newFundingTxId"/> at the current local commitment number,
    /// unless the transaction's output <paramref name="newFundingTxId"/> is the registered pending funding, and after
    /// data loss or a broadcast mark (S1).
    /// </remarks>
    CompactSignature SignSpliceSharedInput(ChannelId channelId, TxId newFundingTxId,
                                           SignedTransaction unsignedSpliceTransaction, int sharedInputIndex) =>
        throw new NotImplementedException("Lane SP1-C (SP1-C-T2)");

    /// <summary>
    /// Verifies the peer's <c>shared_input_signature</c> for the shared input of a splice transaction (SP-SIG-01:
    /// missing, invalid or high-S means the channel fails).
    /// </summary>
    /// <exception cref="Exceptions.SignerException">The signature does not parse, is high-S or does not
    /// verify.</exception>
    void ValidateSpliceSharedInputSignature(ChannelId channelId, SignedTransaction unsignedSpliceTransaction,
                                            int sharedInputIndex, CompactSignature remoteSignature) =>
        throw new NotImplementedException("Lane SP1-C (SP1-C-T2)");

    /// <summary>
    /// <see cref="SignChannelTransaction"/> for the commitment of a given active funding (the current one or a pending
    /// splice): signs input 0 against that funding's output and keys.
    /// </summary>
    CompactSignature SignChannelTransaction(ChannelId channelId, TxId fundingTxId,
                                            SignedTransaction unsignedTransaction) =>
        throw new NotImplementedException("Lane SP1-C (SP1-C-T1)");

    /// <summary>
    /// <see cref="ValidateSignature"/> for the commitment of a given active funding.
    /// </summary>
    void ValidateSignature(ChannelId channelId, TxId fundingTxId, CompactSignature signature,
                           SignedTransaction unsignedTransaction) =>
        throw new NotImplementedException("Lane SP1-C (SP1-C-T1)");

    /// <summary>
    /// <see cref="SignLocalCommitmentForBroadcast"/> for the commitment of a given active funding. Invariant SP-I4:
    /// once commitment <c>n</c> is signed for broadcast on any funding, the same <c>n</c> may be signed on another
    /// active funding (the one that confirmed), never another number.
    /// </summary>
    SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, TxId fundingTxId, ulong commitmentNumber,
                                                      SignedTransaction unsignedCommitment,
                                                      CompactSignature remoteSignature) =>
        throw new NotImplementedException("Lane SP1-C (SP1-C-T3)");

    /// <summary>
    /// The splice <paramref name="fundingTxId"/> was locked both ways (<c>splice_locked</c> sent and received): it
    /// becomes the channel's current funding for signing; the other pending fundings stop being signed for. Keys of
    /// retired fundings stay known for their on-chain resolution (SP-I5).
    /// </summary>
    void LockFunding(ChannelId channelId, TxId fundingTxId) =>
        throw new NotImplementedException("Lane SP1-C");

    /// <summary>
    /// <see cref="LockFunding(ChannelId, TxId)"/> with the locked splice's confirmed short channel id: the pending
    /// funding was usually registered before it confirmed, so without it the channel's signing data has no short channel
    /// id until the channel is registered again, and <see cref="SignChannelAnnouncement"/> is refused meanwhile.
    /// </summary>
    void LockFunding(ChannelId channelId, TxId fundingTxId, ShortChannelId? shortChannelId) =>
        throw new NotImplementedException("Lane SP1-C");

    #endregion

    /// <summary>
    /// The public key of the transient BOLT 12 payer key of an invoice_request with
    /// <paramref name="invoiceRequestMetadata"/> (its <c>invreq_payer_id</c>; BOLT 12 plan §3.6, D3).
    /// </summary>
    /// <remarks>
    /// The secret is <c>HMAC-SHA256(offers_secret, "nltg_bolt12_payer" || invreq_metadata)</c> as a scalar, with
    /// <c>offers_secret = HMAC-SHA256(node_key, "nltg_bolt12")</c>: deterministic per metadata, unrelated across
    /// metadata, never the node key, never stored and never returned.
    /// </remarks>
    /// <exception cref="ArgumentException">The metadata is empty.</exception>
    CompactPubKey GetBolt12PayerId(ReadOnlyMemory<byte> invoiceRequestMetadata) =>
        throw new NotSupportedException("This signer does not sign BOLT 12 messages.");

    /// <summary>
    /// A BIP-340 Schnorr signature of the BOLT 12 digest <c>H(tag, merkleRoot)</c>,
    /// <c>H(tag, msg) = SHA256(SHA256(tag) || SHA256(tag) || msg)</c>, with the key <paramref name="key"/> names (the
    /// node key, a transient payer key or our blinded key for a path_key).
    /// </summary>
    /// <remarks>
    /// The signer computes the tagged hash itself and only accepts the two BOLT 12 signature tags, each for its own key:
    /// a payer key signs only under <c>lightninginvoice_requestsignature</c>, the node key and our blinded keys only
    /// under <c>lightninginvoicesignature</c>, so the keys never sign an arbitrary digest. The auxiliary
    /// randomness is 32 zero bytes (as CLN, whose <c>signature-test.json</c> signature this reproduces), so the
    /// signature is deterministic.
    /// </remarks>
    /// <returns>The 64-byte signature.</returns>
    /// <exception cref="ArgumentException">The tag is not the BOLT 12 signature tag of that key kind, or a path_key is
    /// not a point.
    /// </exception>
    byte[] SignBolt12(Offers.Models.Bolt12SigningKey key, string tag, Hash merkleRoot) =>
        throw new NotSupportedException("This signer does not sign BOLT 12 messages.");
}