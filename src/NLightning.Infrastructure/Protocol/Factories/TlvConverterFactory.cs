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
        _converters.Add(typeof(AttributionDataTlv), new AttributionDataTlvConverter());
        _converters.Add(typeof(BlindedPathTlv), new BlindedPathTlvConverter());
        _converters.Add(typeof(ChannelTypeTlv), new ChannelTypeTlvConverter());
        _converters.Add(typeof(FeeRangeTlv), new FeeRangeTlvConverter());
        _converters.Add(typeof(FundingOutputContributionTlv), new FundingOutputContributionTlvConverter());
        _converters.Add(typeof(FulfillmentPayloadTlv), new FulfillmentPayloadTlvConverter());
        _converters.Add(typeof(FundingTxIdTlv), new FundingTxIdTlvConverter());
        _converters.Add(typeof(NetworksTlv), new NetworksTlvConverter());
        _converters.Add(typeof(MyCurrentFundingLockedTlv), new MyCurrentFundingLockedTlvConverter());
        _converters.Add(typeof(NextFundingTlv), new NextFundingTlvConverter());
        _converters.Add(typeof(RemoteAddressTlv), new RemoteAddressTlvConverter());
        _converters.Add(typeof(RequireConfirmedInputsTlv), new RequireConfirmedInputsTlvConverter());
        _converters.Add(typeof(SharedInputSignatureTlv), new SharedInputSignatureTlvConverter());
        _converters.Add(typeof(SharedInputTxIdTlv), new SharedInputTxIdTlvConverter());
        _converters.Add(typeof(ShortChannelIdTlv), new ShortChannelIdTlvConverter());
        _converters.Add(typeof(StartBatchMessageTypeTlv), new StartBatchMessageTypeTlvConverter());
        _converters.Add(typeof(UpfrontShutdownScriptTlv), new UpfrontShutdownScriptTlvConverter());

        // Simple taproot channels (option_simple_taproot) and BOLTs PR #1324 (taproot interactive-tx/splices; NL-877)
        _converters.Add(typeof(CommitNoncesTlv), new CommitNoncesTlvConverter());
        _converters.Add(typeof(CurrentCommitNonceTlv), new CurrentCommitNonceTlvConverter());
        _converters.Add(typeof(FundingNonceTlv), new FundingNonceTlvConverter());
        _converters.Add(typeof(NextCloseeNonceTlv), new NextCloseeNonceTlvConverter());
        _converters.Add(typeof(NextLocalNonceTlv), new NextLocalNonceTlvConverter());
        _converters.Add(typeof(NextLocalNoncesTlv), new NextLocalNoncesTlvConverter());
        _converters.Add(typeof(PartialSignatureWithNonceTlv), new PartialSignatureWithNonceTlvConverter());
        _converters.Add(typeof(SharedInputPartialSignatureTlv), new SharedInputPartialSignatureTlvConverter());
        _converters.Add(typeof(ShutdownNonceTlv), new ShutdownNonceTlvConverter());

        // Liquidity ads (BOLT PR #1153, TLV 1339; NL-850)
        _converters.Add(typeof(RequestFundingTlv), new RequestFundingTlvConverter());
        _converters.Add(typeof(ProvideFundingTlv), new ProvideFundingTlvConverter());
        _converters.Add(typeof(WillFundRatesTlv), new WillFundRatesTlvConverter());

        // Onion hop payload (BOLT 4) TLVs
        _converters.Add(typeof(AmtToForwardTlv), new AmtToForwardTlvConverter());
        _converters.Add(typeof(OutgoingCltvValueTlv), new OutgoingCltvValueTlvConverter());
        _converters.Add(typeof(OnionShortChannelIdTlv), new OnionShortChannelIdTlvConverter());
        _converters.Add(typeof(PaymentDataTlv), new PaymentDataTlvConverter());
        _converters.Add(typeof(EncryptedRecipientDataTlv), new EncryptedRecipientDataTlvConverter());
        _converters.Add(typeof(CurrentPathKeyTlv), new CurrentPathKeyTlvConverter());
        _converters.Add(typeof(PaymentMetadataTlv), new PaymentMetadataTlvConverter());
        _converters.Add(typeof(TotalAmountMsatTlv), new TotalAmountMsatTlvConverter());

        // Trampoline hop payload TLVs (BOLTs PR 836, NL-875)
        _converters.Add(typeof(OutgoingNodeIdTlv), new OutgoingNodeIdTlvConverter());
        _converters.Add(typeof(TrampolineOnionPacketTlv), new TrampolineOnionPacketTlvConverter());
        _converters.Add(typeof(RecipientFeaturesTlv), new RecipientFeaturesTlvConverter());
        _converters.Add(typeof(RecipientBlindedPathsTlv), new RecipientBlindedPathsTlvConverter());
    }
}