using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Money;

/// <summary>
/// Request for PayInvoice (ClientCommand 10).
/// </summary>
[MessagePackObject]
public sealed class PayInvoiceIpcRequest
{
    /// <summary>
    /// The BOLT 11 invoice to pay.
    /// </summary>
    [Key(0)] public required string Bolt11 { get; init; }

    /// <summary>
    /// The amount to pay when the invoice has none; null when it has one.
    /// </summary>
    [Key(1)] public LightningMoney? Amount { get; init; }

    /// <summary>
    /// How long the daemon waits for the outcome, in seconds, or null for its default.
    /// </summary>
    [Key(2)] public uint? TimeoutSeconds { get; init; }

    /// <summary>
    /// The most the payment may pay in routing fees, or null for the daemon's default (NL-270).
    /// </summary>
    [Key(3)] public LightningMoney? MaxFee { get; init; }

    /// <summary>
    /// The most HTLCs the payment may have in flight at once (1 never splits), or null for the daemon's default.
    /// </summary>
    [Key(4)] public uint? MaxParts { get; init; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>), or null; an older client sends none.
    /// </summary>
    [Key(5)] public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>), or null for none.
    /// </summary>
    [Key(6)] public List<string>? Tags { get; init; }

    /// <summary>
    /// The only channel of ours the payment may leave through (a channel id or a short channel id
    /// <c>BLOCKxTXxOUTPUT</c>), or null for any (NL-609, <c>--out</c>).
    /// </summary>
    [Key(7)] public string? OutgoingChannel { get; init; }

    /// <summary>
    /// For an invoice of our own (a circular rebalance, NL-609): the only channel of ours the payment may come back in
    /// through (a channel id or a short channel id), or null for any (<c>--in</c>).
    /// </summary>
    [Key(8)] public string? IncomingChannel { get; init; }

    public PayInvoiceClientRequest ToClientRequest()
    {
        return new PayInvoiceClientRequest(Bolt11)
        {
            Amount = Amount,
            TimeoutSeconds = TimeoutSeconds,
            MaxFee = MaxFee,
            MaxParts = MaxParts,
            OutgoingChannel = OutgoingChannel,
            IncomingChannel = IncomingChannel,
            Label = Label,
            Tags = Tags ?? []
        };
    }
}