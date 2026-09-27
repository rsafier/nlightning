using System.Buffers.Binary;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// The blob formats of <c>InteractiveTxSessionEntity</c> (migration <c>AddInteractiveTxSessions</c>): each blob starts
/// with a format version byte (<see cref="FormatVersion"/>), integers are big-endian, byte strings are a u32 length and
/// the bytes, optional values a presence byte (0/1) first, amounts in msat. A decoder rejects an unknown version, an
/// unknown enum value, a truncated blob and trailing bytes with <see cref="InvalidOperationException"/>.
/// </summary>
internal static class InteractiveTxSessionEncoding
{
    public const byte FormatVersion = 1;

    public static byte[] EncodeInputs(IReadOnlyList<InteractiveTxInput> inputs)
    {
        var writer = new BlobWriter();
        WriteInputs(writer, inputs);
        return writer.ToArray();
    }

    public static List<InteractiveTxInput> DecodeInputs(byte[] bytes)
    {
        var reader = new BlobReader(bytes, "inputs");
        var inputs = ReadInputs(reader);
        reader.EnsureEnd();
        return inputs;
    }

    public static byte[] EncodeOutputs(IReadOnlyList<InteractiveTxOutput> outputs)
    {
        var writer = new BlobWriter();
        WriteOutputs(writer, outputs);
        return writer.ToArray();
    }

    public static List<InteractiveTxOutput> DecodeOutputs(byte[] bytes)
    {
        var reader = new BlobReader(bytes, "outputs");
        var outputs = ReadOutputs(reader);
        reader.EnsureEnd();
        return outputs;
    }

    /// <summary>The contribution's inputs and outputs; its reservation id is a column of its own.</summary>
    public static byte[] EncodeContribution(InteractiveTxContribution contribution)
    {
        var writer = new BlobWriter();
        writer.WriteU32((uint)contribution.Inputs.Count);
        foreach (var input in contribution.Inputs)
        {
            writer.WriteFixed(input.PrevTxId, CryptoConstants.Sha256HashLen);
            writer.WriteU32(input.PrevTxVout);
            writer.WriteBytes(input.PrevTx);
            writer.WriteU32(input.Sequence);
            writer.WriteU64(input.Amount.MilliSatoshi);
            writer.WriteBytes(input.ScriptPubKey);
            writer.WriteU32(unchecked((uint)input.InputWeight));
        }

        writer.WriteU32((uint)contribution.Outputs.Count);
        foreach (var output in contribution.Outputs)
        {
            writer.WriteU64(output.Amount.MilliSatoshi);
            writer.WriteBytes(output.ScriptPubKey);
            writer.WriteBool(output.IsChange);
        }

        return writer.ToArray();
    }

    public static InteractiveTxContribution DecodeContribution(byte[] bytes, Guid? reservationId)
    {
        var reader = new BlobReader(bytes, "contribution");
        var inputCount = reader.ReadCount();
        var inputs = new List<ContributedInput>(inputCount);
        for (var i = 0; i < inputCount; i++)
        {
            inputs.Add(new ContributedInput(new TxId(reader.ReadFixed(CryptoConstants.Sha256HashLen)),
                                            reader.ReadU32(), reader.ReadBytes(), reader.ReadU32(),
                                            LightningMoney.MilliSatoshis(reader.ReadU64()),
                                            new BitcoinScript(reader.ReadBytes()), unchecked((int)reader.ReadU32())));
        }

        var outputCount = reader.ReadCount();
        var outputs = new List<ContributedOutput>(outputCount);
        for (var i = 0; i < outputCount; i++)
        {
            outputs.Add(new ContributedOutput(LightningMoney.MilliSatoshis(reader.ReadU64()),
                                              new BitcoinScript(reader.ReadBytes()), reader.ReadBool()));
        }

        reader.EnsureEnd();
        return new InteractiveTxContribution(inputs, outputs, reservationId);
    }

