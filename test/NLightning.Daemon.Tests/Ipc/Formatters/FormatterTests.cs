using MessagePack;

namespace NLightning.Daemon.Tests.Ipc.Formatters;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.ValueObjects;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

public class FormatterTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;

    private static readonly MessagePackSerializerOptions s_uncompressedOptions =
        NLightningMessagePackOptions.Options.WithCompression(MessagePackCompression.None);

    private static readonly byte[] s_bytes32 = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void GivenHash_WhenSerialized_ThenIsWrittenAsBin32()
    {
        // Act
        var bytes = MessagePackSerializer.Serialize(new Hash(s_bytes32), s_uncompressedOptions,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([0xc4, 0x20, .. s_bytes32], bytes);
    }

    [Fact]
    public void GivenTxId_WhenSerialized_ThenIsWrittenAsBin32()
    {
        // Act
        var bytes = MessagePackSerializer.Serialize(new TxId(s_bytes32), s_uncompressedOptions,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([0xc4, 0x20, .. s_bytes32], bytes);
    }

    [Fact]
    public void GivenHashAndTxId_WhenRoundTripped_ThenValuesArePreserved()
    {
        // Arrange
        var hash = new Hash(s_bytes32);
        var txId = new TxId(s_bytes32.Reverse().ToArray());

        // Act
        var hashResult = MessagePackSerializer.Deserialize<Hash>(
            MessagePackSerializer.Serialize(hash, s_options, TestContext.Current.CancellationToken), s_options,
            TestContext.Current.CancellationToken);
        var txIdResult = MessagePackSerializer.Deserialize<TxId>(
            MessagePackSerializer.Serialize(txId, s_options, TestContext.Current.CancellationToken), s_options,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(hash, hashResult);
        Assert.Equal(txId, txIdResult);
    }

    [Fact]
    public void GivenResponseWithDefaultHash_WhenRoundTripped_ThenFollowingFieldsAreIntact()
    {
        // Arrange
        var response = new NodeInfoIpcResponse
        {
            PubKey = new CompactPubKey([0x02, .. s_bytes32]),
            ListeningTo = ["127.0.0.1:9735"],
            Network = BitcoinNetwork.Regtest,
            BestBlockHeight = 101,
            BestBlockTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)
        };

        // Act
        var bytes = MessagePackSerializer.Serialize(response, s_options, TestContext.Current.CancellationToken);
        var result = MessagePackSerializer.Deserialize<NodeInfoIpcResponse>(bytes, s_options,
                                                                            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(default, result.BestBlockHash);
        Assert.Equal(response.BestBlockTime, result.BestBlockTime);
    }

    [Fact]
    public void GivenSignedTransaction_WhenRoundTripped_ThenValuesArePreserved()
    {
        // Arrange
        var signedTransaction = new SignedTransaction(new TxId(s_bytes32), [0x02, 0x00, 0x00, 0x00, 0x01]);

        // Act
        var bytes = MessagePackSerializer.Serialize(signedTransaction, s_options,
                                                    TestContext.Current.CancellationToken);
        var result = MessagePackSerializer.Deserialize<SignedTransaction>(bytes, s_options,
                                                                          TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(signedTransaction.TxId, result.TxId);
        Assert.Equal(signedTransaction.RawTxBytes, result.RawTxBytes);
    }

    [Fact]
    public void GivenNullSignedTransaction_WhenRoundTripped_ThenIsNull()
    {
        // Act
        var bytes = MessagePackSerializer.Serialize<SignedTransaction?>(null, s_options,
                                                                        TestContext.Current.CancellationToken);
        var result = MessagePackSerializer.Deserialize<SignedTransaction?>(bytes, s_options,
                                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GivenSubscriptionResponseWithOptionalTxId_WhenRoundTripped_ThenValuesArePreserved(bool hasTxId)
    {
        // Arrange
        var response = new OpenChannelSubscriptionIpcResponse
        {
            ChannelId = new ChannelId(s_bytes32),
            ChannelState = ChannelState.ReadyForUs,
            TxId = hasTxId ? new TxId(s_bytes32) : (TxId?)null,
            Index = hasTxId ? 1u : null
        };

        // Act
        var bytes = MessagePackSerializer.Serialize(response, s_options, TestContext.Current.CancellationToken);
        var result = MessagePackSerializer.Deserialize<OpenChannelSubscriptionIpcResponse>(
            bytes, s_options, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(response.TxId, result.TxId);
        Assert.Equal(response.Index, result.Index);
        Assert.Equal(response.ChannelState, result.ChannelState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GivenOpenChannelResponseWithOptionalFunding_WhenRoundTripped_ThenValuesArePreserved(bool hasTxId)
    {
        // Arrange (NL-535: keys 1 and 2, absent for a v1 open)
        var response = new OpenChannelIpcResponse
        {
            ChannelId = new ChannelId(s_bytes32),
            FundingTxId = hasTxId ? new TxId(s_bytes32) : (TxId?)null,
            FundingOutputIndex = hasTxId ? 2u : null
        };

        // Act
        var bytes = MessagePackSerializer.Serialize(response, s_options, TestContext.Current.CancellationToken);
        var result = MessagePackSerializer.Deserialize<OpenChannelIpcResponse>(
            bytes, s_options, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(response.ChannelId, result.ChannelId);
        Assert.Equal(response.FundingTxId, result.FundingTxId);
        Assert.Equal(response.FundingOutputIndex, result.FundingOutputIndex);
    }

    [Fact]
    public void GivenSubscriptionRequestWithAKnownFunding_WhenRoundTripped_ThenTheClientRequestCarriesIt()
    {
        // Arrange (NL-535: keys 1 and 2)
        var request = new OpenChannelSubscriptionIpcRequest
        {
            ChannelId = new ChannelId(s_bytes32),
            KnownFundingTxId = new TxId(s_bytes32),
            ReportFundingChanges = true
        };

        // Act
        var bytes = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken);
        var result = MessagePackSerializer.Deserialize<OpenChannelSubscriptionIpcRequest>(
                                              bytes, s_options, TestContext.Current.CancellationToken)
                                          .ToClientRequest();

        // Assert
        Assert.Equal(request.ChannelId, result.ChannelId);
        Assert.Equal(request.KnownFundingTxId, result.KnownFundingTxId);
        Assert.True(result.ReportFundingChanges);
    }

    [Fact]
    public void GivenAnOlderClientsSubscriptionRequest_WhenRead_ThenNoFundingChangesAreReported()
    {
        // Arrange: only key 0, as a client before NL-535 writes it
        var older = MessagePackSerializer.Serialize(new object[] { new ChannelId(s_bytes32) }, s_options,
                                                    TestContext.Current.CancellationToken);

        // Act
        var result = MessagePackSerializer.Deserialize<OpenChannelSubscriptionIpcRequest>(
                                              older, s_options, TestContext.Current.CancellationToken)
                                          .ToClientRequest();

        // Assert
        Assert.Null(result.KnownFundingTxId);
        Assert.False(result.ReportFundingChanges);
    }
}