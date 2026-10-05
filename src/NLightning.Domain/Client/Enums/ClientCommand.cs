namespace NLightning.Domain.Client.Enums;

/// <summary>
/// Commands sent by a client.
/// </summary>
/// <remarks>
/// Append-only: never renumber a value, the client and daemon exchange them on the wire. The next free value is 49.
/// </remarks>
public enum ClientCommand
{
    // Reserve 0 for unknown
    Unknown = 0,
    NodeInfo = 1,
    ConnectPeer = 2,
    ListPeers = 3,
    GetAddress = 4,
    WalletBalance = 5,
    OpenChannel = 6,
    OpenChannelSubscription = 7,
    ListChannels = 8,
    CreateInvoice = 9,
    PayInvoice = 10,
    ListInvoices = 11,
    ListPayments = 12,
    CloseChannel = 13,
    ForceCloseChannel = 14,
    PendingSweeps = 15,
    ChainStatus = 16,
    ListNodes = 17,
    ListGraphChannels = 18,
    GetRoute = 19,
    DescribeGraph = 20,
    ExportChanBackup = 21,
    VerifyChanBackup = 22,
    RestoreChanBackup = 23,
    DisconnectPeer = 24,
    Withdraw = 25,
    CreateOffer = 26,
    ListOffers = 27,
    DisableOffer = 28,
    PayOffer = 29,
    FetchInvoice = 30,

    /// <summary>A spontaneous (keysend) payment (lane lh1-l3).</summary>
    Keysend = 31,

    ListPeerStorage = 32,

    /// <summary>Splice wallet funds into a channel (splicing plan §3.10, wave SP1 lane SP1-E).</summary>
    SpliceIn = 33,

    /// <summary>Splice funds out of a channel to an address or our wallet (wave SP1 lane SP1-E).</summary>
    SpliceOut = 34,

    /// <summary>Set a channel's routing policy override (wave sp1 lane SP1-G).</summary>
    SetChannelPolicy = 35,

    /// <summary>Read a channel's routing policy in force (wave sp1 lane SP1-G).</summary>
    GetChannelPolicy = 36,

    /// <summary>
    /// RBF a channel's pending splice at a higher feerate (<c>bumpsplice</c>, splicing plan §3.10, wave SPR lane SPR-B);
    /// answered with the splice response of <see cref="SpliceIn"/>/<see cref="SpliceOut"/>.
    /// </summary>
    BumpSplice = 37,

    /// <summary>
    /// RBF our unconfirmed dual-funded open at a higher feerate (<c>bumpopen</c>, BOLT 2 "Fee bumping", lane dfrbf);
    /// answered with <c>BumpOpenIpcResponse</c>.
    /// </summary>
    BumpOpen = 38,

    /// <summary>
    /// Stop the node gracefully (<c>shutdown</c>, NL-591): refused while HTLCs are in flight; otherwise new activity is
    /// refused from then on and the daemon stops once the answer is sent.
    /// </summary>
    Shutdown = 39,

    /// <summary>
    /// Lists the forwarded payments (NL-597), newest first, paged, filterable.
    /// </summary>
    ListForwards = 40,

    /// <summary>
    /// Lists the sealed accounting events (<c>listaccountingevents</c>, NL-602), in ledger order after a cursor,
    /// filterable; the events committed so far are sealed first.
    /// </summary>
    ListAccountingEvents = 41,

    /// <summary>
    /// The node's live balances by bucket (<c>accountingsnapshot</c>, NL-602): each channel, pending on-chain funds and
    /// the wallet.
    /// </summary>
    AccountingSnapshot = 42,

    /// <summary>
    /// A report of the operational books (<c>accounting report</c>, NL-602 A2): balance sheet, income statement,
    /// channels, peers, fees or the register.
    /// </summary>
    AccountingReport = 43,

    /// <summary>
    /// One page of an export of the books (<c>accounting export</c>, NL-602 A2): hledger, beancount or CSV text streamed
    /// to the client, which writes it; the daemon never writes a file.
    /// </summary>
    AccountingExport = 44,

    /// <summary>
    /// The books' administration (<c>accounting reconcile|rebuild|verify</c>, NL-602 A2).
    /// </summary>
    AccountingAdmin = 45,

    /// <summary>
    /// Liquidity ads (<c>liquidityads rates|sellers|purchases</c>, NL-850): our rates, the sellers we know of (their
    /// <c>init</c> and <c>node_announcement</c>) and the purchases we made and sold with their lease status.
    /// </summary>
    LiquidityAds = 46,

    /// <summary>
    /// Waits until one of our invoices leaves <c>Open</c> (<c>waitinvoice</c>, Cashu plan C0, NL-991), at most the
    /// request's timeout; answers with the invoice either way.
    /// </summary>
    WaitInvoice = 47,

    /// <summary>Pays over caller-supplied routes, single or an MPP shard set, never re-planned (NL-1145).</summary>
    PayRoute = 48
}