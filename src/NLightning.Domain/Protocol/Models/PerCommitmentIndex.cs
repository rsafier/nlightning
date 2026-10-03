namespace NLightning.Domain.Protocol.Models;

using Crypto.Constants;

/// <summary>
/// Converts between commitment numbers and BOLT 3 per-commitment secret indices.
/// </summary>
/// <remarks>
/// Commitment numbers count up from 0 (the commitment built from funding_created/funding_signed). Per-commitment
/// secrets are generated from indices that count down from 2^48-1 (BOLT 3 "Per-commitment Secret Requirements"), so
/// commitment <c>n</c> uses index <c>2^48 - 1 - n</c>. Every signer API takes commitment numbers; only the code that
/// calls <c>IKeyDerivationService.GeneratePerCommitmentSecret</c> converts them with <see cref="From"/>.
/// </remarks>
public static class PerCommitmentIndex
{
    /// <summary>
    /// The largest commitment number (and index): commitment numbers are 48-bit values.
    /// </summary>
    public const ulong MaxCommitmentNumber = CryptoConstants.FirstPerCommitmentIndex;

    /// <summary>
    /// Returns the per-commitment secret index for a commitment number: <c>2^48 - 1 - n</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The number does not fit in 48 bits.</exception>
    public static ulong From(ulong commitmentNumber)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(commitmentNumber, MaxCommitmentNumber);

        return CryptoConstants.FirstPerCommitmentIndex - commitmentNumber;
    }

    /// <summary>
    /// Returns the commitment number of a per-commitment secret index: <c>2^48 - 1 - index</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index does not fit in 48 bits.</exception>
    public static ulong ToCommitmentNumber(ulong index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, CryptoConstants.FirstPerCommitmentIndex);

        return CryptoConstants.FirstPerCommitmentIndex - index;
    }
}