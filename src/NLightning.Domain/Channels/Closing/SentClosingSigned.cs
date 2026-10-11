namespace NLightning.Domain.Channels.Closing;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// Our latest legacy <c>closing_signed</c> (BOLT 2), persisted before it is sent (NL-1377): the fee we proposed or
/// accepted, the txid of our variant of the closing transaction at that fee and our signature of it. With it, the peer
/// can broadcast that transaction; a process that takes the channel over from the same database sees what we signed.
/// </summary>
/// <param name="FeeSatoshis">The <c>fee_satoshis</c> of our <c>closing_signed</c>.</param>
/// <param name="ClosingTxId">The txid of the closing transaction our signature is for (our dust limit's variant).</param>
/// <param name="Signature">Our signature of it.</param>
public sealed record SentClosingSigned(ulong FeeSatoshis, TxId ClosingTxId, CompactSignature Signature);