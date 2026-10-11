namespace NLightning.Domain.Protocol.Payloads;

using Bitcoin.ValueObjects;
using Channels.Constants;
using Channels.ValueObjects;
using Crypto.Constants;
using Crypto.ValueObjects;
using GossipV2;
using Interfaces;
using Tlvs = GossipV2.GossipV2Constants.AnnouncementSignatures2;

/// <summary>
/// The payload of <c>announcement_signatures_2</c> (taproot gossip, BOLTs PR #1059, type 260): a pure TLV stream of
/// <c>channel_id</c> 0, <c>short_channel_id</c> 2, <c>partial_signatures</c> 4 (the sender's two MuSig2 partial
/// signatures of the <c>channel_announcement_2</c> session, node key then funding key, 32 bytes each) and
/// <c>funding_txid</c> 6 (internal byte order). Every record is required.
/// </summary>
/// <remarks>
/// Channel-scoped (<see cref="IChannelMessagePayload"/>): exchanged only with the channel peer and handled under the
/// channel's lock, like v1's <c>announcement_signatures</c>. The records are kept as received.
/// </remarks>
public sealed class AnnouncementSignatures2Payload : IChannelMessagePayload
{
    /// <summary>The length of the <c>partial_signatures</c> record.</summary>
    public const int PartialSignaturesLength = 2 * MusigConstants.PartialSignatureLen;

    /// <summary>The record types the message defines (an unknown even type fails it).</summary>
    public static readonly IReadOnlySet<ulong> KnownTypes = new HashSet<ulong>
    {
        Tlvs.ChannelId, Tlvs.ShortChannelId, Tlvs.PartialSignatures, Tlvs.FundingTxId
    };

    private AnnouncementSignatures2Payload(PureTlvStream stream)
    {
        Stream = stream;
        ChannelId = new ChannelId(PureTlvFields.RequiredFixed(stream, Tlvs.ChannelId,
                                                              ChannelConstants.ChannelIdLength).Span);
        ShortChannelId = new ShortChannelId(PureTlvFields.RequiredFixed(stream, Tlvs.ShortChannelId,
                                                                        ShortChannelId.Length).ToArray());
        var signatures = PureTlvFields.RequiredFixed(stream, Tlvs.PartialSignatures, PartialSignaturesLength).Span;
        NodePartialSignature = new MusigPartialSignature(signatures[..MusigConstants.PartialSignatureLen].ToArray());
        BitcoinPartialSignature =
            new MusigPartialSignature(signatures[MusigConstants.PartialSignatureLen..].ToArray());
        FundingTxId = new TxId(PureTlvFields.RequiredFixed(stream, Tlvs.FundingTxId, CryptoConstants.Sha256HashLen)
                                            .ToArray());
    }

    /// <summary>The records as received or built, in wire order.</summary>
    public PureTlvStream Stream { get; }

    /// <inheritdoc />
    public ChannelId ChannelId { get; }

    /// <summary>The canonical scid of the funding output named by <see cref="FundingTxId"/>.</summary>
    public ShortChannelId ShortChannelId { get; }

    /// <summary>The sender's partial signature with its node key.</summary>
    public MusigPartialSignature NodePartialSignature { get; }

    /// <summary>The sender's partial signature with its funding (bitcoin) key.</summary>
    public MusigPartialSignature BitcoinPartialSignature { get; }

    /// <summary>The funding transaction the signatures are for (the open's or the last locked splice's).</summary>
    public TxId FundingTxId { get; }

    /// <summary>The wire bytes of the payload (without the message type).</summary>
    public byte[] GetBytes() => Stream.GetBytes();

    /// <summary>Builds the payload.</summary>
    public static AnnouncementSignatures2Payload Create(ChannelId channelId, ShortChannelId shortChannelId,
                                                        MusigPartialSignature nodePartialSignature,
                                                        MusigPartialSignature bitcoinPartialSignature,
                                                        TxId fundingTxId)
    {
        var signatures = new byte[PartialSignaturesLength];
        ((ReadOnlySpan<byte>)nodePartialSignature).CopyTo(signatures);
        ((ReadOnlySpan<byte>)bitcoinPartialSignature).CopyTo(signatures.AsSpan(MusigConstants.PartialSignatureLen));

        return new AnnouncementSignatures2Payload(new PureTlvStream([
            new PureTlvRecord(Tlvs.ChannelId, channelId),
            new PureTlvRecord(Tlvs.ShortChannelId, shortChannelId),
            new PureTlvRecord(Tlvs.PartialSignatures, signatures),
            new PureTlvRecord(Tlvs.FundingTxId, fundingTxId)
        ]));
    }

    /// <summary>Builds the payload from parsed records.</summary>
    /// <exception cref="FormatException">A required record is missing or malformed.</exception>
    public static AnnouncementSignatures2Payload FromStream(PureTlvStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new AnnouncementSignatures2Payload(stream);
    }

    /// <summary>Parses the payload (without the message type).</summary>
    /// <exception cref="FormatException">The stream is malformed or a record is missing.</exception>
    public static AnnouncementSignatures2Payload Parse(ReadOnlySpan<byte> payload) =>
        new(PureTlvStream.Parse(payload, KnownTypes));
}