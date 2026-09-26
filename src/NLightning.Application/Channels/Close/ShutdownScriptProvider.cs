using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Channels.Close;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Closing;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The script our <c>shutdown</c> pays to (B2-SHUT-S09, B2-SHUT-S10): the upfront shutdown script we sent in
/// <c>open_channel</c>/<c>accept_channel</c> when there is one (BOLT 2 MUST), else a fresh P2WPKH address of our
/// wallet, so the closing output is found as a deposit by the blockchain monitor.
/// </summary>
/// <remarks>
/// Scoped: <see cref="IBitcoinWalletService"/> uses the scope's unit of work. The address is handed to the blockchain
/// monitor again (idempotent; the monitor keeps watching every wallet address after a deposit), so the closing output
/// is credited to the wallet. The wallet returns its first address without a UTXO (NL-280), so two channels that close
/// at the same time would get the same shutdown address, which links them on chain: an address that is the shutdown
/// script of another channel whose close is not confirmed yet is skipped for the first unused change address (also a
/// wallet address, so the output is credited the same way). A third concurrent close can still collide (logged) until
/// the wallet hands out each address once (NL-280).
/// </remarks>
public class ShutdownScriptProvider
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly ILogger<ShutdownScriptProvider>? _logger;
    private readonly Network _network;
    private readonly IBitcoinWalletService _walletService;

    public ShutdownScriptProvider(IOptions<NodeOptions> nodeOptions, IBitcoinWalletService walletService,
                                  IBlockchainMonitor? blockchainMonitor = null,
                                  IChannelMemoryRepository? channelMemoryRepository = null,
                                  ILogger<ShutdownScriptProvider>? logger = null)
    {
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
        _walletService = walletService;
        _blockchainMonitor = blockchainMonitor;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
    }

    /// <summary>Our <c>shutdown</c> script for <paramref name="channel"/>.</summary>
    /// <exception cref="InvalidOperationException">The upfront script is not a form BOLT 2 allows in
    /// <c>shutdown</c>.</exception>
    public virtual async Task<BitcoinScript> GetLocalScriptAsync(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.LocalUpfrontShutdownScript is { Length: > 0 } upfront)
        {
            if (!ShutdownScriptValidator.IsValid((byte[])upfront, true, false))
                throw new InvalidOperationException("Our upfront shutdown script is not a valid shutdown script");

            return upfront;
        }

        var address = await _walletService.GetUnusedAddressAsync(AddressType.P2Wpkh, false);
        var script = ToScript(address);
        if (IsShutdownScriptOfAnotherOpenClose(channel, script))
        {
            var changeAddress = await _walletService.GetUnusedAddressAsync(AddressType.P2Wpkh, true);
            var changeScript = ToScript(changeAddress);
            if (IsShutdownScriptOfAnotherOpenClose(channel, changeScript))
            {
                _logger?.LogWarning("Shutdown address {Address} of channel {ChannelId} is also the shutdown address of "
                                  + "another channel being closed; the two closes are linked on chain (NL-280)",
                                    address.Address, channel.ChannelId);
            }
            else
            {
                address = changeAddress;
                script = changeScript;
            }
        }

        _blockchainMonitor?.WatchBitcoinAddress(address);
        return script;
    }

    private BitcoinScript ToScript(WalletAddressModel address) =>
        BitcoinAddress.Create(address.Address, _network).ScriptPubKey.ToBytes();

    /// <summary>
    /// True when another loaded channel whose close is not confirmed yet (ShuttingDown, Negotiating, Closing) already
    /// pays its closing output to <paramref name="script"/>.
    /// </summary>
    private bool IsShutdownScriptOfAnotherOpenClose(ChannelModel channel, BitcoinScript script) =>
        _channelMemoryRepository is not null
     && _channelMemoryRepository.FindChannels(c => c.ChannelId != channel.ChannelId
                                                && c.State is ChannelState.ShuttingDown or ChannelState.Negotiating
                                                                 or ChannelState.Closing
                                                && c.LocalShutdownScript is { } other && other == script)
                                .Count > 0;
}