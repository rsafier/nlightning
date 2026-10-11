namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Bitcoin.ValueObjects;
using Money;

/// <summary>
/// An output we contribute to an interactive-tx negotiation (<see cref="InteractiveTxContribution"/>): wallet change,
/// or a splice-out destination. The shared funding output is not one of these (<see cref="SharedFundingSpec"/>).
/// </summary>
/// <param name="Amount">The output value, at least the dust limit.</param>
/// <param name="ScriptPubKey">The output script (P2WPKH, P2WSH or P2TR are always accepted by the peer, IT-R-02).</param>
/// <param name="IsChange">Whether it is our wallet's change.</param>
public sealed record ContributedOutput(LightningMoney Amount, BitcoinScript ScriptPubKey, bool IsChange);