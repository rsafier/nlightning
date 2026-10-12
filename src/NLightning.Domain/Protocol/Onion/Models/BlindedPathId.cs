using System.Security.Cryptography;

namespace NLightning.Domain.Protocol.Onion.Models;

using Crypto.ValueObjects;

/// <summary>
/// The <c>path_id</c> we put in our own hop of a blinded path to one of our invoices (BOLT 4 "Route Blinding": the
/// recipient MAY store private data in <c>encrypted_data_tlv[r].path_id</c> to check that the route is used in the
/// right context and was created by it, and MUST ignore a payment whose <c>path_id</c> does not match).
/// </summary>
/// <remarks>
/// <c>path_id = HMAC-SHA256(key = payment_preimage, "nltg_blinded_path_id")</c>: it depends only on the invoice's
/// preimage, which nobody else knows until the payment settles, so a sender cannot make a path that passes the check
/// for an invoice of ours, and nothing has to be stored per path.
/// </remarks>
public static class BlindedPathId
{
    /// <summary>The <c>path_id</c> length.</summary>
    public const int Length = 32;

    private static ReadOnlySpan<byte> Label => "nltg_blinded_path_id"u8;

    /// <summary>
    /// The <c>path_id</c> of the invoice whose preimage is <paramref name="paymentPreimage"/>.
    /// </summary>
    public static byte[] Compute(Secret paymentPreimage) => HMACSHA256.HashData(paymentPreimage, Label);

    /// <summary>
    /// Whether <paramref name="pathId"/> is the <c>path_id</c> of the invoice with <paramref name="paymentPreimage"/>
    /// (constant time).
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> pathId, Secret paymentPreimage) =>
        pathId.Length == Length && CryptographicOperations.FixedTimeEquals(pathId, Compute(paymentPreimage));
}