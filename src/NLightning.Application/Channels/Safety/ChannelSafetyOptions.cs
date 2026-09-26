namespace NLightning.Application.Channels.Safety;

using Domain.Channels.Policies;
using Domain.Node.Options;

/// <summary>
/// The knobs of the channel safety services (BOLT2 plan N9-T2/T4): the HTLC deadlines and the depth at which a
/// broadcast commitment counts as confirmed. Bound from <see cref="SectionName"/> by the host.
/// </summary>
public sealed class ChannelSafetyOptions
{
    /// <summary>The configuration section the daemon binds (<c>Node:Safety</c>).</summary>
    public const string SectionName = "Node:Safety";

    /// <summary>BOLT 2's G: an offered HTLC still in a commitment at <c>cltv_expiry + G</c> fails the channel.</summary>
    public uint GraceBlocks { get; set; } = HtlcDeadlinePolicy.DefaultGraceBlocks;

    /// <summary>
    /// BOLT 2's fulfillment deadline: a received HTLC we fulfilled (or know the preimage of) still in a commitment at
    /// <c>cltv_expiry - FulfillSafetyBlocks</c> fails the channel.
    /// </summary>
    public uint FulfillSafetyBlocks { get; set; } = HtlcDeadlinePolicy.DefaultFulfillSafetyBlocks;

    /// <summary>
    /// How many blocks before its <c>cltv_expiry</c> an unresolved received HTLC is failed back upstream. Null (the
    /// default) uses the node's <see cref="RoutingOptions.CltvExpiryDelta"/>; never below
    /// <see cref="FulfillSafetyBlocks"/>.
    /// </summary>
    public uint? FailBackBlocks { get; set; }

    /// <summary>
    /// Not used any more (BOLT 5 plan O2-T5): a confirmed commitment moves the channel to <c>OnchainResolving</c>
    /// through the on-chain watcher, and the channel is <c>Closed</c> once its outputs are irrevocably resolved. Kept so
    /// existing configuration files still bind.
    /// </summary>
    public uint CommitmentConfirmationDepth { get; set; } = 1;

    /// <summary>The deadline policy for these options and the node's routing options.</summary>
    public HtlcDeadlinePolicy CreatePolicy(RoutingOptions? routingOptions)
    {
        var failBack = FailBackBlocks ?? routingOptions?.CltvExpiryDelta ?? HtlcDeadlinePolicy.DefaultFailBackBlocks;
        return new HtlcDeadlinePolicy(GraceBlocks, FulfillSafetyBlocks, Math.Max(failBack, FulfillSafetyBlocks));
    }
}