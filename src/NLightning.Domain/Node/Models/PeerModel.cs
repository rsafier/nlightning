using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Node.Models;

using Channels.Models;
using Crypto.ValueObjects;
using Interfaces;
using Options;
using ValueObjects;

public class PeerModel
{
    private PeerAddressInfo? _peerAddressInfo;

    private IPeerService? _peerService;

    public CompactPubKey NodeId { get; }
    public string Host { get; }
    public uint Port { get; }
    public string Type { get; }
    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// We know no address to dial this peer at (NL-497, wave spr): it connected to us, from a loopback address (a local
    /// tunnel, Tor on the same host) or from an ephemeral port, so <see cref="Host"/>/<see cref="Port"/> are only where
    /// it came from. Such a peer is still saved, so its channels are registered at startup, but it is never dialed
    /// (not at startup, not by the reconnect loop): we wait for it to connect again. Persisted by the wave's migration
    /// owner lane.
    /// </summary>
    public bool IsInboundOnly { get; init; }

    public FeatureSet Features
    {
        get
        {
            return _peerService is null
                       ? throw new NullReferenceException($"{nameof(PeerModel)}.{nameof(Features)} was null")
                       : _peerService.Features.GetNodeFeatures();
        }
    }

    public FeatureOptions NegotiatedFeatures
    {
        get
        {
            return _peerService is null
                       ? throw new NullReferenceException($"{nameof(PeerModel)}.{nameof(Features)} was null")
                       : _peerService.Features;
        }
    }

    public PeerAddressInfo PeerAddressInfo
    {
        get
        {
            _peerAddressInfo ??= new PeerAddressInfo($"{NodeId}@{Host}:{Port}");

            return _peerAddressInfo.Value;
        }
    }

    public ICollection<ChannelModel>? Channels { get; set; }

    public PeerModel(CompactPubKey nodeId, string host, uint port, string type)
    {
        NodeId = nodeId;
        Host = host;
        Port = port;
        Type = type;
    }

    public bool TryGetPeerService([MaybeNullWhen(false)] out IPeerService peerService)
    {
        if (_peerService is not null)
        {
            peerService = _peerService;
            return true;
        }

        peerService = null;
        return false;
    }

    public void SetPeerService(IPeerService peerService)
    {
        ArgumentNullException.ThrowIfNull(peerService);

        if (_peerService is not null)
            throw new InvalidOperationException("Peer service already set.");

        _peerService = peerService;
    }
}