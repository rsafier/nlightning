namespace NLightning.Domain.Protocol.Models;

using Bitcoin.ValueObjects;
using Crypto.Hashes;
using Crypto.ValueObjects;

/// <summary>
/// Manages Lightning Network commitment numbers and their obscuring as defined in BOLT3.
/// </summary>
public class CommitmentNumber
{
    /// <summary>
    /// Gets the commitment number value.
    /// </summary>
    public ulong Value { get; }

    /// <summary>
    /// Gets the obscuring factor derived from payment basepoints.
    /// </summary>
    public ulong ObscuringFactor { get; }

    /// <summary>
    /// Gets the obscured commitment number (value XOR obscuring factor).
    /// </summary>
    public ulong ObscuredValue => Value ^ ObscuringFactor;

    /// <summary>
    /// Represents a commitment number in the Lightning Network.
    /// </summary>
    /// <param name="openerPaymentBasepoint">The payment basepoint of the channel opener (funder).</param>
    /// <param name="accepterPaymentBasepoint">The payment basepoint of the channel accepter (fundee).</param>
    /// <param name="sha256">The SHA256 hash function instance.</param>
    /// <param name="initialValue">The commitment number value.</param>
    /// <remarks>
    /// BOLT 3 obscures the commitment number with SHA256(opener payment_basepoint || accepter payment_basepoint),
    /// so the order depends on who opened the channel, not on which side is local.
    /// </remarks>
    public CommitmentNumber(CompactPubKey openerPaymentBasepoint, CompactPubKey accepterPaymentBasepoint,
                            ISha256 sha256, ulong initialValue = 0)
    {
        Value = initialValue;
        ObscuringFactor = CalculateObscuringFactor(openerPaymentBasepoint, accepterPaymentBasepoint, sha256);
    }

    private CommitmentNumber(ulong value, ulong obscuringFactor)
    {
        Value = value;
        ObscuringFactor = obscuringFactor;
    }

    /// <summary>
    /// Returns the next commitment number. This instance is not changed.
    /// </summary>
    /// <returns>A new instance with the value incremented by one and the same obscuring factor.</returns>
    public CommitmentNumber Increment()
    {
        return new CommitmentNumber(Value + 1, ObscuringFactor);
    }

    /// <summary>
    /// Calculates the transaction locktime value using the obscured commitment number.
    /// </summary>
    /// <returns>The transaction locktime.</returns>
    public BitcoinLockTime CalculateLockTime()
    {
        return new BitcoinLockTime((0x20 << 24) | (uint)(ObscuredValue & 0xFFFFFF));
    }

    /// <summary>
    /// Calculates the transaction sequence value using the obscured commitment number.
    /// </summary>
    /// <returns>The transaction sequence.</returns>
    public BitcoinSequence CalculateSequence()
    {
        return new BitcoinSequence((uint)((0x80UL << 24) | ((ObscuredValue >> 24) & 0xFFFFFF)));
    }

    /// <summary>
    /// Calculates the 48-bit obscuring factor by hashing the concatenation of payment basepoints.
    /// </summary>
    /// <param name="openerBasepoint">The opener's payment basepoint.</param>
    /// <param name="accepterBasepoint">The accepter's payment basepoint.</param>
    /// <param name="sha256">The SHA256 hash function instance.</param>
    /// <returns>The 48-bit obscuring factor as ulong.</returns>
    private static ulong CalculateObscuringFactor(CompactPubKey openerBasepoint, CompactPubKey accepterBasepoint,
                                                  ISha256 sha256)
    {
        // Hash the concatenation of payment basepoints
        sha256.AppendData(openerBasepoint);
        sha256.AppendData(accepterBasepoint);

        Span<byte> hashResult = stackalloc byte[32];
        sha256.GetHashAndReset(hashResult);

        // Extract the lower 48 bits (6 bytes) of the hash
        ulong obscuringFactor = 0;
        for (var i = 26; i < 32; i++) // Last 6 bytes of the 32-byte hash
            obscuringFactor = (obscuringFactor << 8) | hashResult[i];

        return obscuringFactor;
    }
}