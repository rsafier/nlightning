namespace NLightning.Daemon.Tests.Client;

using Domain.Payments.Enums;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>waitinvoice</c> (ClientCommand 47, Cashu plan C0, NL-901).
/// </summary>
public class WaitInvoiceCommandTests
{
    private const string HashHex = "abababababababababababababababababababababababababababababababab";

    [Theory]
    [InlineData(new[] { HashHex }, null)]
    [InlineData(new[] { HashHex, "--timeout", "120" }, 120U)]
    [InlineData(new[] { "--timeout", "300", HashHex }, 300U)]
    public void Given_ValidArguments_When_Parsed_Then_HashAndTimeout(string[] args, uint? timeoutSeconds)
    {
        // Act
        var parsed = WaitInvoiceCommands.Parse(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(HashHex, parsed!.Value.PaymentHash.ToString());
        Assert.Equal(timeoutSeconds, parsed.Value.TimeoutSeconds);
        Assert.Null(ClientApp.ValidateArguments("waitinvoice", args));
        Assert.Null(ClientApp.ValidateArguments("wait-invoice", args));
    }

    public static TheoryData<string[]> BadArguments => new()
    {
        Array.Empty<string>(),
        new[] { "abcd" },
        new[] { HashHex, HashHex },
        new[] { HashHex, "--timeout" },
        new[] { HashHex, "--timeout", "0" },
        new[] { HashHex, "--timeout", "301" }
    };

    [Theory]
    [MemberData(nameof(BadArguments))]
    public void Given_BadArguments_When_Validated_Then_UsageError(string[] args)
    {
        // Act
        var error = ClientApp.ValidateArguments("waitinvoice", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(WaitInvoiceCommands.Usage, error);
    }

    [Fact]
    public void Given_ATimedOutWait_When_Printed_Then_TheHeaderSaysSo()
    {
        // Arrange
        var output = new StringWriter();
        var response = new WaitInvoiceIpcResponse
        {
            Invoice = new InvoiceInfoIpcResponse
            {
                PaymentHash = new Domain.Crypto.ValueObjects.Hash(Convert.FromHexString(HashHex)),
                Status = InvoiceStatus.Open,
                CreatedAt = DateTimeOffset.UnixEpoch,
                ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1)
            },
            TimedOut = true
        };

        // Act
        new WaitInvoicePrinter(output).Print(response);

        // Assert
        Assert.StartsWith("Timed out; the invoice is still open:", output.ToString());
        Assert.Contains(HashHex, output.ToString());
    }
}