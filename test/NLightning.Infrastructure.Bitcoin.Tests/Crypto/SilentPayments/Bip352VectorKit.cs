using System.Buffers.Binary;
using System.Text.Json;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.SilentPayments;

internal static class Bip352VectorKit
{
    private static readonly JsonElement s_vectors = Load();

    public static JsonElement Vectors => s_vectors;

    public static TheoryData<int, int, string> Cases(string direction)
    {
        var result = new TheoryData<int, int, string>();
        for (var i = 0; i < s_vectors.GetArrayLength(); i++)
        {
            var vector = s_vectors[i];
            for (var j = 0; j < vector.GetProperty(direction).GetArrayLength(); j++)
                result.Add(i, j, vector.GetProperty("comment").GetString()!);
        }

        return result;
    }

    public static byte[] Hex(JsonElement value) => Convert.FromHexString(value.GetString()!);

    public static string Hex(byte[] value) => Convert.ToHexStringLower(value);

    public static byte[] Outpoint(JsonElement input)
    {
        var result = new byte[36];
        var txid = Hex(input.GetProperty("txid"));
        Array.Reverse(txid);
        txid.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32), input.GetProperty("vout").GetUInt32());
        return result;
    }

    public static byte[][] Witness(JsonElement input)
    {
        var bytes = Hex(input.GetProperty("txinwitness"));
        if (bytes.Length == 0)
            return [];
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var count = ReadCompactSize(reader);
        var items = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var length = ReadCompactSize(reader);
            items[i] = reader.ReadBytes(checked((int)length));
            if (items[i].Length != length)
                throw new InvalidDataException("Truncated vector witness item.");
        }

        if (reader.BaseStream.Position != bytes.Length)
            throw new InvalidDataException("Trailing vector witness data.");
        return items;
    }

    private static uint ReadCompactSize(BinaryReader reader) => reader.ReadByte() switch
    {
        253 => reader.ReadUInt16(),
        254 => reader.ReadUInt32(),
        255 => checked((uint)reader.ReadUInt64()),
        var value => value
    };

    private static JsonElement Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Crypto", "SilentPayments", "Vectors",
                                "send_and_receive_test_vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}