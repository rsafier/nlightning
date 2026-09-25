// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Crypto.ValueObjects;

/// <summary>
/// An invoice we issued (<c>InvoiceModel</c>, BOLT2 plan N8-T2), keyed by payment hash.
/// </summary>
public class InvoiceEntity
{
    /// <summary>
    /// The 32-byte payment hash.
    /// </summary>
    /// <remarks>This is the primary key</remarks>
    public required Hash PaymentHash { get; set; }

    /// <summary>
    /// The 32-byte preimage of <see cref="PaymentHash"/>.
    /// </summary>
    public required byte[] Preimage { get; set; }

    /// <summary>
    /// The 32-byte BOLT 11 <c>payment_secret</c>.
    /// </summary>
    public required byte[] PaymentSecret { get; set; }

    /// <summary>
    /// The requested amount in millisatoshi, or null for an any-amount invoice.
    /// </summary>
    public long? AmountMsat { get; set; }

    /// <summary>
    /// The BOLT 11 description, if any.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The encoded, signed BOLT 11 string.
    /// </summary>
    public required string Bolt11 { get; set; }

    /// <summary>
    /// When the invoice was created (stored as UTC ticks).
    /// </summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// BOLT 11 <c>x</c>, in seconds.
    /// </summary>
    public required uint ExpirySeconds { get; set; }

    /// <summary>
    /// BOLT 11 <c>c</c> (<c>min_final_cltv_expiry_delta</c>).
    /// </summary>
    public required ushort MinFinalCltvExpiry { get; set; }

    /// <summary>
    /// <c>InvoiceStatus</c> (0 open, 1 accepted, 2 settled, 3 canceled).
    /// </summary>
    public required byte Status { get; set; }

    /// <summary>
    /// The amount the paying HTLC carried, in millisatoshi, once accepted.
    /// </summary>
    public long? AmountReceivedMsat { get; set; }

    /// <summary>
    /// When the invoice was settled (stored as UTC ticks).
    /// </summary>
    public DateTimeOffset? SettledAt { get; set; }

    // Default constructor for EF Core
    internal InvoiceEntity()
    {
    }
}