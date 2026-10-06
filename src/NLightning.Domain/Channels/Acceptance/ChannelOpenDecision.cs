namespace NLightning.Domain.Channels.Acceptance;

using Bitcoin.ValueObjects;
using Money;

/// <summary>
/// An external decider's answer to a <see cref="ChannelOpenRequest"/> (LND's <c>ChannelAcceptResponse</c>, NL-1180).
/// A rejection carries the text sent to the opener in the <c>error</c>; an acceptance may set the values we announce
/// (each null or zero means "the node's own value").
/// </summary>
public sealed record ChannelOpenDecision
{
    /// <summary>The generic rejection text (LND's <c>errChannelRejected</c>).</summary>
    public const string GenericRejection = "channel rejected";

    /// <summary>Accept without changing anything.</summary>
    public static ChannelOpenDecision Accepted { get; } = new() { Accept = true };

    /// <summary>Whether the open is accepted.</summary>
    public bool Accept { get; init; }

    /// <summary>The error text the opener gets on a rejection (at most 500 characters).</summary>
    public string? Error { get; init; }

    /// <summary>Our upfront shutdown script (needs <c>option_upfront_shutdown_script</c> negotiated).</summary>
    public BitcoinScript? UpfrontShutdownScript { get; init; }

    /// <summary>The <c>to_self_delay</c> we impose on the opener's outputs.</summary>
    public ushort? ToSelfDelay { get; init; }

    /// <summary>The reserve the opener must keep (v1 opens only).</summary>
    public LightningMoney? ChannelReserve { get; init; }

    /// <summary>The <c>max_htlc_value_in_flight_msat</c> we announce.</summary>
    public LightningMoney? MaxHtlcValueInFlight { get; init; }

    /// <summary>The <c>max_accepted_htlcs</c> we announce.</summary>
    public ushort? MaxAcceptedHtlcs { get; init; }

    /// <summary>The <c>htlc_minimum_msat</c> we announce.</summary>
    public LightningMoney? HtlcMinimum { get; init; }

    /// <summary>The <c>minimum_depth</c> we ask for.</summary>
    public uint? MinimumDepth { get; init; }

    /// <summary>Whether the decider asks for a zero-conf channel (refused: not supported).</summary>
    public bool ZeroConf { get; init; }

    /// <summary>A rejection with <paramref name="error"/> (or the generic text).</summary>
    public static ChannelOpenDecision Rejected(string? error = null) =>
        new() { Accept = false, Error = string.IsNullOrEmpty(error) ? GenericRejection : error };

    /// <summary>Whether the acceptance changes any value we announce.</summary>
    public bool HasOverrides =>
        UpfrontShutdownScript is not null || ToSelfDelay is not null || ChannelReserve is not null
     || MaxHtlcValueInFlight is not null || MaxAcceptedHtlcs is not null || HtlcMinimum is not null
     || MinimumDepth is not null || ZeroConf;
}