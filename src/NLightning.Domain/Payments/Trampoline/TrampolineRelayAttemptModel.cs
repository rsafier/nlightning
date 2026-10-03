namespace NLightning.Domain.Payments.Trampoline;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;

/// <summary>
/// A failed trampoline relay that a payer's retry with the same payment hash replaced (NL-899): what
/// <c>listforwards</c> still shows of it once the relay row made way for the new attempt
/// (<c>ITrampolineRelayDbRepository.RemoveFailedAsync</c>). Keyed by (<see cref="PaymentHash"/>,
/// <see cref="Attempt"/>); immutable. Its status is always <see cref="TrampolineRelayStatus.Failed"/>: it earned
/// nothing.
/// </summary>
/// <param name="PaymentHash">The payment hash (shared with the attempts that followed).</param>
/// <param name="Attempt">The replaced attempt's number for this hash, from 1 (the first failed attempt).</param>
/// <param name="NextNodeId">The next trampoline node, or null for the recipient's blinded paths.</param>
/// <param name="AmountOut">What the next node had to receive.</param>
/// <param name="CltvExpiryOut">The next node's <c>outgoing_cltv_value</c>.</param>
/// <param name="IncomingTotal">The outer onion's <c>total_msat</c>.</param>
/// <param name="IncomingAmount">The sum of the incoming parts the attempt had.</param>
/// <param name="Parts">How many incoming parts it had.</param>
/// <param name="IncomingChannelIds">The channels the parts came in on, in part order, without repeats.</param>
/// <param name="FailureCode">The BOLT 4 failure code we answered with, when we made the error ourselves.</param>
/// <param name="FailureReason">Why it failed (local text).</param>
/// <param name="CreatedAt">When its first part arrived.</param>
/// <param name="CompletedAt">When it failed.</param>
public sealed record TrampolineRelayAttemptModel(
    Hash PaymentHash,
    int Attempt,
    CompactPubKey? NextNodeId,
    LightningMoney AmountOut,
    uint CltvExpiryOut,
    LightningMoney IncomingTotal,
    LightningMoney IncomingAmount,
    int Parts,
    IReadOnlyList<ChannelId> IncomingChannelIds,
    ushort? FailureCode,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);