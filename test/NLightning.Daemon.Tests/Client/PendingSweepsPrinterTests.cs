namespace NLightning.Daemon.Tests.Client;

using Domain.Onchain.Enums;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>pendingsweeps</c> (ClientCommand 15): the broadcasts the node gave up (NL-294) are printed first.
/// </summary>
public class PendingSweepsPrinterTests
{
    [Fact]
    public void Given_AnAbandonedFunding_When_Printed_Then_ItIsListedBeforeTheChannels()
    {
        // Arrange
        using var output = new StringWriter();
        var response = new PendingSweepsIpcResponse
        {
            Channels = [],
            AbandonedBroadcasts =
            [
                new AbandonedBroadcastIpcInfo
                {
                    TransactionId = "ab", Purpose = BroadcastPurpose.Funding, FirstBroadcastHeight = 120
                }
            ]
        };

        // Act
        new PendingSweepsPrinter(output).Print(response);

        // Assert
        Assert.Equal("Abandoned broadcasts (no longer sent):\n"
                   + "  ab Funding first sent at height 120\n"
                   + "No channel is being resolved on chain.\n",
                     output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Given_NoAbandonedBroadcast_When_Printed_Then_NoSectionIsPrinted()
    {
        // Arrange (an older daemon sends no list)
        using var output = new StringWriter();

        // Act
        new PendingSweepsPrinter(output).Print(new PendingSweepsIpcResponse { Channels = [] });

        // Assert
        Assert.Equal("No channel is being resolved on chain.\n", output.ToString().ReplaceLineEndings("\n"));
    }
}