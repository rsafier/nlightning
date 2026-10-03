namespace NLightning.Daemon.Tests.Client;

using Domain.Channels.Enums;
using Domain.Channels.Splicing.Enums;
using Domain.Money;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

public class ListChannelsPrinterFundingsTests
{
    private static readonly byte[] s_channelId = Enumerable.Repeat((byte)0x07, 32).ToArray();

    [Fact]
    public void Given_SplicedChannel_When_Printed_Then_FundingsAndRetiredScidsFollowTheChannel()
    {
        // Arrange: txids in internal order; the printer shows them in display order (reversed, NL-303)
        var currentTxId = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var pendingTxId = Enumerable.Repeat((byte)0xab, 32).ToArray();
        var response = new ListChannelsIpcResponse
        {
            Channels =
            [
                CreateChannel([
                                  new ChannelFundingInfoIpcResponse
                                  {
                                      FundingTxId = currentTxId,
                                      OutputIndex = 1,
                                      Capacity = LightningMoney.Satoshis(1_500_000),
                                      Status = ChannelFundingStatus.Current,
                                      Kind = ChannelFundingKind.Splice,
                                      Depth = 8,
                                      ShortChannelId = (200UL << 40) | (2UL << 16) | 1UL,
                                      SpliceLockedSent = true,
                                      SpliceLockedReceived = true
                                  },
                                  new ChannelFundingInfoIpcResponse
                                  {
                                      FundingTxId = pendingTxId,
                                      OutputIndex = 0,
                                      Capacity = LightningMoney.Satoshis(2_000_000),
                                      Status = ChannelFundingStatus.Pending,
                                      Kind = ChannelFundingKind.Splice
                                  }
                              ],
                              [
                                  new RetiredScidInfoIpcResponse
                                  {
                                      ShortChannelId = (100UL << 40) | (1UL << 16),
                                      RetiredAtHeight = 205,
                                      ExpiresAtHeight = 277
                                  }
                              ])
            ]
        };
        var currentDisplay = Convert.ToHexStringLower(currentTxId.Reverse().ToArray());
        var pendingDisplay = Convert.ToHexStringLower(pendingTxId);

        // Act
        var output = Print(response);

        // Assert
        Assert.Contains(Lines("  Fundings:",
                              "    - Current (Splice)",
                              $"      Outpoint:         {currentDisplay}:1",
                              "      Capacity (sat):   1500000",
                              "      Depth:            8",
                              "      Short Channel Id: 200x2x1",
                              "      splice_locked:    sent Yes, received Yes",
                              "    - Pending (Splice)",
                              $"      Outpoint:         {pendingDisplay}:0",
                              "      Capacity (sat):   2000000",
                              "      Depth:            unconfirmed",
                              "      Short Channel Id: -",
                              "      splice_locked:    sent No, received No",
                              "  Retired SCIDs:",
                              "    - 100x1x0 (retired at 205, expires at 277)"), output);
    }

    [Fact]
    public void Given_InitialFundingOnly_When_Printed_Then_NoSpliceLockedLine()
    {
        // Arrange
        var response = new ListChannelsIpcResponse
        {
            Channels =
            [
                CreateChannel([
                                  new ChannelFundingInfoIpcResponse
                                  {
                                      FundingTxId = new byte[32],
                                      Capacity = LightningMoney.Satoshis(1_000),
                                      Status = ChannelFundingStatus.Current,
                                      Kind = ChannelFundingKind.Initial,
                                      Depth = 3
                                  }
                              ], [])
            ]
        };

        // Act
        var output = Print(response);

        // Assert
        Assert.Contains("    - Current (Initial)\n", output);
        Assert.DoesNotContain("splice_locked", output);
        Assert.DoesNotContain("Retired SCIDs:", output);
    }

    [Fact]
    public void Given_DaemonWithoutFundings_When_Printed_Then_NoFundingSection()
    {
        // Arrange: a daemon that predates wave sp2 sends null lists
        var response = new ListChannelsIpcResponse { Channels = [CreateChannel(null, null)] };

        // Act
        var output = Print(response);

        // Assert
        Assert.DoesNotContain("Fundings:", output);
        Assert.DoesNotContain("Retired SCIDs:", output);
    }

    private static ChannelInfoIpcResponse CreateChannel(List<ChannelFundingInfoIpcResponse>? fundings,
                                                        List<RetiredScidInfoIpcResponse>? retired)
    {
        var peerId = Enumerable.Repeat((byte)0x11, 33).ToArray();
        peerId[0] = 0x02;
        return new ChannelInfoIpcResponse
        {
            ChannelId = s_channelId,
            PeerId = peerId,
            State = ChannelState.Open,
            Capacity = LightningMoney.Satoshis(1_500_000),
            LocalBalance = LightningMoney.Zero,
            RemoteBalance = LightningMoney.Zero,
            Fundings = fundings,
            RetiredShortChannelIds = retired
        };
    }

    private static string Print(ListChannelsIpcResponse response)
    {
        using var writer = new StringWriter();
        writer.NewLine = "\n";
        new ListChannelsPrinter(writer).Print(response);
        return writer.ToString();
    }

    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";
}