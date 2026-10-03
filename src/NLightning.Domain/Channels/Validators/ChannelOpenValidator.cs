namespace NLightning.Domain.Channels.Validators;

using Bitcoin.Transactions.Factories;
using Constants;
using Domain.Enums;
using Exceptions;
using Interfaces;
using Money;
using Node;
using Node.Options;
using Parameters;

public class ChannelOpenValidator : IChannelOpenValidator
{
    /// <summary>
    /// The (even) channel type bits we can operate a channel with.
    /// </summary>
    private static readonly HashSet<int> s_supportedChannelTypeBits =
    [
        (int)Feature.OptionStaticRemoteKey - 1,
        (int)Feature.OptionAnchors - 1,
        (int)Feature.OptionScidAlias - 1,
        (int)Feature.OptionZeroconf - 1
    ];

    /// <summary>
    /// The lowest <c>feerate_per_kw</c> we accept in <c>open_channel</c>: BOLT 3's 253 sat/kw relay floor
    /// (<see cref="FeeUpdateOptions.FeeratePerKwFloor"/>, the floor we also apply to <c>update_fee</c>).
    /// </summary>
    public static readonly LightningMoney MinAcceptableFeeRatePerKw =
        LightningMoney.Satoshis(FeeUpdateOptions.FeeratePerKwFloor);

    /// <summary>
    /// The peer's <c>channel_reserve_satoshis</c> we always accept, whatever the channel size (with
    /// <see cref="NodeOptions.MaxAcceptedChannelReservePercent"/>): LDK's minimum reserve (NL-562).
    /// </summary>
    public static readonly LightningMoney MinAcceptedChannelReserveCap = LightningMoney.Satoshis(1_000);

    /// <summary>
    /// The smallest <c>max_accepted_htlcs</c> we accept from a peer (the old rule's value with our default of 5;
    /// LND and CLN offer 483, LDK 50, Eclair 30).
    /// </summary>
    public const ushort MinAcceptedMaxAcceptedHtlcs = 4;

    private readonly NodeOptions _nodeOptions;

    public ChannelOpenValidator(NodeOptions nodeOptions)
    {
        _nodeOptions = nodeOptions;
    }

    /// <inheritdoc/> 
    public void PerformOptionalChecks(ChannelOpenOptionalValidationParameters parameters)
    {
        // Check if Funding Satoshis is too small
        if (parameters.FundingAmount is not null && parameters.FundingAmount < _nodeOptions.MinimumChannelSize)
            throw new ChannelErrorException($"Funding amount is too small: {parameters.FundingAmount}");

        // Check if we consider htlc_minimum_msat too large. The peer's minimum only bounds the smallest HTLC we can
        // offer it, so, like LDK, we refuse only a minimum that leaves the channel unusable: the whole channel or more
        // (NL-562; the old 1.2 x our own minimum refused LND's 1,000 msat as soon as ours was set below 834 msat)
        if (parameters.HtlcMinimumAmount >= parameters.ChannelAmount)
            throw new ChannelErrorException(
                $"Htlc minimum amount is too large: {parameters.HtlcMinimumAmount.MilliSatoshi} msat "
              + $">= the channel's {parameters.ChannelAmount.Satoshi} sat");

        // Check if we consider max_htlc_value_in_flight_msat too small (Node:MinAcceptedMaxHtlcValueInFlightPercent)
        if (parameters.FundingAmount is not null && parameters.MaxHtlcValueInFlight is not null)
            CheckMaxHtlcValueInFlight(parameters.FundingAmount, parameters.MaxHtlcValueInFlight);

        // Check if we consider channel_reserve_satoshis too large (Node:MaxAcceptedChannelReservePercent, NL-562)
        CheckChannelReserve(parameters.ChannelAmount, parameters.ChannelReserveAmount);

        // Check if we consider max_accepted_htlcs too small. The peer's limit only bounds how many HTLCs we can offer
        // it at once and is independent of ours (NL-562: 0.8 x ours refused Eclair's 30 and LDK's 50 as soon as ours
        // was raised to LND's 483), so the floor is a constant
        if (parameters.MaxAcceptedHtlcs < MinAcceptedMaxAcceptedHtlcs)
            throw new ChannelErrorException(
                $"Max accepted htlcs is too small: {parameters.MaxAcceptedHtlcs} < {MinAcceptedMaxAcceptedHtlcs}");

        // Check if we consider dust_limit_satoshis too large. IE. 75% bigger than our dust limit (619 sat with our
        // default 354; every implementation's default is 354 or 546)
        if (parameters.DustLimitAmount > _nodeOptions.DustLimitAmount * 1.75M)
            throw new ChannelErrorException(
                $"Dust limit amount is too large: {parameters.DustLimitAmount.Satoshi} sat "
              + $"> 1.75 x our {_nodeOptions.DustLimitAmount.Satoshi} sat");
    }

