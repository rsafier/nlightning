namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Bitcoin.ValueObjects;
using Money;

/// <summary>
/// A wallet input we contribute to an interactive-tx negotiation (<see cref="InteractiveTxContribution"/>), before the
/// session gives it a <c>serial_id</c>.
/// </summary>
/// <param name="PrevTxId">The txid of the spent output.</param>
/// <param name="PrevTxVout">The index of the spent output.</param>
/// <param name="PrevTx">The serialized previous transaction, sent as <c>prevtx</c>.</param>
/// <param name="Sequence">The <c>nSequence</c> to use (at most 0xFFFFFFFD, IT-S-01).</param>
/// <param name="Amount">The value of the spent output.</param>
/// <param name="ScriptPubKey">Its scriptPubKey.</param>
/// <param name="InputWeight">The weight the input adds once signed, witness included (charged to us, IT-S-03).</param>
public sealed record ContributedInput(
    TxId PrevTxId,
    uint PrevTxVout,
    byte[] PrevTx,
    uint Sequence,
    LightningMoney Amount,
    BitcoinScript ScriptPubKey,
    int InputWeight);