namespace NLightning.Domain.Onchain.Models;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// One output to spend in a sweep, claim or penalty transaction (BOLT 5 plan O3-T1, O4, O5-T1). Built from a
/// <see cref="CommitmentOutputDescriptor"/> by <see cref="Factories.SweepInputFactory"/>; keys are never stored, the
/// signer re-derives them from the point or secret.
/// </summary>
/// <param name="TxId">The transaction that holds the output (commitment or HTLC transaction).</param>
/// <param name="Vout">The output index.</param>
/// <param name="AmountSat">The output amount in satoshis.</param>
/// <param name="SpendKind">How the output is spent (witness, sequence, locktime and key).</param>
/// <param name="WitnessScript">The P2WSH witness script; null only for a P2WPKH <c>to_remote</c>.</param>
/// <param name="CsvDelay">The relative delay the witness script enforces (<c>to_self_delay</c> on a delayed output, 1
/// on anchor HTLC and <c>to_remote</c> outputs, 0 when there is none); it becomes the input's <c>nSequence</c>.</param>
/// <param name="CltvExpiry">The HTLC's <c>cltv_expiry</c> for <see cref="SweepSpendKind.HtlcTimeoutClaim"/>: the
/// transaction's <c>nLockTime</c> is at least this.</param>
/// <param name="PerCommitmentPoint">The point of the commitment the output belongs to (delayed outputs: ours; HTLC
/// claims: the peer's).</param>
/// <param name="PerCommitmentSecret">The peer's revealed secret, for penalties.</param>
/// <param name="Preimage">The payment preimage, for <see cref="SweepSpendKind.HtlcPreimageClaim"/>.</param>
/// <param name="WitnessPubKey">The key the witness pushes: our <c>payment_basepoint</c> for a P2WPKH
/// <c>to_remote</c>, the <c>revocationpubkey</c> for <see cref="SweepSpendKind.RevokedHtlc"/>.</param>
public sealed record SweepInput(
    TxId TxId,
    uint Vout,
    ulong AmountSat,
    SweepSpendKind SpendKind,
    byte[]? WitnessScript,
    ushort CsvDelay = 0,
    uint CltvExpiry = 0,
    CompactPubKey? PerCommitmentPoint = null,
    Secret? PerCommitmentSecret = null,
    byte[]? Preimage = null,
    CompactPubKey? WitnessPubKey = null)
{
    /// <summary>The key that signs this input.</summary>
    public SweepKeyKind KeyKind => SpendKind.GetKeyKind();
}