    /// <summary>
    /// Fails the open when the peer's <c>channel_reserve_satoshis</c> is above the larger of
    /// <see cref="NodeOptions.MaxAcceptedChannelReservePercent"/> of the channel and
    /// <see cref="MinAcceptedChannelReserveCap"/>. Only v1 opens carry it (open_channel2 and accept_channel2 have no
    /// reserve field: it is 1 % of the channel there).
    /// </summary>
    private void CheckChannelReserve(LightningMoney channelAmount, LightningMoney channelReserve)
    {
        // A cap relative to the channel, not to our own 1 % reserve (NL-562): LDK asks for at least 1,000 sat
        // (MIN_THEIR_CHAN_RESERVE_SATOSHIS), 2 % of a 50k channel; LND refuses above 20 %
        var percentCap = LightningMoney.Satoshis(channelAmount.Satoshi
                                               * (long)_nodeOptions.MaxAcceptedChannelReservePercent / 100);
        var cap = percentCap > MinAcceptedChannelReserveCap ? percentCap : MinAcceptedChannelReserveCap;
        if (channelReserve > cap)
            throw new ChannelErrorException(
                $"Channel reserve amount is too large: {channelReserve.Satoshi} sat > {cap.Satoshi} sat "
              + $"(max of {_nodeOptions.MaxAcceptedChannelReservePercent} % of {channelAmount.Satoshi} sat and "
              + $"{MinAcceptedChannelReserveCap.Satoshi} sat)");
    }

    /// <inheritdoc/>
    public void CheckMaxHtlcValueInFlight(LightningMoney channelAmount, LightningMoney maxHtlcValueInFlight)
    {
        // A floor, not a match of our own limit (NL-552): LDK offers 10 % by default, Eclair 45 %
        var floor = LightningMoney.MilliSatoshis((ulong)(channelAmount.MilliSatoshi
                                                       * (decimal)_nodeOptions.MinAcceptedMaxHtlcValueInFlightPercent
                                                       / 100M));
        if (maxHtlcValueInFlight < floor)
            throw new ChannelErrorException(
                $"Max htlc value in flight is too small: {maxHtlcValueInFlight} < {floor} "
              + $"({_nodeOptions.MinAcceptedMaxHtlcValueInFlightPercent} % of {channelAmount})");
    }

