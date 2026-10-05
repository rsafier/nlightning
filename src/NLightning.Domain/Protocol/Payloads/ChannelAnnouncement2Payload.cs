using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Payloads;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Constants;
using Crypto.Constants;
using Crypto.ValueObjects;
using GossipV2;
using Interfaces;
using ValueObjects;
using Tlvs = GossipV2.GossipV2Constants.ChannelAnnouncement2;

/// <summary>
/// The payload of <c>channel_announcement_2</c> (taproot gossip, BOLTs PR #1059, type 267): a pure TLV stream of
/// <c>chain_hash</c> 0, <c>features</c> 2, <c>short_channel_id</c> 4, <c>capacity_satoshis</c> 6 (tu64),
/// <c>node_id_1</c> 8, <c>node_id_2</c> 10, the optional <c>bitcoin_key_1</c> 12, <c>bitcoin_key_2</c> 14 and
/// <c>merkle_root_hash</c> 16, <c>outpoint</c> 18 (txid in internal byte order || u16 index), and the BIP 340
/// <c>signature</c> 240 of the 2- or 4-key MuSig2 aggregate over <see cref="GetSignatureHash"/>.
/// </summary>
/// <remarks>
/// The payload keeps the records exactly as received (<see cref="Stream"/>), unknown odd ones included, so a stored
/// or relayed announcement re-serializes byte for byte and its signature stays valid. Missing required records
/// (scid, outpoint, capacity, node ids, signature) or a malformed known record fail the parse
/// (<see cref="FormatException"/>), which the wire layer reports as a malformed message (warning and close; the
/// draft's "SHOULD send a warning, MAY close the connection, MUST ignore"). Chain, funding and signature checks are the
/// gossip validator's.
/// </remarks>
public sealed class ChannelAnnouncement2Payload : IMessagePayload
{
    /// <summary>The length of the <c>outpoint</c> record (txid || u16 index).</summary>
    public const int OutpointLength = CryptoConstants.Sha256HashLen + sizeof(ushort);

    /// <summary>The record types the message defines (an unknown even type fails it).</summary>
    public static readonly IReadOnlySet<ulong> KnownTypes = new HashSet<ulong>
    {
        Tlvs.ChainHash, Tlvs.Features, Tlvs.ShortChannelId, Tlvs.Capacity, Tlvs.NodeId1, Tlvs.NodeId2,
        Tlvs.BitcoinKey1, Tlvs.BitcoinKey2, Tlvs.MerkleRootHash, Tlvs.Outpoint, Tlvs.Signature
    };

    private readonly byte[]? _features;
    private readonly byte[]? _merkleRootHash;

    private ChannelAnnouncement2Payload(PureTlvStream stream)
    {
        Stream = stream;
        var chainHash = PureTlvFields.Fixed(stream, Tlvs.ChainHash, CryptoConstants.Sha256HashLen);
        HasChainHash = chainHash is not null;
        ChainHash = chainHash is { } c ? new ChainHash(c.Span) : ChainConstants.Main;
        _features = stream.Get(Tlvs.Features)?.ToArray();
        ShortChannelId = new ShortChannelId(PureTlvFields.RequiredFixed(stream, Tlvs.ShortChannelId,
                                                                        ShortChannelId.Length).ToArray());
        CapacitySatoshis = PureTlvFields.Tu64(stream, Tlvs.Capacity) ?? throw PureTlvFields.Missing(Tlvs.Capacity);
        NodeId1 = PureTlvFields.RequiredPoint(stream, Tlvs.NodeId1);
        NodeId2 = PureTlvFields.RequiredPoint(stream, Tlvs.NodeId2);
        BitcoinKey1 = PureTlvFields.Point(stream, Tlvs.BitcoinKey1);
        BitcoinKey2 = PureTlvFields.Point(stream, Tlvs.BitcoinKey2);
        _merkleRootHash = PureTlvFields.Fixed(stream, Tlvs.MerkleRootHash, CryptoConstants.Sha256HashLen)?.ToArray();
        var outpoint = PureTlvFields.RequiredFixed(stream, Tlvs.Outpoint, OutpointLength).Span;
        FundingTxId = new TxId(outpoint[..CryptoConstants.Sha256HashLen].ToArray());
        FundingOutputIndex = BinaryPrimitives.ReadUInt16BigEndian(outpoint[CryptoConstants.Sha256HashLen..]);
        Signature = new CompactSignature(PureTlvFields.RequiredFixed(stream, Tlvs.Signature,
                                                                     GossipV2Constants.SignatureLength).ToArray());
    }

