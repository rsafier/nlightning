namespace NLightning.Domain.Channels.Factories;

using Bitcoin.Interfaces;
using Bitcoin.Transactions.Enums;
using Bitcoin.Transactions.Extensions;
using Bitcoin.Transactions.Factories;
using Bitcoin.Transactions.Outputs;
using Bitcoin.ValueObjects;
using Client.Requests;
using Closing;
using Constants;
using Crypto.Hashes;
using Crypto.ValueObjects;
using Domain.Enums;
using Enums;
using Exceptions;
using Interfaces;
using Models;
using Money;
using Node;
using Node.Options;
using Policies;
using Protocol.Interfaces;
using Protocol.Messages;
using Protocol.Models;
using Protocol.Payloads;
using Validators.Parameters;
using ValueObjects;

public class ChannelFactory : IChannelFactory
{
    private readonly IChannelIdFactory _channelIdFactory;
    private readonly IChannelOpenValidator _channelOpenValidator;
    private readonly IFeeService _feeService;
    private readonly ILightningSigner _lightningSigner;
    private readonly NodeOptions _nodeOptions;
    private readonly ISha256 _sha256;

    public ChannelFactory(IChannelIdFactory channelIdFactory, IChannelOpenValidator channelOpenValidator,
                          IFeeService feeService, ILightningSigner lightningSigner, NodeOptions nodeOptions,
                          ISha256 sha256)
    {
        _channelIdFactory = channelIdFactory;
        _channelOpenValidator = channelOpenValidator;
        _feeService = feeService;
        _lightningSigner = lightningSigner;
        _nodeOptions = nodeOptions;
        _sha256 = sha256;
    }

