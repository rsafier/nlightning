using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.RPC;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;

/// <summary>
/// NL-534: <see cref="BitcoinChainService.SendTransactionAsync"/> leaves bitcoind's refusals of a transaction (a
/// stored broadcast the chain monitor sends again: inputs spent by the RBF sibling that won, a mempool conflict, a fee
/// too low, already in the chain) to its caller's log level and logs only an unexpected failure as an error.
/// </summary>
public class BitcoinChainServiceSendTests
{
    [Theory]
    [InlineData(-25, "bad-txns-inputs-missingorspent")]
    [InlineData(-26, "txn-mempool-conflict")]
    [InlineData(-26, "insufficient fee, rejecting replacement")]
    [InlineData(-26, "min relay fee not met, 100 < 141")]
    [InlineData(-27, "Transaction already in block chain")]
    public async Task Given_BitcoindRefusesTheTransaction_When_Sent_Then_ThrownAndLoggedBelowWarning(int code,
        string reason)
    {
        // Arrange
        var logger = new RecordingLogger();
        using var node = new FakeRpcNode(_ => Error(code, reason));
        var service = node.CreateService(logger);

        // Act
        var exception = await Assert.ThrowsAsync<RPCException>(() => service.SendTransactionAsync(CreateTransaction()));

        // Assert: the caller still sees the refusal; the service logs it at Debug only
        Assert.Equal(reason, exception.Message);
        Assert.True(BroadcastRefusalRules.IsNodeRefusal(exception));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains(reason));
    }

    [Fact]
    public async Task Given_AnUnexpectedRpcError_When_Sent_Then_LoggedAsAnError()
    {
        // Arrange: an RPC failure that is no verdict on the transaction
        var logger = new RecordingLogger();
        using var node = new FakeRpcNode(_ => Error(-1, "something went wrong"));
        var service = node.CreateService(logger);

        // Act
        var exception = await Assert.ThrowsAsync<RPCException>(() => service.SendTransactionAsync(CreateTransaction()));

        // Assert
        Assert.False(BroadcastRefusalRules.IsNodeRefusal(exception));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Failed to broadcast"));
    }

    [Fact]
    public async Task Given_AnUnreachableNode_When_Sent_Then_LoggedAsAnError()
    {
        // Arrange: the service was built against a node that is gone now
        var logger = new RecordingLogger();
        BitcoinChainService service;
        using (var node = new FakeRpcNode(_ => Error(-1, "unused")))
            service = node.CreateService(logger);

        // Act
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => service.SendTransactionAsync(CreateTransaction()));

        // Assert
        Assert.False(BroadcastRefusalRules.IsNodeRefusal(exception));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Failed to broadcast"));
    }

    [Theory]
    [InlineData(RPCErrorCode.RPC_VERIFY_ERROR, true)]
    [InlineData(RPCErrorCode.RPC_VERIFY_REJECTED, true)]
    [InlineData(RPCErrorCode.RPC_VERIFY_ALREADY_IN_CHAIN, true)]
    [InlineData(RPCErrorCode.RPC_DESERIALIZATION_ERROR, false)]
    [InlineData(RPCErrorCode.RPC_MISC_ERROR, false)]
    [InlineData(RPCErrorCode.RPC_IN_WARMUP, false)]
    public void Given_AnRpcErrorCode_When_Classified_Then_OnlyVerdictsOnTheTransactionAreRefusals(RPCErrorCode code,
        bool expected)
    {
        // Arrange
        var exception = new RPCException(code, "reason", null!);

        // Act / Assert
        Assert.Equal(expected, BroadcastRefusalRules.IsNodeRefusal(exception));
        Assert.False(BroadcastRefusalRules.IsNodeRefusal(new HttpRequestException("Connection refused")));
    }

    private static string Error(int code, string message) =>
        $$"""{"result":null,"error":{"code":{{code}},"message":"{{message}}"},"id":1}""";

    private static Transaction CreateTransaction()
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat((byte)0x11, 32).ToArray()), 0));
        transaction.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
        return transaction;
    }

    private sealed class RecordingLogger : ILogger<BitcoinChainService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}