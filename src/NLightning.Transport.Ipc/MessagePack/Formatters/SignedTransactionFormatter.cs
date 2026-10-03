using System.Buffers;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Formatters;

namespace NLightning.Transport.Ipc.MessagePack.Formatters;

using Domain.Bitcoin.ValueObjects;

/// <summary>
/// Writes a <see cref="SignedTransaction"/> as <c>[TxId, RawTxBytes]</c>, or <c>nil</c> for null.
/// </summary>
/// <remarks>Signatures are not serialized.</remarks>
public class SignedTransactionFormatter : IMessagePackFormatter<SignedTransaction?>
{
    public void Serialize(ref MessagePackWriter writer, SignedTransaction? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteArrayHeader(2);
        options.Resolver.GetFormatterWithVerify<TxId>().Serialize(ref writer, value.TxId, options);
        writer.Write(value.RawTxBytes);
    }

    public SignedTransaction? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;

        if (reader.ReadArrayHeader() != 2)
            throw new SerializationException($"Error deserializing {nameof(SignedTransaction)}");

        var txId = options.Resolver.GetFormatterWithVerify<TxId>().Deserialize(ref reader, options);

        // Read RawTxBytes
        var rawTxBytes = reader.ReadBytes()?.ToArray() ??
                         throw new SerializationException(
                             $"Error deserializing {nameof(SignedTransaction)}.{nameof(SignedTransaction.RawTxBytes)}");

        return new SignedTransaction(txId, rawTxBytes);
    }
}