namespace NLightning.Application.Channels.Splicing.Interfaces;

using Domain.Bitcoin.ValueObjects;

/// <summary>
/// Where a splice-out we initiate pays (splicing plan §3.5 step 1): the operator's address, or a new address of our
/// wallet.
/// </summary>
public interface ISpliceOutDestination
{
    /// <summary>The scriptPubKey of <paramref name="address"/>, or of a new P2WPKH wallet address when it is null.</summary>
    /// <exception cref="ArgumentException">The address is not valid on our network.</exception>
    Task<BitcoinScript> ResolveAsync(string? address, CancellationToken cancellationToken = default);
}