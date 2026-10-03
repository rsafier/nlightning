using MessagePack;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Daemon.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Interfaces;
using Services.Ipc.Factories;
using Transport.Ipc;
using Transport.Ipc.Responses;

/// <summary>
/// <c>info</c> (ClientCommand 1): the node's key, listeners and chain position, plus the connected peers and the
/// channels in memory by phase when the peer manager and channel repository are registered.
/// </summary>
internal sealed class NodeInfoIpcHandler : IIpcCommandHandler
{
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly IPeerManager? _peerManager;
    private readonly INodeInfoQueryService _query;
    private readonly ILogger<NodeInfoIpcHandler> _logger;

    public ClientCommand Command => ClientCommand.NodeInfo;

    public NodeInfoIpcHandler(INodeInfoQueryService query, ILogger<NodeInfoIpcHandler> logger,
                              IPeerManager? peerManager = null,
                              IChannelMemoryRepository? channelMemoryRepository = null)
    {
        _query = query;
        _logger = logger;
        _peerManager = peerManager;
        _channelMemoryRepository = channelMemoryRepository;
    }

    public async Task<IpcEnvelope> HandleAsync(IpcEnvelope envelope, CancellationToken ct)
    {
        try
        {
            var resp = await _query.QueryAsync(ct);
            var channels = _channelMemoryRepository?.FindChannels(_ => true);
            var ipcResp = new NodeInfoIpcResponse
            {
                PubKey = new CompactPubKey(Convert.FromHexString(resp.PubKey)),
                ListeningTo = resp.ListeningTo.Split(',').ToList(),
                Network = resp.Network,
                BestBlockHash = new Hash(Convert.FromHexString(resp.BestBlockHash)),
                BestBlockHeight = resp.BestBlockHeight,
                BestBlockTime = resp.BestBlockTime,
                Implementation = resp.Implementation,
                Version = resp.Version,
                TorMode = resp.TorMode,
                OnionAddress = resp.OnionAddress,
                PeerCount = _peerManager?.ListPeers().Count,
                ActiveChannelCount = channels?.Count(c => c.State == ChannelState.Open),
                PendingChannelCount = channels?.Count(c => c.State is > ChannelState.None and < ChannelState.Open),
                ClosingChannelCount =
                    channels?.Count(c => c.State is > ChannelState.Open and < ChannelState.Closed)
            };
            var payload = MessagePackSerializer.Serialize(ipcResp, cancellationToken: ct);
            return new IpcEnvelope
            {
                Version = envelope.Version,
                Command = envelope.Command,
                CorrelationId = envelope.CorrelationId,
                Kind = IpcEnvelopeKind.Response,
                Payload = payload
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling node info");
            return IpcErrorFactory.CreateErrorEnvelope(envelope, ErrorCodes.ServerError, e.Message);
        }
    }
}