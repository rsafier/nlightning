using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Switch;

using Domain.Channels.Commitments;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Payments.Interception;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Onion;

public sealed partial class HtlcSwitch
{
    /// <summary>
    /// Offers a forward to the connected interceptor (NL-1183, LND's <c>InterceptableSwitch</c>). True when the HTLC
    /// is taken care of: held (its resolution comes through <see cref="ResolveInterceptedAsync"/>), or failed because it
    /// expires too soon to be held or too many forwards are held. False without an interceptor.
    /// </summary>
    private async Task<bool> TryInterceptAsync(ChannelId incomingChannelId, HtlcRecord htlc,
                                               IncomingOnionForward forward, uint height,
                                               CancellationToken cancellationToken)
    {
        if (_forwardInterceptor is not { IsActive: true } interceptor
         || !_channelMemoryRepository.TryGetChannel(incomingChannelId, out var incomingChannel))
            return false;

        var incomingScid = incomingChannel.ShortChannelId != default
                        && incomingChannel.ChannelParams.UseScidAlias != Domain.Enums.FeatureSupport.Compulsory
                               ? incomingChannel.ShortChannelId
                               : incomingChannel.LocalAliases?.FirstOrDefault() ?? incomingChannel.ShortChannelId;
        var intercepted = new InterceptedForward(
            incomingChannelId, htlc.Id, incomingScid,
            forward.HasOutgoingShortChannelId ? forward.OutgoingShortChannelId : default, forward.NextNodeId,
            htlc.PaymentHash, LightningMoneyOf(htlc), forward.AmountToForward, htlc.CltvExpiry,
            forward.OutgoingCltvValue, 0, forward.NextPacket, forward.Payload.CustomRecords);
        var introduction = forward.Blinded?.IsIntroduction ?? false;
        switch (interceptor.Intercept(intercepted, height,
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
            case ForwardInterceptOutcome.Full:
                await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret,
                                    FailureMessage.TemporaryChannelFailure(UpdateFor(incomingChannel, incomingScid)),
                                    cancellationToken, introduction);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Carries out an interceptor's resolution of a held forward, under the incoming HTLC's lock: nothing when the HTLC
    /// was resolved meanwhile; resume = the forward goes on (outgoing channel, policy, circuit, offer); fail = our
    /// failure, or the interceptor's error packet obfuscated like a downstream error; settle = the incoming HTLC is
    /// fulfilled with the interceptor's preimage (checked against the payment hash).
    /// </summary>
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

        var introduction = forward.Blinded?.IsIntroduction ?? false;
        try
        {
            switch (resolution.Action)
            {
                case ForwardInterceptAction.Resume:
                    await ForwardAsync(incomingChannelId, htlc, forward, firstHandling: false, cancellationToken,
                                       intercept: false);
                    return;

                case ForwardInterceptAction.Settle:
                    if (resolution.Preimage is not { } preimage
                     || !SHA256.HashData((byte[])preimage).AsSpan().SequenceEqual((byte[])htlc.PaymentHash))
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

    private static ShortChannelId ScidOrDefault(Domain.Channels.Models.ChannelModel channel) =>
        channel.ShortChannelId != default ? channel.ShortChannelId : channel.LocalAliases?.FirstOrDefault() ?? default;

    private static Domain.Money.LightningMoney LightningMoneyOf(HtlcRecord htlc) =>
        Domain.Money.LightningMoney.MilliSatoshis(htlc.AmountMsat);
}