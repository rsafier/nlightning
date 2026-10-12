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
    /// Testnet4 (BIP 94), a built-in network since NL-012: its own genesis block and chain hash, <c>tb</c> addresses
    /// and <c>lntb</c> invoices like testnet3, bitcoind's RPC port 48332.
    /// </summary>
    public const string Testnet4 = "testnet4";

    /// <summary>
    /// Mutinynet, a custom signet (30 s blocks) with the signet genesis block. It is registered as a custom signet by
    /// default, so it resolves to <see cref="Signet"/> (see <c>BitcoinNetwork.Resolve</c>).
    /// </summary>
    public const string Mutinynet = "mutinynet";
}