using System.Buffers.Binary;
using System.Numerics;

namespace NLightning.Domain.Gossip.Queries;

using Channels.ValueObjects;
using Crypto.Constants;
using Protocol.Payloads;

/// <summary>
/// The <c>channel_update</c> checksum of the BOLT 7 <c>checksums_tlv</c> (<c>gossip_queries_ex</c>, plan G3-T4): the
/// CRC32C (Castagnoli, RFC 3720 appendix B.4) of the update without its <c>signature</c> and <c>timestamp</c> fields,
/// that is <c>chain_hash || short_channel_id</c> followed by everything after the timestamp (the unknown trailing
/// fields included, as CLN and Eclair compute it).
/// </summary>
public static class ChannelUpdateChecksum
{
    private const int ChainHashAndScidOffset = ChannelUpdatePayload.SignatureLength;
    private const int ChainHashAndScidLength = CryptoConstants.Sha256HashLen + ShortChannelId.Length;
    private const int AfterTimestampOffset = ChainHashAndScidOffset + ChainHashAndScidLength + sizeof(uint);

    /// <summary>
    /// The checksum of a <c>channel_update</c> payload (the signed wire bytes without the message type).
    /// </summary>
    /// <exception cref="ArgumentException">The payload is shorter than a <c>channel_update</c>.</exception>
    public static uint Compute(ReadOnlySpan<byte> channelUpdatePayload)
    {
        if (channelUpdatePayload.Length < ChannelUpdatePayload.MinLength)
            throw new ArgumentException($"A channel_update payload is at least {ChannelUpdatePayload.MinLength} "
                                      + $"bytes, got {channelUpdatePayload.Length}.", nameof(channelUpdatePayload));

        var crc = Update(uint.MaxValue, channelUpdatePayload.Slice(ChainHashAndScidOffset, ChainHashAndScidLength));
        crc = Update(crc, channelUpdatePayload[AfterTimestampOffset..]);
        return ~crc;
    }

    /// <summary>
    /// The CRC32C (Castagnoli) of <paramref name="data"/>: initial value 0xFFFFFFFF, reflected, final complement
    /// (RFC 3720 appendix B.4; "123456789" gives 0xE3069283).
    /// </summary>
    public static uint Crc32C(ReadOnlySpan<byte> data) => ~Update(uint.MaxValue, data);

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        // BitOperations.Crc32C is one reflected CRC32C step, without the initial value and the final complement (the
        // SSE4.2/ARMv8 instruction where there is one). Reflected: the bytes go in first to last, so eight at a time
        // is a little-endian read
        while (data.Length >= sizeof(ulong))
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[sizeof(ulong)..];
        }

        foreach (var b in data)
            crc = BitOperations.Crc32C(crc, b);

        return crc;
    }
}