using System.Security.Cryptography;

namespace NLightning.Application.OnionMessages;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Tlv;
using Payments.Routing;

/// <summary>
/// Creates blinded message paths (BOLT 4 "Route Blinding" writer, with the "Onion Messages" rules for the creator of
/// <c>encrypted_recipient_data</c>): every non-final hop names the next node, no hop carries <c>payment_relay</c> or
/// <c>payment_constraints</c>, and every <c>encrypted_data_tlv</c> is padded to the same length (SHOULD).
/// </summary>
/// <remarks>
/// Lane M6-B ships <c>BlindedMessagePathBuilder</c> in Infrastructure.Bitcoin (vector-proven); this factory builds on
/// M5's <see cref="IRouteBlindingService"/> directly so the service does not depend on it.
/// </remarks>
public sealed class MessagePathFactory
{
    private readonly IRouteBlindingService _routeBlindingService;

    public MessagePathFactory(IRouteBlindingService routeBlindingService)
    {
        _routeBlindingService = routeBlindingService;
    }

    /// <summary>
    /// A blinded path over <paramref name="nodeIds"/> (introduction node first, recipient last): each hop but the last
    /// gets <c>next_node_id</c> of the following node, the last gets <paramref name="finalPathId"/> as its
    /// <c>path_id</c> when given.
    /// </summary>
    /// <exception cref="ArgumentException">No node ids.</exception>
    public BlindedPath Create(IReadOnlyList<CompactPubKey> nodeIds, ReadOnlyMemory<byte>? finalPathId = null)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        if (nodeIds.Count == 0)
            throw new ArgumentException("A blinded path needs at least one node", nameof(nodeIds));

        var data = new BlindedRecipientData[nodeIds.Count];
        for (var i = 0; i < nodeIds.Count - 1; i++)
            data[i] = new BlindedRecipientData { NextNodeId = nodeIds[i + 1] };
        data[^1] = new BlindedRecipientData { PathId = finalPathId };
        return Create(nodeIds, data);
    }

    /// <summary>
    /// A blinded path over <paramref name="nodeIds"/> with the given plaintext records, padded to equal lengths.
    /// </summary>
    /// <exception cref="ArgumentException">The lists are empty or differ in length, or a hop carries
    /// <c>payment_relay</c> or <c>payment_constraints</c> (BOLT 4: MUST NOT for onion messages).</exception>
    public BlindedPath Create(IReadOnlyList<CompactPubKey> nodeIds, IReadOnlyList<BlindedRecipientData> data)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(data);
        if (nodeIds.Count == 0 || nodeIds.Count != data.Count)
            throw new ArgumentException("One recipient data per node is needed", nameof(data));
        if (data.Any(d => d.PaymentRelay is not null || d.PaymentConstraints is not null))
            throw new ArgumentException("Message paths carry no payment_relay or payment_constraints", nameof(data));

        var encoded = Pad(data);
        var sessionKey = PaymentOnionFactory.CreateSessionKey();
        try
        {
            return _routeBlindingService.CreateBlindedPath(nodeIds, encoded, new PrivKey(sessionKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    private List<byte[]> Pad(IReadOnlyList<BlindedRecipientData> data)
    {
        var unpadded = data.Select(d => _routeBlindingService.EncodeRecipientData(WithPadding(d, null))).ToList();
        // Every hop gets a padding record (possibly empty) so all end at the same length; a length that no padding
        // record fills exactly (its bigsize length grows at 253) moves the target up
        var target = unpadded.Max(e => e.Length) + 2;
        while (unpadded.Any(e => GetPaddingLength(target - e.Length) is null))
            target++;

        var padded = new List<byte[]>(data.Count);
        for (var i = 0; i < data.Count; i++)
        {
            var paddingLength = GetPaddingLength(target - unpadded[i].Length)!.Value;
            padded.Add(_routeBlindingService.EncodeRecipientData(WithPadding(data[i], new byte[paddingLength])));
        }

        return padded;
    }

    /// <summary>
    /// The padding value length whose record (type 1, bigsize length, value) is exactly <paramref name="recordLength"/>
    /// bytes, or null when there is none.
    /// </summary>
    private static int? GetPaddingLength(int recordLength)
    {
        for (var valueLength = Math.Max(recordLength - 10, 0); valueLength <= recordLength - 2; valueLength++)
            if (1 + BigSizeCodec.GetLength((ulong)valueLength) + valueLength == recordLength)
                return valueLength;
        return null;
    }

    private static BlindedRecipientData WithPadding(BlindedRecipientData data, byte[]? padding) => new()
    {
        Padding = padding,
        ShortChannelId = data.ShortChannelId,
        NextNodeId = data.NextNodeId,
        PathId = data.PathId,
        NextPathKeyOverride = data.NextPathKeyOverride,
        AllowedFeatures = data.AllowedFeatures,
        UnknownOddRecords = data.UnknownOddRecords
    };
}