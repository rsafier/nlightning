// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Cashu;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A quote of the CDK payment processor (<c>CashuQuoteModel</c>, NL-997, migration <c>AddCashuProcessorQuotes</c>),
/// keyed by the mint's quote id.
/// </summary>
public class CashuQuoteEntity
{
    public required string QuoteId { get; set; }

    /// <summary><c>CashuQuoteMethod</c> (1 bolt11, 2 bolt12, 3 onchain).</summary>
    public required byte Method { get; set; }

    /// <summary><c>CashuQuoteDirection</c> (1 incoming, 2 outgoing).</summary>
    public required byte Direction { get; set; }

    public required long AmountMsat { get; set; }
    public long? MaxFeeMsat { get; set; }
    public long? FeeMsat { get; set; }
    public Hash? PaymentHash { get; set; }
    public string? Address { get; set; }
    public string? Request { get; set; }
    public uint? FeeIndex { get; set; }
    public TxId? TxId { get; set; }
    public uint? OutputIndex { get; set; }

    /// <summary><c>CashuQuoteState</c> (1 created, 2 dispatching, 3 pending, 4 paid, 5 failed).</summary>
    public required byte State { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>Stored as UTC ticks.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>Stored as UTC ticks.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }

    // Default constructor for EF Core
    internal CashuQuoteEntity()
    {
    }
}