using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Services;

using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Interfaces;

/// <summary>
/// The default <see cref="IPingBeforeCommit"/>: a peer heard from within
/// <see cref="CommitSchedulerOptions.PingWhenQuietFor"/> is responsive; otherwise a BOLT 1 <c>ping</c> goes out through
/// its <see cref="IPeerService"/> and its <c>pong</c> is awaited for <see cref="CommitSchedulerOptions.PongTimeout"/>.
/// </summary>
/// <remarks>
/// The peer manager is resolved on first use, not injected (it depends on the channel manager, which the scheduler sits
/// next to). Without a peer manager (in-process tests) every peer is responsive. Singleton.
/// </remarks>
public sealed class PingBeforeCommit : IPingBeforeCommit
{
    private readonly ILogger<PingBeforeCommit> _logger;
    private readonly CommitSchedulerOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;
    private IPeerManager? _peerManager;
    private bool _resolved;

    public PingBeforeCommit(ILogger<PingBeforeCommit> logger, IServiceProvider serviceProvider,
                            IOptions<CommitSchedulerOptions>? options = null, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _options = options?.Value ?? new CommitSchedulerOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<bool> EnsureResponsiveAsync(CompactPubKey peerPubKey,
                                                  CancellationToken cancellationToken = default)
    {
        if (!_resolved)
        {
            _peerManager = _serviceProvider.GetService<IPeerManager>();
            _resolved = true;
        }

        if (_peerManager is null)
            return true;

        var peer = _peerManager.GetPeer(peerPubKey);
        if (peer is null || !peer.TryGetPeerService(out var peerService))
            return false;

        var lastReceived = peerService.LastMessageReceivedAt;
        if (lastReceived is not null && _timeProvider.GetUtcNow() - lastReceived.Value < _options.PingWhenQuietFor)
            return true;

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Peer {Peer} was quiet since {LastReceived}: pinging before commitment_signed", peerPubKey,
                             lastReceived);

        if (await peerService.PingAsync(_options.PongTimeout, cancellationToken))
            return true;

        _logger.LogWarning("Peer {Peer} did not answer our ping within {Timeout}: not signing (the connection is closed)",
                           peerPubKey, _options.PongTimeout);
        return false;
    }
}