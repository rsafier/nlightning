namespace NLightning.LndGrpc.Macaroons;

/// <summary>
/// The operations LND v0.21.4-beta's sub-servers require for the methods wave 3 serves (NL-1183, NL-1184): every
/// <c>walletrpc.WalletKit</c> method (<c>walletkit_server.go</c> <c>macPermissions</c>, implemented or not, so an
/// unimplemented one answers <c>UNIMPLEMENTED</c> after the macaroon check) and <c>routerrpc.Router/HtlcInterceptor</c>
/// (<c>router_server.go</c>).
/// </summary>
public static class LndSubServerPermissions
{
    /// <summary>The operations each method requires, by full gRPC method name.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<MacaroonOp>> Methods =
        new Dictionary<string, IReadOnlyList<MacaroonOp>>(StringComparer.Ordinal)
        {
            ["/routerrpc.Router/HtlcInterceptor"] = [new("offchain", "write")],
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

    /// <summary>The operations <paramref name="fullMethod"/> requires, or null when this table does not list it.</summary>
    public static IReadOnlyList<MacaroonOp>? ForMethod(string fullMethod) =>
        Methods.TryGetValue(fullMethod, out var ops) ? ops : null;
}