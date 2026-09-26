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

    public PayInvoiceClientRequest ToClientRequest()
    {
        return new PayInvoiceClientRequest(Bolt11)
        {
            Amount = Amount,
            TimeoutSeconds = TimeoutSeconds,
            MaxFee = MaxFee,
            MaxParts = MaxParts
        };
    }
}