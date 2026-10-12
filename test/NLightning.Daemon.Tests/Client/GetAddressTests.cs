using MessagePack;

namespace NLightning.Daemon.Tests.Client;

using Domain.Bitcoin.Enums;
using NLightning.Client.Ipc;
using NLightning.Client.Printers;
using TestCollections;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

[Collection(SerialTestCollection.Name)]
public class GetAddressTests
{
    [Fact]
    public void GivenP2WpkhAddress_WhenPrinted_ThenIsLabeledP2Wpkh()
    {
        // Arrange
        var response = new GetAddressIpcResponse { AddressP2Wpkh = "bcrt1qexample" };
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        // Act
        try
        {
            new GetAddressPrinter().Print(response);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Assert
        var output = writer.ToString();
        Assert.Contains("P2WPKH: bcrt1qexample", output);
        Assert.DoesNotContain("P2WSH", output);
    }

    [Fact]
    public void GivenNoAddressType_WhenParseAddressType_ThenMatchesRequestDefault()
    {
        // Act
        var result = NamedPipeIpcClient.ParseAddressType(null);

        // Assert
        Assert.Equal(new GetAddressIpcRequest().AddressType, result);
        Assert.Equal(AddressType.P2Tr, result);
    }

    [Fact]
    public void GivenP2WpkhAddress_WhenSerialized_ThenStaysOnWireKeyOne()
    {
        // Arrange
        var response = new GetAddressIpcResponse { AddressP2Tr = "tr", AddressP2Wpkh = "wpkh" };

        // Act
        var bytes = MessagePackSerializer.Serialize(response, NLightningMessagePackOptions.Options,
                                                    TestContext.Current.CancellationToken);
        var raw = MessagePackSerializer.Deserialize<object[]>(bytes, NLightningMessagePackOptions.Options,
                                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["tr", "wpkh"], raw);
    }
}