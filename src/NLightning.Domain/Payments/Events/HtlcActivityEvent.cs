namespace NLightning.Domain.Payments.Events;

/// <summary>A live, committed HTLC observation; channel identifiers are SCIDs (zero for an absent leg).</summary>
public sealed record HtlcActivityEvent(
    HtlcActivityKind Kind, HtlcActivityRole Role, ulong IncomingChannelId, ulong IncomingHtlcId,
    ulong OutgoingChannelId, ulong OutgoingHtlcId, DateTimeOffset OccurredAt,
    ulong IncomingAmountMsat = 0, uint IncomingTimelock = 0,
    ulong OutgoingAmountMsat = 0, uint OutgoingTimelock = 0,
    ReadOnlyMemory<byte> Preimage = default, ushort? WireFailure = null,
    string FailureString = "", bool Settled = false);

public enum HtlcActivityKind { Forward, ForwardFail, Settle, LinkFail, Final }
public enum HtlcActivityRole { Unknown, Send, Receive, Forward }