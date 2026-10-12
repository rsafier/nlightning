namespace NLightning.Domain.Payments.Models;

using Crypto.ValueObjects;
using Enums;
using Keysend;
using Money;
using Offers.Models;

/// <summary>
/// An invoice we issued (BOLT 11, or BOLT 12 for one of our offers), with the secrets the final hop needs to accept a
/// payment for it.
/// </summary>
/// <remarks>
/// <para>The preimage and payment secret come from a CSPRNG and the model is persisted
/// (<c>IInvoiceDbRepository</c>) <b>before</b> the BOLT 11 string (or the BOLT 12 invoice) is handed to anyone, so a
/// payment can never arrive for an invoice we forgot.</para>
/// <para>Status moves only forward: <see cref="InvoiceStatus.Open"/> → <see cref="InvoiceStatus.Accepted"/> →
/// <see cref="InvoiceStatus.Settled"/>, or <see cref="InvoiceStatus.Open"/> → <see cref="InvoiceStatus.Canceled"/>.
/// The mutators throw <see cref="InvalidOperationException"/> on any other transition.</para>
/// </remarks>
public sealed class InvoiceModel
{
    public Hash PaymentHash { get; }

    /// <summary>
    /// The preimage of <see cref="PaymentHash"/>; revealed only in <c>update_fulfill_htlc</c>.
    /// </summary>
    /// <summary>
    /// The preimage of the payment hash; revealed only in <c>update_fulfill_htlc</c>. Null only for a hold invoice
    /// (NL-995), whose preimage arrives from outside with the operator's settle (<see cref="SettleHeld"/>).
    /// </summary>
    public Secret? Preimage { get; private set; }

    /// <summary>
    /// BOLT 11 <c>s</c>: the final hop fails an HTLC whose onion <c>payment_data</c> does not carry it.
    /// </summary>
    public Secret PaymentSecret { get; }

    /// <summary>
    /// The requested amount, or null for an invoice that accepts any amount.
    /// </summary>
    public LightningMoney? Amount { get; }

    public string? Description { get; }

