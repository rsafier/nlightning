namespace NLightning.Domain.Client.Responses;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;
using Payments.Enums;
using Payments.Models;

/// <summary>
/// One forwarded payment, as returned by <c>listforwards</c> (NL-597). The incoming HTLC is the key; the outgoing
/// side carries the requested <c>short_channel_id</c> and, once offered, the channel and HTLC id it went out on.
/// </summary>
public sealed class ForwardInfoClientResponse
{
    public required ChannelId IncomingChannelId { get; init; }
    public required ulong IncomingHtlcId { get; init; }
    public required LightningMoney IncomingAmount { get; init; }
    public required uint IncomingCltvExpiry { get; init; }

    /// <summary>The <c>short_channel_id</c> the incoming onion asked us to forward to.</summary>
    public required ShortChannelId OutgoingShortChannelId { get; init; }

    /// <summary>The channel the outgoing HTLC was offered on, once known.</summary>
    public ChannelId? OutgoingChannelId { get; init; }

    /// <summary>The id of the outgoing HTLC, once known.</summary>
    public ulong? OutgoingHtlcId { get; init; }

    public required LightningMoney OutgoingAmount { get; init; }
    public required uint OutgoingCltvExpiry { get; init; }

    /// <summary>The fee we earn: incoming − outgoing.</summary>
    public required LightningMoney Fee { get; init; }

    public required Hash PaymentHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public required ForwardCircuitStatus Status { get; init; }

    /// <summary>The BOLT 4 failure code, when it was sent in the clear and this circuit failed.</summary>
    public ushort? FailureCode { get; init; }

    /// <summary>The <see cref="Protocol.Onion.Enums.FailureCode"/> name of <see cref="FailureCode"/>, or the hex code
    /// when it is not a known one; null when there is none.</summary>
    public string? FailureCodeName { get; init; }

    /// <summary>
    /// The channel the failure is about, or null when the offer never went out (a local refusal).
    /// </summary>
    public ChannelId? FailureSource { get; init; }

    /// <summary>
    /// The <c>short_channel_id</c> of <see cref="IncomingChannelId"/>, when the channel is loaded and has one;
    /// null otherwise (the client shows the channel id then).
    /// </summary>
    public string? IncomingChannelScid { get; init; }

    /// <summary>The <c>short_channel_id</c> of <see cref="OutgoingChannelId"/>, same rule.</summary>
    public string? OutgoingChannelScid { get; init; }

    /// <summary>The <c>short_channel_id</c> of <see cref="FailureSource"/>, same rule.</summary>
    public string? FailureSourceScid { get; init; }

    /// <summary>Maps a circuit; the scids come from the caller's channel lookups (null = the channel is not
    /// loaded or has no scid, and the client shows the channel id).</summary>
    public static ForwardInfoClientResponse FromModel(ForwardCircuitModel circuit, string? incomingChannelScid = null,
                                                      string? outgoingChannelScid = null,
                                                      string? failureSourceScid = null) =>
        new()
        {
            IncomingChannelId = circuit.IncomingChannelId,
            IncomingHtlcId = circuit.IncomingHtlcId,
            IncomingAmount = circuit.IncomingAmount,
            IncomingCltvExpiry = circuit.IncomingCltvExpiry,
            OutgoingShortChannelId = circuit.OutgoingShortChannelId,
            OutgoingChannelId = circuit.OutgoingChannelId,
            OutgoingHtlcId = circuit.OutgoingHtlcId,
            OutgoingAmount = circuit.OutgoingAmount,
            OutgoingCltvExpiry = circuit.OutgoingCltvExpiry,
            Fee = circuit.Fee,
            PaymentHash = circuit.PaymentHash,
            CreatedAt = circuit.CreatedAt,
            ResolvedAt = circuit.ResolvedAt,
            Status = circuit.Status,
            FailureCode = circuit.FailureCode,
            FailureCodeName = FailureCodeNameOf(circuit.FailureCode),
            FailureSource = circuit.FailureSource,
            IncomingChannelScid = incomingChannelScid,
            OutgoingChannelScid = outgoingChannelScid,
            FailureSourceScid = failureSourceScid
        };

    internal static string? FailureCodeNameOf(ushort? code) =>
        code is not { } value ? null
            : Enum.IsDefined(typeof(Protocol.Onion.Enums.FailureCode), value)
                ? ((Protocol.Onion.Enums.FailureCode)value).ToString()
                : $"0x{value:x4}";
}