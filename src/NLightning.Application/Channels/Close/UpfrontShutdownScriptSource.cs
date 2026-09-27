using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Channels.Close;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Enums;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Networks;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Our <c>upfront_shutdown_script</c> (NL-045, BOLT2 plan N10-T1): when both nodes advertised
/// <c>option_upfront_shutdown_script</c>, a fresh P2WPKH wallet address, reserved in the wallet
/// (<see cref="IBitcoinWalletService.ReserveUnusedAddressAsync"/>) so it is never handed out again, is set on the channel
/// before <c>open_channel</c>/<c>accept_channel</c> is sent. It is persisted with the channel's config and our
/// <c>shutdown</c> must pay to it (<see cref="ShutdownScriptProvider"/>, B2-SHUT-S09). Without the feature on both sides
/// nothing is set: BOLT 2 then lets us send a zero-length script (or none).
/// </summary>
/// <remarks>
/// Singleton: each reservation runs in its own scope, so the wallet's save never commits the caller's unit of work.
/// An address reserved for an open that fails afterwards stays reserved (it is a wallet address, so funds sent to it
/// are still found).
/// </remarks>
public class UpfrontShutdownScriptSource
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly ILogger<UpfrontShutdownScriptSource>? _logger;
    private readonly Network _network;
    private readonly IServiceScopeFactory _scopeFactory;

    public UpfrontShutdownScriptSource(IOptions<NodeOptions> nodeOptions, IServiceScopeFactory scopeFactory,
                                       IBlockchainMonitor? blockchainMonitor = null,
                                       ILogger<UpfrontShutdownScriptSource>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(nodeOptions);
        // Throws on an unknown network, like the wallet: never derive scripts for a network we are not on
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
        _scopeFactory = scopeFactory;
        _blockchainMonitor = blockchainMonitor;
        _logger = logger;
    }

    /// <summary>True when both nodes advertised <c>option_upfront_shutdown_script</c>.</summary>
    public static bool IsNegotiated(FeatureOptions negotiatedFeatures)
    {
        ArgumentNullException.ThrowIfNull(negotiatedFeatures);
        return negotiatedFeatures.UpfrontShutdownScript > FeatureSupport.No;
    }

    /// <summary>
    /// Sets a reserved wallet script as <paramref name="channel"/>'s upfront shutdown script when the feature is
    /// negotiated and the channel has none yet; returns the script announced (null: a zero-length one).
    /// </summary>
    public async Task<BitcoinScript?> AssignIfNegotiatedAsync(ChannelModel channel, FeatureOptions negotiatedFeatures)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsNegotiated(negotiatedFeatures))
            return channel.LocalUpfrontShutdownScript;
        if (channel.LocalUpfrontShutdownScript is { } existing)
            return existing;

        var script = await ReserveAsync();
        channel.SetLocalUpfrontShutdownScript(script);
        _logger?.LogInformation("Announcing upfront shutdown script {Script} for channel {ChannelId}", script,
                                channel.ChannelId);
        return script;
    }

    /// <summary>Reserves a fresh P2WPKH wallet address and returns its script.</summary>
    public virtual async Task<BitcoinScript> ReserveAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var walletService = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
        var address = await walletService.ReserveUnusedAddressAsync(AddressType.P2Wpkh, false);

        // Idempotent: the monitor keeps watching every wallet address, so the closing output is found as a deposit
        _blockchainMonitor?.WatchBitcoinAddress(address);
        return BitcoinAddress.Create(address.Address, _network).ScriptPubKey.ToBytes();
    }
}