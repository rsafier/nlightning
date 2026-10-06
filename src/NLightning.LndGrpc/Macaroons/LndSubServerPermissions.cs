namespace NLightning.LndGrpc.Macaroons;

/// <summary>
/// The macaroon permissions of LND's sub-servers this node serves (LND v0.21.4: <c>lnrpc/routerrpc/router_server.go</c>
/// and <c>lnrpc/invoicesrpc/invoices_server.go</c> <c>macPermissions</c>), by full gRPC method name (NL-1164).
/// </summary>
public static class LndSubServerPermissions
{
    /// <summary><c>routerrpc.Router</c>.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<MacaroonOp>> Router =
        new Dictionary<string, IReadOnlyList<MacaroonOp>>(StringComparer.Ordinal)
        {
            ["/routerrpc.Router/SendPaymentV2"] = [new("offchain", "write")],
            ["/routerrpc.Router/SendToRouteV2"] = [new("offchain", "write")],
            ["/routerrpc.Router/TrackPaymentV2"] = [new("offchain", "read")],
            ["/routerrpc.Router/TrackPayments"] = [new("offchain", "read")],
            ["/routerrpc.Router/EstimateRouteFee"] = [new("offchain", "read")],
            ["/routerrpc.Router/QueryMissionControl"] = [new("offchain", "read")],
            ["/routerrpc.Router/XImportMissionControl"] = [new("offchain", "write")],
            ["/routerrpc.Router/GetMissionControlConfig"] = [new("offchain", "read")],
            ["/routerrpc.Router/SetMissionControlConfig"] = [new("offchain", "write")],
            ["/routerrpc.Router/QueryProbability"] = [new("offchain", "read")],
            ["/routerrpc.Router/ResetMissionControl"] = [new("offchain", "write")],
            ["/routerrpc.Router/BuildRoute"] = [new("offchain", "read")],
            ["/routerrpc.Router/SubscribeHtlcEvents"] = [new("offchain", "read")],
            ["/routerrpc.Router/HtlcInterceptor"] = [new("offchain", "write")],
            ["/routerrpc.Router/UpdateChanStatus"] = [new("offchain", "write")],
            ["/routerrpc.Router/XAddLocalChanAliases"] = [new("offchain", "write")],
            ["/routerrpc.Router/XDeleteLocalChanAliases"] = [new("offchain", "write")],
            ["/routerrpc.Router/DeleteForwardingHistory"] = [new("offchain", "write")]
        };

    /// <summary><c>invoicesrpc.Invoices</c>.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<MacaroonOp>> Invoices =
        new Dictionary<string, IReadOnlyList<MacaroonOp>>(StringComparer.Ordinal)
        {
            ["/invoicesrpc.Invoices/SubscribeSingleInvoice"] = [new("invoices", "read")],
            ["/invoicesrpc.Invoices/SettleInvoice"] = [new("invoices", "write")],
            ["/invoicesrpc.Invoices/CancelInvoice"] = [new("invoices", "write")],
            ["/invoicesrpc.Invoices/AddHoldInvoice"] = [new("invoices", "write")],
            ["/invoicesrpc.Invoices/LookupInvoiceV2"] = [new("invoices", "write")],
            ["/invoicesrpc.Invoices/HtlcModifier"] = [new("invoices", "write")]
        };

    /// <summary>The operations <paramref name="fullMethod"/> requires, or null when no sub-server table lists it.</summary>
    public static IReadOnlyList<MacaroonOp>? ForMethod(string fullMethod) =>
        Router.TryGetValue(fullMethod, out var ops) || Invoices.TryGetValue(fullMethod, out ops) ? ops : null;
}