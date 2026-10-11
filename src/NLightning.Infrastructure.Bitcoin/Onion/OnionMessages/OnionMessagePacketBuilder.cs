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
/// <see cref="IBlindedMessagePathBuilder"/>, with the hop payloads written by the Domain
/// <see cref="OnionMessageTlvsCodec"/>.
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
                hops.Add(new OnionHop(hop.BlindedNodeId, EncodeNonFinal(hop.EncryptedRecipientData)));
        }

        for (var i = 0; i < path.Hops.Count; i++)
        {
            var hop = path.Hops[i];
            var payload = i < path.Hops.Count - 1
                              ? EncodeNonFinal(hop.EncryptedRecipientData)
                              : EncodeFinal(hop.EncryptedRecipientData, replyPath, contents);
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
        var total = hops.Sum(h => GetFramedLength(h.Payload.Length));
        return total switch
        {
            <= OnionMessageConstants.SmallPayloadsLength => OnionMessageConstants.SmallPayloadsLength,
            <= OnionMessageConstants.LargePayloadsLength => OnionMessageConstants.LargePayloadsLength,
            _ => throw new ArgumentException(
                     $"The onion message needs {total} bytes of payloads, more than "
                   + $"{OnionMessageConstants.LargePayloadsLength}.", nameof(hops))
        };
    }

    /// <summary>
    /// The <c>onionmsg_tlv</c> of a non-final hop: only <c>encrypted_recipient_data</c> (BOLT 4 writer).
    /// </summary>
    internal static byte[] EncodeNonFinal(ReadOnlyMemory<byte> encryptedRecipientData) =>
        OnionMessageTlvsCodec.Encode(new OnionMessageTlvs(null, encryptedRecipientData, []));

    /// <summary>
    /// The <c>onionmsg_tlv</c> of the final hop: <c>reply_path</c> (if any), <c>encrypted_recipient_data</c> and the
    /// contents, in ascending type order (Domain <see cref="OnionMessageTlvsCodec"/>, the node's one codec, NL-442).
    /// </summary>
    /// <exception cref="ArgumentException">The contents carry type 2 or 4, an even type other than 64, 66 and 68, or
    /// a type twice.</exception>
    internal static byte[] EncodeFinal(ReadOnlyMemory<byte> encryptedRecipientData, WireBlindedPath? replyPath,
                                       OnionMessageContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        foreach (var record in contents.Records)
        {
            ArgumentNullException.ThrowIfNull(record, nameof(contents));

            // Every BOLT 4 reader ignores a message with an unknown even type: only the BOLT 12 fields are known
            if (record.Type % 2 == 0 && record.Type is not (OnionMessageConstants.ReplyPathType
                                                           or OnionMessageConstants.EncryptedRecipientDataType
                                                           or OnionMessageConstants.InvoiceRequestType
                                                           or OnionMessageConstants.InvoiceType
                                                           or OnionMessageConstants.InvoiceErrorType))
                throw new ArgumentException(
                    $"onionmsg_tlv type {record.Type} is an unknown even type every reader ignores.",
                    nameof(contents));
        }

        // The codec refuses types 2 and 4 in the contents and a type twice
        return OnionMessageTlvsCodec.Encode(new OnionMessageTlvs(replyPath, encryptedRecipientData,
                                                                 contents.Records));
    }

    /// <summary>
    /// The size of one hop in <c>onionmsg_payloads</c>: its BigSize length, the payload and the 32-byte HMAC.
    /// </summary>
    internal static int GetFramedLength(int payloadLength) =>
        SphinxBigSize.GetEncodedLength((ulong)payloadLength) + payloadLength + 32;

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