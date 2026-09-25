namespace NLightning.Domain.Protocol.Models;

using Bitcoin.ValueObjects;
using Crypto.Hashes;
using Crypto.ValueObjects;

/// <summary>
/// Obscures Lightning Network commitment numbers as defined in BOLT 3.
/// </summary>
/// <remarks>
/// This is an immutable per-channel helper: it only holds the obscuring factor. The commitment numbers themselves live
/// on the channel (<c>ChannelModel.LocalCommitmentNumber</c> and <c>RemoteCommitmentNumber</c>), because the local and
/// the remote commitments advance independently. Both commitments of a channel use the same obscuring factor.
/// </remarks>
public class CommitmentNumber
{
    /// <summary>
    /// Commitment numbers are 48-bit values.
    /// </summary>
    public const ulong MaxValue = PerCommitmentIndex.MaxCommitmentNumber;

    /// <summary>
    /// Gets the obscuring factor derived from payment basepoints.
    /// </summary>
    public ulong ObscuringFactor { get; }

    /// <summary>
    /// Creates the obscuring helper of a channel.
    /// </summary>
    /// <param name="openerPaymentBasepoint">The payment basepoint of the channel opener (funder).</param>
    /// <param name="accepterPaymentBasepoint">The payment basepoint of the channel accepter (fundee).</param>
    /// <param name="sha256">The SHA256 hash function instance.</param>
    /// <remarks>
    /// BOLT 3 obscures the commitment number with SHA256(opener payment_basepoint || accepter payment_basepoint),
    /// so the order depends on who opened the channel, not on which side is local.
    /// </remarks>
    public CommitmentNumber(CompactPubKey openerPaymentBasepoint, CompactPubKey accepterPaymentBasepoint,
                            ISha256 sha256)
    {
        ObscuringFactor = CalculateObscuringFactor(openerPaymentBasepoint, accepterPaymentBasepoint, sha256);
    }

    /// <summary>
    /// Returns the obscured commitment number (number XOR obscuring factor).
    /// </summary>
    /// <param name="commitmentNumber">The 48-bit commitment number.</param>
    /// <exception cref="ArgumentOutOfRangeException">The number does not fit in 48 bits.</exception>
    public ulong Obscure(ulong commitmentNumber)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(commitmentNumber, MaxValue);

        return commitmentNumber ^ ObscuringFactor;
    }

    /// <summary>
    /// Calculates the commitment transaction locktime: upper 8 bits 0x20, lower 24 bits the lower 24 bits of the
    /// obscured commitment number.
    /// </summary>
    /// <param name="commitmentNumber">The 48-bit commitment number.</param>
    public BitcoinLockTime LockTime(ulong commitmentNumber)
    {
        return new BitcoinLockTime((0x20 << 24) | (uint)(Obscure(commitmentNumber) & 0xFFFFFF));
    }

    /// <summary>
    /// Calculates the commitment transaction input sequence: upper 8 bits 0x80, lower 24 bits the upper 24 bits of
    /// the obscured commitment number.
    /// </summary>
    /// <param name="commitmentNumber">The 48-bit commitment number.</param>
    public BitcoinSequence Sequence(ulong commitmentNumber)
    {
        return new BitcoinSequence((uint)((0x80UL << 24) | ((Obscure(commitmentNumber) >> 24) & 0xFFFFFF)));
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