namespace NLightning.Daemon.Tests.Client;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// NL-535: <c>openchannel</c> prints the funding transaction as soon as it is published (at once for a dual-funded
/// open), every new attempt of an RBF, returns early with <c>--no-wait</c>, and stops waiting on Ctrl-C after a txid
/// was printed without failing.
/// </summary>
public class OpenChannelCommandTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x42, 32).ToArray());
    private static readonly TxId s_first = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());
    private static readonly TxId s_second = new(Enumerable.Repeat((byte)0xB2, 32).ToArray());

    [Fact]
    public async Task Given_ADualFundedOpen_When_Run_Then_TheFirstTxIdIsPrintedBeforeAnySubscriptionAndEachRbfAfter()
    {
        // Arrange: the daemon answers the first wait with B's bump, the second with channel_ready
        var output = new StringWriter { NewLine = "\n" };
        var known = new List<TxId?>();
        var answers = new Queue<OpenChannelSubscriptionIpcResponse>([
            Funding(s_second),
            new OpenChannelSubscriptionIpcResponse
            {
                ChannelId = s_channelId, ChannelState = ChannelState.ReadyForUs, TxId = s_second, Index = 0
            }
        ]);
        var printedBeforeSubscribing = false;

        // Act
        await OpenChannelMessageHandler.RunAsync(
            _ => Task.FromResult(DualFundedOpen()),
            (channelId, txId, _) =>
            {
                Assert.Equal(s_channelId, channelId);
                printedBeforeSubscribing |= output.ToString().Contains(DisplayOrder.ToHex(s_first));
                known.Add(txId);
                return Task.FromResult(answers.Dequeue());
            }, false, output, TestContext.Current.CancellationToken);

        // Assert
        var text = output.ToString();
        Assert.True(printedBeforeSubscribing);
        Assert.Equal([s_first, s_second], known);
        Assert.Contains($"Funding transaction published. TxId: {DisplayOrder.ToHex(s_first)}, Index: 1\n", text);
        Assert.Contains($"bumpopen {s_channelId} <feerate_per_kw>", text);
        Assert.Contains($"The channel's funding transaction changed (replaces {DisplayOrder.ToHex(s_first)}).", text);
        Assert.Contains($"Funding transaction published. TxId: {DisplayOrder.ToHex(s_second)}, Index: 0\n", text);
        Assert.EndsWith("Channel is now open!\n", text);
    }

    [Fact]
    public async Task Given_NoWaitAndADualFundedOpen_When_Run_Then_ReturnsAfterTheTxIdWithoutSubscribing()
    {
        // Arrange
        var output = new StringWriter { NewLine = "\n" };
        var subscribed = false;

        // Act
        await OpenChannelMessageHandler.RunAsync(_ => Task.FromResult(DualFundedOpen()), (_, _, _) =>
        {
            subscribed = true;
            return Task.FromResult(Funding(s_first));
        }, true, output, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(subscribed);
        Assert.Contains(DisplayOrder.ToHex(s_first), output.ToString());
        Assert.Contains("the open continues on the node", output.ToString());
    }

    [Fact]
    public async Task Given_NoWaitAndAV1Open_When_Run_Then_ReturnsOnceTheFundingIsPublished()
    {
        // Arrange: a v1 open's response carries no txid; the first subscription answer does
        var output = new StringWriter { NewLine = "\n" };
        var calls = 0;

        // Act
        await OpenChannelMessageHandler.RunAsync(
            _ => Task.FromResult(new OpenChannelIpcResponse { ChannelId = s_channelId }), (_, txId, _) =>
            {
                calls++;
                Assert.Null(txId);
                return Task.FromResult(Funding(s_first));
            }, true, output, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, calls);
        Assert.Contains("Peer sent their signature. Sending ours.", output.ToString());
        Assert.Contains(DisplayOrder.ToHex(s_first), output.ToString());
    }

    [Fact]
    public async Task Given_CtrlCAfterTheTxIdWasPrinted_When_Waiting_Then_StopsWithoutAnError()
    {
        // Arrange: the wait for channel_ready is interrupted
        var output = new StringWriter { NewLine = "\n" };
        using var cancellation = new CancellationTokenSource();

        // Act
        await OpenChannelMessageHandler.RunAsync(_ => Task.FromResult(DualFundedOpen()), async (_, _, ct) =>
        {
            await cancellation.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return Funding(s_second);
        }, false, output, cancellation.Token);

        // Assert
        Assert.Contains("Not waiting for the channel to be ready; the open continues on the node.", output.ToString());
    }

    [Fact]
    public async Task Given_CtrlCBeforeAnyTxId_When_Waiting_Then_TheCancellationGoesOn()
    {
        // Arrange: a v1 open cancelled before its funding was published
        var output = new StringWriter { NewLine = "\n" };
        using var cancellation = new CancellationTokenSource();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenChannelMessageHandler.RunAsync(
                                                                    _ => Task.FromResult(new OpenChannelIpcResponse
                                                                    {
                                                                        ChannelId = s_channelId
                                                                    }), async (_, _, ct) =>
                                                                    {
                                                                        await cancellation.CancelAsync();
                                                                        ct.ThrowIfCancellationRequested();
                                                                        return Funding(s_first);
                                                                    }, false, output, cancellation.Token));
    }

    private static OpenChannelIpcResponse DualFundedOpen() =>
        new() { ChannelId = s_channelId, FundingTxId = s_first, FundingOutputIndex = 1 };

    private static OpenChannelSubscriptionIpcResponse Funding(TxId txId) =>
        new() { ChannelId = s_channelId, ChannelState = ChannelState.V1FundingSigned, TxId = txId, Index = 0 };
}