using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Enums;

/// <summary>
/// Response for FetchInvoice (ClientCommand 30), and the fetch half of <see cref="PayOfferIpcResponse"/>.
/// </summary>
[MessagePackObject]
public sealed class FetchInvoiceIpcResponse
{
    [Key(0)] public required FetchInvoiceStatus Status { get; init; }
    [Key(1)] public int Attempts { get; init; }

    /// <summary>The <c>invoice_error</c>'s text, or why the fetch failed.</summary>
    [Key(2)] public string? Error { get; init; }

    [Key(3)] public ulong? ErroneousField { get; init; }
    [Key(4)] public CompactPubKey? NodeId { get; init; }
    [Key(5)] public LightningMoney? Amount { get; init; }
    [Key(6)] public Hash? PaymentHash { get; init; }
    [Key(7)] public DateTimeOffset? CreatedAt { get; init; }
    [Key(8)] public DateTimeOffset? ExpiresAt { get; init; }
    [Key(9)] public int PathCount { get; init; }

    /// <summary>The invoice's TLV stream.</summary>
    [Key(10)] public byte[]? Invoice { get; init; }

    public static FetchInvoiceIpcResponse FromClientResponse(FetchInvoiceClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new FetchInvoiceIpcResponse
        {
            Status = response.Status,
            Attempts = response.Attempts,
            Error = response.Error,
            ErroneousField = response.ErroneousField,
            NodeId = response.NodeId,
            Amount = response.Amount,
            PaymentHash = response.PaymentHash,
            CreatedAt = response.CreatedAt,
            ExpiresAt = response.ExpiresAt,
            PathCount = response.PathCount,
            Invoice = response.Invoice
        };
    }
}