    /// <summary>The records as received or built, in wire order.</summary>
    public PureTlvStream Stream { get; }

    /// <summary>The chain (bitcoin mainnet when the record is absent, the draft's default).</summary>
    public ChainHash ChainHash { get; }

    /// <summary>Whether the <c>chain_hash</c> record is present (the signature covers the records as sent).</summary>
    public bool HasChainHash { get; }

    /// <summary>The raw <c>features</c> bit vector, or null when the record is absent.</summary>
    public ReadOnlyMemory<byte>? Features => _features is null ? null : (ReadOnlyMemory<byte>?)_features;

    /// <summary>The real short channel id of the funding output.</summary>
    public ShortChannelId ShortChannelId { get; }

    /// <summary>The announced capacity in satoshis (at most the funding output's value).</summary>
    public ulong CapacitySatoshis { get; }

    /// <summary>The lexicographically lesser node id.</summary>
    public CompactPubKey NodeId1 { get; }

    /// <summary>The greater node id.</summary>
    public CompactPubKey NodeId2 { get; }

    /// <summary>node_1's funding key, when the 4-key proof is used.</summary>
    public CompactPubKey? BitcoinKey1 { get; }

    /// <summary>node_2's funding key, when the 4-key proof is used.</summary>
    public CompactPubKey? BitcoinKey2 { get; }

    /// <summary>The taproot tweak's merkle root, when present.</summary>
    public ReadOnlyMemory<byte>? MerkleRootHash => _merkleRootHash is null ? null : (ReadOnlyMemory<byte>?)_merkleRootHash;

    /// <summary>The funding transaction's id (internal byte order).</summary>
    public TxId FundingTxId { get; }

    /// <summary>The funding output's index.</summary>
    public ushort FundingOutputIndex { get; }

    /// <summary>The BIP 340 signature (64 zero bytes on an announcement not signed yet).</summary>
    public CompactSignature Signature { get; }

    /// <summary>The bytes the signature covers (the signed range of <see cref="Stream"/>).</summary>
    public byte[] GetSignedData() => Stream.GetSignedBytes();

    /// <summary>
    /// The MuSig2 session message: <c>MsgHash("channel_announcement_2", "signature", m)</c>.
    /// </summary>
    public Hash GetSignatureHash() =>
        GossipV2MsgHash.ComputeSignatureHash(GossipV2Constants.ChannelAnnouncement2Name, Stream);

    /// <summary>The wire bytes of the payload (without the message type).</summary>
    public byte[] GetBytes() => Stream.GetBytes();

    /// <summary>Returns a copy with <paramref name="signature"/> in the signature record; the rest keeps its bytes.</summary>
    public ChannelAnnouncement2Payload WithSignature(CompactSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Value.Length != GossipV2Constants.SignatureLength)
            throw new ArgumentException($"A BIP 340 signature is {GossipV2Constants.SignatureLength} bytes.",
                                        nameof(signature));

