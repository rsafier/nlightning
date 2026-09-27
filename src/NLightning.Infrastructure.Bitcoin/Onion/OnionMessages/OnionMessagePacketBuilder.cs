namespace NLightning.Infrastructure.Bitcoin.Onion.OnionMessages;

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
/// <see cref="IOnionMessagePacketBuilder"/>: the BOLT 4 onion-message writer over <see cref="ISphinxService"/> and
/// <see cref="IBlindedMessagePathBuilder"/>.
/// </summary>
/// <remarks>
/// <para>The prefix hops (up to, not including, the introduction node) become a blinded path of the sender's own,
/// whose last hop carries <c>next_node_id</c> = the introduction node and <c>next_path_key_override</c> =
/// <c>first_path_key</c>, so every Sphinx hop is a blinded node id and the first <c>path_key</c> is the prefix's
/// (or, with no prefix, the destination path's <c>first_path_key</c>).</para>
/// <para>Non-final hops carry only <c>encrypted_recipient_data</c>; the final hop carries it with the
/// <c>reply_path</c> and the contents. The Sphinx packet uses empty associated data and 1300 bytes of
/// <c>onionmsg_payloads</c> when the hops fit, else 32768.</para>
/// </remarks>
internal sealed class OnionMessagePacketBuilder : IOnionMessagePacketBuilder
{
    private readonly ISphinxService _sphinxService;
    private readonly IBlindedMessagePathBuilder _pathBuilder;

    public OnionMessagePacketBuilder(ISphinxService sphinxService, IBlindedMessagePathBuilder pathBuilder)
    {
        _sphinxService = sphinxService;
        _pathBuilder = pathBuilder;
    }

    /// <inheritdoc />
    public OnionMessageMessage Build(IReadOnlyList<CompactPubKey> prefixNodeIds, BlindedPath path,
                                     OnionMessageContents contents, WireBlindedPath? replyPath,
                                     PrivKey? sessionKey = null, PrivKey? prefixPathSessionKey = null)
    {
        ArgumentNullException.ThrowIfNull(prefixNodeIds);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(contents);
        if (path.Hops.Count == 0)
            throw new ArgumentException("The destination path has no hop.", nameof(path));

        var hops = new List<OnionHop>(prefixNodeIds.Count + path.Hops.Count);
        var firstPathKey = path.FirstPathKey;
        if (prefixNodeIds.Count > 0)
        {
            var prefixPath = CreatePrefixPath(prefixNodeIds, path, prefixPathSessionKey);
            firstPathKey = prefixPath.FirstPathKey;
            foreach (var hop in prefixPath.Hops)
                hops.Add(new OnionHop(hop.BlindedNodeId,
                                      OnionMessagePayloadCodec.EncodeNonFinal(hop.EncryptedRecipientData)));
        }

        for (var i = 0; i < path.Hops.Count; i++)
        {
            var hop = path.Hops[i];
            var payload = i < path.Hops.Count - 1
                              ? OnionMessagePayloadCodec.EncodeNonFinal(hop.EncryptedRecipientData)
                              : OnionMessagePayloadCodec.EncodeFinal(hop.EncryptedRecipientData, replyPath, contents);
            hops.Add(new OnionHop(hop.BlindedNodeId, payload));
        }

        var payloadsLength = ChoosePayloadsLength(hops);
        var packet = _sphinxService.Construct(hops, sessionKey ?? OnionMessageSessionKeys.Create(),
                                              ReadOnlySpan<byte>.Empty, payloadsLength,
                                              OnionPacketKind.OnionMessage);

        return new OnionMessageMessage(new OnionMessagePayload(firstPathKey, packet.ToBytes()));
    }

    /// <summary>
    /// 1300 when every framed hop fits, else 32768 (BOLT 4 writer SHOULD).
    /// </summary>
    /// <exception cref="ArgumentException">The hops need more than 32768 bytes.</exception>
    internal static int ChoosePayloadsLength(IReadOnlyList<OnionHop> hops)
    {
        var total = hops.Sum(h => OnionMessagePayloadCodec.GetFramedLength(h.Payload.Length));
        return total switch
        {
            <= OnionMessageConstants.SmallPayloadsLength => OnionMessageConstants.SmallPayloadsLength,
            <= OnionMessageConstants.LargePayloadsLength => OnionMessageConstants.LargePayloadsLength,
            _ => throw new ArgumentException(
                     $"The onion message needs {total} bytes of payloads, more than "
                   + $"{OnionMessageConstants.LargePayloadsLength}.", nameof(hops))
        };
    }

    private BlindedPath CreatePrefixPath(IReadOnlyList<CompactPubKey> prefixNodeIds, BlindedPath path,
                                         PrivKey? prefixPathSessionKey)
    {
        // BOLT 4 writer (sending to a blinded path): unblinded hops up to the introduction node, the last with
        // next_path_key_override = first_path_key so the introduction node gets the path's own path_key
        var data = new List<BlindedRecipientData>(prefixNodeIds.Count);
        for (var i = 0; i < prefixNodeIds.Count - 1; i++)
            data.Add(new BlindedRecipientData { NextNodeId = prefixNodeIds[i + 1] });
        data.Add(new BlindedRecipientData { NextNodeId = path.FirstNodeId, NextPathKeyOverride = path.FirstPathKey });

        return _pathBuilder.CreatePath(prefixNodeIds, data, prefixPathSessionKey);
    }
}