using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Networks;

using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Maps our <see cref="BitcoinNetwork"/> to NBitcoin's <see cref="Network"/>. Use it instead of
/// <c>Network.GetNetwork(name) ?? Network.Main</c>: an unknown network must fail, never fall back to mainnet.
/// </summary>
/// <remarks>
/// A custom signet (Mutinynet or any registered with <see cref="BitcoinNetwork.RegisterCustomSignet"/>) maps to
/// NBitcoin's signet: the same genesis block, <c>tb</c> addresses and testnet key versions. NBitcoin's signet carries
/// the default block challenge, which only matters for validating blocks; we let bitcoind do that.
/// </remarks>
public static class NBitcoinNetworkResolver
{
    /// <summary>
    /// The NBitcoin network for <paramref name="network"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The network is unknown or has no NBitcoin equivalent.</exception>
    public static Network ToNBitcoinNetwork(this BitcoinNetwork network)
    {
        return network.Name switch
        {
            NetworkConstants.Mainnet => Network.Main,
            NetworkConstants.Testnet => Network.TestNet,
            NetworkConstants.Regtest => Network.RegTest,
            NetworkConstants.Signet => NBitcoin.Bitcoin.Instance.Signet,
            _ when network.IsSignet => NBitcoin.Bitcoin.Instance.Signet,
            _ => throw new ArgumentException($"No NBitcoin network for Bitcoin network '{network.Name}'.",
                                             nameof(network))
        };
    }

    /// <summary>
    /// Resolves a configured network name (see <see cref="BitcoinNetwork.Resolve"/>) to its NBitcoin network.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or unknown.</exception>
    public static Network Resolve(string? networkName)
    {
        return BitcoinNetwork.Resolve(networkName).ToNBitcoinNetwork();
    }
}