namespace NLightning.LndGrpc.Macaroons;

/// <summary>
/// LND's macaroon permissions at v0.21.4-beta (<c>rpcserver.go</c>): the operation sets of the three default macaroons
/// and the operations every <c>lnrpc.Lightning</c> method requires (<c>MainRPCServerPermissions</c>), copied so an LND
/// macaroon means the same here.
/// </summary>
public static class LndPermissions
{
    /// <summary>The entity of LND's per-method permission: <c>uri</c> with the full method as the action.</summary>
    public const string UriEntity = "uri";

    /// <summary>The macaroon location LND writes.</summary>
    public const string Location = "lnd";

    /// <summary>LND's <c>DefaultRootKeyID</c>: the only root key id this node has.</summary>
    public static readonly byte[] DefaultRootKeyId = "0"u8.ToArray();

    /// <summary>LND's <c>readPermissions</c> (<c>readonly.macaroon</c>).</summary>
    public static readonly IReadOnlyList<MacaroonOp> Read =
    [
        new("onchain", "read"), new("offchain", "read"), new("address", "read"), new("message", "read"),
        new("peers", "read"), new("info", "read"), new("invoices", "read"), new("signer", "read"),
        new("macaroon", "read")
    ];

    /// <summary>LND's <c>writePermissions</c>.</summary>
    public static readonly IReadOnlyList<MacaroonOp> Write =
    [
        new("onchain", "write"), new("offchain", "write"), new("address", "write"), new("message", "write"),
        new("peers", "write"), new("info", "write"), new("invoices", "write"), new("signer", "generate"),
        new("macaroon", "generate"), new("macaroon", "write")
    ];

    /// <summary>LND's <c>adminPermissions()</c>: read and write (<c>admin.macaroon</c>).</summary>
    public static readonly IReadOnlyList<MacaroonOp> Admin = [.. Read, .. Write];

    /// <summary>LND's <c>invoicePermissions</c> (<c>invoice.macaroon</c>).</summary>
    public static readonly IReadOnlyList<MacaroonOp> Invoice =
    [
        new("invoices", "read"), new("invoices", "write"), new("address", "read"), new("address", "write"),
        new("onchain", "read")
    ];

