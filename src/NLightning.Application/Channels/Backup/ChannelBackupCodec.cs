using System.Buffers.Binary;
using System.Text;

namespace NLightning.Application.Channels.Backup;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Protocol.ValueObjects;
using Models;

/// <summary>
/// The plaintext of a static channel backup (version 1), all integers big-endian:
/// <code>
/// u8 version (1) | 32 chain_hash | 33 node_id | u64 created_at (unix seconds) | u16 channel_count
/// channel_count x (u16 record_length | record)
/// record: 32 channel_id | 33 remote_node_id | 32 funding_txid | u16 funding_output_index | u64 capacity_sat
///         | u32 funding_height | u64 short_channel_id (0: none) | u8 flags | u8 channel_version | u8 use_scid_alias
///         | u32 minimum_depth | u16 channel_type_length | channel_type | u32 key_index | 33 local_funding_pubkey
///         | 33 local_payment_basepoint | 33 remote_funding_pubkey | 33 remote_revocation_basepoint
///         | 33 remote_payment_basepoint | 33 remote_delayed_payment_basepoint | 33 remote_htlc_basepoint
///         | party local | party remote | u8 address_count | address_count x address
/// party:   u64 dust_limit_sat | u64 channel_reserve_sat | u64 htlc_minimum_msat | u16 max_accepted_htlcs
///         | u64 max_htlc_value_in_flight_msat | u16 to_self_delay
/// address: u8 type_length | type (UTF-8) | u8 host_length | host (UTF-8) | u16 port
/// flags:   bit 0 initiator, bit 1 option_anchors, bit 2 announced, bit 3 inferred params
/// </code>
/// A reader skips bytes it does not know at the end of a record (added fields of a later minor revision) and ignores
/// unknown flag bits; anything else that does not fit is refused.
/// <para>
/// Splicing (plan SP2-0, lane SP2-E; NL-478): the funding fields of a record are the channel's <b>current</b> funding
/// (its outpoint, capacity and both funding keys), and a minor revision appends, after the addresses,
/// <c>u32 local_funding_key_index | u8 pending_count | pending_count x (32 txid | u16 output_index | u64 capacity_sat
/// | u32 local_funding_key_index | 33 local_funding_pubkey | 33 remote_funding_pubkey)</c>
/// (<see cref="ChannelBackupEntry.LocalFundingKeyIndex"/>, <see cref="ChannelBackupEntry.PendingFundings"/>), so the
/// version stays 1: an older reader skips them, and its key check refuses a spliced channel's rotated funding key
/// rather than restoring it with the wrong one.
/// </para>
/// </summary>
public static class ChannelBackupCodec
{
    /// <summary>The plaintext version this codec writes and reads.</summary>
    public const byte Version = 1;

    private const byte FlagInitiator = 1;
    private const byte FlagAnchors = 2;
    private const byte FlagAnnounced = 4;
    private const byte FlagInferredParams = 8;

    private const int PubKeyLength = CryptoConstants.CompactPubkeyLen;
    private const int HashLength = CryptoConstants.Sha256HashLen;

    /// <summary>Encodes <paramref name="snapshot"/>.</summary>
    /// <exception cref="ArgumentException">A value does not fit its field (more than 65535 channels, an address
    /// longer than 255 bytes, ...).</exception>
    public static byte[] Encode(ChannelBackupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Channels.Count > ushort.MaxValue)
            throw new ArgumentException($"A backup holds at most {ushort.MaxValue} channels.", nameof(snapshot));

        var writer = new Writer();
        writer.Byte(Version);
        writer.Bytes(snapshot.ChainHash, HashLength);
        writer.Bytes(snapshot.NodeId, PubKeyLength);
        writer.U64((ulong)Math.Max(0, snapshot.CreatedAt.ToUnixTimeSeconds()));
        writer.U16((ushort)snapshot.Channels.Count);
        foreach (var channel in snapshot.Channels)
        {
            var record = EncodeRecord(channel);
            if (record.Length > ushort.MaxValue)
                throw new ArgumentException($"The record of channel {channel.ChannelId} is too long.",
                                            nameof(snapshot));

            writer.U16((ushort)record.Length);
            writer.Bytes(record, record.Length);
        }

