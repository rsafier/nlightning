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
        Assert.Equal(21, (int)ClientCommand.ExportChanBackup);
        Assert.Equal(22, (int)ClientCommand.VerifyChanBackup);
        Assert.Equal(23, (int)ClientCommand.RestoreChanBackup);
        Assert.Equal(24, (int)ClientCommand.DisconnectPeer);
        Assert.Equal(33, (int)ClientCommand.SpliceIn);
        Assert.Equal(34, (int)ClientCommand.SpliceOut);
        Assert.Equal(35, (int)ClientCommand.SetChannelPolicy);
        Assert.Equal(36, (int)ClientCommand.GetChannelPolicy);
        Assert.Equal(37, (int)ClientCommand.BumpSplice);
        Assert.Equal(Enum.GetValues<ClientCommand>().Length, Enum.GetValues<ClientCommand>().Distinct().Count());
    }
}