    /// <summary>The operations each <c>lnrpc.Lightning</c> method requires, by full gRPC method name.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<MacaroonOp>> Methods =
        new Dictionary<string, IReadOnlyList<MacaroonOp>>(StringComparer.Ordinal)
        {
            ["/lnrpc.Lightning/SendCoins"] = [new("onchain", "write")],
            ["/lnrpc.Lightning/ListUnspent"] = [new("onchain", "read")],
            ["/lnrpc.Lightning/SendMany"] = [new("onchain", "write")],
            ["/lnrpc.Lightning/NewAddress"] = [new("address", "write")],
            ["/lnrpc.Lightning/SignMessage"] = [new("message", "write")],
            ["/lnrpc.Lightning/VerifyMessage"] = [new("message", "read")],
            ["/lnrpc.Lightning/ConnectPeer"] = [new("peers", "write")],
            ["/lnrpc.Lightning/DisconnectPeer"] = [new("peers", "write")],
            ["/lnrpc.Lightning/OpenChannel"] = [new("onchain", "write"), new("offchain", "write")],
            ["/lnrpc.Lightning/BatchOpenChannel"] = [new("onchain", "write"), new("offchain", "write")],
            ["/lnrpc.Lightning/OpenChannelSync"] = [new("onchain", "write"), new("offchain", "write")],
            ["/lnrpc.Lightning/CloseChannel"] = [new("onchain", "write"), new("offchain", "write")],
            ["/lnrpc.Lightning/AbandonChannel"] = [new("offchain", "write")],
            ["/lnrpc.Lightning/GetInfo"] = [new("info", "read")],
            ["/lnrpc.Lightning/GetDebugInfo"] =
                [new("info", "read"), new("offchain", "read"), new("onchain", "read"), new("peers", "read")],
            ["/lnrpc.Lightning/GetRecoveryInfo"] = [new("info", "read")],
            ["/lnrpc.Lightning/ListPeers"] = [new("peers", "read")],
            ["/lnrpc.Lightning/WalletBalance"] = [new("onchain", "read")],
            ["/lnrpc.Lightning/EstimateFee"] = [new("onchain", "read")],
            ["/lnrpc.Lightning/ChannelBalance"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/PendingChannels"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/ListChannels"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/SubscribeChannelEvents"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/ClosedChannels"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/AddInvoice"] = [new("invoices", "write")],
            ["/lnrpc.Lightning/LookupInvoice"] = [new("invoices", "read")],
            ["/lnrpc.Lightning/ListInvoices"] = [new("invoices", "read")],
            ["/lnrpc.Lightning/SubscribeInvoices"] = [new("invoices", "read")],
            ["/lnrpc.Lightning/SubscribeTransactions"] = [new("onchain", "read")],
            ["/lnrpc.Lightning/GetTransactions"] = [new("onchain", "read")],
            ["/lnrpc.Lightning/DescribeGraph"] = [new("info", "read")],
            ["/lnrpc.Lightning/GetNodeMetrics"] = [new("info", "read")],
            ["/lnrpc.Lightning/GetChanInfo"] = [new("info", "read")],
            ["/lnrpc.Lightning/GetNodeInfo"] = [new("info", "read")],
            ["/lnrpc.Lightning/QueryRoutes"] = [new("info", "read")],
            ["/lnrpc.Lightning/GetNetworkInfo"] = [new("info", "read")],
            ["/lnrpc.Lightning/StopDaemon"] = [new("info", "write")],
            ["/lnrpc.Lightning/SubscribeChannelGraph"] = [new("info", "read")],
            ["/lnrpc.Lightning/DeleteCanceledInvoice"] = [new("invoices", "write")],
            ["/lnrpc.Lightning/ListPayments"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/DeletePayment"] = [new("offchain", "write")],
            ["/lnrpc.Lightning/DeleteAllPayments"] = [new("offchain", "write")],
            ["/lnrpc.Lightning/DebugLevel"] = [new("info", "write")],
            ["/lnrpc.Lightning/DecodePayReq"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/FeeReport"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/UpdateChannelPolicy"] = [new("offchain", "write")],
            ["/lnrpc.Lightning/ForwardingHistory"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/RestoreChannelBackups"] = [new("offchain", "write")],
            ["/lnrpc.Lightning/ExportChannelBackup"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/VerifyChanBackup"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/ExportAllChannelBackups"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/SubscribeChannelBackups"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/ChannelAcceptor"] = [new("onchain", "write"), new("offchain", "write")],
            ["/lnrpc.Lightning/BakeMacaroon"] = [new("macaroon", "generate")],
            ["/lnrpc.Lightning/ListMacaroonIDs"] = [new("macaroon", "read")],
            ["/lnrpc.Lightning/DeleteMacaroonID"] = [new("macaroon", "write")],
            ["/lnrpc.Lightning/ListPermissions"] = [new("info", "read")],
            ["/lnrpc.Lightning/CheckMacaroonPermissions"] = [new("macaroon", "read")],
            ["/lnrpc.Lightning/SubscribePeerEvents"] = [new("peers", "read")],
            ["/lnrpc.Lightning/FundingStateStep"] = [new("onchain", "write"), new("offchain", "write")],
            ["/lnrpc.Lightning/RegisterRPCMiddleware"] = [new("macaroon", "write")],
            ["/lnrpc.Lightning/SendCustomMessage"] = [new("offchain", "write")],
            ["/lnrpc.Lightning/SubscribeCustomMessages"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/SendOnionMessage"] = [new("offchain", "write")],
            ["/lnrpc.Lightning/SubscribeOnionMessages"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/LookupHtlcResolution"] = [new("offchain", "read")],
            ["/lnrpc.Lightning/ListAliases"] = [new("offchain", "read")]
        };

    /// <summary>The operations <paramref name="fullMethod"/> requires, or null for a method LND does not know.</summary>
    public static IReadOnlyList<MacaroonOp>? ForMethod(string fullMethod) =>
        Methods.TryGetValue(fullMethod, out var ops) ? ops : LndSubServerPermissions.ForMethod(fullMethod);
}