        return new ChannelAnnouncement2Payload(Stream.With(new PureTlvRecord(Tlvs.Signature, signature.Value)));
    }

    /// <summary>
    /// Builds an announcement (our own) with an all-zero signature, to be signed with <see cref="WithSignature"/>.
    /// <c>chain_hash</c> is written only off mainnet (the draft: "Otherwise, the field should not be set").
    /// </summary>
    public static ChannelAnnouncement2Payload Create(ChainHash chainHash, ReadOnlySpan<byte> features,
                                                     ShortChannelId shortChannelId, ulong capacitySatoshis,
                                                     CompactPubKey nodeId1, CompactPubKey nodeId2,
                                                     CompactPubKey? bitcoinKey1, CompactPubKey? bitcoinKey2,
                                                     ReadOnlySpan<byte> merkleRootHash, TxId fundingTxId,
                                                     ushort fundingOutputIndex)
    {
        if (((ReadOnlySpan<byte>)nodeId1).SequenceCompareTo(nodeId2) >= 0)
            throw new ArgumentException("node_id_1 must be lexicographically less than node_id_2.", nameof(nodeId1));
        if (bitcoinKey1.HasValue != bitcoinKey2.HasValue)
            throw new ArgumentException("Both bitcoin keys or none.", nameof(bitcoinKey1));
        if (!merkleRootHash.IsEmpty && merkleRootHash.Length != CryptoConstants.Sha256HashLen)
            throw new ArgumentException("The merkle root is 32 bytes.", nameof(merkleRootHash));

        var records = new List<PureTlvRecord>();
        if (chainHash != ChainConstants.Main)
            records.Add(new PureTlvRecord(Tlvs.ChainHash, chainHash.Value));
        if (!features.IsEmpty)
            records.Add(new PureTlvRecord(Tlvs.Features, features));
        records.Add(new PureTlvRecord(Tlvs.ShortChannelId, shortChannelId));
        records.Add(PureTlvFields.Tu64Record(Tlvs.Capacity, capacitySatoshis));
        records.Add(new PureTlvRecord(Tlvs.NodeId1, nodeId1));
        records.Add(new PureTlvRecord(Tlvs.NodeId2, nodeId2));
        if (bitcoinKey1 is { } key1 && bitcoinKey2 is { } key2)
        {
            records.Add(new PureTlvRecord(Tlvs.BitcoinKey1, key1));
            records.Add(new PureTlvRecord(Tlvs.BitcoinKey2, key2));
        }

        if (!merkleRootHash.IsEmpty)
            records.Add(new PureTlvRecord(Tlvs.MerkleRootHash, merkleRootHash));

        var outpoint = new byte[OutpointLength];
        ((ReadOnlySpan<byte>)fundingTxId).CopyTo(outpoint);
        BinaryPrimitives.WriteUInt16BigEndian(outpoint.AsSpan(CryptoConstants.Sha256HashLen), fundingOutputIndex);
        records.Add(new PureTlvRecord(Tlvs.Outpoint, outpoint));
        records.Add(new PureTlvRecord(Tlvs.Signature, new byte[GossipV2Constants.SignatureLength]));

        return new ChannelAnnouncement2Payload(new PureTlvStream(records));
    }

    /// <summary>Builds the payload from parsed records (the wire definition and the graph's stored bytes).</summary>
    /// <exception cref="FormatException">A required record is missing or a known one is malformed.</exception>
    public static ChannelAnnouncement2Payload FromStream(PureTlvStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ChannelAnnouncement2Payload(stream);
    }

    /// <summary>Parses the payload (without the message type).</summary>
    /// <exception cref="FormatException">The stream or a known record is malformed, or a required record is missing.</exception>
    public static ChannelAnnouncement2Payload Parse(ReadOnlySpan<byte> payload) =>
        new(PureTlvStream.Parse(payload, KnownTypes));

    /// <summary>
    /// Parses the payload, or returns false when it is malformed (stored bytes read back by the graph, NL-878).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out ChannelAnnouncement2Payload? result)
    {
        try
        {
            result = Parse(payload);
            return true;
        }
        catch (FormatException)
        {
            result = null;
            return false;
        }
    }
}