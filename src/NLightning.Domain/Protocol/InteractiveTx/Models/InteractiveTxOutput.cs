namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Bitcoin.ValueObjects;
using Enums;
using Money;

/// <summary>
/// One output of an interactive-tx negotiation, as currently added by either side (BOLT 2 <c>tx_add_output</c>).
/// </summary>
/// <param name="SerialId">The <c>serial_id</c>: even when added by the initiator, odd otherwise (IT-S-01); the
/// transaction's outputs are sorted by it.</param>
/// <param name="AddedBy">The side that added it.</param>
/// <param name="Amount">The output value (<c>sats</c>): at least the dust limit and at most MAX_MONEY (IT-R-02).</param>
/// <param name="ScriptPubKey">The output script.</param>
/// <param name="IsShared">Whether this is the channel's (new) funding output (<see cref="SharedFundingSpec"/>).</param>
public sealed record InteractiveTxOutput(
    ulong SerialId,
    InteractiveTxParty AddedBy,
    LightningMoney Amount,
    BitcoinScript ScriptPubKey,
    bool IsShared);