namespace NLightning.Domain.Onchain.Models;

using Crypto.ValueObjects;
using Enums;

/// <summary>
/// What the signer needs to sign one input of a sweep, claim or penalty (BOLT 5 plan §3.5,
/// <c>ILightningSigner.SignSweepInput</c>). It carries the script and the amount rather than a sighash, so a remote
/// signer can check what it signs.
/// </summary>
/// <param name="UnsignedTransaction">The whole unsigned transaction (its other inputs may be unsigned too).</param>
/// <param name="InputIndex">The input to sign.</param>
/// <param name="WitnessScript">The P2WSH witness script of the spent output (the BIP 143 script code). Null only for a
/// P2WPKH <c>to_remote</c> (<see cref="SweepKeyKind.Payment"/>): the script code is then the P2PKH script of the
/// payment key.</param>
/// <param name="AmountSat">The amount of the spent output, in satoshis.</param>
/// <param name="KeyKind">Which key signs.</param>
/// <param name="PerCommitmentPoint">The per-commitment point the key is tweaked with:
/// <see cref="SweepKeyKind.DelayedPayment"/> (ours), <see cref="SweepKeyKind.HtlcRemotePoint"/> (the peer's). For
/// <see cref="SweepKeyKind.Revocation"/> it is optional and, when given, must equal <c>secret * G</c>.</param>
/// <param name="PerCommitmentSecret">The peer's revealed per-commitment secret, for
/// <see cref="SweepKeyKind.Revocation"/> only.</param>
public sealed record SweepSigningContext(
    byte[] UnsignedTransaction,
    int InputIndex,
    byte[]? WitnessScript,
    ulong AmountSat,
    SweepKeyKind KeyKind,
    CompactPubKey? PerCommitmentPoint = null,
    Secret? PerCommitmentSecret = null);