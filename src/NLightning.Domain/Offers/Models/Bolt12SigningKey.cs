namespace NLightning.Domain.Offers.Models;

using Crypto.ValueObjects;
using Enums;

/// <summary>
/// Which of the node's keys signs a BOLT 12 message (<see cref="Bitcoin.Interfaces.ILightningSigner.SignBolt12"/>). It
/// names the key; the secret itself never leaves the signer.
/// </summary>
public sealed record Bolt12SigningKey
{
    private Bolt12SigningKey(Bolt12SigningKeyKind kind, ReadOnlyMemory<byte> invoiceRequestMetadata,
                             CompactPubKey? pathKey)
    {
        Kind = kind;
        InvoiceRequestMetadata = invoiceRequestMetadata;
        PathKey = pathKey;
    }

    /// <summary>
    /// The key kind.
    /// </summary>
    public Bolt12SigningKeyKind Kind { get; }

    /// <summary>
    /// The <c>invreq_metadata</c> the payer key is derived from (<see cref="Bolt12SigningKeyKind.Payer"/> only).
    /// </summary>
    public ReadOnlyMemory<byte> InvoiceRequestMetadata { get; }

    /// <summary>
    /// The path_key of our hop in one of our blinded paths (<see cref="Bolt12SigningKeyKind.BlindedRecipient"/> only).
    /// </summary>
    public CompactPubKey? PathKey { get; }

    /// <summary>
    /// The node key (the key of <c>offer_issuer_id</c> for our offers).
    /// </summary>
    public static Bolt12SigningKey Node { get; } = new(Bolt12SigningKeyKind.Node, ReadOnlyMemory<byte>.Empty, null);

    /// <summary>
    /// The transient payer key of an invoice_request with <paramref name="invoiceRequestMetadata"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The metadata is empty (BOLT 12: <c>invreq_metadata</c> is mandatory).</exception>
    public static Bolt12SigningKey Payer(ReadOnlyMemory<byte> invoiceRequestMetadata)
    {
        if (invoiceRequestMetadata.IsEmpty)
            throw new ArgumentException("invreq_metadata must not be empty.", nameof(invoiceRequestMetadata));

        // A copy, so the caller cannot change the key after the fact
        return new Bolt12SigningKey(Bolt12SigningKeyKind.Payer, invoiceRequestMetadata.ToArray(), null);
    }

    /// <summary>
    /// The blinded key of our hop in one of our blinded paths, for <paramref name="pathKey"/>.
    /// </summary>
    public static Bolt12SigningKey BlindedRecipient(CompactPubKey pathKey) =>
        new(Bolt12SigningKeyKind.BlindedRecipient, ReadOnlyMemory<byte>.Empty, pathKey);
}