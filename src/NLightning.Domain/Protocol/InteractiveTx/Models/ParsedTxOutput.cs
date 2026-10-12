namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Bitcoin.ValueObjects;
using Money;

/// <summary>An output of a <see cref="ParsedInteractiveTx"/>.</summary>
/// <param name="Amount">Its amount (whole satoshis).</param>
/// <param name="ScriptPubKey">Its script.</param>
public sealed record ParsedTxOutput(LightningMoney Amount, BitcoinScript ScriptPubKey);