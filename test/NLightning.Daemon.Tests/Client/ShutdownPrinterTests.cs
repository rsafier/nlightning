namespace NLightning.Daemon.Tests.Client;

using Domain.Client.Enums;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>shutdown</c> (ClientCommand 39): an accepted stop reports the HTLCs of force-closed channels
/// with the nearest expiry (NL-1006).
/// </summary>
public class ShutdownPrinterTests
{
    [Fact]
    public void Given_AStopWithHtlcsResolvingOnChain_When_Printed_Then_TheyAreReportedWithTheNearestExpiry()
    {
        // Arrange
        using var output = new StringWriter();
        var response = new ShutdownIpcResponse
        {
            ChannelCount = 2,
            Outcome = ShutdownOutcome.Stopped,
            BusyChannels = [],
            HtlcsResolvingOnChain = 1,
            NearestCltvExpiry = 500,
            BlocksUntilDeadline = 40
        };

        // Act
        new ShutdownPrinter(output).Print(response);

        // Assert
        Assert.Equal("Node is shutting down (no HTLCs in flight)\n"
                   + "  Channels:          2\n"
                   + "  They reestablish when the node starts again.\n"
                   + "  HTLCs on chain:    1 (force-closed channels; the on-chain resolvers resume at the\n"
                   + "                     next start)\n"
                   + "  Nearest HTLC expiry: block 500 (our deadline to act: in 40 block(s))\n"
                   + "  Have the node back before the deadlines.\n",
                     output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Given_AStopWithoutHtlcs_When_Printed_Then_NoOnchainSectionIsPrinted()
    {
        // Arrange
        using var output = new StringWriter();
        var response = new ShutdownIpcResponse { ChannelCount = 0, BusyChannels = [] };

        // Act
        new ShutdownPrinter(output).Print(response);

        // Assert
        Assert.Equal("Node is shutting down (no HTLCs in flight)\n"
                   + "  Channels:          0\n",
                     output.ToString().ReplaceLineEndings("\n"));
    }
}