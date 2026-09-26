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
}