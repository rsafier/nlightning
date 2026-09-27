namespace NLightning.Domain.Protocol.InteractiveTx.Interfaces;

using Bitcoin.ValueObjects;
using Models;

/// <summary>
/// Reads the <c>prevtx</c> of a received <c>tx_add_input</c> (BOLT 2 "The tx_add_input Message", IT-R-01; NL-041).
/// Implemented by <c>PrevTxInspector</c> in Infrastructure.Bitcoin (lane IT-B, IT2-T1), since parsing a transaction
/// needs NBitcoin.
/// </summary>
public interface IPrevTxInspector
{
    /// <summary>
    /// Parses <paramref name="prevTx"/> and reads output <paramref name="prevTxVout"/>. Pure and synchronous, so the
    /// session can call it while it applies the message. Never throws for malformed input: an unparsable transaction or
    /// an out-of-range <c>prevtx_vout</c> is an invalid <see cref="PrevTxInspection"/>.
    /// </summary>
    /// <param name="prevTx">The serialized previous transaction.</param>
    /// <param name="prevTxVout">The spent output's index.</param>
    PrevTxInspection Inspect(ReadOnlyMemory<byte> prevTx, uint prevTxVout);

    /// <summary>
    /// Whether <paramref name="txId"/> is confirmed on our chain: the <c>require_confirmed_inputs</c> check the driver
    /// runs on each input the peer adds before it hands the message to the session (BOLT 2: fail the negotiation on an
    /// unconfirmed input when we required confirmed ones).
    /// </summary>
    Task<bool> IsConfirmedAsync(TxId txId, CancellationToken cancellationToken = default);
}