    public async Task<ChannelModel> CreateChannelV1AsNonInitiatorAsync(OpenChannel1Message message,
                                                                       FeatureOptions negotiatedFeatures,
                                                                       CompactPubKey remoteNodeId)
    {
        var payload = message.Payload;

        // If dual fund is negotiated fail the channel
        if (negotiatedFeatures.DualFund == FeatureSupport.Compulsory)
            throw new ChannelErrorException("We can only accept dual fund channels", payload.ChannelId);

        // Perform optional checks for the channel
        _channelOpenValidator.PerformOptionalChecks(
            ChannelOpenOptionalValidationParameters.FromOpenChannel1Payload(payload));

        // Perform mandatory checks for the channel
        var currentFee = await _feeService.GetFeeRatePerKwAsync();
        _channelOpenValidator.PerformMandatoryChecks(
            ChannelOpenMandatoryValidationParameters.FromOpenChannel1Payload(
                message.ChannelTypeTlv, currentFee, negotiatedFeatures, payload), out var minimumDepth);

        // BOLT 2: upfront_shutdown_script is only required when option_upfront_shutdown_script was negotiated (NL-046);
        // a channel_type alone doesn't make it mandatory
        if (message.UpfrontShutdownScriptTlv is null && negotiatedFeatures.UpfrontShutdownScript > FeatureSupport.No)
            throw new ChannelErrorException("Upfront shutdown script is required but not provided", payload.ChannelId);

        BitcoinScript? remoteUpfrontShutdownScript = null;
        if (message.UpfrontShutdownScriptTlv is not null && message.UpfrontShutdownScriptTlv.Value.Length > 0)
        {
            // BOLT 2: a non-empty upfront_shutdown_script is a shutdown form the negotiated features allow; a P2TR
            // script without option_shutdown_anysegwit could never close cooperatively (NL-776)
            if (!ShutdownScriptValidator.IsValidUpfront(message.UpfrontShutdownScriptTlv.Value, negotiatedFeatures))
                throw new ChannelErrorException("upfront_shutdown_script is not a valid shutdown script",
                                                payload.ChannelId, "upfront_shutdown_script is not a valid form");

            remoteUpfrontShutdownScript = message.UpfrontShutdownScriptTlv.Value;
        }

        // Calculate the amounts
        var toLocalAmount = payload.PushAmount;
        var toRemoteAmount = payload.FundingAmount - payload.PushAmount;

        // Generate local keys through the signer
        var localKeyIndex = _lightningSigner.CreateNewChannel(out var localBasepoints, out var firstPerCommitmentPoint);

        // Create the local key set
        var localKeySet = new ChannelKeySetModel(localKeyIndex, localBasepoints.FundingPubKey,
                                                 localBasepoints.RevocationBasepoint, localBasepoints.PaymentBasepoint,
                                                 localBasepoints.DelayedPaymentBasepoint, localBasepoints.HtlcBasepoint,
                                                 firstPerCommitmentPoint);

        // Create the remote key set from the message
        var remoteKeySet = ChannelKeySetModel.CreateForRemote(message.Payload.FundingPubKey,
                                                              message.Payload.RevocationBasepoint,
                                                              message.Payload.PaymentBasepoint,
                                                              message.Payload.DelayedPaymentBasepoint,
                                                              message.Payload.HtlcBasepoint,
                                                              message.Payload.FirstPerCommitmentPoint);

        // Our upfront shutdown script is a reserved wallet address, set by the caller once the channel is admitted
        // (ChannelModel.SetLocalUpfrontShutdownScript, NL-045); without one a zero-length script is sent
        BitcoinScript? localUpfrontShutdownScript = null;

        // Generate the channel configuration
        var useScidAlias = FeatureSupport.No;
        if (negotiatedFeatures.ScidAlias > FeatureSupport.No)
        {
            if (message.ChannelTypeTlv?.Features.IsFeatureSet(Feature.OptionScidAlias, true) ?? false)
                useScidAlias = FeatureSupport.Compulsory;
            else
                useScidAlias = FeatureSupport.Optional;
        }

        // The opener's values bind us (NL-194); ours are announced in accept_channel and bind the opener
        var remoteParams = new ChannelParty(payload.DustLimitAmount, payload.ChannelReserveAmount,
                                            payload.HtlcMinimumAmount, payload.MaxAcceptedHtlcs,
                                            payload.MaxHtlcValueInFlight, payload.ToSelfDelay,
                                            remoteUpfrontShutdownScript);
        var localParams = CreateLocalParamsAsNonInitiator(payload, localUpfrontShutdownScript,
                                                          negotiatedFeatures.OptionSplice > FeatureSupport.No);

        // The channel type decides anchors, not the init features (the opener may pick a type without them); a simple
        // taproot type (NL-877 T5, accepted by the validator only when negotiated and private) keeps the anchors
        // semantics
        var optionAnchorOutputs = message.ChannelTypeTlv?.Features.IsFeatureSet(Feature.OptionAnchors, true) ?? false;
        var isSimpleTaproot = TaprootChannelType.IsTaprootChannelType(message.ChannelTypeTlv?.Features);
        // The opener's announce_channel bit is stored with the channel (NL-341): a public channel is announced once it
        // is deep enough (BOLT 7). The validator refused it together with option_scid_alias in the channel type
        var channelParams = new ChannelParams(localParams, remoteParams, payload.FeeRatePerKw, minimumDepth,
                                              optionAnchorOutputs, useScidAlias)
        {
            AnnounceChannel = payload.ChannelFlags.AnnounceChannel,
            OptionSimpleTaproot = isSimpleTaproot
        };

        // Generate the commitment number (the remote is the opener: opener basepoint first)
        var commitmentNumber = new CommitmentNumber(remoteKeySet.PaymentCompactBasepoint,
                                                    localKeySet.PaymentCompactBasepoint, _sha256);

        try
        {
            var fundingOutput = new FundingOutputInfo(payload.FundingAmount, localKeySet.FundingCompactPubKey,
                                                      remoteKeySet.FundingCompactPubKey);

            // Create the channel
            return new ChannelModel(channelParams, payload.ChannelId, commitmentNumber, fundingOutput, false, null,
                                    null, toLocalAmount, localKeySet, 0, 0, toRemoteAmount, remoteKeySet, 0,
                                    remoteNodeId, 0, ChannelState.V1Opening, ChannelVersion.V1);
        }
        catch (Exception e)
        {
            throw new ChannelErrorException("Error creating commitment transaction", payload.ChannelId, e);
        }
    }

