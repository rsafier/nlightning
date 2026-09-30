namespace NLightning.Application.Onchain;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;

/// <summary>
/// What was told upstream about one of our offered HTLCs (<see cref="HtlcUpstreamOutcome"/>), read from the one-way
/// state the switch keeps: the incoming HTLC's removal (live in a loaded channel), the forward circuit, or our
/// payment. Nothing is stored here: a fail upstream is final, so the answer cannot change once it is not
/// <see cref="HtlcUpstreamOutcome.Unknown"/>.
/// </summary>
/// <remarks>
/// Mirrors what the resolvers compute per round to suppress a second event
/// (<c>RemoteCommitResolver.ComputeUpstreamResolvedAsync</c>), but names the outcome instead of only "resolved", so a
/// close that a reorg replaced can be resolved against what was already told upstream (NL-330). Deliberately not
/// shared with the resolvers: their per-round facts cache has different unknowns.
/// </remarks>
internal static class HtlcUpstreamOutcomeReader
{
    /// <summary>
    /// The outcome upstream of our offered HTLC <paramref name="outgoingHtlcId"/> on
    /// <paramref name="outgoingChannelId"/>, from the HTLC's stored origin.
    /// </summary>
    public static async Task<HtlcUpstreamOutcome> ReadAsync(IUnitOfWork unitOfWork, ChannelId outgoingChannelId,
                                                            ulong outgoingHtlcId)
    {
        var origin = await unitOfWork.ChannelStateDbRepository.GetHtlcOriginAsync(
                         outgoingChannelId, new HtlcKey(HtlcDirection.Outgoing, outgoingHtlcId));
        switch (origin)
        {
            case
            {
                Kind: HtlcOriginKind.Forwarded, IncomingChannelId: { } incomingChannelId,
                IncomingHtlcId: { } incomingHtlcId
            }:
                {
                    var incomingChannel = await unitOfWork.ChannelDbRepository.GetByIdAsync(incomingChannelId);
                    var incoming = incomingChannel?.Commitments?.GetHtlc(HtlcDirection.Incoming, incomingHtlcId);
                    if (incoming?.Removal is { } removal)
                        return removal.Kind == HtlcRemovalKind.Fulfill
                                   ? HtlcUpstreamOutcome.Fulfilled
                                   : HtlcUpstreamOutcome.Failed;

                    if (incoming is not null)
                        return HtlcUpstreamOutcome.Unknown;

                    // The upstream channel is not loaded (closed, or not in this process): its circuit decides
                    var circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(incomingChannelId,
                                      incomingHtlcId);
                    return circuit?.Status == ForwardCircuitStatus.Failed ? HtlcUpstreamOutcome.Failed
                         : circuit?.Status == ForwardCircuitStatus.Fulfilled ? HtlcUpstreamOutcome.Fulfilled
                         : HtlcUpstreamOutcome.Unknown;
                }

            case { Kind: HtlcOriginKind.Local, PaymentHash: { } paymentHash }:
                {
                    var payment = await unitOfWork.PaymentDbRepository.GetByPaymentHashAsync(paymentHash);
                    return payment?.Status == PaymentStatus.Succeeded ? HtlcUpstreamOutcome.Fulfilled
                         : payment is { Status: not PaymentStatus.InFlight } ? HtlcUpstreamOutcome.Failed
                         : HtlcUpstreamOutcome.Unknown;
                }

            default:
                // No origin stored (an HTLC offered before NL-250): what happened upstream cannot be told
                return HtlcUpstreamOutcome.Unknown;
        }
    }
}