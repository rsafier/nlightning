using MessagePack;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Daemon.Handlers;
using Domain.Channels.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Node.Interfaces;
using Interfaces;
using Services.Ipc.Factories;
using Transport.Ipc;
using Transport.Ipc.Responses;

/// <summary>
/// The connected peers (ClientCommand 3): address, features, the channels that are not closed and their HTLCs in
/// flight, counted from the channels in memory (a peer's stored channel list misses the ones opened since startup).
/// </summary>
internal class ListPeersIpcHandler : IIpcCommandHandler
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IPeerManager _peerManager;
    private readonly ILogger<ListPeersIpcHandler> _logger;

    public ClientCommand Command => ClientCommand.ListPeers;

    public ListPeersIpcHandler(IPeerManager peerManager, IChannelMemoryRepository channelMemoryRepository,
                               ILogger<ListPeersIpcHandler> logger)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _peerManager = peerManager;
        _logger = logger;
    }

    public Task<IpcEnvelope> HandleAsync(IpcEnvelope envelope, CancellationToken ct)
    {
        try
        {
            var resp = _peerManager.ListPeers();
            var ipcResp = new ListPeersIpcResponse();

            if (resp.Count > 0)
            {
                ipcResp.Peers = new List<PeerInfoIpcResponse>(resp.Count);
                foreach (var peer in resp)
                {
                    var summary = PeerChannelSummary.For(_channelMemoryRepository, peer.NodeId);
                    ipcResp.Peers.Add(new PeerInfoIpcResponse
                    {
                        Address = $"{peer.Host}:{peer.Port}",
                        Connected = true,
                        Features = peer.Features,
                        Id = peer.NodeId,
                        ChannelQty = (uint)summary.ChannelCount,
                        HtlcsInFlight = (uint)summary.HtlcsInFlight
                    });
                }
            }

            var payload = MessagePackSerializer.Serialize(ipcResp, cancellationToken: ct);
            var responseEnvelope = new IpcEnvelope
            {
                Version = envelope.Version,
                Command = envelope.Command,
                CorrelationId = envelope.CorrelationId,
                Kind = IpcEnvelopeKind.Response,
                Payload = payload
            };
            return Task.FromResult(responseEnvelope);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error listing peers");
            return Task.FromResult(IpcErrorFactory.CreateErrorEnvelope(envelope, ErrorCodes.ServerError, e.Message));
        }
    }
}