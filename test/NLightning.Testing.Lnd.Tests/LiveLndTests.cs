namespace NLightning.Testing.Lnd.Tests;

using Lnrpc;

/// <summary>
/// Against a real LND, named by environment variables (no Docker code here): <c>NLTG_LND_GRPC</c> (https://host:port),
/// <c>NLTG_LND_TLS_CERT</c> and <c>NLTG_LND_MACAROON</c> (file paths). Explicit, category Live: run them with
/// <c>dotnet run --project test/NLightning.Testing.Lnd.Tests -f net10.0 -- -explicit only -trait Category=Live</c>.
/// </summary>
public class LiveLndTests
{
    [Fact(Explicit = true)]
    [Trait("Category", "Live")]
    public async Task Given_ARealLnd_When_Connecting_Then_TheGeneratedClientsTalkToIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var settings = LndSettings.FromFiles(Environment.GetEnvironmentVariable("NLTG_LND_GRPC")!,
                                             Environment.GetEnvironmentVariable("NLTG_LND_TLS_CERT")!,
                                             Environment.GetEnvironmentVariable("NLTG_LND_MACAROON")!);

        using var pool = new LndNodePool(new LndNodePoolConfig { StartBackgroundUpdates = false }
                                             .AddConnectionSettings(settings));

        // Act
        await pool.WaitUntilAllReadyAsync(TimeSpan.FromSeconds(90), ct);
        var connection = pool.GetLndNodeConnection();
        var state = await connection.GetStateSafeAsync(TimeSpan.FromSeconds(10), ct);
        var version = await connection.VersionerClient.GetVersionAsync(new Verrpc.VersionRequest(),
                                                                       cancellationToken: ct);
        var address = await connection.LightningClient.NewAddressAsync(
            new NewAddressRequest { Type = AddressType.TaprootPubkey }, cancellationToken: ct);
        var fee = await connection.WalletKitClient.EstimateFeeAsync(
            new Walletrpc.EstimateFeeRequest { ConfTarget = 6 }, cancellationToken: ct);
        var invoice = await connection.LightningClient.AddInvoiceAsync(new Invoice { Value = 1_000, Memo = "nltg" },
                                                                       cancellationToken: ct);
        var lookup = await connection.InvoiceClient.LookupInvoiceV2Async(
            new Invoicesrpc.LookupInvoiceMsg { PaymentHash = invoice.RHash }, cancellationToken: ct);

        // Assert
        Assert.Equal(66, connection.LocalNodePubKey.Length);
        Assert.Equal(WalletState.ServerActive, state);
        Assert.StartsWith("0.21.", version.Version_);
        Assert.StartsWith("bcrt1p", address.Address);
        Assert.True(fee.SatPerKw > 0);
        Assert.Equal(invoice.PaymentRequest, lookup.PaymentRequest);
        TestContext.Current.SendDiagnosticMessage(
            $"LND {version.Version_} {connection.LocalAlias} {connection.LocalNodePubKey} state {state}");
    }
}