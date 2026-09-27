namespace NLightning.Application.Tests.OnionMessages.Harness;

using Application.OnionMessages;
using Application.Payments.Routing;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Payloads;

/// <summary>
/// An <see cref="IOnionMessagePacketBuilder"/> over the real Sphinx and route blinding (BOLT 4 writer): the prefix is a
/// blinded path the sender makes itself whose last hop carries <c>next_path_key_override = first_path_key</c>, every
/// non-final hop carries only <c>encrypted_recipient_data</c>, and the payloads are 1300 bytes when they fit, else
/// 32768.
/// </summary>
/// <remarks>Lane M6-B ships the production builder; the harness uses this one so it does not depend on it.</remarks>
internal sealed class HarnessOnionMessagePacketBuilder(ISphinxService sphinxService,
                                                       IRouteBlindingService routeBlindingService)
    : IOnionMessagePacketBuilder
{
    private const int HmacLength = 32;

    public OnionMessageMessage Build(IReadOnlyList<CompactPubKey> prefixNodeIds, BlindedPath path,
                                     OnionMessageContents contents, WireBlindedPath? replyPath,
                                     PrivKey? sessionKey = null, PrivKey? prefixPathSessionKey = null)
    {
        var hops = new List<BlindedPathHop>();
        CompactPubKey firstPathKey;
        if (prefixNodeIds.Count > 0)
        {
            var data = new List<byte[]>();
            for (var i = 0; i < prefixNodeIds.Count; i++)
            {
                var last = i == prefixNodeIds.Count - 1;
                data.Add(routeBlindingService.EncodeRecipientData(new BlindedRecipientData
                {
                    NextNodeId = last ? path.FirstNodeId : prefixNodeIds[i + 1],
                    NextPathKeyOverride = last ? path.FirstPathKey : null
                }));
            }

            var prefixPath = routeBlindingService.CreateBlindedPath(
                prefixNodeIds, data, prefixPathSessionKey ?? new PrivKey(PaymentOnionFactory.CreateSessionKey()));
            hops.AddRange(prefixPath.Hops);
            firstPathKey = prefixPath.FirstPathKey;
        }
        else
        {
            firstPathKey = path.FirstPathKey;
        }

        hops.AddRange(path.Hops);
        var payloads = new List<byte[]>();
        for (var i = 0; i < hops.Count; i++)
        {
            var final = i == hops.Count - 1;
            payloads.Add(OnionMessagePayloadCodec.Encode(
                             new OnionMessageTlvs(final ? replyPath : null, hops[i].EncryptedRecipientData,
                                                  final ? contents.Records : [])));
        }

        return BuildFromPayloads(firstPathKey, hops.Select(h => h.BlindedNodeId).ToList(), payloads, sessionKey);
    }

    /// <summary>
    /// An onion message whose hops (the keys each one peels with) carry exactly <paramref name="payloads"/>, for the
    /// reader-rule tests.
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
        var packet = sphinxService.Construct(onionHops, sessionKey ?? new PrivKey(PaymentOnionFactory.CreateSessionKey()),
                                             [], length, OnionPacketKind.OnionMessage);
        return new OnionMessageMessage(new OnionMessagePayload(pathKey, packet.ToBytes()));
    }

    private static int BigSizeLength(int value) => value < 0xfd ? 1 : value <= ushort.MaxValue ? 3 : 5;
}