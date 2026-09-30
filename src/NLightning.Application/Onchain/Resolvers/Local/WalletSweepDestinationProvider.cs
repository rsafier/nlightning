using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain.Resolvers.Local;

using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Networks;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Sweeps pay to an unused P2WPKH address of our wallet (<see cref="IBitcoinWalletService"/> is scoped, so each call
/// opens a scope), which is handed to the chain monitor again so the swept output is credited as a deposit (as
/// <c>ShutdownScriptProvider</c> does for a closing output).
/// </summary>
/// <remarks>
/// The wallet reserves every address it hands out (NL-280). Without a channel, each call reserves one. For a channel
/// (NL-463, like the penalties) the destination is reserved once and cached for the process, so the channel's sweeps,
/// anchor sweep and CPFP change reuse it across resolution rounds and RBF rebuilds instead of growing the wallet.
/// </remarks>
public sealed class WalletSweepDestinationProvider : ISweepDestinationProvider
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly Network _network;
    private readonly SemaphoreSlim _destinationLock = new(1, 1);
    private readonly ConcurrentDictionary<ChannelId, byte[]> _destinations = [];
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

    /// <inheritdoc />
    public async Task<byte[]> GetDestinationScriptAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        if (_destinations.TryGetValue(channelId, out var cached))
            return cached;

        await _destinationLock.WaitAsync(cancellationToken);
        try
        {
            if (_destinations.TryGetValue(channelId, out cached))
                return cached;

            var script = await GetDestinationScriptAsync(cancellationToken);
            _destinations[channelId] = script;
            return script;
        }
        finally
        {
            _destinationLock.Release();
        }
    }
}