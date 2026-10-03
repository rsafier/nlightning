namespace NLightning.Application.Payments.Switch;

/// <summary>
/// Settings of <see cref="HtlcSwitch"/> (the daemon may bind them from <c>Node:Switch</c>).
/// </summary>
public sealed class HtlcSwitchOptions
{
    /// <summary>
    /// The default <see cref="MppTimeout"/>: BOLT 4 says a final node SHOULD wait at least 60 seconds after the first
    /// HTLC of an incomplete set before it fails the set.
    /// </summary>
    public static readonly TimeSpan DefaultMppTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long an incomplete multi-part HTLC set is held, from its first part, before every part is failed with
    /// <c>mpp_timeout</c> (BOLT 4 <c>basic_mpp</c>). Zero or less means <see cref="DefaultMppTimeout"/>.
    /// </summary>
    public TimeSpan MppTimeout { get; set; } = DefaultMppTimeout;

    /// <summary>
    /// The default <see cref="BlindedErrorMaxDelay"/>.
    /// </summary>
    public static readonly TimeSpan DefaultBlindedErrorMaxDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The upper bound of the random delay before we send <c>update_fail_htlc</c> with <c>invalid_onion_blinding</c>
    /// as the introduction node of a blinded route (BOLT 2 and BOLT 4 SHOULD: without it a sender could time how far
    /// into the route a failure happened). Zero or less turns the delay off.
    /// </summary>
    public TimeSpan BlindedErrorMaxDelay { get; set; } = DefaultBlindedErrorMaxDelay;
}