    public static byte[] EncodeConstructedTx(ConstructedInteractiveTx tx)
    {
        var writer = new BlobWriter();
        writer.WriteFixed(tx.TxId, CryptoConstants.Sha256HashLen);
        writer.WriteBytes(tx.UnsignedTx);
        writer.WriteU32(tx.Locktime);
        WriteInputs(writer, tx.Inputs);
        WriteOutputs(writer, tx.Outputs);
        writer.WriteU64(unchecked((ulong)tx.EstimatedWeight));
        writer.WriteBool(tx.SharedOutputIndex.HasValue);
        if (tx.SharedOutputIndex.HasValue)
            writer.WriteU32(tx.SharedOutputIndex.Value);

        return writer.ToArray();
    }

    public static ConstructedInteractiveTx DecodeConstructedTx(byte[] bytes)
    {
        var reader = new BlobReader(bytes, "constructed transaction");
        var txId = new TxId(reader.ReadFixed(CryptoConstants.Sha256HashLen));
        var unsignedTx = reader.ReadBytes();
        var locktime = reader.ReadU32();
        var inputs = ReadInputs(reader);
        var outputs = ReadOutputs(reader);
        var weight = unchecked((long)reader.ReadU64());
        uint? sharedOutputIndex = reader.ReadBool() ? reader.ReadU32() : null;
        reader.EnsureEnd();

        return new ConstructedInteractiveTx(txId, unsignedTx, locktime, inputs, outputs, weight, sharedOutputIndex);
    }

    public static byte[] EncodeWitnesses(IReadOnlyList<Witness> witnesses)
    {
        var writer = new BlobWriter();
        writer.WriteU32((uint)witnesses.Count);
        foreach (var witness in witnesses)
            writer.WriteBytes(witness);

        return writer.ToArray();
    }

    public static List<Witness> DecodeWitnesses(byte[] bytes)
    {
        var reader = new BlobReader(bytes, "witnesses");
        var count = reader.ReadCount();
        var witnesses = new List<Witness>(count);
        for (var i = 0; i < count; i++)
            witnesses.Add(new Witness(reader.ReadBytes()));

        reader.EnsureEnd();
        return witnesses;
    }

    private static void WriteInputs(BlobWriter writer, IReadOnlyList<InteractiveTxInput> inputs)
    {
        writer.WriteU32((uint)inputs.Count);
        foreach (var input in inputs)
        {
            writer.WriteU64(input.SerialId);
            writer.WriteByte((byte)input.AddedBy);
            writer.WriteFixed(input.PrevTxId, CryptoConstants.Sha256HashLen);
            writer.WriteU32(input.PrevTxVout);
            writer.WriteU32(input.Sequence);
            writer.WriteU64(input.Amount.MilliSatoshi);
            writer.WriteBytes(input.ScriptPubKey);
            writer.WriteBool(input.PrevTx is not null);
            if (input.PrevTx is not null)
                writer.WriteBytes(input.PrevTx);
            writer.WriteBool(input.IsShared);
        }
    }

    private static List<InteractiveTxInput> ReadInputs(BlobReader reader)
    {
        var count = reader.ReadCount();
        var inputs = new List<InteractiveTxInput>(count);
        for (var i = 0; i < count; i++)
        {
            var serialId = reader.ReadU64();
            var addedBy = reader.ReadParty();
            var prevTxId = new TxId(reader.ReadFixed(CryptoConstants.Sha256HashLen));
            var vout = reader.ReadU32();
            var sequence = reader.ReadU32();
            var amount = LightningMoney.MilliSatoshis(reader.ReadU64());
            var script = new BitcoinScript(reader.ReadBytes());
            var prevTx = reader.ReadBool() ? reader.ReadBytes() : null;
            var isShared = reader.ReadBool();
            inputs.Add(new InteractiveTxInput(serialId, addedBy, prevTxId, vout, sequence, amount, script, prevTx,
                                              isShared));
        }

        return inputs;
    }

    private static void WriteOutputs(BlobWriter writer, IReadOnlyList<InteractiveTxOutput> outputs)
    {
        writer.WriteU32((uint)outputs.Count);
        foreach (var output in outputs)
        {
            writer.WriteU64(output.SerialId);
            writer.WriteByte((byte)output.AddedBy);
            writer.WriteU64(output.Amount.MilliSatoshi);
            writer.WriteBytes(output.ScriptPubKey);
            writer.WriteBool(output.IsShared);
        }
    }