    /// <summary>
    /// The encoded, signed BOLT 11 string, or null for a BOLT 12 invoice (<see cref="Bolt12"/> is set instead: BOLT 12
    /// invoices have no string form and travel only in onion messages).
    /// </summary>
    public string? Bolt11 { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// BOLT 11 <c>x</c>, in seconds.
    /// </summary>
    public uint ExpirySeconds { get; }

    /// <summary>
    /// BOLT 11 <c>c</c> (<c>min_final_cltv_expiry_delta</c>).
    /// </summary>
    public ushort MinFinalCltvExpiry { get; }

    public InvoiceStatus Status { get; private set; }

    /// <summary>
    /// The amount the paying HTLC carried, once <see cref="InvoiceStatus.Accepted"/>.
    /// </summary>
    public LightningMoney? AmountReceived { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public DateTimeOffset ExpiresAt => CreatedAt.AddSeconds(ExpirySeconds);

    /// <summary>
    /// The BOLT 12 side of an invoice issued for one of our offers, or null for a BOLT 11 invoice.
    /// </summary>
    public Bolt12InvoiceDetails? Bolt12 { get; }

    /// <summary>
    /// The custom records of a spontaneous payment we received (<see cref="InvoiceKind.Keysend"/>), or null for an
    /// invoice we issued.
    /// </summary>
    public KeysendDetails? Keysend { get; }

    /// <summary>
    /// <see cref="InvoiceKind.Bolt12"/> when <see cref="Bolt12"/> is set, <see cref="InvoiceKind.Keysend"/> when
    /// <see cref="Keysend"/> is, else <see cref="InvoiceKind.Bolt11"/>.
    /// </summary>
    public InvoiceKind Kind => Bolt12 is not null ? InvoiceKind.Bolt12
                             : Keysend is not null ? InvoiceKind.Keysend
                             : InvoiceKind.Bolt11;

    /// <summary>
    /// The operator's label (NL-602 A3-T1, migration <c>AddAccountingFinancial</c>; at most 256 UTF-8 bytes), or null.
    /// The accounting writers copy it into the event's details.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// The operator's tags as one canonical <c>k=v</c> list (NL-602 A3-T1, at most 1 KiB), or null.
    /// </summary>
    public string? Tags { get; set; }

    /// <summary>
    /// LND's <c>add_index</c> (NL-1165): 1, 2, 3, ... in the order invoices were saved, read from the store (the unit
    /// of work assigns it when the invoice is first saved); null on a model not read back since.
    /// </summary>
    public ulong? AddIndex { get; set; }

    /// <summary>LND's <c>settle_index</c> (NL-1165): assigned in the save that settles the invoice; null until read back
    /// settled.</summary>
    public ulong? SettleIndex { get; set; }

    /// <summary>The HTLCs that paid (or are held for) the invoice (NL-1167), recorded when the set is held or settles;
    /// empty before, and for rows from before the record existed.</summary>
    public IReadOnlyList<InvoiceHtlc> Htlcs { get; set; } = [];

    public InvoiceModel(Hash paymentHash, Secret? preimage, Secret paymentSecret, LightningMoney? amount,
                        string? description, string? bolt11, DateTimeOffset createdAt, uint expirySeconds,
                        ushort minFinalCltvExpiry, InvoiceStatus status = InvoiceStatus.Open,
                        LightningMoney? amountReceived = null, DateTimeOffset? settledAt = null,
                        Bolt12InvoiceDetails? bolt12 = null, KeysendDetails? keysend = null)
    {
        if (bolt12 is not null && keysend is not null)
            throw new ArgumentException("A keysend record is not a BOLT 12 invoice.", nameof(keysend));
        if (preimage is null && (bolt12 is not null || keysend is not null))
            throw new ArgumentException("Only a BOLT 11 invoice can be a hold invoice (no preimage).",
                                        nameof(preimage));
        if (bolt12 is null && keysend is null)
            ArgumentException.ThrowIfNullOrWhiteSpace(bolt11);
        else if (bolt11 is not null)
            throw new ArgumentException("A BOLT 12 invoice or a keysend record has no BOLT 11 string.",
                                        nameof(bolt11));
        if (amount is { IsZero: true })
            throw new ArgumentOutOfRangeException(nameof(amount), "An invoice amount must be positive; use null for "
                                                                + "any amount.");
        if (expirySeconds == 0)
            throw new ArgumentOutOfRangeException(nameof(expirySeconds), "The expiry must be positive.");
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown invoice status.");
        if (status is InvoiceStatus.Accepted or InvoiceStatus.Settled or InvoiceStatus.Held
             && amountReceived is null)
            throw new ArgumentException("An accepted, settled or held invoice needs the amount received.",
                                        nameof(amountReceived));
        if (preimage is null && status is InvoiceStatus.Accepted)
            throw new ArgumentException("An accepted invoice needs its preimage.", nameof(preimage));
        if (status == InvoiceStatus.Settled && settledAt is null)
            throw new ArgumentException("A settled invoice needs its settlement time.", nameof(settledAt));

        PaymentHash = paymentHash;
        Preimage = preimage;
        PaymentSecret = paymentSecret;
        Amount = amount;
        Description = description;
        Bolt11 = bolt11;
        CreatedAt = createdAt;
        ExpirySeconds = expirySeconds;
        MinFinalCltvExpiry = minFinalCltvExpiry;
        Status = status;
        AmountReceived = amountReceived;
        SettledAt = settledAt;
        Bolt12 = bolt12;
        Keysend = keysend;
    }

    /// <summary>
    /// True when the invoice is still <see cref="InvoiceStatus.Open"/> and <paramref name="now"/> is at or past
    /// <see cref="ExpiresAt"/>.
    /// </summary>
    public bool IsExpired(DateTimeOffset now) => Status == InvoiceStatus.Open && now >= ExpiresAt;

    /// <summary>
    /// An HTLC paying <paramref name="amountReceived"/> is locked in and we are about to fulfill it.
    /// </summary>
    public void Accept(LightningMoney amountReceived)
    {
        ArgumentNullException.ThrowIfNull(amountReceived);
        if (Status != InvoiceStatus.Open)
            throw new InvalidOperationException($"Cannot accept an invoice that is {Status}.");

        AmountReceived = amountReceived;
        Status = InvoiceStatus.Accepted;
    }

    /// <summary>
    /// The fulfill is irrevocably committed.
    /// </summary>
    public void Settle(DateTimeOffset settledAt)
    {
        if (Status != InvoiceStatus.Accepted)
            throw new InvalidOperationException($"Cannot settle an invoice that is {Status}.");

        SettledAt = settledAt;
        Status = InvoiceStatus.Settled;
    }

    /// <summary>
    /// Cancels an open invoice.
    /// </summary>
    public void Cancel()
    {
        if (Status is not (InvoiceStatus.Open or InvoiceStatus.Held))
            throw new InvalidOperationException($"Cannot cancel an invoice that is {Status}.");

        Status = InvoiceStatus.Canceled;
    }

    /// <summary>
    /// A hold invoice's paying HTLC set completed (NL-995): <c>Open -> Held</c>, the amount received recorded. The
    /// parts stay locked in, nothing fulfilled or failed, until <see cref="SettleHeld"/> or <see cref="Cancel"/>.
    /// </summary>
    public void Hold(LightningMoney amountReceived)
    {
        ArgumentNullException.ThrowIfNull(amountReceived);
        if (Status != InvoiceStatus.Open)
            throw new InvalidOperationException($"Cannot hold an invoice that is {Status}.");
        if (Preimage is not null)
            throw new InvalidOperationException("Only a hold invoice (no preimage) can be held.");

        Status = InvoiceStatus.Held;
        AmountReceived = amountReceived;
    }

    /// <summary>
    /// The operator's settle of a held invoice (NL-995): the outside preimage (verified against the payment hash by
    /// the caller) is stored and the invoice settles, <c>Held -> Settled</c>.
    /// </summary>
    public void SettleHeld(Secret preimage, DateTimeOffset settledAt)
    {
        if (Status != InvoiceStatus.Held)
            throw new InvalidOperationException($"Cannot settle a held invoice that is {Status}.");

        Preimage = preimage;
        Status = InvoiceStatus.Settled;
        SettledAt = settledAt;
    }
}