using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// An unused P2WPKH address of our wallet, handed to the blockchain monitor so the swept output is credited to the
/// wallet balance. The scoped <see cref="IBitcoinWalletService"/> comes from a scope of its own on every call: when no
/// unused address is left it derives new ones and saves them, and that save must never commit what a resolution round
/// has staged (the executor saves the round once).
/// </summary>
/// <remarks>
/// The wallet hands out its first address without a UTXO (NL-280), so sweeps decided before the first of them
/// confirms share an address; that links them on chain but loses nothing.
/// </remarks>
public sealed class WalletRemoteSweepDestination : IRemoteSweepDestination
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly Network _network;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public WalletRemoteSweepDestination(IOptions<NodeOptions> nodeOptions, IServiceScopeFactory serviceScopeFactory,
                                        IBlockchainMonitor? blockchainMonitor = null)
    {
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
        _serviceScopeFactory = serviceScopeFactory;
        _blockchainMonitor = blockchainMonitor;
    }

    /// <inheritdoc />
    public async Task<byte[]> GetScriptAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = _serviceScopeFactory.CreateScope();
        var walletService = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
        var address = await walletService.GetUnusedAddressAsync(AddressType.P2Wpkh, false);
        _blockchainMonitor?.WatchBitcoinAddress(address);
        return BitcoinAddress.Create(address.Address, _network).ScriptPubKey.ToBytes();
    }
}