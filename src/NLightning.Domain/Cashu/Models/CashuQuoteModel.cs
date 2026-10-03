namespace NLightning.Domain.Cashu.Models;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Enums;
using Money;

/// <summary>
/// A Cashu mint's quote as the CDK payment processor keeps it (NL-997, table <c>CashuQuotes</c>): the mint's quote id
/// bound to what we created or paid for it, so the binding survives a restart of the node or of the mint.
/// </summary>
/// <remarks>
/// <para>Rows exist for every melt (all methods: the quote id names the payment, CDK's <c>QUOTE_ID</c> identifier) and
/// for on-chain mint quotes (the quote's address). BOLT 11 and BOLT 12 mint quotes need none: their invoices and
/// offers carry the processor's label.</para>
/// <para>A melt row is saved as <see cref="CashuQuoteState.Dispatching"/> before its payment starts and moves to
/// <see cref="CashuQuoteState.Pending"/>, <see cref="CashuQuoteState.Paid"/> or <see cref="CashuQuoteState.Failed"/>.
/// </para>
/// </remarks>
public sealed class CashuQuoteModel
{
    /// <summary>The longest quote id stored (CDK's ids are UUIDs or base64 strings).</summary>
    public const int QuoteIdMaxLength = 128;

    /// <summary>The longest address stored.</summary>
    public const int AddressMaxLength = 128;

    public CashuQuoteModel(string quoteId, CashuQuoteMethod method, CashuQuoteDirection direction,
                           LightningMoney amount, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quoteId);
        if (quoteId.Length > QuoteIdMaxLength)
            throw new ArgumentException($"A quote id is at most {QuoteIdMaxLength} characters.", nameof(quoteId));

        QuoteId = quoteId;
        Method = method;
        Direction = direction;
        Amount = amount;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        State = CashuQuoteState.Created;
    }

    /// <summary>The mint's quote id.</summary>
    public string QuoteId { get; }

    public CashuQuoteMethod Method { get; }
    public CashuQuoteDirection Direction { get; }

    /// <summary>What the quote pays (a melt) or asks for (an on-chain mint quote: zero, any amount).</summary>
    public LightningMoney Amount { get; set; }

    /// <summary>The melt's fee limit, when known.</summary>
    public LightningMoney? MaxFee { get; set; }

    /// <summary>The fee the melt paid, once known.</summary>
    public LightningMoney? Fee { get; set; }

    /// <summary>The Lightning payment of a melt (BOLT 11, or the BOLT 12 invoice we fetched), once known.</summary>
    public Hash? PaymentHash { get; set; }

    /// <summary>The address of an on-chain quote (ours for a mint quote, the destination for a melt).</summary>
    public string? Address
    {
        get;
        set
        {
            if (value is { Length: > AddressMaxLength })
                throw new ArgumentException($"An address is at most {AddressMaxLength} characters.", nameof(value));
            field = value;
        }
    }

    /// <summary>What a melt pays: the BOLT 11 invoice or the BOLT 12 offer.</summary>
    public string? Request { get; set; }

    /// <summary>The fee option the mint chose for an on-chain melt (its <c>fee_index</c>).</summary>
    public uint? FeeIndex { get; set; }

    /// <summary>The transaction of an on-chain melt, once sent.</summary>
    public TxId? TxId { get; set; }

    /// <summary>The destination output of an on-chain melt's transaction.</summary>
    public uint? OutputIndex { get; set; }

    public CashuQuoteState State { get; private set; }

    /// <summary>Why a melt failed.</summary>
    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The on-chain melt's proof for the mint: <c>txid:vout</c>, once sent.</summary>
    public string? Outpoint => TxId is { } txId && OutputIndex is { } index ? $"{txId}:{index}" : null;

    /// <summary>Moves the quote to <paramref name="state"/> (clearing a failure reason unless it fails).</summary>
    public void SetState(CashuQuoteState state, DateTimeOffset at, string? failureReason = null)
    {
        State = state;
        FailureReason = state == CashuQuoteState.Failed ? failureReason ?? "The payment failed." : null;
        UpdatedAt = at;
    }

    /// <summary>Rebuilds a stored quote.</summary>
    public static CashuQuoteModel Restore(string quoteId, CashuQuoteMethod method, CashuQuoteDirection direction,
                                          LightningMoney amount, LightningMoney? maxFee, LightningMoney? fee,
                                          Hash? paymentHash, string? address, string? request, uint? feeIndex,
                                          TxId? txId, uint? outputIndex, CashuQuoteState state,
                                          string? failureReason, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        return new CashuQuoteModel(quoteId, method, direction, amount, createdAt)
        {
            MaxFee = maxFee,
            Fee = fee,
            PaymentHash = paymentHash,
            Address = address,
            Request = request,
            FeeIndex = feeIndex,
            TxId = txId,
            OutputIndex = outputIndex,
            State = state,
            FailureReason = failureReason,
            UpdatedAt = updatedAt
        };
    }
}