namespace NLightning.Application.Payments.Trampoline;

/// <summary>
/// How this node relays trampoline payments as an intermediate trampoline node (BOLTs PR 836, NL-875 TR3): its
/// trampoline fee and expiry policy and the limits of the relay engine. Bound from <c>Node:Trampoline</c> by the daemon
/// (<see cref="SectionName"/>); every value has a default.
/// </summary>
/// <remarks>
/// <para>Relaying is on only while <c>trampoline_routing</c> is advertised (<c>Features:OptionTrampolineRouting</c>,
/// experimental until the owner decides, D-TR2/D-TR7): these settings change nothing otherwise.</para>
/// <para>The fee and the delta are what a payer learns from <c>trampoline_fee_or_expiry_insufficient</c> (NODE|26)
/// when it offered too little (D-TR6).</para>
/// </remarks>
public sealed class TrampolineOptions
{
    /// <summary>The configuration section the daemon binds.</summary>
    public const string SectionName = "Node:Trampoline";

    /// <summary>The default <see cref="LegTimeout"/>.</summary>
    public static readonly TimeSpan DefaultLegTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The base fee of a relay, msat (D-TR6: 1000).</summary>
    public uint FeeBaseMsat { get; set; } = 1_000;

    /// <summary>The proportional fee of a relay, millionths of the amount forwarded (D-TR6: 1000 = 0.1 %).</summary>
    public uint FeeProportionalMillionths { get; set; } = 1_000;

    /// <summary>
    /// The fewest blocks between the lowest incoming part's <c>cltv_expiry</c> and the trampoline payload's
    /// <c>outgoing_cltv_value</c> (D-TR6: 576, about four days, which covers the outgoing route to the next trampoline
    /// node).
    /// </summary>
    public ushort CltvExpiryDelta { get; set; } = 576;

    /// <summary>
    /// The most relays whose outgoing payment runs at once (Sending). A complete set beyond it is failed with
    /// <c>temporary_trampoline_failure</c> (risk R3: a relay holds its incoming HTLCs while it routes); 0 refuses every
    /// relay.
    /// </summary>
    public int MaxRelaysInFlight { get; set; } = 32;

    /// <summary>
    /// How long the outgoing leg may retry before it gives up (its <c>Deadline</c>); the relay then fails its incoming
    /// parts. Zero or less means <see cref="DefaultLegTimeout"/>.
    /// </summary>
    public TimeSpan LegTimeout { get; set; } = DefaultLegTimeout;

    /// <summary>
    /// The blocks to keep between the current height and the lowest incoming part's <c>cltv_expiry</c> when the leg
    /// must be over: a set that leaves fewer is refused, and the leg's deadline is cut so that it ends before (the HTLC
    /// deadline monitor fails an unresolved incoming HTLC back 34 blocks before its expiry, so keep this above).
    /// </summary>
    public uint MinCltvMarginBlocks { get; set; } = 48;

    /// <summary>The <see cref="LegTimeout"/> to use (the default when it is not positive).</summary>
    public TimeSpan EffectiveLegTimeout => LegTimeout > TimeSpan.Zero ? LegTimeout : DefaultLegTimeout;

    /// <summary>The configuration errors, empty when valid.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (CltvExpiryDelta == 0)
            errors.Add($"{SectionName}:{nameof(CltvExpiryDelta)} must be positive.");
        if (MaxRelaysInFlight < 0)
            errors.Add($"{SectionName}:{nameof(MaxRelaysInFlight)} cannot be negative.");
        if (MinCltvMarginBlocks >= CltvExpiryDelta)
            errors.Add($"{SectionName}:{nameof(MinCltvMarginBlocks)} ({MinCltvMarginBlocks}) must be below "
                     + $"{nameof(CltvExpiryDelta)} ({CltvExpiryDelta}).");
        return errors;
    }
}