namespace NLightning.Domain.Bitcoin.Transactions.Enums;

/// <summary>
/// The BOLT 3 commitment format of a channel type: which scripts its commitment and HTLC transactions use, which
/// commitment weight its fee is computed with and whether its HTLC transactions pay a fee.
/// </summary>
/// <remarks>
/// <see cref="Anchors"/> and <see cref="SimpleTaproot"/> both have the two 330 sat anchor outputs, the
/// <c>1 OP_CSV</c> to_remote and HTLC delays and zero-fee HTLC transactions signed by the counterparty with
/// <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> (see <c>CommitmentFormatExtensions.HasAnchorOutputs</c>).
/// </remarks>
public enum CommitmentFormat : byte
{
    /// <summary><c>option_static_remotekey</c> without anchors: P2WSH outputs, P2WPKH to_remote, HTLC fees.</summary>
    StaticRemoteKey = 0,

    /// <summary><c>option_anchors</c>: P2WSH outputs and anchors keyed to the funding keys (BOLT 3).</summary>
    Anchors = 1,

    /// <summary>
    /// <c>option_simple_taproot</c> (bolt-simple-taproot.md): a MuSig2 key-path funding output and P2TR commitment and
    /// HTLC outputs (tapscript trees), anchors keyed to <c>local_delayedpubkey</c>/<c>remotepubkey</c>.
    /// </summary>
    SimpleTaproot = 2
}