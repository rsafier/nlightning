namespace NLightning.Domain.Channels.Interfaces;

using Money;
using Validators.Parameters;

public interface IChannelOpenValidator
{
    /// <summary>
    /// Conducts optional validation checks on channel parameters to ensure compliance with acceptable ranges
    /// and configurations beyond the mandatory requirements.
    /// </summary>
    /// <remarks>
    /// This method verifies that optional configuration parameters meet recommended safety and usability thresholds:
    /// - Validates that the funding amount meets the minimum channel size threshold.
    /// - Checks that the HTLC minimum amount is below the channel amount (as LDK does, NL-562).
    /// - Validates that the maximum HTLC value in flight is enough relative to the channel funds
    ///   (<see cref="CheckMaxHtlcValueInFlight"/>).
    /// - Ensures the channel reserve amount is at most the larger of <c>Node:MaxAcceptedChannelReservePercent</c> of the
    ///   channel and 1,000 sat (NL-562).
    /// - Verifies that the maximum number of accepted HTLCs meets a constant minimum (independent of ours, NL-562).
    /// - Confirms that the dust limit is not excessively large relative to the node's configured dust limit.
    /// </remarks>
    /// <param name="parameters">The parameters containing the channel's configuration parameters, including funding amount, HTLC limits, and related settings.</param>
    /// <exception cref="Exceptions.ChannelErrorException">
    /// Thrown when one of the optional checks fails, including missing channel type when required, insufficient funding,
    /// excessively high or low HTLC value limits, or incompatible reserve and dust limits.
    /// </exception>
    void PerformOptionalChecks(ChannelOpenOptionalValidationParameters parameters);

    /// <summary>
    /// Fails the open when the peer's <c>max_htlc_value_in_flight_msat</c> is below
    /// <c>Node:MinAcceptedMaxHtlcValueInFlightPercent</c> of the channel (NL-552); the same rule for v1 and v2 opens,
    /// in both roles.
    /// </summary>
    /// <param name="channelAmount">The whole channel (both contributions of a dual-funded open).</param>
    /// <param name="maxHtlcValueInFlight">The peer's <c>max_htlc_value_in_flight_msat</c>.</param>
    /// <exception cref="Exceptions.ChannelErrorException">The peer's limit is below the floor.</exception>
    void CheckMaxHtlcValueInFlight(LightningMoney channelAmount, LightningMoney maxHtlcValueInFlight);

    /// <summary>
    /// Enforce mandatory checks when establishing a new Lightning Network channel.
    /// </summary>
    /// <remarks>
    /// The method validates channel parameters to ensure they comply with predefined safety and compatibility checks:
    /// - ChainHash must be compatible with the node's network.
    /// - Push amount must not exceed 1000 times the funding amount.
    /// - To_self_delay must not exceed the node's <c>MaxAcceptedToSelfDelay</c>.
    /// - Max_accepted_htlcs must not exceed the allowed maximum.
    /// - Fee rate per kw must fall within acceptable limits.
    /// - Dust limit must be lower than or equal to the channel reserve amount and adhere to minimum thresholds.
    /// - Funding amount must be sufficient to cover fees and the channel reserve.
    /// - Large channels must only be supported if negotiated features include support for them.
    /// - Additional validation may apply to channel types based on negotiated options.
    /// </remarks>
    /// <param name="channelTypeTlv">Optional TLV data specifying the channel type, which may impose additional constraints.</param>
    /// <param name="currentFeeRatePerKw">The current network fee rate per kiloweight, used for fee validation.</param>
    /// <param name="negotiatedFeatures">Negotiated feature options between the participating nodes, affecting channel setup constraints.</param>
    /// <param name="payload">The payload containing the channel's configuration parameters and constraints.</param>
    /// <param name="minimumDepth">The minimum number of confirmations required for the channel to be considered operational.</param>
    /// <exception cref="Exceptions.ChannelErrorException">
    /// Thrown when any of the mandatory checks fail, such as invalid chain hash, excessive push amount, unreasonably large delay,
    /// invalid funding amount, unsupported large channel, or mismatched channel type.
    /// </exception>
    void PerformMandatoryChecks(ChannelOpenMandatoryValidationParameters parameters, out uint minimumDepth);
}