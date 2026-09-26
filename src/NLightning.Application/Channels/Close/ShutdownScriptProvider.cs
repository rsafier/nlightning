using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Channels.Close;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Channels.Models;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The script our <c>shutdown</c> pays to (B2-SHUT-S09, B2-SHUT-S10): the upfront shutdown script we sent in
/// <c>open_channel</c>/<c>accept_channel</c> when there is one (BOLT 2 MUST), else a fresh P2WPKH address of our
/// wallet, so the closing output is found as a deposit by the blockchain monitor.
/// </summary>
/// <remarks>Scoped: <see cref="IBitcoinWalletService"/> uses the scope's unit of work.</remarks>
public class ShutdownScriptProvider
{
    private readonly Network _network;
    private readonly IBitcoinWalletService _walletService;

    public ShutdownScriptProvider(IOptions<NodeOptions> nodeOptions, IBitcoinWalletService walletService)
    {
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
        _walletService = walletService;
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
        return BitcoinAddress.Create(address.Address, _network).ScriptPubKey.ToBytes();
    }
}