using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

using Crypto.Enums;

/// <summary>
/// A party of a BIP 327 (MuSig2) session sent an invalid value (the reference implementation's
/// <c>InvalidContributionError</c>): the session cannot go on, and the party named by <see cref="Signer"/> is to blame.
/// </summary>
[ExcludeFromCodeCoverage]
public class MusigInvalidContributionException : MusigException
{
    /// <summary>
    /// The index of the offending signer in the list the operation took, or null when the value has no single author
    /// (an aggregate nonce).
    /// </summary>
    public int? Signer { get; }

    /// <summary>
    /// What was invalid.
    /// </summary>
    public MusigContribution Contribution { get; }

    public MusigInvalidContributionException(int? signer, MusigContribution contribution)
        : base(signer is null
                   ? $"Invalid MuSig2 contribution: {contribution}."
                   : $"Invalid MuSig2 contribution from signer {signer}: {contribution}.")
    {
        Signer = signer;
        Contribution = contribution;
    }
}