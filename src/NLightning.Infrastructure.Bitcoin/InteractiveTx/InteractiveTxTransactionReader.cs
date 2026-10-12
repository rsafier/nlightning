using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.InteractiveTx;

/// <summary>
/// Strict readers for the byte strings an interactive-tx negotiation carries: a <c>prevtx</c> and a
/// <c>witness_data</c> (BIP 141 witness stack serialization). Both refuse trailing bytes, so exactly one encoding is
/// accepted for each value.
/// </summary>
public static class InteractiveTxTransactionReader
{
    /// <summary>The largest witness item count or item length accepted (a transaction is at most 4 MWU).</summary>
    private const ulong MaxWitnessValue = 4_000_000;

    /// <summary>
    /// Parses a serialized transaction (with or without witnesses). False, never an exception, when the bytes are not
    /// exactly one transaction with at least one input.
    /// </summary>
    public static bool TryReadTransaction(ReadOnlySpan<byte> bytes, out Transaction? transaction)
    {
        transaction = null;
        if (bytes.IsEmpty)
            return false;

        try
        {
            var consensusFactory = Network.Main.Consensus.ConsensusFactory;
            var tx = consensusFactory.CreateTransaction();
            using var memory = new MemoryStream(bytes.ToArray(), false);
            var stream = new BitcoinStream(memory, false)
            {
                ConsensusFactory = consensusFactory,
                TransactionOptions = TransactionOptions.All
            };
            tx.ReadWrite(stream);

            // One transaction and nothing after it; a transaction without inputs has no valid encoding (BIP 144)
            if (memory.Position != memory.Length || tx.Inputs.Count == 0)
                return false;

            transaction = tx;
            return true;
        }
        catch (Exception e) when (e is FormatException or EndOfStreamException or ArgumentException
                                      or InvalidOperationException or OverflowException or IOException
                                      or ArgumentOutOfRangeException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a BIP 141 witness stack serialization: a CompactSize item count, then each item as a CompactSize length and
    /// its bytes. False when the encoding is not canonical, truncated or followed by other bytes.
    /// </summary>
    public static bool TryReadWitness(ReadOnlySpan<byte> bytes, out WitScript? witness)
    {
        witness = null;
        var offset = 0;
        if (!TryReadCompactSize(bytes, ref offset, out var count) || count > MaxWitnessValue)
            return false;

        var items = new List<byte[]>((int)Math.Min(count, 64));
        for (ulong i = 0; i < count; i++)
        {
            if (!TryReadCompactSize(bytes, ref offset, out var length) || length > MaxWitnessValue
             || (ulong)(bytes.Length - offset) < length)
                return false;

            items.Add(bytes.Slice(offset, (int)length).ToArray());
            offset += (int)length;
        }

        if (offset != bytes.Length)
            return false;

        witness = new WitScript(items.ToArray());
        return true;
    }

    /// <summary>Serializes a witness stack as <c>tx_signatures</c> carries it (BIP 141).</summary>
    public static byte[] WriteWitness(WitScript witness)
    {
        using var memory = new MemoryStream();
        var pushes = witness.Pushes.ToArray();
        WriteCompactSize(memory, (ulong)pushes.Length);
        foreach (var push in pushes)
        {
            WriteCompactSize(memory, (ulong)push.Length);
            memory.Write(push);
        }

        return memory.ToArray();
    }

    /// <summary>Reads a canonical (minimally encoded) CompactSize.</summary>
    private static bool TryReadCompactSize(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
    {
        value = 0;
        if (offset >= bytes.Length)
            return false;

        var prefix = bytes[offset++];
        switch (prefix)
        {
            case < 0xFD:
                value = prefix;
                return true;
            case 0xFD:
                if (bytes.Length - offset < 2)
                    return false;
                value = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
                offset += 2;
                return value >= 0xFD;
            case 0xFE:
                if (bytes.Length - offset < 4)
                    return false;
                value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
                offset += 4;
                return value > 0xFFFF;
            default:
                if (bytes.Length - offset < 8)
                    return false;
                value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
                offset += 8;
                return value > 0xFFFFFFFF;
        }
    }

    private static void WriteCompactSize(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[9];
        int length;
        if (value < 0xFD)
        {
            buffer[0] = (byte)value;
            length = 1;
        }
        else if (value <= 0xFFFF)
        {
            buffer[0] = 0xFD;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer[1..], (ushort)value);
            length = 3;
        }
        else if (value <= 0xFFFFFFFF)
        {
            buffer[0] = 0xFE;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], (uint)value);
            length = 5;
        }
        else
        {
            buffer[0] = 0xFF;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], value);
            length = 9;
        }

        stream.Write(buffer[..length]);
    }
}