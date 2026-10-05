using System.Buffers;
using System.Runtime.Serialization;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Wire;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Exceptions;

/// <summary>
/// One declarative wire definition for a message: the message type, an encode and a decode lambda over the
/// <see cref="WireWriter"/>/<see cref="WireReader"/> primitives, and (for messages with a TLV extension) the known
/// TLV types with their typed decoders. One definition drives both directions — the property the equivalence tests
/// pin against the hand-written codecs (plan <c>docs/agents/CODEC_REDESIGN_PLAN.md</c>).
/// </summary>
/// <remarks>
/// The decode side reproduces the hand-written serializers' behavior: body fields first, then the extension read
/// strictly (increasing types, canonical BigSize, length bounds, unknown even types rejected by the definition's
/// known set, unknown odd types dropped on re-encode because the message ctor rebuilds <c>Extension</c> from the
/// typed TLVs), then the message constructed through its normal constructor. Errors take the same two shapes the
/// hand-written pair produced — payload/body and construction failures escape as
/// <see cref="PayloadSerializationException"/>, extension failures are wrapped in
/// <see cref="MessageSerializationException"/> — so <see cref="MessageService"/> keeps answering malformed messages
/// with a connection <c>warning</c>.
/// </remarks>
public sealed class MessageWire<TMessage> : MessageWireBase, IMessageTypeSerializer<TMessage>
    where TMessage : class, IMessage
{
    private readonly WireEncode<TMessage> _encodeBody;
    private readonly WireDecode<TMessage> _decodeBody;
    private readonly Dictionary<BigSize, TlvDef> _tlvsByType;
    private readonly bool _strictEmptyExtension;
    private readonly bool _keepRawExtension;
    private readonly bool _wrapBodyErrors;
    private ITlvConverterFactory? _converters;

    internal MessageWire(MessageTypes type, WireEncode<TMessage> encodeBody, WireDecode<TMessage> decodeBody,
                       params TlvDef[] tlvs)
    {
        Type = type;
        _encodeBody = encodeBody;
        _decodeBody = decodeBody;
        _tlvsByType = tlvs.ToDictionary(t => t.Type);
    }

    /// <summary>
    /// The form for messages whose extension has no typed TLVs but whose trailing bytes still carry meaning:
    /// <paramref name="strictEmptyExtension"/> validates the trailing records with an empty known set (BOLT 1:
    /// unknown even type fails, unknown odd is ignored — splice_locked, peer_storage, onion_message),
    /// <paramref name="keepRawExtension"/> hands the raw extension region to the constructor via
    /// <see cref="WireTlvs.RawBytes"/> (gossip payloads that replay unknown records verbatim), and
    /// <paramref name="wrapBodyErrors"/> reproduces a legacy serializer that wrapped body errors into
    /// <see cref="MessageSerializationException"/> (the closing pair). Raw TLV definitions (the closing pair's
    /// fixed-length closing_tlvs signatures) can ride along in <paramref name="tlvs"/>.
    /// </summary>
    internal MessageWire(MessageTypes type, WireEncode<TMessage> encodeBody, WireDecode<TMessage> decodeBody,
                       bool strictEmptyExtension, bool keepRawExtension, bool wrapBodyErrors = false,
                       params TlvDef[] tlvs)
    {
        Type = type;
        _encodeBody = encodeBody;
        _decodeBody = decodeBody;
        _tlvsByType = tlvs.ToDictionary(t => t.Type);
        _strictEmptyExtension = strictEmptyExtension;
        _keepRawExtension = keepRawExtension;
        _wrapBodyErrors = wrapBodyErrors;
    }

    public override MessageTypes Type { get; }

    public override Type MessageType => typeof(TMessage);

    /// <summary>
    /// Binds the TLV converter factory the registry was built with. Called once by <see cref="WireRegistry"/>
    /// before first use; typed TLV decoding without a bound factory is a programming error.
    /// </summary>
    internal override void Bind(ITlvConverterFactory converters) => _converters = converters;

    /// <summary>Encodes the message (payload fields, then the extension records) straight onto the stream.</summary>
    private void Encode(TMessage message, Stream stream)
    {
        var writer = new WireWriter();
        try
        {
            _encodeBody(ref writer, message);
            WriteExtension(ref writer, message.Extension);
            writer.WriteTo(stream);
        }
        finally
        {
            writer.ReturnBuffer();
        }
    }

    /// <summary>
    /// Decodes one message body (already sliced off the wire type prefix). Error wrapping matches the hand-written
    /// serializer pair: body (payload) errors escape as <see cref="PayloadSerializationException"/>; extension errors
    /// are wrapped in <see cref="MessageSerializationException"/>.
    /// </summary>
    internal TMessage DecodeMessage(ReadOnlySpan<byte> body)
    {
        var reader = new WireReader(body);
        WireConstruct<TMessage> construct;
        try
        {
            construct = _decodeBody(ref reader);
        }
        catch (Exception e)
        {
            throw WrapBodyError(e);
        }

        var tlvs = _tlvsByType.Count > 0 || _strictEmptyExtension || _keepRawExtension
            ? ReadTlvs(ref reader)
            : WireTlvs.Empty;
        try
        {
            return construct(tlvs);
        }
        catch (Exception e)
        {
            throw WrapBodyError(e);
        }
    }

    /// <summary>Body/constructor failures are PayloadSerializationException, unless the legacy serializer this
    /// definition replaces wrapped them into MessageSerializationException (the closing pair).</summary>
    private Exception WrapBodyError(Exception e)
    {
        return _wrapBodyErrors
            ? new MessageSerializationException($"Error deserializing {typeof(TMessage).Name}", e)
            : new PayloadSerializationException($"Error deserializing {typeof(TMessage).Name} payload", e);
    }

    /// <inheritdoc />
    public Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not TMessage typedMessage)
            throw new SerializationException($"Message is not of type {typeof(TMessage).Name}");

        Encode(typedMessage, stream);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TMessage> DeserializeAsync(Stream stream)
    {
        // The stream is seekable and bounded to one message (the long-standing serializer contract)
        var length = (int)(stream.Length - stream.Position);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            stream.ReadExactly(buffer.AsSpan(0, length));
            return Task.FromResult(DecodeMessage(buffer.AsSpan(0, length)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }

    private void WriteExtension(ref WireWriter writer, TlvStream? extension)
    {
        if (extension is null)
            return;

        BigSize? previousType = null;
        foreach (var tlv in extension.GetTlvs())
        {
            // BOLT 1: types MUST be strictly increasing on the wire (duplicates rejected), as TlvStreamSerializer did
            if (previousType is { } previous && tlv.Type.Value <= previous.Value)
                throw new SerializationException(
                    $"TLV type {tlv.Type.Value} is not greater than the previous type {previous.Value}.");
            previousType = tlv.Type;

            // Typed TLVs are converted through their registered converter, exactly as TlvStreamSerializer did —
            // some typed TLVs (e.g. fee_range) only compute their wire bytes in ConvertToBase
            var baseTlv = tlv.GetType() == typeof(BaseTlv) ? tlv : ConvertToBase(tlv);
            writer.BigSize(baseTlv.Type.Value);
            writer.BigSize((ulong)baseTlv.Length.Value);
            writer.Bytes(baseTlv.Value);
        }
    }

    private BaseTlv ConvertToBase(BaseTlv tlv)
    {
        var converters = _converters
                      ?? throw new InvalidOperationException(
                             $"The wire definition of {typeof(TMessage).Name} was not bound to a converter factory");

        return converters.GetConverter(tlv.GetType())
                    ?.ConvertToBase(tlv)
               ?? throw new SerializationException($"No converter found for tlv type {tlv.GetType().Name}");
    }

    private WireTlvs ReadTlvs(ref WireReader reader)
    {
        try
        {
            return ReadTlvsCore(ref reader);
        }
        catch (Exception e) when (e is SerializationException or InvalidCastException)
        {
            throw new MessageSerializationException($"Error deserializing {typeof(TMessage).Name}", e);
        }
    }

    private WireTlvs ReadTlvsCore(ref WireReader reader)
    {
        var converters = _converters
                      ?? throw new InvalidOperationException(
                             $"The wire definition of {typeof(TMessage).Name} was not bound to a converter factory");

        var typed = new Dictionary<BigSize, object?>();
        var raws = new Dictionary<BigSize, BaseTlv>();
        ReadOnlyMemory<byte>? rawBytes = _keepRawExtension ? reader.RemainingBytes().ToArray() : null;
        BigSize? previousType = null;
        while (reader.Remaining > 0)
        {
            var type = new BigSize(reader.BigSize());
            var length = reader.BigSize();

            // BOLT 1: length must not exceed the bytes remaining in the message (checked before allocating)
            if (length > (ulong)reader.Remaining)
                throw new SerializationException(
                    $"TLV length {length} exceeds the {reader.Remaining} bytes remaining in the message.");

            // BOLT 1: types MUST be strictly increasing (duplicates rejected)
            if (previousType is { } previous && type.Value <= previous.Value)
                throw new SerializationException(
                    $"TLV type {type.Value} is not greater than the previous type {previous.Value}.");
            previousType = type;

            var value = reader.BytesArray((int)length);
            var raw = new BaseTlv(type, new BigSize(length), value);
            raws[type] = raw;

            if (_tlvsByType.TryGetValue(type, out var def))
                typed[type] = def.Decode(raw, converters);
            // BOLT 1: an unknown even type MUST fail the stream; the known set is the definition's TLV table
            else if (type.Value % 2 == 0)
                throw new SerializationException($"Unknown even TLV type {type.Value}.");
        }

        return new WireTlvs(typed, raws, rawBytes);
    }
}

/// <summary>Encodes the payload fields of <typeparamref name="TMessage"/> in wire order.</summary>
internal delegate void WireEncode<TMessage>(ref WireWriter writer, TMessage message)
    where TMessage : class, IMessage;

/// <summary>
/// Decodes the payload fields of <typeparamref name="TMessage"/> in wire order and returns the constructor
/// continuation, which runs once the extension records (which follow the fixed fields) are decoded into
/// <see cref="WireTlvs"/>.
/// </summary>
internal delegate WireConstruct<TMessage> WireDecode<TMessage>(ref WireReader reader)
    where TMessage : class, IMessage;

/// <summary>Constructs the message from the decoded extension's typed TLVs.</summary>
internal delegate TMessage WireConstruct<TMessage>(WireTlvs tlvs)
    where TMessage : class, IMessage;

/// <summary>
/// One entry of a message's TLV table: the wire type, whether an unknown record of it is tolerated (odd advisory
/// TLVs whose value may be malformed, like init's <c>remote_addr</c>), and how the typed TLV is built from the raw
/// record. Typed TLVs decode through the registered <c>ITlvConverter</c> — the same code the hand-written
/// serializers used, so the typed values cannot drift.
/// </summary>
public sealed class TlvDef
{
    private delegate object? DecodeDelegate(BaseTlv raw, ITlvConverterFactory converters);

    private readonly DecodeDelegate? _decode;

    private TlvDef(BigSize type, DecodeDelegate? decode)
    {
        Type = type;
        _decode = decode;
    }

    public BigSize Type { get; }

    public bool IsLenient { get; private init; }

    /// <summary>A typed TLV decoded through its registered converter; a decode failure fails the message.</summary>
    public static TlvDef Typed<TTlv>(BigSize type) where TTlv : BaseTlv
    {
        return new TlvDef(type, (raw, converters) =>
        {
            var converter = converters.GetConverter<TTlv>()
                         ?? throw new SerializationException($"No converter found for tlv type {nameof(TTlv)}");
            return converter.ConvertFromBase(raw);
        });
    }

    /// <summary>
    /// A raw TLV with an exact value length and no Domain type or converter: the record marks the type known
    /// (BOLT 1 unknown-even rejection), enforces the length like the legacy hand-parsers did, and exposes the
    /// value bytes via <c>Get&lt;byte[]&gt;</c> (the closing pair's 64/98/32-byte signatures, closing_tlvs 1-7).
    /// </summary>
    public static TlvDef Raw(BigSize type, int exactLength)
    {
        return new TlvDef(type, (raw, _) =>
        {
            if (raw.Value.Length != exactLength)
                throw new SerializationException(
                    $"TLV type {type.Value} holds {raw.Value.Length} bytes, not {exactLength}");
            return raw.Value;
        });
    }

    /// <summary>
    /// A known raw record of any length with no Domain type or converter (the gossip-query TLVs: query_flags 1,
    /// query_option 1, reply_channel_range timestamps 1 / checksums 3 — all odd, all kept as raw bytes): it marks
    /// the type known (BOLT 1 unknown-even rejection) and exposes the value bytes via <c>Get&lt;byte[]&gt;</c>.
    /// </summary>
    public static TlvDef RawKnown(BigSize type)
    {
        return new TlvDef(type, (raw, _) => raw.Value);
    }

    /// <summary>
    /// An advisory TLV: a decode failure leaves the typed value null and the raw record available via
    /// <see cref="WireTlvs.RawValue"/> (init's undecodable <c>remote_addr</c> / liquidity-ads rates, NL-344).
    /// </summary>
    public static TlvDef Lenient<TTlv>(BigSize type) where TTlv : BaseTlv
    {
        return new TlvDef(type, (raw, converters) =>
        {
            var converter = converters.GetConverter<TTlv>()
                         ?? throw new SerializationException($"No converter found for tlv type {nameof(TTlv)}");
            try
            {
                return converter.ConvertFromBase(raw);
            }
            catch (Exception e) when (e is InvalidCastException or ArgumentException)
            {
                return null;
            }
        })
        {
            IsLenient = true
        };
    }

    internal object? Decode(BaseTlv raw, ITlvConverterFactory converters)
    {
        if (_decode is null)
            return raw;

        try
        {
            return _decode(raw, converters);
        }
        catch (Exception e) when (!IsLenient && e is not SerializationException)
        {
            throw new SerializationException($"Error deserializing TLV type {Type.Value}", e);
        }
    }
}

/// <summary>
/// The decoded extension of one message: the typed TLVs and the raw records, both keyed by wire type. The
/// definition's decode lambda picks the typed TLVs it knows and any raw value it must keep (init's undecodable
/// advisory records).
/// </summary>
public sealed class WireTlvs
{
    public static readonly WireTlvs Empty = new([], []);

    private readonly Dictionary<BigSize, object?> _typed;
    private readonly Dictionary<BigSize, BaseTlv> _raws;

    internal WireTlvs(Dictionary<BigSize, object?> typed, Dictionary<BigSize, BaseTlv> raws,
                      ReadOnlyMemory<byte>? rawBytes = null)
    {
        _typed = typed;
        _raws = raws;
        RawBytes = rawBytes;
    }

    /// <summary>
    /// The raw extension region, when the definition asked to keep it (<see cref="WireTlvs"/> overloads): gossip
    /// payloads replay unknown records byte-verbatim, so the constructor rebuilds its ExtraData from this.
    /// </summary>
    public ReadOnlyMemory<byte>? RawBytes { get; }

    /// <summary>The typed TLV of <paramref name="type"/>, or null when the message carried none.</summary>
    public T? Get<T>(BigSize type) where T : class
    {
        return _typed.TryGetValue(type, out var value) ? value as T : null;
    }

    /// <summary>The raw value of the record of <paramref name="type"/>, or null when the message carried none.</summary>
    public byte[]? RawValue(BigSize type)
    {
        return _raws.TryGetValue(type, out var raw) ? raw.Value : null;
    }
}