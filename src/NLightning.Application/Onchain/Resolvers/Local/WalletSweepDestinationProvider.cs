using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain.Resolvers.Local;

using Domain.Bitcoin.Enums;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Networks;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Sweeps pay to an unused P2WPKH address of our wallet (<see cref="IBitcoinWalletService"/> is scoped, so each call
/// opens a scope), which is handed to the chain monitor again so the swept output is credited as a deposit (as
/// <c>ShutdownScriptProvider</c> does for a closing output).
/// </summary>
/// <remarks>
/// The wallet reserves every address it hands out (NL-280), so every sweep pays to an address of its own.
/// </remarks>
public sealed class WalletSweepDestinationProvider : ISweepDestinationProvider
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly Network _network;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public WalletSweepDestinationProvider(IOptions<NodeOptions> nodeOptions, IServiceScopeFactory serviceScopeFactory,
                                          IBlockchainMonitor? blockchainMonitor = null)
    {
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
        _serviceScopeFactory = serviceScopeFactory;
        _blockchainMonitor = blockchainMonitor;
    }

    /// <inheritdoc />
    public async Task<byte[]> GetDestinationScriptAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var wallet = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
        var address = await wallet.GetUnusedAddressAsync(AddressType.P2Wpkh, false);
        cancellationToken.ThrowIfCancellationRequested();

        _blockchainMonitor?.WatchBitcoinAddress(address);
        return BitcoinAddress.Create(address.Address, _network).ScriptPubKey.ToBytes();
    }
}