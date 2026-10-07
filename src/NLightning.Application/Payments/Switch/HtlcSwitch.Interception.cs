using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Switch;

using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Payments.Interception;
using Domain.Payments.Keysend;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Onion;

public sealed partial class HtlcSwitch
{
    /// <summary>
    /// Offers a forward to the interceptor (NL-1183, LND's <c>InterceptableSwitch</c>). True when the HTLC is taken care
    /// of: held (its resolution comes through <see cref="ResolveInterceptedAsync"/>), or failed because it expires too
    /// soon (or too far) to be held, too many forwards are held, or an interceptor is required and none is connected
    /// (NL-1182). False without an interceptor (and none required).
    /// </summary>
    /// <param name="isReplay">The switch handled this HTLC before (its onion's shared secret was stored): with an
    /// interceptor required and none connected it is held instead of failed (LND's replay rule).</param>
    private async Task<bool> TryInterceptAsync(ChannelId incomingChannelId, HtlcRecord htlc,
                                               IncomingOnionForward forward, uint height, bool isReplay,
                                               CancellationToken cancellationToken)
    {
        if (_forwardInterceptor is not { } interceptor || (!interceptor.IsActive && !interceptor.IsRequired)
         || !_channelMemoryRepository.TryGetChannel(incomingChannelId, out var incomingChannel))
            return false;

        var intercepted = ToInterceptedForward(incomingChannel, htlc, forward);
        var incomingScid = intercepted.IncomingShortChannelId;
        var introduction = forward.Blinded?.IsIntroduction ?? false;
        switch (interceptor.Intercept(intercepted, height, isReplay,
                                      resolution => ResolveInterceptedAsync(incomingChannelId, htlc.Id, forward,
                                                                            resolution)))
        {
            case ForwardInterceptOutcome.Held:
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Holding the forward of HTLC {HtlcId} of channel {ChannelId} for the interceptor",
                                     htlc.Id, incomingChannelId);
                return true;
            case ForwardInterceptOutcome.ExpiryTooSoon:
                await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret,
                                    FailureMessage.ExpiryTooSoon(UpdateFor(incomingChannel, incomingScid)),
                                    cancellationToken, introduction);
                return true;
            case ForwardInterceptOutcome.ExpiryTooFar:
                await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret, FailureMessage.ExpiryTooFar(),
                                    cancellationToken, introduction);
                return true;
            case ForwardInterceptOutcome.Full:
                await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret,
                                    FailureMessage.TemporaryChannelFailure(UpdateFor(incomingChannel, incomingScid)),
                                    cancellationToken, introduction);
                return true;
            case ForwardInterceptOutcome.InterceptorRequired:
                _logger.LogInformation("Failed back the forward of HTLC {HtlcId} of channel {ChannelId}: an HTLC "
                                     + "interceptor is required and none is connected", htlc.Id, incomingChannelId);
                await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret,
                                    FailureMessage.TemporaryChannelFailure(UpdateFor(incomingChannel, incomingScid)),
                                    cancellationToken, introduction);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Holds a forward whose incoming channel is closing on chain for an interceptor's settle (NL-1182, LND's on-chain
    /// interception: the preimage it gives lets the resolvers claim the HTLC on chain). Nothing is forwarded or failed
    /// from such a channel. Held only while an interceptor is connected or required, or when the forward was already
    /// held off chain; the resolvers hand the HTLC to the switch every round, so a client that connects later is offered
    /// it at the next block. True when it is held (or was settled already).
    /// </summary>
    private bool TryInterceptOnChain(ChannelId incomingChannelId, HtlcRecord htlc, IncomingOnionForward forward)
    {
        if (_forwardInterceptor is not { } interceptor
         || !_channelMemoryRepository.TryGetChannel(incomingChannelId, out var incomingChannel))
            return false;

        // Settled by the interceptor already: the resolvers claim it with the preimage on its record
        if (htlc.KnownPreimage is { } known && Hashes(known, htlc.PaymentHash))
            return true;

        // Past its expiry the peer can take it by the timeout path: nothing to offer any more
        if (CurrentHeight >= htlc.CltvExpiry)
            return false;

        return interceptor.InterceptOnChain(ToInterceptedForward(incomingChannel, htlc, forward),
                                            resolution => ResolveInterceptedAsync(incomingChannelId, htlc.Id, forward,
                                                                                  resolution));
    }

    /// <summary>
    /// Carries out an interceptor's resolution of a held forward, under the incoming HTLC's lock: nothing when the HTLC
    /// was resolved meanwhile; resume = the forward goes on (outgoing channel, policy, circuit, offer); resume modified =
    /// the same with the interceptor's amounts and custom records (NL-1182); fail = our failure, or the interceptor's
    /// error packet obfuscated like a downstream error; settle = the incoming HTLC is fulfilled with the interceptor's
    /// preimage (checked against the payment hash), or, when its channel is closing on chain, the preimage is persisted
    /// on its record for the resolvers' claim.
    /// </summary>
    /// <exception cref="ForwardHeldOnChainException">A resume or fail reached a forward whose incoming channel is closing
    /// on chain (nothing done; the hub keeps it held for a settle).</exception>
    private async Task ResolveInterceptedAsync(ChannelId incomingChannelId, ulong htlcId, IncomingOnionForward forward,
                                               ForwardInterceptResolution resolution)
    {
        var cancellationToken = _disposeCts.Token;
        using var incomingLock = await _incomingLocks.AcquireAsync((incomingChannelId, htlcId), cancellationToken);
        if (GetAwaitingIncomingHtlc(incomingChannelId, htlcId) is not { } htlc)
            return;

        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(incomingChannelId, htlcId) is not null)
                return;
        }

        // NL-1182: a channel closing on chain carries no update any more; only the preimage helps (LND refuses resume
        // and fail of an HTLC on chain)
        if (IsOnchain(incomingChannelId))
        {
            if (resolution.Action != ForwardInterceptAction.Settle)
                throw new ForwardHeldOnChainException(
                    $"channel {incomingChannelId} of HTLC {htlcId} is closing on chain: only a settle is possible");

            await SettleInterceptedOnchainAsync(incomingChannelId, htlc, forward, resolution, cancellationToken);
            return;
        }

        var introduction = forward.Blinded?.IsIntroduction ?? false;
        try
        {
            switch (resolution.Action)
            {
                case ForwardInterceptAction.Resume:
                    await ForwardAsync(incomingChannelId, htlc, forward, firstHandling: false, cancellationToken,
                                       intercept: false);
                    return;

                case ForwardInterceptAction.ResumeModified:
                    await ForwardAsync(incomingChannelId, htlc, forward, firstHandling: false, cancellationToken,
                                       intercept: false, modification: resolution);
                    return;

                case ForwardInterceptAction.Settle:
                    if (resolution.Preimage is not { } preimage || !Hashes(preimage, htlc.PaymentHash))
                    {
                        _logger.LogWarning("Interceptor settle of HTLC {HtlcId} of channel {ChannelId} with a wrong "
                                         + "preimage ignored", htlcId, incomingChannelId);
                        return;
                    }

                    // NL-1182: no outgoing leg took anything out of the channels, so the whole amount is ours; its
                    // event commits with the fulfill or not at all
                    await _channelOperations.FulfillHtlcAsync(
                        incomingChannelId, htlcId, preimage,
                        unitOfWork => StageInterceptedSettleAsync(unitOfWork, incomingChannelId, htlc, forward,
                                                                  cancellationToken),
                        cancellationToken);
                    _logger.LogInformation("Settled intercepted HTLC {HtlcId} of channel {ChannelId} with the "
                                         + "interceptor's preimage", htlcId, incomingChannelId);
                    return;

                case ForwardInterceptAction.Fail when resolution.ErrorPacket is { } packet:
                    await _channelOperations.FailHtlcAsync(incomingChannelId, htlcId,
                                                           _failureOnionService.WrapErrorPacket(forward.SharedSecret,
                                                               packet), cancellationToken);
                    LogFailedBack(incomingChannelId, htlc, "interceptor error packet");
                    return;

                case ForwardInterceptAction.Fail:
                    await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret,
                                        InterceptFailure(resolution.FailureCode, incomingChannelId, htlc), cancellationToken,
                                        introduction);
                    return;
            }
        }
        catch (CommitmentRefusedException e)
        {
            // Nothing was persisted: the hub must keep the HTLC held for retry and expiry protection
            _logger.LogWarning("Could not carry out the interceptor's {Action} of HTLC {HtlcId} of channel {ChannelId}: "
                             + "{Reason}", resolution.Action, htlcId, incomingChannelId, e.Message);
            throw;
        }
    }

    /// <summary>
    /// An interceptor's settle of a forward whose incoming channel is closing on chain (NL-1182): the preimage goes on
    /// the incoming HTLC's record (<see cref="HtlcRecord.KnownPreimage"/>) with the <c>InterceptedHtlcSettled</c> event
    /// in the same save, and the BOLT 5 resolvers claim the HTLC with it (<c>Onchain/Resolvers/InterceptorClaims</c>).
    /// Nothing is sent to the peer.
    /// </summary>
    private async Task SettleInterceptedOnchainAsync(ChannelId incomingChannelId, HtlcRecord htlc,
                                                     IncomingOnionForward forward,
                                                     ForwardInterceptResolution resolution,
                                                     CancellationToken cancellationToken)
    {
        if (resolution.Preimage is not { } preimage || !Hashes(preimage, htlc.PaymentHash))
        {
            _logger.LogWarning("Interceptor settle of on-chain HTLC {HtlcId} of channel {ChannelId} with a wrong "
                             + "preimage ignored", htlc.Id, incomingChannelId);
            return;
        }

        if (!await MarkPartAsync(incomingChannelId, htlc.Id, preimage,
                                 unitOfWork => StageInterceptedSettleAsync(unitOfWork, incomingChannelId, htlc, forward,
                                                                           cancellationToken),
                                 cancellationToken))
            return;

        _logger.LogInformation("Settled on-chain HTLC {HtlcId} of channel {ChannelId} with the interceptor's preimage; "
                             + "it is claimed on chain", htlc.Id, incomingChannelId);
    }

    /// <summary>
    /// Stages the <c>InterceptedHtlcSettled</c> accounting event of an interceptor's settle on the fulfill's unit of
    /// work (NL-1182). Never throws.
    /// </summary>
    private Task StageInterceptedSettleAsync(IUnitOfWork unitOfWork, ChannelId incomingChannelId, HtlcRecord htlc,
                                             IncomingOnionForward forward, CancellationToken cancellationToken) =>
        PaymentAccountingEvents.StageInterceptedHtlcSettledAsync(
            unitOfWork, incomingChannelId, htlc.Id, () =>
            {
                _channelMemoryRepository.TryGetChannel(incomingChannelId, out var incoming);
                return PaymentAccountingEvents.InterceptedHtlcSettled(
                    incomingChannelId, htlc.Id, htlc.PaymentHash, LightningMoneyOf(htlc), incoming,
                    forward.HasOutgoingShortChannelId ? forward.OutgoingShortChannelId : (ShortChannelId?)null,
                    forward.NextNodeId,
                    forward.AmountToForward, _timeProvider.GetUtcNow(), CurrentHeight);
            }, _logger, cancellationToken);

    /// <summary>The held forward as the interceptor sees it (LND's <c>InterceptedPacket</c>).</summary>
    private static InterceptedForward ToInterceptedForward(ChannelModel incomingChannel, HtlcRecord htlc,
                                                           IncomingOnionForward forward)
    {
        var incomingScid = incomingChannel.ShortChannelId != default
                        && incomingChannel.ChannelParams.UseScidAlias != Domain.Enums.FeatureSupport.Compulsory
                               ? incomingChannel.ShortChannelId
                               : incomingChannel.LocalAliases?.FirstOrDefault() ?? incomingChannel.ShortChannelId;
        return new InterceptedForward(
            incomingChannel.ChannelId, htlc.Id, incomingScid,
            forward.HasOutgoingShortChannelId ? forward.OutgoingShortChannelId : default, forward.NextNodeId,
            htlc.PaymentHash, LightningMoneyOf(htlc), forward.AmountToForward, htlc.CltvExpiry,
            forward.OutgoingCltvValue, 0, forward.NextPacket, forward.Payload.CustomRecords,
            WireCustomRecordCodec.Decode(htlc.WireCustomRecords));
    }

    /// <summary>The failure an interceptor's code stands for (LND: the three BADONION codes or
    /// <c>temporary_channel_failure</c> with the incoming channel's update).</summary>
    private FailureMessage InterceptFailure(FailureCode code, ChannelId incomingChannelId, HtlcRecord htlc)
    {
        var sha256 = Sha256Of(htlc.OnionRoutingPacket);
        return code switch
        {
            FailureCode.InvalidOnionHmac => FailureMessage.InvalidOnionHmac(sha256),
            FailureCode.InvalidOnionKey => FailureMessage.InvalidOnionKey(sha256),
            FailureCode.InvalidOnionVersion => FailureMessage.InvalidOnionVersion(sha256),
            _ => FailureMessage.TemporaryChannelFailure(
                     _channelMemoryRepository.TryGetChannel(incomingChannelId, out var channel)
                         ? UpdateFor(channel, ScidOrDefault(channel))
                         : [])
        };
    }

    private static bool Hashes(Secret preimage, Hash paymentHash) =>
        SHA256.HashData((byte[])preimage).AsSpan().SequenceEqual((byte[])paymentHash);

    private static ShortChannelId ScidOrDefault(ChannelModel channel) =>
        channel.ShortChannelId != default ? channel.ShortChannelId : channel.LocalAliases?.FirstOrDefault() ?? default;

    private static Domain.Money.LightningMoney LightningMoneyOf(HtlcRecord htlc) =>
        Domain.Money.LightningMoney.MilliSatoshis(htlc.AmountMsat);
}