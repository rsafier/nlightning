namespace NLightning.Domain.Protocol.InteractiveTx;

using Bitcoin.ValueObjects;
using Models;
using Money;

/// <summary>
/// The shared funding input and output of a negotiation (splicing plan §3.9): what the session must see added, and how
/// much of each belongs to each side for the <c>tx_complete</c> and <c>tx_signatures</c> rules.
/// </summary>
/// <remarks>
/// <para>For a splice (BOLT 2 "Channel Splicing") the initiator adds <see cref="SharedInput"/> and the new funding
/// output; the acceptor checks both against this spec. For a dual-funded open <see cref="SharedInput"/> is null (the
/// plan's §3.9 allows the whole spec to be null there; the host decides).</para>
/// <para>The shares feed IT-R-04 ("the peer's inputs cover its outputs", its share of the funding output counting as
/// one of its outputs and its share of the shared input as one of its inputs) and IT-SIG-01 (the total each side
/// contributed decides who sends <c>tx_signatures</c> first). Which share the fees of the shared input/output come from
/// (splicing plan D16) is the host's business: the shares here are after fees.</para>
/// </remarks>
/// <param name="SharedInput">The funding output the transaction spends (a splice), or null (a dual-funded open).</param>
/// <param name="SharedOutputScript">The (new) funding output's P2WSH 2-of-2 script.</param>
/// <param name="SharedOutputAmount">The (new) funding output's value.</param>
/// <param name="LocalInputShare">Our part of <see cref="SharedInput"/> (our channel balance, rounded down to the
/// satoshi); zero without a shared input.</param>
/// <param name="RemoteInputShare">The peer's part of <see cref="SharedInput"/>; zero without a shared input.</param>
/// <param name="LocalOutputShare">Our part of the shared output (our balance after the splice).</param>
/// <param name="RemoteOutputShare">The peer's part of the shared output.</param>
public sealed record SharedFundingSpec(
    SharedFundingInput? SharedInput,
    BitcoinScript SharedOutputScript,
    LightningMoney SharedOutputAmount,
    LightningMoney LocalInputShare,
    LightningMoney RemoteInputShare,
    LightningMoney LocalOutputShare,
    LightningMoney RemoteOutputShare);