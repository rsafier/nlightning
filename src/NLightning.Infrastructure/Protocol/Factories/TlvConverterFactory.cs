namespace NLightning.Infrastructure.Protocol.Factories;

using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Tlv.Converters;
using Tlv.Converters.Onion;

public class TlvConverterFactory : ITlvConverterFactory
{
    private readonly Dictionary<Type, ITlvConverter> _converters = new();

    public TlvConverterFactory()
    {
        RegisterConverters();
    }

    public ITlvConverter<TTlv>? GetConverter<TTlv>() where TTlv : BaseTlv
    {
        return _converters.GetValueOrDefault(typeof(TTlv)) as ITlvConverter<TTlv>;
    }

    public ITlvConverter? GetConverter(Type tlvType)
    {
        ArgumentNullException.ThrowIfNull(tlvType);
        return _converters.GetValueOrDefault(tlvType);
    }

    /// <summary>
    /// The TLV types that have a registered converter.
    /// </summary>
    public IReadOnlyCollection<Type> RegisteredTlvTypes => _converters.Keys;

    private void RegisterConverters()
    {
        _converters.Add(typeof(BlindedPathTlv), new BlindedPathTlvConverter());
        _converters.Add(typeof(ChannelTypeTlv), new ChannelTypeTlvConverter());
        _converters.Add(typeof(FeeRangeTlv), new FeeRangeTlvConverter());
        _converters.Add(typeof(FundingOutputContributionTlv), new FundingOutputContributionTlvConverter());
        _converters.Add(typeof(NetworksTlv), new NetworksTlvConverter());
        _converters.Add(typeof(NextFundingTlv), new NextFundingTlvConverter());
        _converters.Add(typeof(RemoteAddressTlv), new RemoteAddressTlvConverter());
        _converters.Add(typeof(RequireConfirmedInputsTlv), new RequireConfirmedInputsTlvConverter());
        _converters.Add(typeof(ShortChannelIdTlv), new ShortChannelIdTlvConverter());
        _converters.Add(typeof(UpfrontShutdownScriptTlv), new UpfrontShutdownScriptTlvConverter());

        // Onion hop payload (BOLT 4) TLVs
        _converters.Add(typeof(AmtToForwardTlv), new AmtToForwardTlvConverter());
        _converters.Add(typeof(OutgoingCltvValueTlv), new OutgoingCltvValueTlvConverter());
        _converters.Add(typeof(OnionShortChannelIdTlv), new OnionShortChannelIdTlvConverter());
        _converters.Add(typeof(PaymentDataTlv), new PaymentDataTlvConverter());
        _converters.Add(typeof(EncryptedRecipientDataTlv), new EncryptedRecipientDataTlvConverter());
        _converters.Add(typeof(CurrentPathKeyTlv), new CurrentPathKeyTlvConverter());
        _converters.Add(typeof(PaymentMetadataTlv), new PaymentMetadataTlvConverter());
        _converters.Add(typeof(TotalAmountMsatTlv), new TotalAmountMsatTlvConverter());
    }
}