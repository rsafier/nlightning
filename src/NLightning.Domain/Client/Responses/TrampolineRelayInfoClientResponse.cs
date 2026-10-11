namespace NLightning.Domain.Client.Responses;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;
using Payments.Trampoline;

/// <summary>
/// One trampoline payment we relayed (NL-875), as <c>listforwards</c> shows it next to the forwards (kind
/// <c>trampoline</c>): N incoming parts paid into one outgoing payment to the next trampoline node.
/// </summary>
public sealed class TrampolineRelayInfoClientResponse
{
    public required Hash PaymentHash { get; init; }
    public required TrampolineRelayStatus Status { get; init; }

    /// <summary>The incoming parts stored so far.</summary>
    public required int Parts { get; init; }

    /// <summary>The channels the parts came in on, in part order, without repeats.</summary>
    public required IReadOnlyList<ChannelId> IncomingChannelIds { get; init; }

    /// <summary>The <c>short_channel_id</c> of each of <see cref="IncomingChannelIds"/> when the channel is loaded and
    /// has one, else null (the client shows the channel id).</summary>
    public required IReadOnlyList<string?> IncomingChannelScids { get; init; }

    /// <summary>The sum of the incoming parts' amounts.</summary>
    public required LightningMoney IncomingAmount { get; init; }

    /// <summary>The outer onion's <c>total_msat</c> the incoming set had to reach.</summary>
    public required LightningMoney IncomingTotal { get; init; }

    /// <summary>What the next node had to receive (<c>amt_to_forward</c>).</summary>
    public required LightningMoney AmountOut { get; init; }

    /// <summary>What the relay earned once fulfilled (incoming sum minus what the outgoing payment cost); null
    /// before.</summary>
    public LightningMoney? FeeEarned { get; init; }

    /// <summary>The next trampoline node; null when the relay paid the recipient's blinded paths.</summary>
    public CompactPubKey? NextNodeId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The BOLT 4 failure code we answered with, when we made the error ourselves.</summary>
    public ushort? FailureCode { get; init; }

    /// <summary>The failure code's name (or hex when unknown), or null when there is no code.</summary>
    public string? FailureCodeName { get; init; }

    /// <summary>
    /// For a failed attempt that a payer's retry with the same payment hash replaced (NL-899), its number (1 = the
    /// first); null for the relay the hash has now.
    /// </summary>
    public int? ReplacedAttempt { get; init; }

    /// <summary>Maps a relay and its parts; <paramref name="scidOf"/> names a loaded channel's scid (or null).</summary>
    public static TrampolineRelayInfoClientResponse FromModel(TrampolineRelayModel relay,
                                                              IReadOnlyList<TrampolineRelayPartModel> parts,
                                                              Func<ChannelId, string?>? scidOf = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(parts);

        var channels = parts.Select(p => p.ChannelId).Distinct().ToList();
        return new TrampolineRelayInfoClientResponse
        {
            PaymentHash = relay.PaymentHash,
            Status = relay.Status,
            Parts = parts.Count,
            IncomingChannelIds = channels,
            IncomingChannelScids = channels.Select(c => scidOf?.Invoke(c)).ToList(),
            IncomingAmount = parts.Aggregate(LightningMoney.Zero, (sum, p) => sum + p.Amount),
            IncomingTotal = relay.IncomingTotal,
            AmountOut = relay.AmountOut,
            FeeEarned = relay.FeeEarned,
            NextNodeId = relay.NextNodeId,
            CreatedAt = relay.CreatedAt,
            CompletedAt = relay.CompletedAt,
            FailureCode = relay.FailureCode,
            FailureCodeName = ForwardInfoClientResponse.FailureCodeNameOf(relay.FailureCode)
        };
    }

    /// <summary>Maps a replaced failed attempt (NL-899); <paramref name="scidOf"/> names a loaded channel's scid (or
    /// null).</summary>
    public static TrampolineRelayInfoClientResponse FromAttempt(TrampolineRelayAttemptModel attempt,
                                                                Func<ChannelId, string?>? scidOf = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        return new TrampolineRelayInfoClientResponse
        {
            PaymentHash = attempt.PaymentHash,
            Status = TrampolineRelayStatus.Failed,
            Parts = attempt.Parts,
            IncomingChannelIds = attempt.IncomingChannelIds,
            IncomingChannelScids = attempt.IncomingChannelIds.Select(c => scidOf?.Invoke(c)).ToList(),
            IncomingAmount = attempt.IncomingAmount,
            IncomingTotal = attempt.IncomingTotal,
            AmountOut = attempt.AmountOut,
            NextNodeId = attempt.NextNodeId,
            CreatedAt = attempt.CreatedAt,
            CompletedAt = attempt.CompletedAt,
            FailureCode = attempt.FailureCode,
            FailureCodeName = ForwardInfoClientResponse.FailureCodeNameOf(attempt.FailureCode),
            ReplacedAttempt = attempt.Attempt
        };
    }
}