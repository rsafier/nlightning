using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Fees;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Payments.Onion;
using Payments.Switch;

/// <summary>
/// The receiver half of BOLT 2 <c>max_dust_htlc_exposure_msat</c> (BOLT2 plan N9-T3, B2-DUST-01/02): an
/// <see cref="IHtlcSwitch"/> decorator that fails an incoming trimmed HTLC which pushed a commitment's dust exposure
/// over the limit, once it is locked in, before the switch can forward it or reveal its preimage. Every other event,
/// and every HTLC within the limit, goes to the decorated switch unchanged.
/// </summary>
/// <remarks>
/// <para>The limit is the channel's (<see cref="DustExposurePolicy.Resolve"/>: the one stored with its commitment
/// state, else <see cref="NodeOptions.MaxDustHtlcExposureMsat"/>) and the rule is
/// <see cref="DustExposurePolicy.CheckLockedInIncoming"/>. The HTLC is failed with <c>temporary_channel_failure</c>
/// in an error onion made with its onion's shared secret (the onion is peeled without the replay check: it is failed
/// either way). A malformed onion is left to the decorated switch, which fails it with
/// <c>update_fail_malformed_htlc</c> (no preimage either way).</para>
/// <para>Idempotent and safe with replays: the handling of one incoming HTLC is serialized by a per-HTLC lock, an HTLC
/// that is no longer waiting for a resolution is passed on (the decorated switch skips it), and an HTLC the decorated
/// switch already started on (a forward circuit or a stored onion secret exists) is never failed here, so an HTLC that
/// was forwarded is never failed behind the switch's back. A refused failure (the peer is away) persists nothing; the
/// event is derived again when the link comes back.</para>
/// </remarks>
public sealed class DustExposureHtlcSwitch : IHtlcSwitch
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelOperations _channelOperations;
    private readonly IFailureOnionService _failureOnionService;
    private readonly IHtlcSwitch _inner;
    private readonly KeyedAsyncLock<(ChannelId, ulong)> _incomingLocks = new();
    private readonly ILogger<DustExposureHtlcSwitch> _logger;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly IncomingOnionProcessor _onionProcessor;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public DustExposureHtlcSwitch(IHtlcSwitch inner, IChannelMemoryRepository channelMemoryRepository,
                                  IChannelOperations channelOperations, IFailureOnionService failureOnionService,
                                  IncomingOnionProcessor onionProcessor, IServiceScopeFactory serviceScopeFactory,
                                  IOptions<NodeOptions> nodeOptions, ILogger<DustExposureHtlcSwitch> logger)
    {
        _inner = inner;
        _channelMemoryRepository = channelMemoryRepository;
        _channelOperations = channelOperations;
        _failureOnionService = failureOnionService;
        _onionProcessor = onionProcessor;
        _serviceScopeFactory = serviceScopeFactory;
        _nodeOptions = nodeOptions;
        _logger = logger;
    }

    /// <summary>The decorated switch.</summary>
    public IHtlcSwitch Inner => _inner;

    /// <inheritdoc />
    public async Task HandleAsync(IChannelDomainEvent channelEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelEvent);
        if (channelEvent is not IncomingHtlcLockedIn lockedIn)
        {
            await _inner.HandleAsync(channelEvent, cancellationToken);
            return;
        }

        using var incomingLock =
            await _incomingLocks.AcquireAsync((lockedIn.ChannelId, lockedIn.HtlcId), cancellationToken);
        if (!await TryFailOverExposedAsync(lockedIn.ChannelId, lockedIn.HtlcId, cancellationToken))
            await _inner.HandleAsync(channelEvent, cancellationToken);
    }

    /// <summary>
    /// Fails the HTLC when it breaks the receiver rule and nothing else acted on it yet; true when it was handled here
    /// (failed, or the failure was refused and waits for the replay).
    /// </summary>
    private async Task<bool> TryFailOverExposedAsync(ChannelId channelId, ulong htlcId,
                                                     CancellationToken cancellationToken)
    {
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
         || channel.Commitments is not { } commitments
         || commitments.GetHtlc(HtlcDirection.Incoming, htlcId) is not { State: HtlcState.RcvdAddAckRevocation } htlc)
            return false;

        if (DustExposurePolicy.Resolve(commitments, _nodeOptions.Value.MaxDustHtlcExposureMsat) is not { } maxDust
         || DustExposurePolicy.CheckLockedInIncoming(commitments, htlc, maxDust) is not { } excess)
            return false;

        if (await WasProcessedAsync(channelId, htlcId))
        {
            _logger.LogWarning("Incoming HTLC {HtlcId} of channel {ChannelId} is over the dust limit ({Excess}) but "
                             + "was already processed; leaving it to the switch", htlcId, channelId, excess);
            return false;
        }

        var result = await _onionProcessor.ProcessAsync(htlc.OnionRoutingPacket, htlc.PaymentHash, replayOwner: null,
                                                        htlc.PathKey);
        if (result.SharedSecretOrNull is not { } sharedSecret)
            return false;

        var reason = _failureOnionService.CreateErrorPacket(sharedSecret, FailureMessage.TemporaryChannelFailure());
        try
        {
            await _channelOperations.FailHtlcAsync(channelId, htlcId, reason, cancellationToken);
            _logger.LogWarning("Failed incoming HTLC {HtlcId} of channel {ChannelId} ({AmountMsat} msat): {Excess} "
                             + "(B2-DUST-01/02)", htlcId, channelId, htlc.AmountMsat, excess);
        }
        catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
        {
            _logger.LogWarning("Could not fail incoming HTLC {HtlcId} of channel {ChannelId} over the dust limit "
                             + "({Excess}): {Reason}", htlcId, channelId, excess, e.Message);
        }

        return true;
    }

    private async Task<bool> WasProcessedAsync(ChannelId channelId, ulong htlcId)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        if (await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(channelId, htlcId) is not null)
            return true;

        return await unitOfWork.ChannelStateDbRepository.GetOnionSharedSecretAsync(
                   channelId, new HtlcKey(HtlcDirection.Incoming, htlcId)) is not null;
    }
}