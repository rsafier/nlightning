// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Crypto.ValueObjects;

/// <summary>
/// An invoice we issued (<c>InvoiceModel</c>, BOLT2 plan N8-T2; BOLT 12 columns since <c>AddBolt12Offers</c>), keyed
/// by payment hash.
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
    /// The encoded, signed BOLT 11 string, or null for a BOLT 12 invoice.
    /// </summary>
    public string? Bolt11 { get; set; }

    /// <summary>
    /// <c>InvoiceKind</c> (0 BOLT 11, 1 BOLT 12; migration <c>AddBolt12Offers</c>, existing rows 0).
    /// </summary>
    public required byte Kind { get; set; }

    /// <summary>
    /// The offer a BOLT 12 invoice was issued for (foreign key to <c>Offers</c>).
    /// </summary>
    public Hash? OfferId { get; set; }

    /// <summary>
    /// The signed BOLT 12 invoice's TLV stream, as sent.
    /// </summary>
    public byte[]? Bolt12InvoiceBytes { get; set; }

    /// <summary>
    /// The payer's custom records of a keysend record (<c>InvoiceKind.Keysend</c>), as a TLV stream (migration
    /// <c>AddPaymentCustomRecords</c>; before it they were stored in <see cref="Bolt12InvoiceBytes"/>).
    /// </summary>
    public byte[]? CustomRecords { get; set; }

    /// <summary>
    /// The invoice_request's 33-byte <c>invreq_payer_id</c>.
    /// </summary>
    public CompactPubKey? InvoiceRequestPayerId { get; set; }

    /// <summary>
    /// The invoice_request's <c>invreq_quantity</c>, if any.
    /// </summary>
    public ulong? Quantity { get; set; }

    /// <summary>
    /// The invoice_request's <c>invreq_payer_note</c>, if any.
    /// </summary>
    public string? PayerNote { get; set; }

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