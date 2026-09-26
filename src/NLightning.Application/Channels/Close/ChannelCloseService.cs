using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Close;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Interfaces;

/// <summary>
/// <see cref="IChannelCloseService"/> (IPC <c>closechannel</c>, BOLT2 plan N10-T3): takes the channel's lock, checks
/// the channel's link, runs <see cref="ChannelCloseCoordinator.InitiateAsync"/> and
/// <see cref="ChannelCloseCoordinator.AdvanceAsync"/>, publishes their messages under the lock, then optionally waits
/// for the closing transaction. With <c>option_simple_close</c>, a request with a feerate on a Negotiating or Closing
/// channel sends a new <c>closing_complete</c> of ours at that feerate (RBF, N11).
/// </summary>
public sealed class ChannelCloseService : IChannelCloseService
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly ClosingFeeEstimator? _feeEstimator;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelMessagePublisher _channelMessagePublisher;
    private readonly ILogger<ChannelCloseService> _logger;
    private readonly IPeerLivenessProbe _peerLivenessProbe;
    private readonly ClosingNegotiationRegistry _registry;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public ChannelCloseService(IChannelLockProvider channelLockProvider,
                               IChannelMemoryRepository channelMemoryRepository,
                               IChannelMessagePublisher channelMessagePublisher, ILogger<ChannelCloseService> logger,
                               IPeerLivenessProbe peerLivenessProbe, ClosingNegotiationRegistry registry,
                               IServiceScopeFactory serviceScopeFactory, ClosingFeeEstimator? feeEstimator = null)
    {
        _feeEstimator = feeEstimator;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _channelMessagePublisher = channelMessagePublisher;
        _logger = logger;
        _peerLivenessProbe = peerLivenessProbe;
        _registry = registry;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <inheritdoc />
    public async Task<ChannelCloseResult> CloseChannelAsync(ChannelId channelId, ChannelCloseRequest request,
                                                            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The close's fee estimate is fetched here, before the lock: under it the coordinator waits only briefly
        if (request.FeeRatePerKw is null && _feeEstimator is not null)
            await _feeEstimator.PrefetchAsync(cancellationToken);

        using var scope = _serviceScopeFactory.CreateScope();
        ChannelModel channel;
        Task<TxId>? closingTx = null;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var loaded))
                throw new KeyNotFoundException($"Channel {channelId} is not loaded");
            channel = loaded;

            switch (channel.State)
            {
                case ChannelState.Failed:
                    throw new InvalidOperationException($"Channel {channelId} was failed; it can't be closed mutually");
                case ChannelState.Negotiating or ChannelState.Closing
                    when request.FeeRatePerKw is { } bumpFeerate && _registry.Get(channelId).SimpleClose:
                    // option_simple_close: a new closing_complete of ours at the requested feerate (RBF, N11)
                    if (!await _peerLivenessProbe.IsAliveAsync(channelId, channel.RemoteNodeId, cancellationToken))
                        throw new InvalidOperationException(
                            $"The peer of channel {channelId} is not connected on the channel's link");

                    var bumpCoordinator = scope.ServiceProvider.GetRequiredService<ChannelCloseCoordinator>();
                    _channelMessagePublisher.Publish(channel.RemoteNodeId,
                                                     await bumpCoordinator.BumpSimpleCloseAsync(channel, bumpFeerate));
                    _logger.LogInformation("Bumping the closing fee of channel {ChannelId} to {Feerate} sat/kw",
                                           channelId, bumpFeerate);
                    break;
                case ChannelState.Closing or ChannelState.Closed:
                    return ToResult(channel);
                case ChannelState.ShuttingDown or ChannelState.Negotiating:
                    // Already closing: only report (and wait)
                    _registry.Get(channelId).Request ??= request;
                    break;
                case ChannelState.Open:
                    if (!await _peerLivenessProbe.IsAliveAsync(channelId, channel.RemoteNodeId, cancellationToken))
                        throw new InvalidOperationException(
                            $"The peer of channel {channelId} is not connected on the channel's link");

                    var coordinator = scope.ServiceProvider.GetRequiredService<ChannelCloseCoordinator>();
                    var messages = await coordinator.InitiateAsync(channel, request);
                    messages.AddRange(await coordinator.AdvanceAsync(channel));
                    _channelMessagePublisher.Publish(channel.RemoteNodeId, messages);
                    _logger.LogInformation("Closing channel {ChannelId} with peer {Peer}", channelId,
                                           channel.RemoteNodeId);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Channel {channelId} is {Enum.GetName(channel.State)}; only an open channel can be closed");
            }

            if (request.WaitFor is { } wait && wait > TimeSpan.Zero)
                closingTx = _registry.Get(channelId).WaitForClosingTxAsync();
        }

        if (closingTx is not null)
        {
            try
            {
                await closingTx.WaitAsync(request.WaitFor!.Value, cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogInformation("Channel {ChannelId} has no closing transaction yet", channelId);
            }
        }

        return ToResult(channel);
    }

    private static ChannelCloseResult ToResult(ChannelModel channel) =>
        new(channel.ChannelId, channel.State, channel.ClosingTransaction?.TxId);
}