    public async Task<ChannelModel> CreateChannelV1AsInitiatorAsync(OpenChannelClientRequest request,
                                                                    FeatureOptions negotiatedFeatures,
                                                                    CompactPubKey remoteNodeId)
    {
        // If dual fund is negotiated fail the channel
        if (negotiatedFeatures.DualFund == FeatureSupport.Compulsory)
            throw new ChannelErrorException("We can only open dual fund channels to this peer");

        // Check if the FundingAmount is too small
        if (request.FundingAmount < _nodeOptions.MinimumChannelSize)
            throw new ChannelErrorException(
                $"Funding amount is smaller than our MinimumChannelSize: {request.FundingAmount} < {_nodeOptions.MinimumChannelSize}");

        // Check if our fee is too big
        if (request.FeeRatePerKw is not null && request.FeeRatePerKw > ChannelConstants.MaxFeePerKw)
            throw new ChannelErrorException($"Fee rate per kw is too large: {request.FeeRatePerKw}");

        // Check if our fee is too big
        if (request.FeeRatePerKw is not null && request.FeeRatePerKw < ChannelConstants.MinFeePerKw)
            throw new ChannelErrorException($"Fee rate per kw is too small: {request.FeeRatePerKw}");

        // Check if the dust limit is greater than the channel reserve amount
        var channelReserveAmount = GetOurChannelReserveFromFundingAmount(request.FundingAmount);
        if (request.ChannelReserveAmount is not null && request.ChannelReserveAmount > channelReserveAmount)
            channelReserveAmount = request.ChannelReserveAmount;

        // Announce our configured dust limit unless the request overrides it (open_channel carries this value)
        var dustLimitAmount = _nodeOptions.DustLimitAmount;
        if (request.DustLimitAmount is not null)
        {
            // Check if dust_limit_satoshis is too small
            if (request.DustLimitAmount < ChannelConstants.MinDustLimitAmount)
                throw new ChannelErrorException($"Dust limit amount is too small: {request.DustLimitAmount}");

            dustLimitAmount = request.DustLimitAmount;
        }

        if (dustLimitAmount > channelReserveAmount)
            channelReserveAmount = dustLimitAmount;

        // Check if there are enough funds to pay for fees
        // Our estimate, at least Node:MinCommitmentFeeRatePerKw (NL-564); a feerate the request names is used as given
        var currentFeeRatePerKw = request.FeeRatePerKw
                               ?? LightningMoney.Satoshis(_nodeOptions.GetCommitmentFeeRatePerKw(
                                                              (await _feeService.GetFeeRatePerKwAsync()).Satoshi));
        // A simple taproot channel only when the operator asks for it (plan D-T2: anchors stay the default type of our
        // opens; NL-877 T5)
        if (request.IsSimpleTaproot)
            CheckSimpleTaprootOpen(request, negotiatedFeatures);
        var hasAnchors = negotiatedFeatures.OptionAnchors > FeatureSupport.No;
        var format = request.IsSimpleTaproot
                         ? CommitmentFormat.SimpleTaproot
                         : CommitmentFormatExtensions.FromOptionAnchors(hasAnchors);
        var expectedFee = CommitmentFeeCalculator.FunderCost((ulong)currentFeeRatePerKw.Satoshi, format, 0);
        if (request.FundingAmount < expectedFee + channelReserveAmount)
            throw new ChannelErrorException($"Funding amount is too small to cover fees: {request.FundingAmount}");

        // Check the push amount: it can't exceed the funding, and our remaining amount must pay the full fee
        var pushAmount = request.PushAmount ?? LightningMoney.Zero;
        if (pushAmount > request.FundingAmount)
            throw new ChannelErrorException($"Push amount is too large: {pushAmount} > {request.FundingAmount}");

        if (request.FundingAmount - pushAmount < expectedFee)
            throw new ChannelErrorException(
                $"Funder amount is too small to cover fees: {request.FundingAmount - pushAmount} < {expectedFee}");

        // Check if this is a large channel and if we support it
        if (request.FundingAmount >= ChannelConstants.LargeChannelAmount &&
            negotiatedFeatures.LargeChannels == FeatureSupport.No)
            throw new ChannelErrorException("The peer doesn't support large channels");

        // Check if we want zeroconf and if it's negotiated
        var minimumDepth = _nodeOptions.MinimumDepth;
        if (request.IsZeroConfChannel)
        {
            // A zero-conf channel has no confirmed short channel id to announce (BOLT 7 plan: public + zeroconf is
            // refused)
            if (request.IsPublic)
                throw new ChannelErrorException("A public channel can't be zero-conf");

            if (_nodeOptions.Features.ZeroConf == FeatureSupport.No)
                throw new ChannelErrorException(
                    "ZeroConf feature not supported, change our configuration and try again");

            if (negotiatedFeatures.ZeroConf == FeatureSupport.No)
                throw new ChannelErrorException("ZeroConf not supported by our peer");

            minimumDepth = 0U;
        }

        // Calculate the amounts
        var toRemoteAmount = request.PushAmount ?? LightningMoney.Zero;
        var toLocalAmount = request.FundingAmount - toRemoteAmount;

        // Generate our MaxHtlcValueInFlight if not provided: no cap on a channel that can be spliced (NL-880)
        var maxHtlcValueInFlight = request.MaxHtlcValueInFlight
                                ?? MaxHtlcValueInFlightRules.GetAnnounced(
                                       _nodeOptions, request.FundingAmount,
                                       negotiatedFeatures.OptionSplice > FeatureSupport.No);

        // Generate local keys through the signer
        var localKeyIndex = _lightningSigner.CreateNewChannel(out var localBasepoints, out var firstPerCommitmentPoint);

        // Create the local key set
        var localKeySet = new ChannelKeySetModel(localKeyIndex, localBasepoints.FundingPubKey,
                                                 localBasepoints.RevocationBasepoint, localBasepoints.PaymentBasepoint,
                                                 localBasepoints.DelayedPaymentBasepoint, localBasepoints.HtlcBasepoint,
                                                 firstPerCommitmentPoint);

        // Our upfront shutdown script is a reserved wallet address, set by the caller before open_channel is sent
        // (ChannelModel.SetLocalUpfrontShutdownScript, NL-045). BOLT 2 allows a zero-length one even when the feature
        // is negotiated, so a compulsory peer is no reason to refuse the open
        BitcoinScript? localUpfrontShutdownScript = null;

        // Generate the channel configuration: only our values are known until accept_channel arrives
        var localParams = new ChannelParty(dustLimitAmount, channelReserveAmount,
                                           request.HtlcMinimumAmount ?? _nodeOptions.HtlcMinimumAmount,
                                           request.MaxAcceptedHtlcs ?? _nodeOptions.MaxAcceptedHtlcs,
                                           maxHtlcValueInFlight, request.ToSelfDelay ?? _nodeOptions.ToSelfDelay,
                                           localUpfrontShutdownScript);

        // We put option_scid_alias in the channel type whenever the peer negotiated it, except for a public channel:
        // BOLT 2 forbids option_scid_alias in the channel type together with announce_channel (the channel_ready alias
        // is still exchanged when the feature is negotiated)
        var useScidAlias = negotiatedFeatures.ScidAlias == FeatureSupport.No
                               ? FeatureSupport.No
                               : request.IsPublic
                                   ? FeatureSupport.Optional
                                   : FeatureSupport.Compulsory;
        var channelParams = new ChannelParams(localParams, ChannelParty.Unknown,
                                              request.FeeRatePerKw ?? currentFeeRatePerKw, minimumDepth,
                                              negotiatedFeatures.OptionAnchors != FeatureSupport.No, useScidAlias)
        {
            AnnounceChannel = request.IsPublic,
            OptionSimpleTaproot = request.IsSimpleTaproot
        };

        try
        {
            // Create the channel using only our data
            return new ChannelModel(channelParams, _channelIdFactory.CreateTemporaryChannelId(), null,
                                    null, true, null, null, toLocalAmount, localKeySet, 0, 0, toRemoteAmount,
                                    null, 0, remoteNodeId, 0, ChannelState.V1Opening, ChannelVersion.V1);
        }
        catch (Exception e)
        {
            throw new ChannelErrorException("Error creating commitment transaction", e);
        }
    }

