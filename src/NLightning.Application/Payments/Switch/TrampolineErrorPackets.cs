namespace NLightning.Application.Payments.Switch;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;

/// <summary>
/// The <c>update_fail_htlc</c> reason of an HTLC that reached us as a trampoline node (BOLTs PR 836 "Returning
/// Errors", NL-875): obfuscated with the trampoline layer's keys first, then with the outer layer's, so it travels
/// upstream like any failure and only the origin reads it. <c>attribution_data</c>, when added, covers the outer layer
/// only (PR 836 defines none for the trampoline layer).
/// </summary>
/// <remarks>
/// The attributed forms produce the same reason bytes as
/// <see cref="ITrampolineFailureOnionService.CreateTrampolineErrorPacket"/> and
/// <see cref="ITrampolineFailureOnionService.WrapTrampolineErrorPacket"/>: the trampoline layer is built with
/// <see cref="IFailureOnionService"/>, then <see cref="IAttributionDataService.WrapErrorPacket"/> obfuscates it with
/// the outer secret and initializes the attribution data as the erring node of the outer route (from an empty
/// downstream block, which is what <see cref="IAttributionDataService.CreateErrorPacket"/> computes for a packet it
/// creates itself). Used by <see cref="HtlcSwitch"/> for the final trampoline hop and meant for the relay engine.
/// </remarks>
public static class TrampolineErrorPackets
{
    /// <summary>
    /// A failure we originate as a trampoline node, without <c>attribution_data</c>.
    /// </summary>
    public static byte[] Create(ITrampolineFailureOnionService trampolineFailureOnionService,
                                Secret trampolineSharedSecret, Secret outerSharedSecret, FailureMessage failure)
    {
        ArgumentNullException.ThrowIfNull(trampolineFailureOnionService);
        return trampolineFailureOnionService.CreateTrampolineErrorPacket(trampolineSharedSecret, outerSharedSecret,
                                                                         failure);
    }

    /// <summary>
    /// A failure we originate as a trampoline node, with <c>attribution_data</c> for the outer layer and our hold time.
    /// </summary>
    public static AttributedErrorPacket CreateAttributed(IFailureOnionService failureOnionService,
                                                         IAttributionDataService attributionDataService,
                                                         Secret trampolineSharedSecret, Secret outerSharedSecret,
                                                         FailureMessage failure, uint holdTime)
    {
        ArgumentNullException.ThrowIfNull(failureOnionService);
        ArgumentNullException.ThrowIfNull(attributionDataService);

        var trampolineLayer = failureOnionService.CreateErrorPacket(trampolineSharedSecret, failure);
        return attributionDataService.WrapErrorPacket(outerSharedSecret, trampolineLayer, ReadOnlySpan<byte>.Empty,
                                                      holdTime);
    }

    /// <summary>
    /// An intermediate trampoline node re-wraps the failure the next trampoline node encrypted for the origin
    /// (<see cref="TrampolineDownstreamFailure.UnwrappedPacket"/>), with <c>attribution_data</c> for the outer layer: the
    /// downstream route's attribution belongs to our own payment and is not relayed.
    /// </summary>
    public static AttributedErrorPacket WrapAttributed(IFailureOnionService failureOnionService,
                                                       IAttributionDataService attributionDataService,
                                                       Secret trampolineSharedSecret, Secret outerSharedSecret,
                                                       ReadOnlySpan<byte> unwrappedPacket, uint holdTime)
    {
        ArgumentNullException.ThrowIfNull(failureOnionService);
        ArgumentNullException.ThrowIfNull(attributionDataService);

        var trampolineLayer = failureOnionService.WrapErrorPacket(trampolineSharedSecret, unwrappedPacket);
        return attributionDataService.WrapErrorPacket(outerSharedSecret, trampolineLayer, ReadOnlySpan<byte>.Empty,
                                                      holdTime);
    }
}