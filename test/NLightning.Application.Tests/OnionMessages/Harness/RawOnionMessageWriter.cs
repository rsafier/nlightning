namespace NLightning.Application.Tests.OnionMessages.Harness;

using Application.Payments.Routing;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.Payloads;

/// <summary>
/// Writes onion messages and blinded paths that the production writers refuse (a non-final hop with a
/// <c>path_id</c> or without a next hop, hand-made hop payloads), so the reader-rule tests can send them.
/// </summary>
/// <remarks>
/// Test-only: every well-formed message and path of the harness comes from the production
/// <c>OnionMessagePacketBuilder</c> and <c>BlindedMessagePathBuilder</c> (NL-442).
/// </remarks>
internal sealed class RawOnionMessageWriter(ISphinxService sphinxService, IRouteBlindingService routeBlindingService)
{
    private const int HmacLength = 32;

    /// <summary>
    /// A blinded path over <paramref name="nodeIds"/> with exactly <paramref name="data"/> (no writer rule checked,
    /// no padding).
    /// </summary>
    public BlindedPath CreatePath(IReadOnlyList<CompactPubKey> nodeIds, IReadOnlyList<BlindedRecipientData> data) =>
        routeBlindingService.CreateBlindedPath(nodeIds,
                                               data.Select(routeBlindingService.EncodeRecipientData).ToList(),
                                               new PrivKey(PaymentOnionFactory.CreateSessionKey()));

    /// <summary>
    /// An onion message whose hops (the keys each one peels with) carry exactly <paramref name="payloads"/>.
    /// </summary>
    public OnionMessageMessage BuildFromPayloads(CompactPubKey pathKey, IReadOnlyList<CompactPubKey> hopKeys,
                                                 IReadOnlyList<byte[]> payloads, PrivKey? sessionKey = null)
    {
        var total = payloads.Sum(p => BigSizeLength(p.Length) + p.Length + HmacLength);
        var length = total <= OnionMessageConstants.SmallPayloadsLength
                         ? OnionMessageConstants.SmallPayloadsLength
                         : total <= OnionMessageConstants.LargePayloadsLength
                             ? OnionMessageConstants.LargePayloadsLength
                             : throw new ArgumentException("The hops do not fit 32768 bytes of payloads");

        var onionHops = hopKeys.Select((key, i) => new OnionHop(key, payloads[i])).ToList();
        var packet = sphinxService.Construct(onionHops,
                                             sessionKey ?? new PrivKey(PaymentOnionFactory.CreateSessionKey()), [],
                                             length, OnionPacketKind.OnionMessage);
        return new OnionMessageMessage(new OnionMessagePayload(pathKey, packet.ToBytes()));
    }

    private static int BigSizeLength(int value) => value < 0xfd ? 1 : value <= ushort.MaxValue ? 3 : 5;
}