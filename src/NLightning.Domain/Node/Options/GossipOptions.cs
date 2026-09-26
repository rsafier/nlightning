using System.Net;

namespace NLightning.Domain.Node.Options;

using Gossip.Addresses;
using Protocol.ValueObjects;

/// <summary>
/// BOLT 7 options of the node's own public channels (configuration section <c>Gossip</c>, BOLT 7 plan §3.2 and D12).
/// </summary>
public sealed class GossipOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Gossip";

    /// <summary>
    /// The depth BOLT 7 requires before a channel is announced: <c>announcement_signatures</c> and
    /// <c>channel_announcement</c> are only sent, and a <c>channel_announcement</c> is only accepted, at 6
    /// confirmations.
    /// </summary>
    public const uint MinimumAnnouncementDepth = 6;

    /// <summary>
    /// Whether a peer may open a public channel to us (<c>announce_channel</c> set in <c>open_channel</c>). When false
    /// such an <c>open_channel</c> is refused with an <c>error</c> (BOLT 2 lets the receiver fail a channel whose
    /// <c>announce_channel</c> it does not want). Default true.
    /// </summary>
    public bool AcceptPublicChannels { get; set; } = true;

    /// <summary>
    /// Whether public channels are allowed on mainnet: <c>openchannel --public</c>, and accepting a peer's
    /// <c>open_channel</c> with <c>announce_channel</c> (refused with an <c>error</c> otherwise, since BOLT 7 would
    /// oblige us to send <c>announcement_signatures</c> for it). Public channels stay off on mainnet until the BOLT 7
    /// plan's Proof G1 passed (D12). Default false.
    /// </summary>
    public bool AllowPublicChannelsOnMainnet { get; set; }

    /// <summary>
    /// The confirmations of the funding transaction before our <c>announcement_signatures</c> go out. Only regtest may
    /// use less than <see cref="MinimumAnnouncementDepth"/>; elsewhere a lower value is raised to it.
    /// </summary>
    public uint AnnouncementDepth { get; set; } = MinimumAnnouncementDepth;

    /// <summary>
    /// The addresses our <c>node_announcement</c> gives (BOLT 7 address descriptors), each <c>host:port</c>: an IPv4
    /// address, a bracketed IPv6 address (<c>[::1]:9735</c>), a Tor v3 <c>.onion</c> name or one DNS hostname. Empty by
    /// default (plan D11): the listen addresses are not announced on their own, for privacy.
    /// </summary>
    public List<string> AnnounceAddresses { get; set; } = [];

    /// <summary>
    /// How often our own gossip (<c>channel_announcement</c>, <c>channel_update</c>, <c>node_announcement</c>) goes
    /// out to the connected peers (BOLT 7: SHOULD flush outgoing gossip every 60 seconds). Default 60 s.
    /// </summary>
    public TimeSpan OwnGossipFlushInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How old our <c>node_announcement</c> may get before it is signed again with a new timestamp, so peers that prune
    /// nodes after two weeks without news keep ours (LND/CLN practice). Default 13 days.
    /// </summary>
    public TimeSpan NodeAnnouncementRefreshInterval { get; set; } = TimeSpan.FromDays(13);

    /// <summary>
    /// How long the peer of an announced channel may stay away (its link down) before our <c>channel_update</c> for the
    /// channel is published again with the <c>disable</c> bit, so payers stop routing through it (BOLT 7 plan G1-T5,
    /// NL-349; LND and CLN use 20 minutes). A newer enabled update follows once the peer is back. Zero or less turns it
    /// off. Default 20 minutes.
    /// </summary>
    public TimeSpan DisableAfter { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The depth in effect on <paramref name="network"/>: <see cref="AnnouncementDepth"/> (at least 1) on regtest,
    /// else at least <see cref="MinimumAnnouncementDepth"/>.
    /// </summary>
    public uint GetAnnouncementDepth(BitcoinNetwork network) =>
        network == BitcoinNetwork.Regtest
            ? Math.Max(1U, AnnouncementDepth)
            : Math.Max(MinimumAnnouncementDepth, AnnouncementDepth);

    /// <summary>
    /// Whether our channels may be announced on <paramref name="network"/> (plan D12): everywhere but mainnet, and on
    /// mainnet only with <see cref="AllowPublicChannelsOnMainnet"/>. It gates <c>openchannel --public</c>, a peer's public
    /// <c>open_channel</c> (refused where not allowed) and our <c>announcement_signatures</c>.
    /// </summary>
    public bool ArePublicChannelsAllowed(BitcoinNetwork network) =>
        network != BitcoinNetwork.Mainnet || AllowPublicChannelsOnMainnet;

    /// <summary>
    /// <see cref="AnnounceAddresses"/> as address descriptors in the order BOLT 7 requires (ascending type; the order
    /// given is kept within a type).
    /// </summary>
    /// <exception cref="ArgumentException">An entry does not parse, has port 0 or is a Tor v2 name, or more than one
    /// DNS hostname is given (BOLT 7 origin rules).</exception>
    public IReadOnlyList<AddressDescriptor> GetAnnounceAddressDescriptors()
    {
        var descriptors = (AnnounceAddresses ?? []).Select(ParseAnnounceAddress)
                                                   .OrderBy(d => d.Type)
                                                   .ToList();
        _ = AddressDescriptorCodec.EncodeList(descriptors);
        return descriptors;
    }

    /// <summary>
    /// Every configuration error of these options (the announced addresses); empty when valid.
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        try
        {
            _ = GetAnnounceAddressDescriptors();
            return [];
        }
        catch (ArgumentException e)
        {
            return [$"{SectionName}:{nameof(AnnounceAddresses)}: {e.Message}"];
        }
    }

    /// <summary>
    /// One <c>host:port</c> entry: IPv4, bracketed IPv6, a <c>.onion</c> name (Tor v3) or a DNS hostname.
    /// </summary>
    private static AddressDescriptor ParseAnnounceAddress(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            throw new ArgumentException("An announced address is empty.", nameof(entry));

        var text = entry.Trim();
        var separator = text.LastIndexOf(':');
        if (separator <= 0 || !ushort.TryParse(text[(separator + 1)..], out var port) || port == 0)
            throw new ArgumentException($"'{entry}' is not host:port with a port from 1 to 65535.", nameof(entry));

        var host = text[..separator];
        if (host.StartsWith('[') && host.EndsWith(']'))
            return AddressDescriptor.FromHost(AddressDescriptorType.IPv6, host[1..^1], port);
        if (host.Contains(':'))
            throw new ArgumentException($"'{entry}': write an IPv6 address in brackets ([::1]:9735).", nameof(entry));
        if (IPAddress.TryParse(host, out var ip))
            return AddressDescriptor.FromIpAddress(ip, port);
        if (host.EndsWith(".onion", StringComparison.OrdinalIgnoreCase))
            return AddressDescriptor.FromHost(AddressDescriptorType.TorV3, host, port);

        return AddressDescriptor.FromDnsHostname(host, port);
    }
}