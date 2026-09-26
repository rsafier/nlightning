namespace NLightning.Daemon.Tests.Client;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI shows every txid in the display (bitcoind, block explorer) byte order, the reverse of the internal order a
/// <see cref="TxId"/> holds and prints with <c>ToString()</c>. Pinned against the Mutinynet funding tx of our first
/// public channel, whose <c>openchannel</c> and <c>listchannels</c> output showed the internal order.
/// </summary>
public class TxIdDisplayTests
{
    private const string DisplayHex = "12482a42baf84a4945374ad40c3a77dcd16de1590cfe7374306ae21169aa8d0c";
    private const string InternalHex = "0c8daa6911e26a307473fe0c59e16dd1dc773a0cd44a3745494af8ba422a4812";

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x07, 32).ToArray());
    private static readonly CompactPubKey s_peer = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);

    private static TxId FundingTxId => new(Convert.FromHexString(InternalHex));

    // Bitcoin's genesis block hash
    private const string GenesisDisplayHex = "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f";
    private const string GenesisInternalHex = "6fe28c0ab6f1b372c1a6a246ae63f74f931e8365e15a089c68d6190000000000";

    [Fact]
    public void Given_TxIdInInternalOrder_When_DisplayOrderToHex_Then_ReturnsReversedHexAndKeepsTheTxId()
    {
        // Arrange
        var txId = FundingTxId;

        // Act
        var display = DisplayOrder.ToHex(txId);

        // Assert
        Assert.Equal(DisplayHex, display);
        Assert.Equal(InternalHex, txId.ToString());
    }

    [Fact]
    public void Given_TxIdInInternalOrder_When_IpcToDisplay_Then_MatchesTheClientHelper()
    {
        // Arrange (close, force close and pendingsweeps map their txids with this on the daemon side)
        var txId = FundingTxId;

        // Act
        var display = PendingSweepsIpcResponse.ToDisplay(txId);

        // Assert
        Assert.Equal(DisplayHex, display);
    }

    [Fact]
    public void Given_NoBytes_When_DisplayOrderToHex_Then_ReturnsDash()
    {
        // Act / Assert
        Assert.Equal("-", DisplayOrder.ToHex(null));
        Assert.Equal("-", DisplayOrder.ToHex([]));
    }

    [Fact]
    public void Given_BestBlockHash_When_InfoPrinted_Then_HashIsInDisplayOrder()
    {
        // Arrange
        var response = new NodeInfoIpcResponse
        {
            PubKey = s_peer,
            ListeningTo = ["127.0.0.1:9735"],
            BestBlockHash = new Hash(Convert.FromHexString(GenesisInternalHex)),
            BestBlockHeight = 0
        };

        // Act
        var output = Print(w => new NodeInfoPrinter(w).Print(response));

        // Assert
        Assert.Contains($"  Best Block Hash:   {GenesisDisplayHex}\n", output);
        Assert.DoesNotContain(GenesisInternalHex, output);
    }

    [Fact]
    public void Given_ChannelWithFundingOutput_When_ListChannelsPrinted_Then_FundingOutputIsInDisplayOrder()
    {
        // Arrange
        var response = new ListChannelsIpcResponse
        {
            Channels =
            [
                new ChannelInfoIpcResponse
                {
                    ChannelId = s_channelId,
                    PeerId = s_peer,
                    State = ChannelState.Open,
                    FundingTxId = FundingTxId,
                    FundingOutputIndex = 0,
                    Capacity = LightningMoney.Satoshis(200_000),
                    LocalBalance = LightningMoney.Satoshis(200_000),
                    RemoteBalance = LightningMoney.Zero
                }
            ]
        };

        // Act
        var output = Print(w => new ListChannelsPrinter(w).Print(response));

        // Assert
        Assert.Contains($"  Funding Output:     {DisplayHex}:0\n", output);
        Assert.DoesNotContain(InternalHex, output);
    }

    [Fact]
    public void Given_FundingSigned_When_OpenChannelSubscriptionPrinted_Then_TxIdIsInDisplayOrder()
    {
        // Arrange
        var response = new OpenChannelSubscriptionIpcResponse
        {
            ChannelId = s_channelId,
            ChannelState = ChannelState.V1FundingSigned,
            TxId = FundingTxId,
            Index = 0
        };

        // Act
        var output = Print(w => new OpenChannelSubscriptionPrinter(w).Print(response));

        // Assert
        Assert.Contains($"Funding transaction published. TxId: {DisplayHex}, Index: 0\n", output);
        Assert.DoesNotContain(InternalHex, output);
    }

    [Fact]
    public void Given_FundingSignedWithoutTxId_When_OpenChannelSubscriptionPrinted_Then_TxIdIsDash()
    {
        // Arrange
        var response = new OpenChannelSubscriptionIpcResponse
        {
            ChannelId = s_channelId,
            ChannelState = ChannelState.V1FundingSigned
        };

        // Act
        var output = Print(w => new OpenChannelSubscriptionPrinter(w).Print(response));

        // Assert
        Assert.Contains("Funding transaction published. TxId: -, Index: \n", output);
    }

    [Fact]
    public void Given_ClosingTxId_When_CloseChannelMappedAndPrinted_Then_TxIdIsInDisplayOrder()
    {
        // Arrange
        var clientResponse = new CloseChannelClientResponse(s_channelId, ChannelState.Closing, FundingTxId);

        // Act
        var response = CloseChannelIpcResponse.FromClientResponse(clientResponse);
        var output = Print(w => new CloseChannelPrinter(w).Print(response));

        // Assert
        Assert.Equal(DisplayHex, response.ClosingTxId);
        Assert.Contains($"  Closing TxId: {DisplayHex}\n", output);
    }

    [Fact]
    public void Given_CommitmentTxId_When_ForceCloseMappedAndPrinted_Then_TxIdIsInDisplayOrder()
    {
        // Arrange
        var clientResponse =
            new ForceCloseChannelClientResponse(s_channelId, ChannelState.Failed, "Broadcast", FundingTxId);

        // Act
        var response = ForceCloseChannelIpcResponse.FromClientResponse(clientResponse);
        var output = Print(w => new ForceCloseChannelPrinter(w).Print(response));

        // Assert
        Assert.Equal(DisplayHex, response.CommitmentTxId);
        Assert.Contains($"  Commitment TxId: {DisplayHex}\n", output);
    }

    private static string Print(Action<TextWriter> print)
    {
        using var writer = new StringWriter();
        writer.NewLine = "\n";
        print(writer);
        return writer.ToString();
    }
}