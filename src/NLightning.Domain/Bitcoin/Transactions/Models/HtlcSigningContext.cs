namespace NLightning.Domain.Bitcoin.Transactions.Models;

using Crypto.ValueObjects;

/// <summary>
/// What the signer needs to sign or verify one HTLC-timeout/HTLC-success transaction (BOLT 3).
/// </summary>
/// <param name="HtlcTransaction">
/// The unsigned HTLC transaction with the witness script and amount of the commitment output it spends (the BIP 143
/// sighash inputs), as returned by the HTLC transaction builder.
/// </param>
/// <param name="PerCommitmentPoint">
/// The per-commitment point of the commitment the HTLC output belongs to: the HTLC keys are
/// <c>htlc_basepoint + SHA256(per_commitment_point || htlc_basepoint) * G</c>.
/// </param>
/// <param name="HasAnchors">
/// Whether option_anchors applies: the counterparty's signature on the holder's HTLC transaction is then
/// <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c>, otherwise <c>SIGHASH_ALL</c>.
/// </param>
public sealed record HtlcSigningContext(
    HtlcTransactionBuildResult HtlcTransaction,
    CompactPubKey PerCommitmentPoint,
    bool HasAnchors);