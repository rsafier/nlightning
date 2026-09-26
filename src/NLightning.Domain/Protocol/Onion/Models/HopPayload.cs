namespace NLightning.Domain.Protocol.Onion.Models;

using Channels.ValueObjects;
using Constants;
using Crypto.ValueObjects;
using Money;
using Protocol.Models;
using Protocol.Tlv;
using Protocol.ValueObjects;
using Tlv;

/// <summary>
/// A parsed BOLT 4 per-hop <c>payload</c> TLV stream.
/// </summary>
/// <remarks>
/// <para>
/// Known types (<see cref="OnionPayloadTlvTypes.KnownTypes"/>) must be present as their typed TLV classes
/// (<see cref="AmtToForwardTlv"/>, <see cref="OutgoingCltvValueTlv"/>, ...). Any other type must be a raw
/// <see cref="BaseTlv"/>, which is kept verbatim so unknown odd records survive a parse/serialize round trip.
/// </para>
/// <para>
/// The typed accessors are all nullable: which fields are required depends on the hop's position and whether it is
/// inside a blinded route, and that is checked by <see cref="Validators.HopPayloadValidator"/>, not here.
/// </para>
/// <para>The instance copies the records it is given, so later changes to the source stream do not affect it.</para>
/// </remarks>
public sealed class HopPayload
{
    private static readonly Dictionary<BigSize, Type> s_knownRuntimeTypes = new()
    {
        [OnionPayloadTlvTypes.AmtToForward] = typeof(AmtToForwardTlv),
        [OnionPayloadTlvTypes.OutgoingCltvValue] = typeof(OutgoingCltvValueTlv),
        [OnionPayloadTlvTypes.ShortChannelId] = typeof(OnionShortChannelIdTlv),
        [OnionPayloadTlvTypes.PaymentData] = typeof(PaymentDataTlv),
        [OnionPayloadTlvTypes.EncryptedRecipientData] = typeof(EncryptedRecipientDataTlv),
        [OnionPayloadTlvTypes.CurrentPathKey] = typeof(CurrentPathKeyTlv),
        [OnionPayloadTlvTypes.PaymentMetadata] = typeof(PaymentMetadataTlv),
        [OnionPayloadTlvTypes.TotalAmountMsat] = typeof(TotalAmountMsatTlv)
    };

    private readonly TlvStream _tlvStream = new();
    private readonly Dictionary<BigSize, int> _recordOffsets = [];

    /// <summary>amt_to_forward (type 2).</summary>
    public LightningMoney? AmtToForward { get; }

    /// <summary>outgoing_cltv_value (type 4).</summary>
    public uint? OutgoingCltvValue { get; }

    /// <summary>short_channel_id (type 6).</summary>
    public ShortChannelId? ShortChannelId { get; }

    /// <summary>payment_data (type 8): payment_secret and total_msat.</summary>
    public PaymentDataTlv? PaymentData { get; }

    /// <summary>encrypted_recipient_data (type 10).</summary>
    public ReadOnlyMemory<byte>? EncryptedRecipientData { get; }

    /// <summary>current_path_key (type 12). Only prefix-checked; not validated as a curve point.</summary>
    public CompactPubKey? CurrentPathKey { get; }

    /// <summary>payment_metadata (type 16).</summary>
    public ReadOnlyMemory<byte>? PaymentMetadata { get; }

    /// <summary>total_amount_msat (type 18).</summary>
    public LightningMoney? TotalAmountMsat { get; }

    /// <summary>
    /// Whether the payload is for a hop inside a blinded route (<c>encrypted_recipient_data</c> is present).
    /// </summary>
    public bool IsBlinded => EncryptedRecipientData is not null;

    /// <summary>
    /// All records, in ascending type order.
    /// </summary>
    public IEnumerable<BaseTlv> Tlvs => _tlvStream.GetTlvs();

    /// <summary>
    /// The records whose type is not in <see cref="OnionPayloadTlvTypes.KnownTypes"/>, in ascending type order.
    /// </summary>
    public IEnumerable<BaseTlv> UnknownTlvs => Tlvs.Where(tlv => !OnionPayloadTlvTypes.KnownTypes.Contains(tlv.Type));

    /// <summary>
    /// Creates a payload from typed onion TLVs and/or raw unknown records.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when a known type is not given as its typed TLV class, when an unknown type is given as a typed TLV of
    /// another namespace, or when a type appears twice.
    /// </exception>
    public HopPayload(params BaseTlv[] tlvs) : this(tlvs, null)
    { }

