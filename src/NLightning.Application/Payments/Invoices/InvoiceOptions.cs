namespace NLightning.Application.Payments.Invoices;

/// <summary>
/// When our invoices carry BOLT 11 <c>r</c> route hints (BOLT 7 plan G4, <see cref="InvoiceService"/>).
/// </summary>
public enum InvoiceRouteHintMode
{
    /// <summary>
    /// Hints only when no announced (public) channel can deliver the invoice: a payer finds a public node through the
    /// gossip graph, so the hints would only reveal our private channels. The default.
    /// </summary>
    Auto,

    /// <summary>Hints for our usable channels whatever our public channels (the behaviour before the graph).</summary>
    Always,

    /// <summary>Never any hint (a node that is only reachable publicly, or on purpose not at all).</summary>
    Never
}

/// <summary>
/// Options of our invoices, bound from <c>Node:Invoices</c>.
/// </summary>
public sealed class InvoiceOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Node:Invoices";

    /// <summary>When invoices carry route hints (default <see cref="InvoiceRouteHintMode.Auto"/>).</summary>
    public InvoiceRouteHintMode RouteHints { get; set; } = InvoiceRouteHintMode.Auto;

    /// <summary>The default of <see cref="PublicChannelGracePeriod"/>: 10 minutes.</summary>
    public static readonly TimeSpan DefaultPublicChannelGracePeriod = TimeSpan.FromMinutes(10);

    /// <summary>
    /// With <see cref="InvoiceRouteHintMode.Auto"/>, how long an announced channel must have been in our own gossip
    /// graph (with both policies) before invoices leave the hints out, so our announcement and updates have had time to
    /// be relayed (several gossip flush intervals; default 10 minutes).
    /// </summary>
    public TimeSpan PublicChannelGracePeriod { get; set; } = DefaultPublicChannelGracePeriod;

    /// <summary>The default of <see cref="BlindedPathDummyHops"/>.</summary>
    public const int DefaultBlindedPathDummyHops = 1;

    /// <summary>The most dummy hops <see cref="BlindedPathDummyHops"/> may ask for.</summary>
    public const int MaxBlindedPathDummyHops = 4;

    /// <summary>
    /// How many dummy hops our blinded payment paths end with (BOLT 4 "Route Blinding": the writer MAY add dummy hops
    /// at the end of the path to obscure the path length). Each is a hop of our own node that relays to our node
    /// (<c>next_node_id</c> = us, a <c>payment_relay</c> copied from the introduction node's), padded like the others,
    /// so a sender sees a path of <c>2 + BlindedPathDummyHops</c> hops that looks like one through
    /// <c>BlindedPathDummyHops</c> more nodes. Default 1, LND's default shape (<c>blinding.num-hops</c> 2 with one real
    /// hop pads one dummy); 0 turns them off; at most <see cref="MaxBlindedPathDummyHops"/> (a value outside 0 to that
    /// is clamped with a warning). Applies to BOLT 12 invoice paths and bLIP 39 BOLT 11 paths.
    /// </summary>
    public int BlindedPathDummyHops { get; set; } = DefaultBlindedPathDummyHops;

    /// <summary>
    /// Our BOLT 11 invoices carry bLIP 39 blinded payment paths (tagged field 20) instead of route hints and a payment
    /// secret, and are signed by an ephemeral key, so they do not reveal our node id (default false). bLIP 39 is a
    /// draft (merged in lightning/blips as Draft); LND 0.18+ reads and pays it, CLN, Eclair and LDK do not. When no
    /// path can be built (no channel whose peer's <c>channel_update</c> we hold, or no block processed yet) the invoice
    /// is refused rather than issued without paths.
    /// </summary>
    public bool BlindedPaths { get; set; }
}