    private static List<InteractiveTxOutput> ReadOutputs(BlobReader reader)
    {
        var count = reader.ReadCount();
        var outputs = new List<InteractiveTxOutput>(count);
        for (var i = 0; i < count; i++)
        {
            outputs.Add(new InteractiveTxOutput(reader.ReadU64(), reader.ReadParty(),
                                                LightningMoney.MilliSatoshis(reader.ReadU64()),
                                                new BitcoinScript(reader.ReadBytes()), reader.ReadBool()));
        }

        return outputs;
    }

    private sealed class BlobWriter
    {
        private readonly MemoryStream _stream = new();

        public BlobWriter()
        {
            _stream.WriteByte(FormatVersion);
        }

        public void WriteByte(byte value) => _stream.WriteByte(value);

        public void WriteBool(bool value) => _stream.WriteByte(value ? (byte)1 : (byte)0);

        public void WriteU32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void WriteU64(ulong value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void WriteFixed(byte[] value, int length)
        {
            if (value.Length != length)
                throw new InvalidOperationException($"Expected {length} bytes, got {value.Length}");

            _stream.Write(value);
        }

        public void WriteBytes(byte[] value)
        {
            // A default value object (e.g. default(Witness)) converts to a null array despite the annotation
            if (value is null)
                throw new ArgumentException("A byte string to persist is null (a default value object?)",
                                            nameof(value));

            WriteU32((uint)value.Length);
            _stream.Write(value);
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    private sealed class BlobReader
    {
        private readonly byte[] _bytes;
        private readonly string _name;
        private int _offset;

        public BlobReader(byte[] bytes, string name)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            _bytes = bytes;
            _name = name;

            var version = ReadByte();
            if (version != FormatVersion)
                throw new InvalidOperationException(
                    $"Interactive-tx {_name} blob has format version {version}, expected {FormatVersion}");
        }

        public byte ReadByte()
        {
            Require(1);
            return _bytes[_offset++];
        }

        public bool ReadBool()
        {
            return ReadByte() switch
            {
                0 => false,
                1 => true,
                var other => throw new InvalidOperationException(
                                 $"Interactive-tx {_name} blob has an invalid boolean {other}")
            };
        }

        public InteractiveTxParty ReadParty()
        {
            var value = ReadByte();
            var party = (InteractiveTxParty)value;
            if (!Enum.IsDefined(party))
                throw new InvalidOperationException($"Interactive-tx {_name} blob has an unknown party {value}");

            return party;
        }

        public uint ReadU32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        public ulong ReadU64()
        {
            Require(8);
            var value = BinaryPrimitives.ReadUInt64BigEndian(_bytes.AsSpan(_offset, 8));
            _offset += 8;
            return value;
        }

        /// <summary>A list count, bounded by the bytes left (every item takes at least one byte).</summary>
        public int ReadCount()
        {
            var count = ReadU32();
            if (count > _bytes.Length - _offset)
                throw new InvalidOperationException($"Interactive-tx {_name} blob has an impossible count {count}");

            return (int)count;
        }

        public byte[] ReadFixed(int length)
        {
            Require(length);
            var value = _bytes.AsSpan(_offset, length).ToArray();
            _offset += length;
            return value;
        }

        public byte[] ReadBytes()
        {
            var length = ReadU32();
            if (length > int.MaxValue)
                throw new InvalidOperationException($"Truncated interactive-tx {_name} blob");

            return ReadFixed((int)length);
        }

        public void EnsureEnd()
        {
            if (_offset != _bytes.Length)
                throw new InvalidOperationException(
                    $"Interactive-tx {_name} blob has {_bytes.Length - _offset} trailing bytes");
        }

        private void Require(int length)
        {
            if (length > _bytes.Length - _offset)
                throw new InvalidOperationException($"Truncated interactive-tx {_name} blob");
        }
    }
}