    /// <summary>
    /// Creates a payload from the records of <paramref name="tlvStream"/>.
    /// </summary>
    /// <exception cref="ArgumentException">See <see cref="HopPayload(BaseTlv[])"/>.</exception>
    public HopPayload(TlvStream tlvStream) : this(tlvStream, null)
    { }

    /// <summary>
    /// Creates a payload from the records of <paramref name="tlvStream"/>, remembering where each record started in
    /// the decrypted byte stream so failures can report <c>invalid_onion_payload</c> offsets.
    /// </summary>
    /// <param name="tlvStream">The records.</param>
    /// <param name="recordOffsets">The byte offset of each record, keyed by type; may be <c>null</c>.</param>
    /// <exception cref="ArgumentException">See <see cref="HopPayload(BaseTlv[])"/>.</exception>
    public HopPayload(TlvStream tlvStream, IReadOnlyDictionary<BigSize, int>? recordOffsets)
        : this(tlvStream?.GetTlvs() ?? throw new ArgumentNullException(nameof(tlvStream)), recordOffsets)
    { }

    private HopPayload(IEnumerable<BaseTlv> tlvs, IReadOnlyDictionary<BigSize, int>? recordOffsets)
    {
        ArgumentNullException.ThrowIfNull(tlvs);

        foreach (var tlv in tlvs)
        {
            ArgumentNullException.ThrowIfNull(tlv, nameof(tlvs));
            EnsureExpectedRuntimeType(tlv);
            _tlvStream.Add(tlv);

            switch (tlv)
            {
                case AmtToForwardTlv amtToForward:
                    AmtToForward = amtToForward.AmountToForward;
                    break;
                case OutgoingCltvValueTlv outgoingCltvValue:
                    OutgoingCltvValue = outgoingCltvValue.OutgoingCltvValue;
                    break;
                case OnionShortChannelIdTlv shortChannelId:
                    ShortChannelId = shortChannelId.ShortChannelId;
                    break;
                case PaymentDataTlv paymentData:
                    PaymentData = paymentData;
                    break;
                case EncryptedRecipientDataTlv encryptedRecipientData:
                    EncryptedRecipientData = encryptedRecipientData.EncryptedRecipientData;
                    break;
                case CurrentPathKeyTlv currentPathKey:
                    CurrentPathKey = currentPathKey.PathKey;
                    break;
                case PaymentMetadataTlv paymentMetadata:
                    PaymentMetadata = paymentMetadata.PaymentMetadata;
                    break;
                case TotalAmountMsatTlv totalAmountMsat:
                    TotalAmountMsat = totalAmountMsat.TotalAmount;
                    break;
            }
        }

        if (recordOffsets is null)
            return;

        foreach (var (type, offset) in recordOffsets)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(recordOffsets), "Record offsets cannot be negative.");

            _recordOffsets[type] = offset;
        }
    }

    /// <summary>
    /// Gets the record of <paramref name="type"/>, if present.
    /// </summary>
    public bool TryGetTlv(BigSize type, out BaseTlv? tlv)
    {
        return _tlvStream.TryGetTlv(type, out tlv);
    }

    /// <summary>
    /// Gets the byte offset at which the record of <paramref name="type"/> started in the decrypted byte stream.
    /// </summary>
    /// <returns><c>false</c> when the record is absent or the payload was not built by a parser.</returns>
    public bool TryGetRecordOffset(BigSize type, out int offset)
    {
        return _recordOffsets.TryGetValue(type, out offset) && _tlvStream.TryGetTlv(type, out _);
    }

    /// <summary>
    /// Returns a new <see cref="TlvStream"/> holding the same records (for serialization).
    /// </summary>
    public TlvStream ToTlvStream()
    {
        var tlvStream = new TlvStream();
        foreach (var tlv in _tlvStream.GetTlvs())
            tlvStream.Add(tlv);

        return tlvStream;
    }

    private static void EnsureExpectedRuntimeType(BaseTlv tlv)
    {
        var expectedType = GetExpectedRuntimeType(tlv.Type);
        if (tlv.GetType() == expectedType)
            return;

        throw new ArgumentException(
            $"TLV type {tlv.Type.Value} must be a {expectedType.Name} in a hop payload, not a {tlv.GetType().Name}.",
            nameof(tlv));
    }

    private static Type GetExpectedRuntimeType(BigSize type)
    {
        return s_knownRuntimeTypes.GetValueOrDefault(type, typeof(BaseTlv));
    }
}