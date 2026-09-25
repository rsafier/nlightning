namespace NLightning.Domain.Payments.Models;

using Crypto.ValueObjects;
using Enums;
using Money;

/// <summary>
/// An invoice we issued (BOLT 11), with the secrets the final hop needs to accept a payment for it.
/// </summary>
/// <remarks>
/// <para>The preimage and payment secret come from a CSPRNG and the model is persisted
/// (<c>IInvoiceDbRepository</c>) <b>before</b> the BOLT 11 string is handed to anyone, so a payment can never arrive
/// for an invoice we forgot.</para>
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
    public Secret Preimage { get; }

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
    /// The encoded, signed BOLT 11 string.
    /// </summary>
    public string Bolt11 { get; }

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

    public InvoiceModel(Hash paymentHash, Secret preimage, Secret paymentSecret, LightningMoney? amount,
                        string? description, string bolt11, DateTimeOffset createdAt, uint expirySeconds,
                        ushort minFinalCltvExpiry, InvoiceStatus status = InvoiceStatus.Open,
                        LightningMoney? amountReceived = null, DateTimeOffset? settledAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bolt11);
        if (amount is { IsZero: true })
            throw new ArgumentOutOfRangeException(nameof(amount), "An invoice amount must be positive; use null for "
                                                                + "any amount.");
        if (expirySeconds == 0)
            throw new ArgumentOutOfRangeException(nameof(expirySeconds), "The expiry must be positive.");
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown invoice status.");
        if (status is InvoiceStatus.Accepted or InvoiceStatus.Settled && amountReceived is null)
            throw new ArgumentException("An accepted or settled invoice needs the amount received.",
                                        nameof(amountReceived));
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
        if (Status != InvoiceStatus.Open)
            throw new InvalidOperationException($"Cannot cancel an invoice that is {Status}.");

        Status = InvoiceStatus.Canceled;
    }
}