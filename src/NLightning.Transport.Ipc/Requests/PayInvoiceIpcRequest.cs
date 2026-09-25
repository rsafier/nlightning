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

    public PayInvoiceClientRequest ToClientRequest()
    {
        return new PayInvoiceClientRequest(Bolt11)
        {
            Amount = Amount,
            TimeoutSeconds = TimeoutSeconds
        };
    }
}