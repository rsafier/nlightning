using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Exceptions;

public sealed partial class WalletPsbtService
{
    private PSBT LoadFundingTemplate(byte[] bytes)
    {
        try { return LoadPsbt(bytes); }
        catch (WalletPsbtException)
        {
            try
            {
                // BIP174's unsigned transaction uses the non-witness encoding. NBitcoin's v0 decoder instead
                // interprets zero inputs as a witness marker. Normalize only this otherwise valid template to v2.
                if (bytes.Length > 4 * 1024 * 1024 || !bytes.AsSpan().StartsWith(new byte[] { 0x70, 0x73, 0x62, 0x74, 0xff }))
                    throw;
                var maps = ReadTemplateMaps(bytes);
                var global = maps[0];
                var unsigned = global.FirstOrDefault(p => p.Key.AsSpan().SequenceEqual(new byte[] { 0 }));
                if (unsigned.Value is null || unsigned.Value.Length < 10 || unsigned.Value[4] != 0)
                    throw;
                foreach (var pair in global)
                    if (pair.Key.Length == 1 && pair.Key[0] is >= 2 and <= 6)
                        throw new FormatException("PSBT v0 contains v2 global fields.");
                var version = global.FirstOrDefault(p => p.Key.AsSpan().SequenceEqual(new byte[] { 0xfb }));
                if (version.Value is not null && (version.Value.Length != 4 || version.Value.Any(b => b != 0)))
                    throw;
                using var countReader = new BinaryReader(new MemoryStream(unsigned.Value, writable: false));
                countReader.BaseStream.Position = 5; // Version + canonical empty input vector.
                var outputCount = ReadTemplateSize(countReader);
                if (outputCount == 0 || outputCount > (unsigned.Value.Length - 10) / 9)
                    throw new FormatException("Invalid inputless transaction output count.");
                var transaction = _network.CreateTransaction();
                var stream = new BitcoinStream(unsigned.Value)
                {
                    TransactionOptions = TransactionOptions.None,
                    ConsensusFactory = _network.Consensus.ConsensusFactory
                };
                transaction.ReadWrite(stream);
                if (stream.Inner.Position != stream.Inner.Length || transaction.Inputs.Count != 0
                    || transaction.Outputs.Count == 0 || maps.Count != transaction.Outputs.Count + 1)
                    throw new FormatException("Invalid inputless PSBT transaction or output map count.");
                using var canonicalBytes = new MemoryStream();
                transaction.ReadWrite(new BitcoinStream(canonicalBytes, true)
                {
                    TransactionOptions = TransactionOptions.None,
                    ConsensusFactory = _network.Consensus.ConsensusFactory
                });
                if (!canonicalBytes.ToArray().AsSpan().SequenceEqual(unsigned.Value))
                    throw new FormatException("Noncanonical inputless unsigned transaction.");
                var required = ReadTemplateMaps(PSBT.FromTransaction(transaction, _network, PSBTVersion.PSBTv2).ToBytes());
                var merged = new List<List<KeyValuePair<byte[], byte[]>>>
                {
                    global.Where(p => !(p.Key.Length == 1 && p.Key[0] is 0 or 0xfb)).ToList()
                };
                merged[0].AddRange(required[0]);
                for (var index = 1; index < maps.Count; index++)
                {
                    if (maps[index].Any(p => p.Key.Length == 1 && p.Key[0] is 3 or 4))
                        throw new FormatException("PSBT v0 contains v2 output fields.");
                    merged.Add(maps[index].Concat(required[index]).ToList());
                }
                return LoadPsbt(WriteTemplateMaps(merged));
            }
            catch (Exception e) when (e is FormatException or ArgumentException or EndOfStreamException or InvalidDataException or OverflowException)
            {
                throw new WalletPsbtException(WalletPsbtError.InvalidArgument, $"the inputless PSBT does not parse: {e.Message}");
            }
        }
    }

    private static List<List<KeyValuePair<byte[], byte[]>>> ReadTemplateMaps(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        stream.Position = 5;
        var maps = new List<List<KeyValuePair<byte[], byte[]>>>();
        while (stream.Position < stream.Length)
        {
            var map = new List<KeyValuePair<byte[], byte[]>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                var keyLength = ReadTemplateSize(reader);
                if (keyLength == 0) break;
                var key = ReadTemplateBytes(reader, keyLength);
                var value = ReadTemplateBytes(reader, ReadTemplateSize(reader));
                if (!seen.Add(Convert.ToHexString(key))) throw new FormatException("Duplicate PSBT key.");
                if (map.Count >= 100_000) throw new FormatException("PSBT map entry limit exceeded.");
                map.Add(new(key, value));
            }
            if (maps.Count >= 100_001) throw new FormatException("PSBT map count limit exceeded.");
            maps.Add(map);
        }
        if (maps.Count == 0) throw new FormatException("PSBT has no global map.");
        return maps;
    }

    private static int ReadTemplateSize(BinaryReader reader)
    {
        var prefix = reader.ReadByte();
        var value = prefix switch { 253 => reader.ReadUInt16(), 254 => reader.ReadUInt32(), 255 => reader.ReadUInt64(), _ => prefix };
        if ((prefix == 253 && value < 253) || (prefix == 254 && value <= ushort.MaxValue)
            || (prefix == 255 && value <= uint.MaxValue) || value > 4 * 1024 * 1024)
            throw new FormatException("Invalid or oversized PSBT CompactSize.");
        return checked((int)value);
    }

    private static byte[] ReadTemplateBytes(BinaryReader reader, int count)
    {
        if (count > reader.BaseStream.Length - reader.BaseStream.Position) throw new EndOfStreamException();
        var result = reader.ReadBytes(count);
        if (result.Length != count) throw new EndOfStreamException();
        return result;
    }

    private static byte[] WriteTemplateMaps(List<List<KeyValuePair<byte[], byte[]>>> maps)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 0x70, 0x73, 0x62, 0x74, 0xff });
        foreach (var map in maps)
        {
            foreach (var pair in map)
            {
                WriteTemplateSize(writer, pair.Key.Length); writer.Write(pair.Key);
                WriteTemplateSize(writer, pair.Value.Length); writer.Write(pair.Value);
            }
            writer.Write((byte)0);
        }
        return stream.ToArray();
    }

    private static void WriteTemplateSize(BinaryWriter writer, int count)
    {
        if (count < 253) writer.Write((byte)count);
        else if (count <= ushort.MaxValue) { writer.Write((byte)253); writer.Write((ushort)count); }
        else { writer.Write((byte)254); writer.Write((uint)count); }
    }
}