namespace NLightning.Infrastructure.Bitcoin.Onion.OnionMessages;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;

/// <summary>
/// <see cref="IBlindedMessagePathBuilder"/> on M5's <see cref="IRouteBlindingService.CreateBlindedPath"/>.
/// </summary>
/// <remarks>
/// Equal-length padding: the target is the longest unpadded <c>encrypted_data_tlv</c>; a shorter hop gets a
/// <c>padding</c> record (type 1) of zeros whose encoded size fills the difference. A record costs at least 2 bytes
/// (type and BigSize length), and the BigSize length grows at 253 and 65536, so some differences (1, 255, 256, ...)
/// cannot be filled exactly: the target then grows by one until every hop can be padded. This reproduces
/// <c>blinded-onion-message-onion-test.json</c> (Bob unpadded, Carol 5 zero bytes, Dave an empty padding).
/// </remarks>
internal sealed class BlindedMessagePathBuilder : IBlindedMessagePathBuilder
{
    private readonly IRouteBlindingService _routeBlindingService;

    public BlindedMessagePathBuilder(IRouteBlindingService routeBlindingService)
    {
        _routeBlindingService = routeBlindingService;
    }

    /// <inheritdoc />
    public BlindedPath CreatePath(IReadOnlyList<CompactPubKey> nodeIds,
                                  IReadOnlyList<BlindedRecipientData> recipientData, PrivKey? sessionKey = null)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        if (nodeIds.Count == 0)
            throw new ArgumentException("A blinded path has at least one hop.", nameof(nodeIds));

        var encoded = EncodePaddedHops(recipientData);
        if (encoded.Count != nodeIds.Count)
            throw new ArgumentException("Every hop needs one encrypted_data_tlv.", nameof(recipientData));

        return _routeBlindingService.CreateBlindedPath(nodeIds, encoded,
                                                       sessionKey ?? OnionMessageSessionKeys.Create());
    }

    /// <inheritdoc />
    public BlindedPath CreateMessagePath(IReadOnlyList<CompactPubKey> nodeIds, ReadOnlyMemory<byte>? pathId = null,
                                         PrivKey? sessionKey = null)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        if (nodeIds.Count == 0)
            throw new ArgumentException("A blinded path has at least one hop.", nameof(nodeIds));

        var data = new List<BlindedRecipientData>(nodeIds.Count);
        for (var i = 0; i < nodeIds.Count - 1; i++)
            data.Add(new BlindedRecipientData { NextNodeId = nodeIds[i + 1] });
        data.Add(new BlindedRecipientData { PathId = pathId });

        return CreatePath(nodeIds, data, sessionKey);
    }

    /// <summary>
    /// Checks the writer rules and serializes every hop's <c>encrypted_data_tlv</c>, padded to the same length.
    /// </summary>
    /// <exception cref="ArgumentException">A writer rule is broken.</exception>
    internal IReadOnlyList<byte[]> EncodePaddedHops(IReadOnlyList<BlindedRecipientData> recipientData)
    {
        ArgumentNullException.ThrowIfNull(recipientData);
        if (recipientData.Count == 0)
            throw new ArgumentException("A blinded path has at least one hop.", nameof(recipientData));

        var unpadded = new List<BlindedRecipientData>(recipientData.Count);
        for (var i = 0; i < recipientData.Count; i++)
        {
            var data = recipientData[i] ?? throw new ArgumentException($"Hop {i} has no data.", nameof(recipientData));
            CheckWriterRules(data, i, i == recipientData.Count - 1);
            unpadded.Add(WithPadding(data, null));
        }

        var lengths = unpadded.Select(d => _routeBlindingService.EncodeRecipientData(d).Length).ToList();
        var target = lengths.Max();
        while (lengths.Any(length => GetPaddingValueLength(target - length) is null))
            target++;

        var encoded = new List<byte[]>(unpadded.Count);
        for (var i = 0; i < unpadded.Count; i++)
        {
            var paddingLength = GetPaddingValueLength(target - lengths[i])!.Value;
            var data = paddingLength < 0 ? unpadded[i] : WithPadding(unpadded[i], new byte[paddingLength]);
            encoded.Add(_routeBlindingService.EncodeRecipientData(data));
        }

        return encoded;
    }

    /// <summary>
    /// The padding value length whose record (type 1, BigSize length, value) takes exactly
    /// <paramref name="difference"/> bytes: -1 for no record (a difference of 0), null when none fits.
    /// </summary>
    internal static int? GetPaddingValueLength(int difference)
    {
        if (difference == 0)
            return -1;

        // type 1 is one byte; try each BigSize length size: 1 byte (< 253), 3 (< 65536), 5
        foreach (var lengthSize in new[] { 1, 3, 5 })
        {
            var valueLength = difference - 1 - lengthSize;
            if (valueLength >= 0 && SphinxBigSize.GetEncodedLength((ulong)valueLength) == lengthSize)
                return valueLength;
        }

        return null;
    }

    private static void CheckWriterRules(BlindedRecipientData data, int index, bool isFinal)
    {
        // BOLT 4 onion messages: the creator MUST NOT include payment_relay or payment_constraints
        if (data.PaymentRelay is not null || data.PaymentConstraints is not null)
            throw new ArgumentException(
                $"Hop {index}: an onion-message path carries no payment_relay or payment_constraints.",
                nameof(data));

        if (isFinal)
            return;

        // BOLT 4: MUST include either next_node_id or short_channel_id for each non-final node; a reader ignores a
        // non-final hop with a path_id
        if (data.NextNodeId is null && data.ShortChannelId is null)
            throw new ArgumentException($"Hop {index} is not final and names no next_node_id or short_channel_id.",
                                        nameof(data));
        if (data.PathId is not null)
            throw new ArgumentException($"Hop {index} is not final and carries a path_id.", nameof(data));
    }

    private static BlindedRecipientData WithPadding(BlindedRecipientData data, ReadOnlyMemory<byte>? padding) =>
        new()
        {
            Padding = padding,
            ShortChannelId = data.ShortChannelId,
            NextNodeId = data.NextNodeId,
            PathId = data.PathId,
            NextPathKeyOverride = data.NextPathKeyOverride,
            PaymentRelay = data.PaymentRelay,
            PaymentConstraints = data.PaymentConstraints,
            AllowedFeatures = data.AllowedFeatures,
            UnknownOddRecords = data.UnknownOddRecords
        };
}