    /// <summary>
    /// Our simple taproot open (NL-877 T5): our <c>Features:OptionSimpleTaproot</c> advertised (the experimental gate:
    /// <c>Features:AllowExperimentalFeatures</c>), the peer supporting it and <c>option_simple_close</c> (the spec's
    /// dependency; LND and Eclair refuse it otherwise), and a private channel (the spec forbids
    /// <c>announce_channel</c>; taproot gossip is T7).
    /// </summary>
    private void CheckSimpleTaprootOpen(OpenChannelClientRequest request, FeatureOptions negotiatedFeatures)
    {
        if (!_nodeOptions.Features.IsSimpleTaprootAdvertised)
            throw new ChannelErrorException(
                "Simple taproot channels are not enabled on this node: set Features:OptionSimpleTaproot=Optional "
              + "and Features:AllowExperimentalFeatures=true");
        if (negotiatedFeatures.OptionSimpleTaproot == FeatureSupport.No)
            throw new ChannelErrorException("The peer does not support simple taproot channels (feature bits 80/81)");
        if (negotiatedFeatures.OptionSimpleClose == FeatureSupport.No)
            throw new ChannelErrorException(
                "A simple taproot channel needs option_simple_close, which the peer did not negotiate");
        if (request.IsPublic)
            throw new ChannelErrorException("Simple taproot channels are private: --public can't be used with them");
    }

