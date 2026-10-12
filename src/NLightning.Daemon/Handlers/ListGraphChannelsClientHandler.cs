using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Handlers;

using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Options;
using Interfaces;

/// <summary>
/// Lists the channels of the gossip graph (ClientCommand 18, BOLT 7 plan G2-T6), ordered by short channel id: both
/// policies, the verification and, for a channel whose funding output was spent, the spend height (it is removed 72
/// blocks later). Filters: one short channel id, one node. Refused with <c>invalid_operation</c> while the graph is
/// disabled.
/// </summary>
public sealed class ListGraphChannelsClientHandler
    : IClientCommandHandler<ListGraphChannelsClientRequest, ListGraphChannelsClientResponse>
{
    private readonly IGraphStore _graphStore;
    private readonly GossipGraphOptions _options;
    private readonly NodeOptions _nodeOptions;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListGraphChannels;

    public ListGraphChannelsClientHandler(IGraphStore graphStore, IOptions<GossipGraphOptions> options,
                                          IOptions<NodeOptions> nodeOptions)
    {
        _graphStore = graphStore;
        _options = options.Value;
        _nodeOptions = nodeOptions.Value;
    }

    /// <inheritdoc/>
    public Task<ListGraphChannelsClientResponse> HandleAsync(ListGraphChannelsClientRequest request,
                                                             CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        GraphClientGuards.ThrowIfDisabled(_options, _nodeOptions);

        var channels = _graphStore.GetSnapshot().Channels
                                  .Where(c => request.ShortChannelId is not { } scid || c.ShortChannelId == scid)
                                  .Where(c => request.NodeId is not { } node || c.NodeId1 == node || c.NodeId2 == node)
                                  .OrderBy(c => c.ShortChannelId.BlockHeight)
                                  .ThenBy(c => c.ShortChannelId.TransactionIndex)
                                  .ThenBy(c => c.ShortChannelId.OutputIndex)
                                  .ToList();
        return Task.FromResult(new ListGraphChannelsClientResponse(channels));
    }
}