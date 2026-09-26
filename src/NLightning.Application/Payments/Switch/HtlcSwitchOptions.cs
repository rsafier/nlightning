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
}