    /// <summary>
    /// The values we announce in accept_channel. BOLT 2: our channel_reserve_satoshis must be at least the opener's
    /// dust_limit_satoshis, and our dust_limit_satoshis at most the opener's channel_reserve_satoshis.
    /// </summary>
    private ChannelParty CreateLocalParamsAsNonInitiator(OpenChannel1Payload payload,
                                                         BitcoinScript? localUpfrontShutdownScript,
                                                         bool spliceNegotiated)
    {
        var dustLimitAmount = _nodeOptions.DustLimitAmount;
        if (dustLimitAmount > payload.ChannelReserveAmount)
            throw new ChannelErrorException(
                $"Our dust limit ({dustLimitAmount}) is above the opener's channel reserve ({payload.ChannelReserveAmount})",
                payload.ChannelId, "Channel reserve is below our dust limit");

        var channelReserveAmount = GetOurChannelReserveFromFundingAmount(payload.FundingAmount);
        if (channelReserveAmount < payload.DustLimitAmount)
            channelReserveAmount = payload.DustLimitAmount;
        if (channelReserveAmount < dustLimitAmount)
            channelReserveAmount = dustLimitAmount;

        var maxHtlcValueInFlight = MaxHtlcValueInFlightRules.GetAnnounced(_nodeOptions, payload.FundingAmount,
                                                                          spliceNegotiated);

        return new ChannelParty(dustLimitAmount, channelReserveAmount, _nodeOptions.HtlcMinimumAmount,
                                _nodeOptions.MaxAcceptedHtlcs, maxHtlcValueInFlight, _nodeOptions.ToSelfDelay,
                                localUpfrontShutdownScript);
    }

    private LightningMoney GetOurChannelReserveFromFundingAmount(LightningMoney fundingAmount)
    {
        return fundingAmount * 0.01M;
    }
}