    /// <inheritdoc/> 
    public void PerformMandatoryChecks(ChannelOpenMandatoryValidationParameters parameters,
                                       out uint minimumDepth)
    {
        // Check if ChainHash is compatible
        if (parameters.ChainHash is not null && parameters.ChainHash != _nodeOptions.BitcoinNetwork.ChainHash)
            throw new ChannelErrorException("ChainHash is not compatible");

        // BOLT 2: fail the channel if to_self_delay is unreasonably large (Node:MaxAcceptedToSelfDelay, NL-550)
        if (parameters.ToSelfDelay > _nodeOptions.MaxAcceptedToSelfDelay)
            throw new ChannelErrorException(
                $"To self delay is too large: {parameters.ToSelfDelay} > {_nodeOptions.MaxAcceptedToSelfDelay}");

        // Check max_accepted_htlcs is too large
        if (parameters.MaxAcceptedHtlcs > ChannelConstants.MaxAcceptedHtlcs)
            throw new ChannelErrorException($"Max accepted htlcs is too small: {parameters.MaxAcceptedHtlcs}");

        if (parameters.FeeRatePerKw is not null)
        {
            // BOLT 2: fail the channel if feerate_per_kw is unreasonably large
            if (parameters.FeeRatePerKw > ChannelConstants.MaxFeePerKw)
                throw new ChannelErrorException($"Fee rate per kw is too large: {parameters.FeeRatePerKw}");

            // BOLT 2: fail the channel if feerate_per_kw is too small for timely processing. The opener chooses and
            // pays the feerate, and peers open at their own estimate (CLN opens at 253 sat/kw on an idle chain), so
            // anything from the relay floor up is accepted, whatever our estimate says (NL-289), as LND and CLN do
            if (parameters.FeeRatePerKw < MinAcceptableFeeRatePerKw)
                throw new ChannelErrorException(
                    $"Fee rate per kw is too small: {parameters.FeeRatePerKw} < {MinAcceptableFeeRatePerKw}");
        }

        // Check if the dust limit is greater than the channel reserve amount
        if (parameters.DustLimitAmount > parameters.ChannelReserveAmount)
            throw new ChannelErrorException(
                $"Dust limit({parameters.DustLimitAmount}) is greater than channel reserve({parameters.ChannelReserveAmount})");

        // Check if dust_limit_satoshis is too small
        if (parameters.DustLimitAmount < ChannelConstants.MinDustLimitAmount)
            throw new ChannelErrorException($"Dust limit amount is too small: {parameters.DustLimitAmount}");

        if (parameters.FundingAmount is not null)
        {
            // Check if the push amount is too large (push_msat <= 1000 * funding_satoshis; both are msat here)
            if (parameters.PushAmount is not null
             && parameters.PushAmount > parameters.FundingAmount)
                throw new ChannelErrorException($"Push amount is too large: {parameters.PushAmount}");

            // Check if there are enough funds to pay for fees (and both anchors when option_anchors applies).
            // The initial commitment is built with the peer's feerate_per_kw, so use it when present.
            var feeRatePerKw = parameters.FeeRatePerKw ?? parameters.CurrentFeeRatePerKw;
            var hasAnchors = parameters.NegotiatedFeatures.OptionAnchors > FeatureSupport.No;
            var format = TaprootChannelType.GetCommitmentFormat(parameters.ChannelTypeTlv?.Features, hasAnchors);
            var expectedFee = CommitmentFeeCalculator.FunderCost((ulong)feeRatePerKw.Satoshi, format, 0);
            if (parameters.FundingAmount < expectedFee + parameters.ChannelReserveAmount)
                throw new ChannelErrorException(
                    $"Funding amount is too small to cover fees: {parameters.FundingAmount}");

            // BOLT 2: the funder's amount for the initial commitment (funding - push) must pay the full fee
            var funderAmount = parameters.FundingAmount - (parameters.PushAmount ?? LightningMoney.Zero);
            if (funderAmount < expectedFee)
                throw new ChannelErrorException(
                    $"Funder amount is too small to cover fees: {funderAmount} < {expectedFee}");

            // BOLT 2: fail if both to_local and to_remote of the initial commitment are <= channel_reserve_satoshis
            // (NL-220). Outputs are whole satoshis (rounded down); the funder's output is net of fee and anchors.
            if (parameters.PushAmount is not null)
            {
                var funderOutputSats = funderAmount.Satoshi - expectedFee.Satoshi;
                var fundeeOutputSats = parameters.PushAmount.Satoshi;
                var reserveSats = parameters.ChannelReserveAmount.Satoshi;
                if (funderOutputSats <= reserveSats && fundeeOutputSats <= reserveSats)
                    throw new ChannelErrorException(
                        $"Both initial outputs are at or below the channel reserve: to_local {funderOutputSats} sat, "
                      + $"to_remote {fundeeOutputSats} sat, channel_reserve {reserveSats} sat");
            }

            // Check if this is a large channel and if we support it
            if (parameters.FundingAmount >= ChannelConstants.LargeChannelAmount &&
                parameters.NegotiatedFeatures.LargeChannels == FeatureSupport.No)
                throw new ChannelErrorException("We don't support large channels");
        }

        // Check if ChannelType exists
        minimumDepth = _nodeOptions.MinimumDepth;
        if (parameters.ChannelTypeTlv is null)
            throw new ChannelErrorException("ChannelTypeTlv is not present");

        // BOLT 2: fail the channel if the channel_type is not suitable. We know option_static_remotekey, optionally
        // with option_anchors, and the option_scid_alias/option_zeroconf variations; channel types only use even bits.
        // A simple taproot type (bit 80 without 12/22, NL-877) is known when option_simple_taproot is negotiated
        var isTaproot = TaprootChannelType.IsTaprootChannelType(parameters.ChannelTypeTlv.Features);
        foreach (var bit in parameters.ChannelTypeTlv.Features.GetSetBits())
            if (!s_supportedChannelTypeBits.Contains(bit) && !(isTaproot && bit == TaprootChannelType.CompulsoryBit))
                throw new ChannelErrorException($"Unsupported channel type bit {bit}",
                                                "ChannelTypeTlv: This channel type is not supported");

        if (isTaproot)
        {
            // bolt-simple-taproot.md: the type needs option_simple_taproot, and a taproot channel MUST NOT be announced
            if (parameters.NegotiatedFeatures.OptionSimpleTaproot == FeatureSupport.No)
                throw new ChannelErrorException("Simple taproot channel type requested but option_simple_taproot is "
                                              + "not negotiated",
                                                "ChannelTypeTlv: We don't support option_simple_taproot");

            if (parameters.ChannelFlags is not null && parameters.ChannelFlags.Value.AnnounceChannel)
                throw new ChannelErrorException("A simple taproot channel cannot be announced",
                                                "ChannelTypeTlv: taproot channel type for a public channel");
        }
        else
        {
            // Check if OptionStaticRemoteKey is Compulsory
            if (!parameters.ChannelTypeTlv.Features.IsFeatureSet(Feature.OptionStaticRemoteKey, true))
                throw new ChannelErrorException("Static remote key feature is compulsory but not set by peer",
                                                "ChannelTypeTlv: Static remote key is compulsory");

            if (parameters.ChannelTypeTlv.Features.IsFeatureSet(Feature.OptionAnchors, true)
             && parameters.NegotiatedFeatures.OptionAnchors == FeatureSupport.No)
                throw new ChannelErrorException("Anchor outputs feature is not supported but requested by peer",
                                                "ChannelTypeTlv: We don't support anchor outputs");
        }

        if (parameters.ChannelTypeTlv.Features.IsFeatureSet(Feature.OptionScidAlias, true))
        {
            if (parameters.NegotiatedFeatures.ScidAlias == FeatureSupport.No)
                throw new ChannelErrorException("Scid alias feature is not negotiated but requested by peer",
                                                "ChannelTypeTlv: We don't support option_scid_alias");

            if (parameters.ChannelFlags is not null && parameters.ChannelFlags.Value.AnnounceChannel)
                throw new ChannelErrorException("Invalid channel flags for OPTION_SCID_ALIAS",
                                                "ChannelTypeTlv: We want to announce this channel");
        }

        // Check for ZeroConf feature
        if (parameters.ChannelTypeTlv.Features.IsFeatureSet(Feature.OptionZeroconf, true))
        {
            if (_nodeOptions.Features.ZeroConf == FeatureSupport.No)
                throw new ChannelErrorException("ZeroConf feature not supported but requested by peer",
                                                "ChannelTypeTlv: We don't support ZeroConf with you");

            minimumDepth = 0U;
        }
    }
}