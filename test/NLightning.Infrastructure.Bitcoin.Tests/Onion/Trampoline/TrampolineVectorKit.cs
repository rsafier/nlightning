using System.Text.Json;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.Trampoline;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Onion.RouteBlinding;
using Infrastructure.Bitcoin.Onion.Trampoline;

/// <summary>
/// Loads the PR 836 trampoline vectors (<c>Vectors/</c>) and holds the services and small helpers their tests share.
/// </summary>
/// <remarks>
/// Hop payloads are taken from the vectors' raw hex (their spaces removed), never re-encoded from the typed fields, so
/// these tests do not depend on the TLV classes. An onion hop's <c>hex</c>/<c>encoded_tlvs</c> starts with its bigsize
/// length; a blinded path hop's <c>encoded_tlvs</c> (the <c>encrypted_data_tlv</c> plaintext) does not.
/// </remarks>
internal static class TrampolineVectorKit
{
    /// <summary>
    /// The outer payload's <c>trampoline_onion_packet</c> type.
    /// </summary>
    public const ulong TrampolineOnionPacketType = 20;

    /// <summary>
    /// The outer payload's <c>current_path_key</c> type.
    /// </summary>
    public const ulong CurrentPathKeyType = 12;

    public static SphinxService Sphinx { get; } = new(new Secp256K1Math());

    public static TrampolineOnionService TrampolineOnions { get; } = new(Sphinx);

    public static RouteBlindingService RouteBlinding { get; } = new(new Secp256K1Math());

    public static FailureOnionService FailureOnions { get; } = new(new SpecFailureMessageSerializer());

    public static TrampolineFailureOnionService TrampolineFailures { get; } =
        new(FailureOnions, new SpecFailureMessageSerializer());

    /// <summary>
    /// Parses a vector file from the test output's <c>Onion/Trampoline/Vectors</c> folder.
    /// </summary>
    public static JsonElement Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Onion", "Trampoline", "Vectors", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The bytes of a hex field, spaces removed.
    /// </summary>
    public static byte[] Hex(JsonElement element, string property) => Hex(element.GetProperty(property).GetString()!);

    public static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty));

    public static PrivKey PrivKey(JsonElement element, string property) => new(Hex(element, property));

    public static CompactPubKey PubKey(JsonElement element, string property) => new(Hex(element, property));

    public static Secret Secret(JsonElement element, string property) => new(Hex(element, property));

    public static IReadOnlyList<Secret> Secrets(JsonElement array) =>
        array.EnumerateArray().Select(e => new Secret(Hex(e.GetString()!))).ToList();

    /// <summary>
    /// An onion hop payload without its bigsize length prefix, checking that the prefix matches the rest.
    /// </summary>
    public static byte[] StripLengthPrefix(byte[] framed)
    {
        var (length, prefixLength) = ReadBigSize(framed, 0);
        Assert.Equal((ulong)(framed.Length - prefixLength), length);
        return framed[prefixLength..];
    }

    /// <summary>
    /// The value of the first TLV record of <paramref name="type"/> in a TLV stream (the test fails without one).
    /// </summary>
    public static byte[] FindTlv(ReadOnlySpan<byte> stream, ulong type)
    {
        var offset = 0;
        while (offset < stream.Length)
        {
            var (recordType, typeLength) = ReadBigSize(stream, offset);
            offset += typeLength;
            var (length, lengthLength) = ReadBigSize(stream, offset);
            offset += lengthLength;
            if (recordType == type)
                return stream.Slice(offset, (int)length).ToArray();

            offset += (int)length;
        }

        Assert.Fail($"No TLV record of type {type}.");
        return [];
    }

    /// <summary>
    /// The onion hop built from a vector hop: its node id (<paramref name="nodeIdProperty"/>) and payload (the hex in
    /// <paramref name="payloadPath"/>, length prefix removed).
    /// </summary>
    public static OnionHop Hop(JsonElement hop, string nodeIdProperty, params string[] payloadPath)
    {
        var payload = hop;
        foreach (var property in payloadPath)
            payload = payload.GetProperty(property);

        return new OnionHop(PubKey(hop, nodeIdProperty), StripLengthPrefix(Hex(payload.GetString()!)));
    }

    private static (ulong Value, int Length) ReadBigSize(ReadOnlySpan<byte> data, int offset)
    {
        return data[offset] switch
        {
            0xfd => ((ulong)(data[offset + 1] << 8 | data[offset + 2]), 3),
            0xfe => ((ulong)data[offset + 1] << 24 | (ulong)data[offset + 2] << 16 | (ulong)data[offset + 3] << 8
                   | data[offset + 4], 5),
            0xff => throw new NotSupportedException("8-byte bigsize values do not occur in these vectors."),
            var b => (b, 1)
        };
    }
}