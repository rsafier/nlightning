namespace NLightning.Domain.Tests.Client;

using Domain.Client.Enums;

public class ClientCommandTests
{
    [Fact]
    public void Given_ClientCommands_When_Read_Then_WireValuesAreStable()
    {
        // Assert (IPC wire values; append-only, never renumber)
        Assert.Equal(8, (int)ClientCommand.ListChannels);
        Assert.Equal(9, (int)ClientCommand.CreateInvoice);
        Assert.Equal(10, (int)ClientCommand.PayInvoice);
        Assert.Equal(11, (int)ClientCommand.ListInvoices);
        Assert.Equal(12, (int)ClientCommand.ListPayments);
        Assert.Equal(Enum.GetValues<ClientCommand>().Length, Enum.GetValues<ClientCommand>().Distinct().Count());
    }
}