        return writer.ToArray();
    }

    /// <summary>Decodes a plaintext written by <see cref="Encode"/>.</summary>
    /// <exception cref="ChannelBackupFormatException">An unknown version, a truncated or malformed backup, or bytes
    /// after the last channel.</exception>
    public static ChannelBackupSnapshot Decode(ReadOnlySpan<byte> plaintext)
    {
        var reader = new Reader(plaintext);
        var version = reader.Byte();
        if (version != Version)
            throw new ChannelBackupFormatException($"Unknown channel backup version {version}.");

        var chainHash = new ChainHash(reader.Bytes(HashLength));
        var nodeId = reader.PubKey();
        var createdAt = reader.U64();
        if (createdAt > (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            throw new ChannelBackupFormatException("The backup's creation time is out of range.");

        var count = reader.U16();
        var channels = new List<ChannelBackupEntry>(count);
        var seen = new HashSet<ChannelId>();
        for (var i = 0; i < count; i++)
        {
            var length = reader.U16();
            var channel = DecodeRecord(reader.Bytes(length));
            if (!seen.Add(channel.ChannelId))
                throw new ChannelBackupFormatException($"Channel {channel.ChannelId} is in the backup twice.");

            channels.Add(channel);
        }

        if (!reader.IsAtEnd)
            throw new ChannelBackupFormatException("Unexpected bytes after the last channel of the backup.");

        return new ChannelBackupSnapshot(chainHash, nodeId, DateTimeOffset.FromUnixTimeSeconds((long)createdAt),
                                         channels);
    }

    private static byte[] EncodeRecord(ChannelBackupEntry channel)
    {
        var writer = new Writer();
        writer.Bytes(channel.ChannelId, HashLength);
        writer.Bytes(channel.RemoteNodeId, PubKeyLength);
        writer.Bytes(channel.FundingTxId, HashLength);
        writer.U16(channel.FundingOutputIndex);
        writer.U64(channel.CapacitySat);
        writer.U32(channel.FundingHeight);
        writer.U64(channel.ShortChannelId is { } scid ? BinaryPrimitives.ReadUInt64BigEndian((byte[])scid) : 0);

        var flags = (byte)((channel.IsInitiator ? FlagInitiator : 0)
                         | (channel.OptionAnchorOutputs ? FlagAnchors : 0)
                         | (channel.AnnounceChannel ? FlagAnnounced : 0)
                         | (channel.HasInferredParams ? FlagInferredParams : 0));
        writer.Byte(flags);
        writer.Byte((byte)channel.Version);
        writer.Byte((byte)channel.UseScidAlias);
        writer.U32(channel.MinimumDepth);

        if (channel.ChannelType.Length > ushort.MaxValue)
            throw new ArgumentException($"The channel_type of channel {channel.ChannelId} is too long.",
                                        nameof(channel));
        writer.U16((ushort)channel.ChannelType.Length);
        writer.Bytes(channel.ChannelType, channel.ChannelType.Length);

        writer.U32(channel.KeyIndex);
        writer.Bytes(channel.LocalFundingPubKey, PubKeyLength);
        writer.Bytes(channel.LocalPaymentBasepoint, PubKeyLength);
        writer.Bytes(channel.RemoteFundingPubKey, PubKeyLength);
        writer.Bytes(channel.RemoteRevocationBasepoint, PubKeyLength);
        writer.Bytes(channel.RemotePaymentBasepoint, PubKeyLength);
        writer.Bytes(channel.RemoteDelayedPaymentBasepoint, PubKeyLength);
        writer.Bytes(channel.RemoteHtlcBasepoint, PubKeyLength);
        WriteParty(writer, channel.Local);
        WriteParty(writer, channel.Remote);

        var addresses = channel.Addresses.Take(byte.MaxValue).ToList();
        writer.Byte((byte)addresses.Count);
        foreach (var address in addresses)
        {
            writer.ShortString(address.Type, nameof(address.Type));
            writer.ShortString(address.Host, nameof(address.Host));
            writer.U16(address.Port);
        }

        return writer.ToArray();
    }

    private static ChannelBackupEntry DecodeRecord(ReadOnlySpan<byte> record)
    {
        var reader = new Reader(record);
        var channelId = new ChannelId(reader.Bytes(HashLength));
        var remoteNodeId = reader.PubKey();
        var fundingTxId = reader.Bytes(HashLength).ToArray();
        var fundingIndex = reader.U16();
        var capacity = reader.U64();
        var fundingHeight = reader.U32();
        var scid = reader.U64();
        var flags = reader.Byte();
        var version = reader.Byte();
        if (!Enum.IsDefined(typeof(ChannelVersion), version))
            throw new ChannelBackupFormatException($"Unknown channel version {version}.");

        var scidAlias = reader.Byte();
        if (!Enum.IsDefined(typeof(FeatureSupport), scidAlias))
            throw new ChannelBackupFormatException($"Unknown feature support value {scidAlias}.");

        var minimumDepth = reader.U32();
        var channelType = reader.Bytes(reader.U16()).ToArray();
        var keyIndex = reader.U32();
        var localFunding = reader.PubKey();
        var localPayment = reader.PubKey();
        var remoteFunding = reader.PubKey();
        var remoteRevocation = reader.PubKey();
        var remotePayment = reader.PubKey();
        var remoteDelayed = reader.PubKey();
        var remoteHtlc = reader.PubKey();
        var local = ReadParty(ref reader);
        var remote = ReadParty(ref reader);

        var addressCount = reader.Byte();
        var addresses = new List<ChannelBackupAddress>(addressCount);
        for (var i = 0; i < addressCount; i++)
        {
            var type = reader.ShortString();
            var host = reader.ShortString();
            addresses.Add(new ChannelBackupAddress(type, host, reader.U16()));
        }

        // Bytes left in the record belong to fields of a later minor revision: skipped
        return new ChannelBackupEntry
        {
            ChannelId = channelId,
            RemoteNodeId = remoteNodeId,
            Addresses = addresses,
            FundingTxId = fundingTxId,
            FundingOutputIndex = fundingIndex,
            CapacitySat = capacity,
            FundingHeight = fundingHeight,
            ShortChannelId = scid == 0 ? (ShortChannelId?)null : new ShortChannelId(scid),
            IsInitiator = (flags & FlagInitiator) != 0,
            OptionAnchorOutputs = (flags & FlagAnchors) != 0,
            AnnounceChannel = (flags & FlagAnnounced) != 0,
            HasInferredParams = (flags & FlagInferredParams) != 0,
            Version = (ChannelVersion)version,
            UseScidAlias = (FeatureSupport)scidAlias,
            MinimumDepth = minimumDepth,
            ChannelType = channelType,
            KeyIndex = keyIndex,
            LocalFundingPubKey = localFunding,
            LocalPaymentBasepoint = localPayment,
            RemoteFundingPubKey = remoteFunding,
            RemoteRevocationBasepoint = remoteRevocation,
            RemotePaymentBasepoint = remotePayment,
            RemoteDelayedPaymentBasepoint = remoteDelayed,
            RemoteHtlcBasepoint = remoteHtlc,
            Local = local,
            Remote = remote
        };
    }

    private static void WriteParty(Writer writer, ChannelBackupParty party)
    {
        writer.U64(party.DustLimitSat);
        writer.U64(party.ChannelReserveSat);
        writer.U64(party.HtlcMinimumMsat);
        writer.U16(party.MaxAcceptedHtlcs);
        writer.U64(party.MaxHtlcValueInFlightMsat);
        writer.U16(party.ToSelfDelay);
    }

    private static ChannelBackupParty ReadParty(ref Reader reader) =>
        new(reader.U64(), reader.U64(), reader.U64(), reader.U16(), reader.U64(), reader.U16());

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public void Byte(byte value) => _stream.WriteByte(value);

        public void U16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void U32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void U64(ulong value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void Bytes(ReadOnlySpan<byte> value, int expectedLength)
        {
            if (value.Length != expectedLength)
                throw new ArgumentException($"Expected {expectedLength} bytes, got {value.Length}.", nameof(value));

            _stream.Write(value);
        }

        public void ShortString(string value, string name)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > byte.MaxValue)
                throw new ArgumentException($"The {name} '{value}' is longer than {byte.MaxValue} bytes.", name);

            Byte((byte)bytes.Length);
            _stream.Write(bytes);
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public readonly bool IsAtEnd => _position == _data.Length;

        public ReadOnlySpan<byte> Bytes(int length)
        {
            if (length > _data.Length - _position)
                throw new ChannelBackupFormatException("The channel backup is truncated.");

            var slice = _data.Slice(_position, length);
            _position += length;
            return slice;
        }

        public byte Byte() => Bytes(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Bytes(2));

        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Bytes(4));

        public ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Bytes(8));

        public CompactPubKey PubKey()
        {
            var bytes = Bytes(PubKeyLength);
            if (bytes[0] is not (0x02 or 0x03))
                throw new ChannelBackupFormatException("A public key of the channel backup is not compressed.");

            return new CompactPubKey(bytes.ToArray());
        }

        public string ShortString()
        {
            var length = Byte();
            try
            {
                return new UTF8Encoding(false, true).GetString(Bytes(length));
            }
            catch (DecoderFallbackException)
            {
                throw new ChannelBackupFormatException("A string of the channel backup is not valid UTF-8.");
            }
        }
    }
}