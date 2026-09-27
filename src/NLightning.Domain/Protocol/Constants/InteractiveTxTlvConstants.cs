using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Constants;

using ValueObjects;

/// <summary>
/// TLV types of the interactive-tx messages' TLV streams (BOLT 2 "Interactive Transaction Construction").
/// </summary>
/// <remarks>
/// Each message has its own TLV namespace and the numbers collide with <see cref="TlvConstants"/> (both types here are
/// 0), so they live in their own class: always read them against the message they belong to.
/// </remarks>
[ExcludeFromCodeCoverage]
public static class InteractiveTxTlvConstants
{
    /// <summary>
    /// <c>tx_add_input_tlvs</c> type 0 <c>shared_input_txid</c> [<c>sha256</c>:<c>funding_txid</c>] (IT-W-01): the
    /// input is the channel's current funding output (a splice), sent with <c>prevtx_len</c> = 0.
    /// </summary>
    public static readonly BigSize SharedInputTxId = 0;

    /// <summary>
    /// <c>tx_signatures_tlvs</c> type 0 <c>shared_input_signature</c> [<c>signature</c>:<c>signature</c>] (IT-W-03): the
    /// sender's signature for the shared (2-of-2 funding) input.
    /// </summary>
    public static readonly BigSize SharedInputSignature = 0;
}