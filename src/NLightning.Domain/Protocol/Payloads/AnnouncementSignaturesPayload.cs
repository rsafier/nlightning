using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Payloads;

using Channels.Constants;
using Channels.ValueObjects;
using Crypto.Constants;
using Crypto.ValueObjects;
using Interfaces;

/// <summary>
/// Represents the payload of the announcement_signatures message (BOLT 7, type 259).
/// </summary>
/// <remarks>
/// <para>
/// Wire layout: <c>channel_id(32) || short_channel_id(8) || node_signature(64) || bitcoin_signature(64)</c>, optionally
/// followed by a TLV extension (none is defined), kept verbatim in <see cref="ExtraData"/> so parse → serialize is
/// byte-identical. The message-type serializer reads that extension strictly (an unknown even type is rejected, BOLT 1).
/// </para>
/// <para>
/// Unlike the other gossip messages this one is channel-scoped (<see cref="IChannelMessagePayload"/>): it is exchanged
/// only with the channel peer and handled under the channel's lock. The two signatures sign the
/// <see cref="ChannelAnnouncementPayload.GetSignatureHash"/> of the channel's announcement with the sender's node key
/// and funding key.
/// </para>
/// </remarks>
public sealed class AnnouncementSignaturesPayload : IChannelMessagePayload
{
    /// <summary>
    /// The length of one signature field.
    /// </summary>
    public const int SignatureLength = CryptoConstants.MaxSignatureSize;

    /// <summary>
    /// The length of the known fields (the minimum length of the payload, without the message type).
    /// </summary>
    public const int MinLength = ChannelConstants.ChannelIdLength + ShortChannelId.Length + 2 * SignatureLength;

    private readonly byte[] _nodeSignature;
    private readonly byte[] _bitcoinSignature;
    private readonly byte[] _extraData;

    /// <inheritdoc />
    public ChannelId ChannelId { get; }

    /// <summary>
    /// The real short channel id of the funding output.
    /// </summary>
    public ShortChannelId ShortChannelId { get; }

    /// <summary>
    /// The sender's node-key signature of the channel_announcement hash. Every read returns a fresh copy.
    /// </summary>
    public CompactSignature NodeSignature => new(_nodeSignature.ToArray());

    /// <summary>
    /// The sender's funding-key signature of the channel_announcement hash. Every read returns a fresh copy.
    /// </summary>
    public CompactSignature BitcoinSignature => new(_bitcoinSignature.ToArray());

    /// <summary>
    /// Bytes after <c>bitcoin_signature</c> (a TLV extension), kept verbatim.
    /// </summary>
    public ReadOnlyMemory<byte> ExtraData => _extraData;

    /// <summary>
    /// Creates an <c>announcement_signatures</c> payload.
    /// </summary>
    /// <exception cref="ArgumentException">A signature is not 64 bytes.</exception>
    public AnnouncementSignaturesPayload(ChannelId channelId, ShortChannelId shortChannelId,
                                        CompactSignature nodeSignature, CompactSignature bitcoinSignature,
                                        ReadOnlyMemory<byte> extraData = default)
    {
        _nodeSignature = CopySignature(nodeSignature, nameof(nodeSignature));
        _bitcoinSignature = CopySignature(bitcoinSignature, nameof(bitcoinSignature));
        ChannelId = channelId;
        ShortChannelId = shortChannelId;
        _extraData = extraData.ToArray();
    }

    /// <summary>
    /// The wire bytes of the payload (without the message type), <see cref="ExtraData"/> included.
    /// </summary>
    public byte[] GetBytes()
    {
        var bytes = new byte[MinLength + _extraData.Length];
        var offset = 0;
        ((ReadOnlySpan<byte>)ChannelId).CopyTo(bytes.AsSpan(offset));
        offset += ChannelConstants.ChannelIdLength;
        ((ReadOnlySpan<byte>)ShortChannelId).CopyTo(bytes.AsSpan(offset));
        offset += ShortChannelId.Length;
        _nodeSignature.CopyTo(bytes, offset);
        offset += SignatureLength;
        _bitcoinSignature.CopyTo(bytes, offset);
        offset += SignatureLength;
        _extraData.CopyTo(bytes, offset);
        return bytes;
    }

    /// <summary>
    /// Parses an <c>announcement_signatures</c> payload (without the message type). Bytes after
    /// <c>bitcoin_signature</c> are kept as <see cref="ExtraData"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The payload is shorter than <see cref="MinLength"/>.</exception>
    public static AnnouncementSignaturesPayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MinLength)
            throw new ArgumentException(
                $"An announcement_signatures payload is at least {MinLength} bytes, got {payload.Length}.",
                nameof(payload));

        var offset = 0;
        var channelId = new ChannelId(payload.Slice(offset, ChannelConstants.ChannelIdLength));
        offset += ChannelConstants.ChannelIdLength;
        var shortChannelId = new ShortChannelId(payload.Slice(offset, ShortChannelId.Length).ToArray());
        offset += ShortChannelId.Length;
        var nodeSignature = new CompactSignature(payload.Slice(offset, SignatureLength).ToArray());
        offset += SignatureLength;
        var bitcoinSignature = new CompactSignature(payload.Slice(offset, SignatureLength).ToArray());
        offset += SignatureLength;

        return new AnnouncementSignaturesPayload(channelId, shortChannelId, nodeSignature, bitcoinSignature,
                                                 payload[offset..].ToArray());
    }

    /// <summary>
    /// Parses an <c>announcement_signatures</c> payload (without the message type).
    /// </summary>
    /// <returns><c>false</c> if the payload is shorter than <see cref="MinLength"/>.</returns>
    public static bool TryParse(ReadOnlySpan<byte> payload,
                                [NotNullWhen(true)] out AnnouncementSignaturesPayload? announcementSignatures)
    {
        announcementSignatures = payload.Length < MinLength ? null : Parse(payload);
        return announcementSignatures is not null;
    }

    private static byte[] CopySignature(CompactSignature signature, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(signature, parameterName);
        if (signature.Value.Length != SignatureLength)
            throw new ArgumentException($"An announcement_signatures signature is {SignatureLength} bytes.",
                                        parameterName);

        return signature.Value.ToArray();
    }
}