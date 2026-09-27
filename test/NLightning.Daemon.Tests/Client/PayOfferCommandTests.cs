namespace NLightning.Daemon.Tests.Client;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Enums;
using NLightning.Client;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>payoffer</c> and <c>fetchinvoice</c>: argument parsing and the printed result.
/// </summary>
public class PayOfferCommandTests
{
    private const string Offer = "lno1qgsqvgnwgcg35z6ee2h3yczraddm72xrfua9uve2rlrm9deu7xyfzrc";

    [Fact]
    public void Given_EveryOption_When_ParsedForPayoffer_Then_AllAreKept()
    {
        // Arrange
        string[] args = [Offer, "12000", "--quantity", "2", "--note=hi there", "--max-fee-msat", "300",
                         "--max-parts=3", "--timeout", "90"];

        // Act
        var parsed = ClientApp.ParsePayOfferOptions(args, true, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(new PayOfferArguments(Offer, 12_000, 2, "hi there", 90, 300, 3), parsed);
        Assert.Null(ClientApp.ValidateArguments("payoffer", args));
    }

    [Theory]
    [InlineData("")]
    [InlineData("OFFER 0")]
    [InlineData("OFFER 10 extra")]
    [InlineData("OFFER --quantity 0")]
    [InlineData("OFFER --quantity")]
    [InlineData("OFFER --timeout 301")]
    [InlineData("OFFER --max-parts 0")]
    [InlineData("OFFER --fee 3")]
    public void Given_InvalidArguments_When_ValidatedForPayoffer_Then_UsageError(string arguments)
    {
        // Arrange
        var args = arguments.Length == 0
                       ? []
                       : arguments.Replace("OFFER", Offer, StringComparison.Ordinal).Split(' ');

        // Act / Assert
        Assert.NotNull(ClientApp.ValidateArguments("payoffer", args));
    }

    [Fact]
    public void Given_APaymentOption_When_ValidatedForFetchinvoice_Then_UsageError()
    {
        // Act / Assert
        Assert.NotNull(ClientApp.ValidateArguments("fetchinvoice", [Offer, "--max-parts", "2"]));
        Assert.Null(ClientApp.ValidateArguments("fetch-invoice", [Offer, "5000", "--note", "x"]));
    }

    [Fact]
    public void Given_AnInvoiceErrorWithoutPayment_When_Printed_Then_TheErrorAndNothingPaidAreShown()
    {
        // Arrange
        var output = new StringWriter();
        var response = new PayOfferIpcResponse
        {
            Fetch = new FetchInvoiceIpcResponse
            {
                Status = FetchInvoiceStatus.InvoiceError,
                Attempts = 1,
                Error = "out of stock",
                ErroneousField = 86
            }
        };

        // Act
        new PayOfferPrinter(output).Print(response);

        // Assert
        var text = output.ToString();
        Assert.Contains("invoice_error: out of stock", text);
        Assert.Contains("Erroneous field: 86", text);
        Assert.Contains("Nothing was paid.", text);
    }

    [Fact]
    public void Given_AReceivedInvoice_When_PrintedByFetchinvoice_Then_ItsFieldsAreShown()
    {
        // Arrange
        var output = new StringWriter();
        var fetch = new FetchInvoiceIpcResponse
        {
            Status = FetchInvoiceStatus.Received,
            Attempts = 1,
            NodeId = new CompactPubKey(Convert.FromHexString(
                                           "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c")),
            Amount = LightningMoney.MilliSatoshis(10_000),
            PaymentHash = new Hash(new byte[32]),
            PathCount = 2,
            Invoice = [0xAB]
        };

        // Act
        new PayOfferPrinter(output).PrintFetch(fetch);

        // Assert
        var text = output.ToString();
        Assert.Contains("Amount: 10000 msat", text);
        Assert.Contains("Blinded paths: 2", text);
        Assert.Contains("Invoice (hex): ab", text);
    }
}