using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Constants;

using ValueObjects;

/// <summary>
/// TLV types the simple taproot channels proposal (<c>option_simple_taproot</c>) and BOLTs PR #1324 (taproot in
/// interactive-tx and splices, as Eclair 0.14.3 speaks it) add to existing messages.
/// </summary>
/// <remarks>
/// Each message has its own TLV namespace, so the numbers collide with <see cref="TlvConstants"/> and with each other
/// (4 is <c>next_local_nonce</c> in <c>open_channel</c> but <c>commit_nonces</c> in <c>tx_complete</c>; 22 is
/// <c>next_local_nonces</c> in <c>revoke_and_ack</c> but <c>next_closee_nonce</c> in <c>closing_sig</c>): always read
/// them against the message they belong to. Every one of them is even, so it must be in its message serializer's
/// known-type set (NL-001).
/// </remarks>
[ExcludeFromCodeCoverage]
public static class TaprootTlvConstants
{
    /// <summary>
    /// <c>partial_signature_with_nonce</c> (type 2) in <c>funding_created</c>, <c>funding_signed</c> and
    /// <c>commitment_signed</c>: [<c>32*byte</c>:<c>partial_signature</c>] [<c>66*byte</c>:<c>public_nonce</c>].
    /// </summary>
    public static readonly BigSize PartialSignatureWithNonce = 2;

    /// <summary>
    /// <c>next_local_nonce</c> (type 4) in <c>open_channel</c>, <c>accept_channel</c> and <c>channel_ready</c>:
    /// [<c>66*byte</c>:<c>public_nonce</c>].
    /// </summary>
    public static readonly BigSize NextLocalNonce = 4;

    /// <summary>
    /// <c>shutdown_nonce</c> (type 8) in <c>shutdown</c>: the sender's closee nonce, [<c>66*byte</c>:<c>public_nonce</c>].
    /// </summary>
    public static readonly BigSize ShutdownNonce = 8;

    /// <summary>
    /// <c>next_local_nonces</c> (type 22) in <c>revoke_and_ack</c> and <c>channel_reestablish</c>:
    /// [<c>...*nonce_entry</c>], each entry [<c>32*byte</c>:<c>funding_txid</c>] [<c>66*byte</c>:<c>public_nonce</c>].
    /// </summary>
    public static readonly BigSize NextLocalNonces = 22;

    /// <summary>
    /// <c>current_commit_nonce</c> (type 24) in <c>channel_reestablish</c> (BOLTs PR #1324): the nonce for the
    /// commitment of an interactive transaction whose <c>commitment_signed</c> the sender is still missing.
    /// </summary>
    public static readonly BigSize CurrentCommitNonce = 24;

    /// <summary>
    /// <c>closing_tlvs</c> type 5 <c>closer_no_closee</c> in <c>closing_complete</c> (98-byte partial signature with
    /// nonce) and <c>closing_sig</c> (32-byte partial signature): the transaction with only the closer's output.
    /// </summary>
    public static readonly BigSize CloserNoClosee = 5;

    /// <summary>
    /// <c>closing_tlvs</c> type 6 <c>no_closer_closee</c>: the transaction with only the closee's output.
    /// </summary>
    public static readonly BigSize NoCloserClosee = 6;

    /// <summary>
    /// <c>closing_tlvs</c> type 7 <c>closer_and_closee</c>: the transaction with both outputs.
    /// </summary>
    public static readonly BigSize CloserAndClosee = 7;

    /// <summary>
    /// <c>next_closee_nonce</c> (type 22) in <c>closing_sig</c>: the closee's nonce for the next
    /// <c>closing_complete</c>, [<c>66*byte</c>:<c>public_nonce</c>].
    /// </summary>
    public static readonly BigSize NextCloseeNonce = 22;

    /// <summary>
    /// <c>commit_nonces</c> (type 4) in <c>tx_complete</c> (BOLTs PR #1324): the current commitment's nonce, then the
    /// next commitment's, [<c>66*byte</c>] [<c>66*byte</c>].
    /// </summary>
    public static readonly BigSize CommitNonces = 4;

    /// <summary>
    /// <c>funding_nonce</c> (type 6) in <c>tx_complete</c> (BOLTs PR #1324): the signing nonce for the shared (previous
    /// funding) input of a splice, [<c>66*byte</c>:<c>public_nonce</c>].
    /// </summary>
    public static readonly BigSize FundingNonce = 6;

    /// <summary>
    /// <c>shared_input_partial_signature</c> (type 2) in <c>tx_signatures</c> (BOLTs PR #1324): the partial signature
    /// with nonce for the shared MuSig2 input of a splice, 98 bytes.
    /// </summary>
    public static readonly BigSize SharedInputPartialSignature = 2;
}