namespace NLightning.LndGrpc.Macaroons;

/// <summary>
/// The macaroon permissions of LND's sub-servers this node serves (LND v0.21.4: <c>lnrpc/routerrpc/router_server.go</c>,
/// <c>lnrpc/invoicesrpc/invoices_server.go</c> and <c>lnrpc/walletrpc/walletkit_server.go</c> <c>macPermissions</c>), by
/// full gRPC method name (NL-1164, NL-1184). Every WalletKit method is listed, implemented or not, so an unimplemented
/// one answers <c>UNIMPLEMENTED</c> after the macaroon check.
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

    /// <summary><c>walletrpc.WalletKit</c>.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<MacaroonOp>> WalletKit =
        new Dictionary<string, IReadOnlyList<MacaroonOp>>(StringComparer.Ordinal)
        {
            ["/walletrpc.WalletKit/DeriveNextKey"] = [new("address", "read")],
            ["/walletrpc.WalletKit/DeriveKey"] = [new("address", "read")],
            ["/walletrpc.WalletKit/NextAddr"] = [new("address", "read")],
            ["/walletrpc.WalletKit/GetTransaction"] = [new("onchain", "read")],
            ["/walletrpc.WalletKit/PublishTransaction"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/SubmitPackage"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/SendOutputs"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/EstimateFee"] = [new("onchain", "read")],
            ["/walletrpc.WalletKit/PendingSweeps"] = [new("onchain", "read")],
            ["/walletrpc.WalletKit/BumpFee"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/BumpForceCloseFee"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/ListSweeps"] = [new("onchain", "read")],
            ["/walletrpc.WalletKit/LabelTransaction"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/LeaseOutput"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/ReleaseOutput"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/ListLeases"] = [new("onchain", "read")],
            ["/walletrpc.WalletKit/ListUnspent"] = [new("onchain", "read")],
            ["/walletrpc.WalletKit/ListAddresses"] = [new("onchain", "read")],
            ["/walletrpc.WalletKit/SignMessageWithAddr"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/VerifyMessageWithAddr"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/FundPsbt"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/SignPsbt"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/FinalizePsbt"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/ListAccounts"] = [new("onchain", "read")],
            ["/walletrpc.WalletKit/XCreateAccount"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/RequiredReserve"] = [new("onchain", "read")],
            ["/walletrpc.WalletKit/ImportAccount"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/ImportPublicKey"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/ImportTapscript"] = [new("onchain", "write")],
            ["/walletrpc.WalletKit/RemoveTransaction"] = [new("onchain", "write")]
        };

    /// <summary>The operations <paramref name="fullMethod"/> requires, or null when no sub-server table lists it.</summary>
    public static IReadOnlyList<MacaroonOp>? ForMethod(string fullMethod) =>
        Signer.TryGetValue(fullMethod, out var ops) || Loop.TryGetValue(fullMethod, out ops) || Router.TryGetValue(fullMethod, out ops) || Invoices.TryGetValue(fullMethod, out ops)
                                                   || WalletKit.TryGetValue(fullMethod, out ops)
            ? ops
            : null;

    /// <summary>LND signrpc's permission table, including deliberately unimplemented methods.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<MacaroonOp>> Signer =
        new Dictionary<string, IReadOnlyList<MacaroonOp>>(StringComparer.Ordinal)
        {
            ["/signrpc.Signer/SignOutputRaw"] = [new("signer", "generate")],
            ["/signrpc.Signer/ComputeInputScript"] = [new("signer", "generate")],
            ["/signrpc.Signer/SignMessage"] = [new("signer", "generate")],
            ["/signrpc.Signer/VerifyMessage"] = [new("signer", "read")],
            ["/signrpc.Signer/DeriveSharedKey"] = [new("signer", "generate")],
            ["/signrpc.Signer/MuSig2CombineKeys"] = [new("signer", "read")],
            ["/signrpc.Signer/MuSig2CreateSession"] = [new("signer", "generate")],
            ["/signrpc.Signer/MuSig2RegisterNonces"] = [new("signer", "generate")],
            ["/signrpc.Signer/MuSig2Sign"] = [new("signer", "generate")],
            ["/signrpc.Signer/MuSig2CombineSig"] = [new("signer", "generate")],
            ["/signrpc.Signer/MuSig2Cleanup"] = [new("signer", "generate")],
            ["/signrpc.Signer/MuSig2RegisterCombinedNonce"] = [new("signer", "generate")],
            ["/signrpc.Signer/MuSig2GetCombinedNonce"] = [new("signer", "read")],
        };

    /// <summary>Loop startup and chain notifications (LND chainrpc/verrpc permission tables).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<MacaroonOp>> Loop =
        new Dictionary<string, IReadOnlyList<MacaroonOp>>(StringComparer.Ordinal)
        {
            ["/verrpc.Versioner/GetVersion"] = [new("info", "read")],
            ["/chainrpc.ChainNotifier/RegisterBlockEpochNtfn"] = [new("onchain", "read")],
            ["/chainrpc.ChainNotifier/RegisterConfirmationsNtfn"] = [new("onchain", "read")],
            ["/chainrpc.ChainNotifier/RegisterSpendNtfn"] = [new("onchain", "read")]
        };
}