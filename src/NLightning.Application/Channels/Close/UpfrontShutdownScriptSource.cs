using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Channels.Close;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
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
/// A peer's <c>open_channel</c> must not cost a wallet address for good (a peer that opens and walks away would grow the
/// wallet without bound): the script of a fundee open is remembered with its temporary channel and handed to a later
/// fundee open once that temporary channel is gone (expired, dropped with the connection, refused) without having
/// reached <c>funding_created</c> (its model still <see cref="ChannelState.V1Opening"/>), so nothing was persisted
/// with it. A channel that got past the open keeps its script (it is persisted with the channel). Our own opens (the
/// IPC funder path) reuse an abandoned reservation the same way (NL-463). The reuse needs the
/// <see cref="IChannelMemoryRepository"/> and lasts for the process; the caller must have added the temporary channel
/// before the script is assigned, and a reservation is not carried across a restart (that needs the reservation
/// persisted against the channel, a schema change).
/// </remarks>
public class UpfrontShutdownScriptSource
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly ILogger<UpfrontShutdownScriptSource>? _logger;
    private readonly Network _network;
    private readonly List<OpenReservation> _fundeeReservations = [];
    private readonly Lock _fundeeReservationsLock = new();
    private readonly IServiceScopeFactory _scopeFactory;

    public UpfrontShutdownScriptSource(IOptions<NodeOptions> nodeOptions, IServiceScopeFactory scopeFactory,
                                       IBlockchainMonitor? blockchainMonitor = null,
                                       ILogger<UpfrontShutdownScriptSource>? logger = null,
                                       IChannelMemoryRepository? channelMemoryRepository = null)
    {
        ArgumentNullException.ThrowIfNull(nodeOptions);
        // Throws on an unknown network, like the wallet: never derive scripts for a network we are not on
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
        _scopeFactory = scopeFactory;
        _blockchainMonitor = blockchainMonitor;
        _logger = logger;
        _channelMemoryRepository = channelMemoryRepository;
    }

    /// <summary>The open reservations remembered for reuse (tests).</summary>
    internal int ReservationCount
    {
        get
        {
            lock (_fundeeReservationsLock)
                return _fundeeReservations.Count;
        }
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
    /// <param name="channel">The channel being opened.</param>
    /// <param name="negotiatedFeatures">The features negotiated with the peer.</param>
    /// <param name="peer">The counterparty, whose temporary channel <paramref name="channel"/> already is (the fundee
    /// of a peer's <c>open_channel</c>, or the funder from the IPC handler). The script of an open abandoned before
    /// <c>funding_created</c> is then reused (see the class remarks); null reserves a fresh address.</param>
    public async Task<BitcoinScript?> AssignIfNegotiatedAsync(ChannelModel channel, FeatureOptions negotiatedFeatures,
                                                              CompactPubKey? peer = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsNegotiated(negotiatedFeatures))
            return channel.LocalUpfrontShutdownScript;
        if (channel.LocalUpfrontShutdownScript is { } existing)
            return existing;

        var script = peer is { } p && _channelMemoryRepository is not null
                         ? await ReuseOrReserveAsync(p, channel)
                         : await ReserveAsync();
        channel.SetLocalUpfrontShutdownScript(script);
        _logger?.LogInformation("Announcing upfront shutdown script {Script} for channel {ChannelId}", script,
                                channel.ChannelId);
        return script;
    }

    private async Task<BitcoinScript> ReuseOrReserveAsync(CompactPubKey peer, ChannelModel channel)
    {
        lock (_fundeeReservationsLock)
        {
            // A channel past the open owns its script for good (persisted with it at funding_created)
            _fundeeReservations.RemoveAll(r => r.Channel.State is not (ChannelState.None or ChannelState.V1Opening));
            for (var i = 0; i < _fundeeReservations.Count; i++)
            {
                var reservation = _fundeeReservations[i];
                if (_channelMemoryRepository!.TryGetTemporaryChannel(reservation.Peer,
                                                                     reservation.TemporaryChannelId, out _))
                    continue;

                // Abandoned before funding_created: nothing holds its script, so it goes to this open
                _fundeeReservations[i] = new OpenReservation(peer, channel.ChannelId, channel, reservation.Script);
                _logger?.LogDebug("Reusing the upfront shutdown script {Script} of an abandoned open for channel "
                                + "{ChannelId}", reservation.Script, channel.ChannelId);
                return reservation.Script;
            }
        }

        var script = await ReserveAsync();
        lock (_fundeeReservationsLock)
            _fundeeReservations.Add(new OpenReservation(peer, channel.ChannelId, channel, script));
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

    /// <summary>A script handed to an open, with the temporary channel that holds it.</summary>
    private sealed record OpenReservation(CompactPubKey Peer, ChannelId TemporaryChannelId, ChannelModel Channel,
                                          BitcoinScript Script);
}