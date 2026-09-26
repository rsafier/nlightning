using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// A fresh P2WPKH address of our wallet (scoped: the wallet service uses the scope's unit of work). The address is
/// handed to the blockchain monitor, so the swept output is credited to the wallet balance.
/// </summary>
public sealed class WalletRemoteSweepDestination : IRemoteSweepDestination
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly Network _network;
    private readonly IBitcoinWalletService _walletService;

    public WalletRemoteSweepDestination(IOptions<NodeOptions> nodeOptions, IBitcoinWalletService walletService,
                                        IBlockchainMonitor? blockchainMonitor = null)
    {
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
        _walletService = walletService;
        _blockchainMonitor = blockchainMonitor;
    }

    /// <inheritdoc />
    public async Task<byte[]> GetScriptAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = await _walletService.GetUnusedAddressAsync(AddressType.P2Wpkh, false);
        _blockchainMonitor?.WatchBitcoinAddress(address);
        return BitcoinAddress.Create(address.Address, _network).ScriptPubKey.ToBytes();
    }
}