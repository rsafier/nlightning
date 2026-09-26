using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Constants;

/// <summary>
/// Constants for the different networks.
/// </summary>
/// <remarks>
/// The network constants are used to identify the network name.
/// </remarks>
[ExcludeFromCodeCoverage]
public static class NetworkConstants
{
    public const string Mainnet = "mainnet";
    public const string Testnet = "testnet";
    public const string Regtest = "regtest";
    public const string Signet = "signet";

    /// <summary>
    /// Mutinynet, a custom signet (30 s blocks) with the signet genesis block. It is registered as a custom signet by
    /// default, so it resolves to <see cref="Signet"/> (see <c>BitcoinNetwork.Resolve</c>).
    /// </summary>
    public const string Mutinynet = "mutinynet";
}