using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Switch;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Extensions;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// The HTLC switch of a node that neither receives payments nor forwards yet (BOLT2 plan N6-T2, §3.10): every incoming
/// HTLC is failed back once it is locked in, with a proper BOLT 4 error onion so the origin can read the reason.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IncomingHtlcLockedIn"/>: the onion is peeled with the node key (<see cref="ISphinxService"/>).
/// <list type="bullet">
/// <item>A BADONION failure (or any failure inside a blinded route, reported as <c>invalid_onion_blinding</c>) is
/// returned in <c>update_fail_malformed_htlc</c> with <c>sha256_of_onion</c>.</item>
/// <item>Any other peel failure with a shared secret (<c>invalid_onion_payload</c>) is encrypted for the origin.</item>
/// <item>A good onion: its shared secret is stored with the HTLC (<see cref="IChannelOperations.RecordOnionSecretAsync"/>)
/// and the HTLC is failed with <c>incorrect_or_unknown_payment_details</c> (htlc_msat, current height) when we are
/// the final hop (we have no invoices yet), or <c>temporary_node_failure</c> when we would have to forward (no
/// forwarding yet), encrypted with that secret (<see cref="IFailureOnionService.CreateErrorPacket"/>).</item>
/// </list>
/// The onion is not recorded in the replay cache: a replay after a restart must be allowed to fail the HTLC again.
/// </para>
/// <para>
/// Outgoing events: fulfilled and failed HTLCs are only logged (no payments or circuits exist yet); a settled one has
/// its archived row pruned (NL-243), since nothing else needs it.
/// </para>
/// <para>
/// Idempotent (events are replayed on startup): an incoming HTLC that is already being removed is skipped, and a
/// refused operation (for example a channel that failed or closed meanwhile) is logged, not thrown. Replace it with
/// <c>services.Replace(ServiceDescriptor.Singleton&lt;IHtlcSwitch, ...&gt;())</c> once final-hop and forwarding exist
/// (ABCD W2-B).
/// </para>
/// </remarks>
public sealed class LocalOnlyHtlcSwitch : IHtlcSwitch
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelOperations _channelOperations;
    private readonly IFailureOnionService _failureOnionService;
    private readonly ILogger<LocalOnlyHtlcSwitch> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISphinxService _sphinxService;

    public LocalOnlyHtlcSwitch(IChannelLockProvider channelLockProvider,
                               IChannelMemoryRepository channelMemoryRepository, IChannelOperations channelOperations,
                               IFailureOnionService failureOnionService, ILogger<LocalOnlyHtlcSwitch> logger,
                               IServiceScopeFactory serviceScopeFactory, ISphinxService sphinxService,
                               IBlockchainMonitor? blockchainMonitor = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _channelOperations = channelOperations;
        _failureOnionService = failureOnionService;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _sphinxService = sphinxService;
    }

    /// <inheritdoc />
    public async Task HandleAsync(IChannelDomainEvent channelEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelEvent);
        try
        {
            switch (channelEvent)
            {
                case IncomingHtlcLockedIn lockedIn:
                    await FailBackAsync(lockedIn, cancellationToken);
                    break;
                case OutgoingHtlcFulfilled fulfilled:
                    _logger.LogInformation("HTLC {HtlcId} we offered on channel {ChannelId} was fulfilled",
                                           fulfilled.HtlcId, fulfilled.ChannelId);
                    break;
                case OutgoingHtlcFailed failed:
                    _logger.LogInformation("HTLC {HtlcId} we offered on channel {ChannelId} failed ({Kind})",
                                           failed.HtlcId, failed.ChannelId, failed.Removal.Kind);
                    break;
                case OutgoingHtlcSettled settled:
                    await PruneAsync(settled, cancellationToken);
                    break;
            }
        }
        catch (CommitmentRefusedException e)
        {
            // Nothing was persisted or sent: the HTLC stays locked in and the event is replayed on the next start
            _logger.LogWarning("Could not act on {Event} for HTLC {HtlcId} of channel {ChannelId}: {Reason}",
                               channelEvent.GetType().Name, channelEvent.HtlcId, channelEvent.ChannelId, e.Message);
        }
        catch (KeyNotFoundException e)
        {
            _logger.LogWarning("Could not act on {Event} for HTLC {HtlcId}: {Reason}", channelEvent.GetType().Name,
                               channelEvent.HtlcId, e.Message);
        }
    }

    private async Task FailBackAsync(IncomingHtlcLockedIn lockedIn, CancellationToken cancellationToken)
    {
        var channelId = lockedIn.ChannelId;
        var htlc = lockedIn.Htlc;

        // Idempotency: an event replayed after the removal was sent (or for a channel gone meanwhile) does nothing
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
         || channel.Commitments?.GetHtlc(HtlcDirection.Incoming, htlc.Id) is not
         { State: HtlcState.RcvdAddAckRevocation })
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("HTLC {HtlcId} of channel {ChannelId} is not waiting for a resolution", htlc.Id,
                                 channelId);
            return;
        }

        var onionBytes = htlc.OnionRoutingPacket;
        var sha256OfOnion = Sha256Of(onionBytes.Span);

        // Inside a blinded route every failure is invalid_onion_blinding, returned unencrypted (BOLT 4)
        if (htlc.PathKey is not null)
        {
            await _channelOperations.FailMalformedHtlcAsync(channelId, htlc.Id,
                                                            FailureCode.InvalidOnionBlinding, sha256OfOnion,
                                                            cancellationToken);
            LogFailed(htlc, channelId, "invalid_onion_blinding (blinded route)");
            return;
        }

        PeeledOnion peeled;
        try
        {
            peeled = _sphinxService.PeelAsLocalNode(new OnionPacket(onionBytes.Span), (byte[])htlc.PaymentHash);
        }
        catch (OnionException e) when (e.FailureCode.IsBadOnion() || e.SharedSecret is null)
        {
            var code = e.FailureCode.IsBadOnion()
                           ? e.FailureCode
                           : FailureCode.InvalidOnionHmac;
            var hash = e.FailureData is { Length: CryptoConstants.Sha256HashLen } data
                           ? new Hash(data.ToArray())
                           : sha256OfOnion;
            await _channelOperations.FailMalformedHtlcAsync(channelId, htlc.Id, code, hash, cancellationToken);
            LogFailed(htlc, channelId, $"malformed onion ({code})");
            return;
        }
        catch (OnionException e)
        {
            var failure = ToFailureMessage(e);
            await _channelOperations.FailHtlcAsync(channelId, htlc.Id,
                                                   _failureOnionService.CreateErrorPacket(e.SharedSecret!.Value,
                                                                                          failure),
                                                   cancellationToken);
            LogFailed(htlc, channelId, failure.Code.ToString());
            return;
        }

        // Keep the secret with the HTLC before failing it, so the failure can be rebuilt after a restart
        await _channelOperations.RecordOnionSecretAsync(channelId, htlc.Id, peeled.SharedSecret, cancellationToken);

        var message = peeled.IsFinal
                          ? FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(htlc.AmountMsat),
                                                                            _blockchainMonitor
                                                                              ?.LastProcessedBlockHeight ?? 0)
                          : FailureMessage.TemporaryNodeFailure();
        var reason = _failureOnionService.CreateErrorPacket(peeled.SharedSecret, message);
        await _channelOperations.FailHtlcAsync(channelId, htlc.Id, reason, cancellationToken);
        LogFailed(htlc, channelId, message.Code.ToString());
    }

    private async Task PruneAsync(OutgoingHtlcSettled settled, CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        using var channelLock = await _channelLockProvider.AcquireAsync(settled.ChannelId, cancellationToken);

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.ChannelStateDbRepository.PruneSettledHtlcsAsync(
            settled.ChannelId, [new HtlcKey(HtlcDirection.Outgoing, settled.HtlcId)]);
        await unitOfWork.SaveChangesAsync();
    }

    private static FailureMessage ToFailureMessage(OnionException e)
    {
        try
        {
            return new FailureMessage(e.FailureCode, e.FailureData ?? ReadOnlyMemory<byte>.Empty);
        }
        catch (ArgumentException)
        {
            return FailureMessage.InvalidOnionPayload(new(0), 0);
        }
    }

    private static Hash Sha256Of(ReadOnlySpan<byte> bytes)
    {
        using var sha256 = new Sha256();
        var hash = new byte[CryptoConstants.Sha256HashLen];
        sha256.AppendData(bytes);
        sha256.GetHashAndReset(hash);
        return new Hash(hash);
    }

    private void LogFailed(HtlcRecord htlc, ChannelId channelId, string reason)
    {
        _logger.LogInformation("Failed back incoming HTLC {HtlcId} of {AmountMsat} msat on channel {ChannelId}: {Reason}",
                               htlc.Id, htlc.AmountMsat, channelId, reason);
    }
}