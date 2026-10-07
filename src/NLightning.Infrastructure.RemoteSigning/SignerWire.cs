using System.Text.Json;
using System.Text.Json.Serialization;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Offers.Enums;
using NLightning.Domain.Offers.Models;
using NLightning.Domain.Protocol.Payloads;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Explicit wire representations. No private-key types or polymorphic type names are accepted.</summary>
public static class SignerWire
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    public static byte[] Encode(object?[] values) => JsonSerializer.SerializeToUtf8Bytes(values, Options);
    public static JsonElement[] Decode(byte[] bytes) => JsonSerializer.Deserialize<JsonElement[]>(bytes, Options)
        ?? throw new JsonException("Missing arguments.");
    public static T Read<T>(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null && Nullable.GetUnderlyingType(typeof(T)) is not null) return default!;
        return element.Deserialize<T>(Options) ?? throw new JsonException("Missing required argument.");
    }
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { MaxDepth = 32 };
        options.Converters.Add(new CompactPubKeyConverter());
        options.Converters.Add(new HashConverter());
        options.Converters.Add(new SecretConverter());
        options.Converters.Add(new CompactSignatureConverter());
        options.Converters.Add(new MusigPublicNonceConverter());
        options.Converters.Add(new MusigPartialSignatureConverter());
        options.Converters.Add(new MusigPartialSignatureWithNonceConverter());
        options.Converters.Add(new TxIdConverter());
        options.Converters.Add(new BitcoinScriptConverter());
        options.Converters.Add(new BitcoinKeyPathConverter());
        options.Converters.Add(new ChannelIdConverter());
        options.Converters.Add(new ShortChannelIdConverter());
        options.Converters.Add(new MoneyConverter());
        options.Converters.Add(new AnnouncementConverter());
        options.Converters.Add(new Bolt12KeyConverter());
        options.Converters.Add(new UtxoConverter());
        return options;
    }
    private sealed class CompactPubKeyConverter : JsonConverter<CompactPubKey>
    {
        public override CompactPubKey Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, CompactPubKey value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override CompactPubKey ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, CompactPubKey value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class HashConverter : JsonConverter<Hash>
    {
        public override Hash Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, Hash value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override Hash ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, Hash value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class SecretConverter : JsonConverter<Secret>
    {
        public override Secret Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override Secret ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class CompactSignatureConverter : JsonConverter<CompactSignature>
    {
        public override CompactSignature Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, CompactSignature value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override CompactSignature ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, CompactSignature value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class MusigPublicNonceConverter : JsonConverter<MusigPublicNonce>
    {
        public override MusigPublicNonce Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, MusigPublicNonce value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override MusigPublicNonce ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, MusigPublicNonce value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class MusigPartialSignatureConverter : JsonConverter<MusigPartialSignature>
    {
        public override MusigPartialSignature Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, MusigPartialSignature value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override MusigPartialSignature ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, MusigPartialSignature value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class MusigPartialSignatureWithNonceConverter : JsonConverter<MusigPartialSignatureWithNonce>
    {
        public override MusigPartialSignatureWithNonce Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, MusigPartialSignatureWithNonce value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override MusigPartialSignatureWithNonce ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, MusigPartialSignatureWithNonce value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class TxIdConverter : JsonConverter<TxId>
    {
        public override TxId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, TxId value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override TxId ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, TxId value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class BitcoinScriptConverter : JsonConverter<BitcoinScript>
    {
        public override BitcoinScript Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, BitcoinScript value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override BitcoinScript ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, BitcoinScript value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class BitcoinKeyPathConverter : JsonConverter<BitcoinKeyPath>
    {
        public override BitcoinKeyPath Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, BitcoinKeyPath value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override BitcoinKeyPath ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, BitcoinKeyPath value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class ChannelIdConverter : JsonConverter<ChannelId>
    {
        public override ChannelId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, ChannelId value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override ChannelId ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, ChannelId value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class ShortChannelIdConverter : JsonConverter<ShortChannelId>
    {
        public override ShortChannelId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, ShortChannelId value, JsonSerializerOptions options) => writer.WriteBase64StringValue((byte[])value);
        public override ShortChannelId ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(Convert.FromBase64String(reader.GetString()!));
        public override void WriteAsPropertyName(Utf8JsonWriter writer, ShortChannelId value, JsonSerializerOptions options) => writer.WritePropertyName(Convert.ToBase64String((byte[])value));
    }
    private sealed class MoneyConverter : JsonConverter<LightningMoney>
    {
        public override LightningMoney Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => LightningMoney.MilliSatoshis(reader.GetUInt64());
        public override void Write(Utf8JsonWriter writer, LightningMoney value, JsonSerializerOptions options) => writer.WriteNumberValue(value.MilliSatoshi);
    }
    private sealed class AnnouncementConverter : JsonConverter<ChannelAnnouncement2Payload>
    {
        public override ChannelAnnouncement2Payload Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => ChannelAnnouncement2Payload.Parse(reader.GetBytesFromBase64());
        public override void Write(Utf8JsonWriter writer, ChannelAnnouncement2Payload value, JsonSerializerOptions options) => writer.WriteBase64StringValue(value.GetBytes());
    }
    private sealed class Bolt12KeyConverter : JsonConverter<Bolt12SigningKey>
    {
        public override Bolt12SigningKey Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var args = JsonSerializer.Deserialize<JsonElement[]>(ref reader, options)!;
            return SignerWire.Read<Bolt12SigningKeyKind>(args[0]) switch
            {
                Bolt12SigningKeyKind.Node => Bolt12SigningKey.Node,
                Bolt12SigningKeyKind.Payer => Bolt12SigningKey.Payer(SignerWire.Read<byte[]>(args[1])),
                Bolt12SigningKeyKind.BlindedRecipient => Bolt12SigningKey.BlindedRecipient(SignerWire.Read<CompactPubKey>(args[1])),
                _ => throw new JsonException("Invalid signing key kind.")
            };
        }
        public override void Write(Utf8JsonWriter writer, Bolt12SigningKey value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new object?[] { value.Kind, value.Kind == Bolt12SigningKeyKind.Payer ? value.InvoiceRequestMetadata.ToArray() : value.PathKey }, options);
    }
    private sealed class UtxoConverter : JsonConverter<NLightning.Domain.Bitcoin.Wallet.Models.UtxoModel>
    {
        public override NLightning.Domain.Bitcoin.Wallet.Models.UtxoModel Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader); var a = doc.RootElement;
            var dto = a.Deserialize<WalletUtxo>(options)!;
            var utxo = dto.SilentPayment is { } output
                ? new NLightning.Domain.Bitcoin.Wallet.Models.UtxoModel(output)
                : new NLightning.Domain.Bitcoin.Wallet.Models.UtxoModel(dto.TxId, dto.Index, dto.Amount, dto.BlockHeight,
                    dto.AddressIndex, dto.IsAddressChange, dto.AddressType);
            if (utxo.TxId != dto.TxId || utxo.Index != dto.Index || utxo.Amount != dto.Amount
             || utxo.BlockHeight != dto.BlockHeight || dto.SilentPayment is not null && dto.WalletAddress is not null)
                throw new JsonException("Inconsistent silent payment output context.");
            utxo.LockedToChannelId = dto.LockedToChannelId; utxo.UsedInTransactionId = dto.UsedInTransactionId;
            if (dto.WalletAddress is not null) utxo.SetWalletAddress(dto.WalletAddress);
            return utxo;
        }
        public override void Write(Utf8JsonWriter writer, NLightning.Domain.Bitcoin.Wallet.Models.UtxoModel value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, WalletUtxo.From(value), options);
    }
}