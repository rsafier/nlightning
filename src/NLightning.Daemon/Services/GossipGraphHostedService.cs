using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Services;

using Application.Gossip.Graph;

/// <summary>
/// Starts and stops the BOLT 7 graph (plan G2-T4/G2-T5): the gossip ingress (graph load, workers, write-behind) and
/// the graph pruner. Registered before <see cref="NltgDaemonService"/>, so it starts first (the pruner is subscribed
/// before the chain monitor processes its first block) and stops last (the ingress writes the pending graph changes
/// after the peers and the chain monitor stopped). Nothing starts while the graph is disabled (mainnet by default,
/// plan D12).
/// </summary>
public sealed class GossipGraphHostedService : IHostedService
{
    private readonly GossipIngress _ingress;
    private readonly GraphPruner _pruner;
    private readonly ILogger<GossipGraphHostedService> _logger;

    public GossipGraphHostedService(GossipIngress ingress, GraphPruner pruner,
                                    ILogger<GossipGraphHostedService> logger)
    {
        _ingress = ingress;
        _pruner = pruner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_ingress.IsEnabled)
        {
            _logger.LogInformation("The gossip graph is disabled (Gossip:Enabled)");
            return;
        }

        try
        {
            await _ingress.StartAsync().WaitAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The ingress retries nothing by itself, but a failed load must not stop the node
            _logger.LogError(e, "Could not start the gossip ingress");
        }

        _pruner.Start();
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _pruner.StopAsync();
        await _ingress.StopAsync();
    }
}