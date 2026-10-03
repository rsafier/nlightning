using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Services;

using Contracts.Control;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Transport.Interfaces;
using Infrastructure.Transport.Tor;
using Interfaces;

public sealed class NodeInfoQueryService : INodeInfoQueryService
{
    private readonly NodeOptions _nodeOptions;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IServiceProvider _services;
    private readonly ITcpService _tcpService;
    private readonly ITorOnionService? _torOnionService;

    public NodeInfoQueryService(IOptions<NodeOptions> nodeOptions, ISecureKeyManager secureKeyManager,
                                IServiceProvider services, ITcpService tcpService,
                                ITorOnionService? torOnionService = null)
    {
        _torOnionService = torOnionService;
        _nodeOptions = nodeOptions.Value;
        _secureKeyManager = secureKeyManager;
        _services = services;
        _tcpService = tcpService;
    }

    public async Task<NodeInfoResponse> QueryAsync(CancellationToken ct)
    {
        // resolve per-call scope to access repositories
        using var scope = _services.CreateScope();
        var uow = scope.ServiceProvider.GetService<IUnitOfWork>();

        var bestHashHex = string.Empty;
        long bestHeight = 0;
        DateTimeOffset? bestTime = null;

        if (uow is not null)
        {
            try
            {
                var state = await uow.BlockchainStateDbRepository.GetStateAsync();
                if (state is not null)
                {
                    bestHeight = state.LastProcessedHeight;
                    bestHashHex = state.LastProcessedBlockHash.ToString();
                    bestTime = AsUtc(state.LastProcessedAt);
                }
            }
            catch
            {
                // ignore, return defaults
            }
        }

        var pubKeyString = _secureKeyManager.GetNodePubKey().ToString();
        var listeningToString = string.Join(',', _tcpService.ListeningTo.Select(e => e.ToString()).ToList());

        return new NodeInfoResponse
        {
            PubKey = pubKeyString,
            ListeningTo = listeningToString,
            Network = _nodeOptions.BitcoinNetwork,
            BestBlockHash = bestHashHex,
            BestBlockHeight = bestHeight,
            BestBlockTime = bestTime,
            Implementation = "NLightning",
            Version = typeof(NodeInfoQueryService).Assembly.GetName().Version?.ToString(),
            TorMode = _nodeOptions.Tor.Mode.ToString(),
            OnionAddress = _torOnionService?.OnionHost is { } onionHost
                               ? $"{pubKeyString}@{onionHost}:{_torOnionService.OnionPort}"
                               : null
        };
    }

    /// <summary>
    /// The stored processing time as a UTC instant (NL-885): it is written as <see cref="DateTime.UtcNow"/>, and the
    /// database hands it back without a kind, which the implicit conversion to <see cref="DateTimeOffset"/> took as
    /// local time (a UTC wall time with the local offset attached).
    /// </summary>
    internal static DateTimeOffset AsUtc(DateTime time) =>
        time.Kind == DateTimeKind.Local
            ? new DateTimeOffset(time.ToUniversalTime(), TimeSpan.Zero)
            : new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc), TimeSpan.Zero);
}