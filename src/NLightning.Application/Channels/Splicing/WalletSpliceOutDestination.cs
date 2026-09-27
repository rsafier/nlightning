using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Channels.Splicing;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// The default <see cref="ISpliceOutDestination"/>: the operator's address parsed on our network, or a new P2WPKH
/// address of our wallet (reserved by <see cref="IBitcoinWalletService.GetUnusedAddressAsync"/> in a scope of its own,
/// NL-280), which the chain monitor then watches so the spliced-out amount shows up as a deposit.
/// </summary>
public sealed class WalletSpliceOutDestination : ISpliceOutDestination
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly Network _network;
    private readonly IServiceScopeFactory? _scopeFactory;

    public WalletSpliceOutDestination(IOptions<NodeOptions> nodeOptions, IServiceScopeFactory? scopeFactory = null,
                                      IBlockchainMonitor? blockchainMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(nodeOptions);
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork)
                ?? throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
        _scopeFactory = scopeFactory;
        _blockchainMonitor = blockchainMonitor;
    }

    /// <inheritdoc />
    public async Task<BitcoinScript> ResolveAsync(string? address, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            try
            {
                return BitcoinAddress.Create(address, _network).ScriptPubKey.ToBytes();
            }
            catch (FormatException e)
            {
                throw new ArgumentException($"{address} is not a valid address on {_network.Name}", nameof(address), e);
            }
        }

        // The wallet service is scoped: a scope of its own (it saves the reserved address itself, NL-280)
        using var scope = _scopeFactory?.CreateScope();
        var walletService = scope?.ServiceProvider.GetService<IBitcoinWalletService>()
                         ?? throw new InvalidOperationException("No wallet to splice out to; give an address");
        var walletAddress = await walletService.GetUnusedAddressAsync(AddressType.P2Wpkh, false);
        _blockchainMonitor?.WatchBitcoinAddress(walletAddress);
        return BitcoinAddress.Create(walletAddress.Address, _network).ScriptPubKey.ToBytes();
    }
}