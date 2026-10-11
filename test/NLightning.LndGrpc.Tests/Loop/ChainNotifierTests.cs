using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using NBitcoin;

namespace NLightning.LndGrpc.Tests.Loop;

using Infrastructure.Bitcoin.Wallet.Interfaces;
using LndGrpc.Chainrpc;
using LndGrpc.Services;
using ConfEvent = LndGrpc.Chainrpc.ConfEvent;

public sealed class ChainNotifierTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Given_ACommittedTipAboveCoreAfterDisconnect_When_TheBranchIsReplaced_Then_StreamsSurvive(int kind)
    {
        using var state = new State();
        var activeTip = 3U;
        state.Chain.Setup(c => c.GetCurrentBlockHeightAsync()).ReturnsAsync(() => activeTip);
        state.Chain.Setup(c => c.GetBlockHashAsync(It.IsAny<uint>())).Returns((uint height) => height <= activeTip
            ? Task.FromResult(state.Blocks[height].GetHash())
            : Task.FromException<uint256>(new NBitcoin.RPC.RPCException(NBitcoin.RPC.RPCErrorCode.RPC_INVALID_PARAMETER,
                "Block height out of range", null!)));
        var funding = state.Funding();
        var spending = Network.RegTest.CreateTransaction();
        spending.Inputs.Add(new TxIn(new NBitcoin.OutPoint(funding.GetHash(), 0)));
        spending.Outputs.Add(Money.Satoshis(900), funding.Outputs[0].ScriptPubKey);
        state.Blocks[2].Transactions.Add(funding);
        state.Blocks[2].Transactions.Add(spending);
        var epochs = new Writer<BlockEpoch>();
        var confs = new Writer<ConfEvent>();
        var spends = new Writer<SpendEvent>();
        var task = kind switch
        {
            0 => state.Service.RegisterBlockEpochNtfn(new BlockEpoch(), epochs, state.Context),
            1 => state.Service.RegisterConfirmationsNtfn(new ConfRequest
            {
                Txid = ByteString.CopyFrom(funding.GetHash().ToBytes()),
                Script = ByteString.CopyFrom(new byte[] { 0x51 }),
                NumConfs = 1,
                HeightHint = 1
            }, confs, state.Context),
            _ => state.Service.RegisterSpendNtfn(new SpendRequest
            {
                Outpoint = new Outpoint { Hash = ByteString.CopyFrom(funding.GetHash().ToBytes()), Index = 0 },
                Script = ByteString.CopyFrom(new byte[] { 0x51 }),
                HeightHint = 1
            }, spends, state.Context)
        };
        if (kind == 0) Assert.Equal(3U, (await epochs.Read()).Height);
        else if (kind == 1) Assert.NotNull((await confs.Read()).Conf);
        else Assert.NotNull((await spends.Read()).Spend);
        activeTip = 1;
        await Task.Delay(TimeSpan.FromMilliseconds(750), state.Context.CancellationToken);
        Assert.False(task.IsCompleted);
        state.ReplaceFrom(2);
        activeTip = 3;
        if (kind == 0) Assert.Equal(2U, (await epochs.Read()).Height);
        else if (kind == 1) Assert.NotNull((await confs.Read()).Reorg);
        else Assert.NotNull((await spends.Read()).Reorg);
        await state.End(task);
    }

    [Fact]
    public async Task Given_AHeightLookupRacesADisconnect_When_RegisteringEpochs_Then_TheHeightIsRetried()
    {
        using var state = new State();
        var reads = 0;
        state.Chain.Setup(c => c.GetBlockHashAsync(3U)).Returns(() => ++reads == 2
            ? Task.FromException<uint256>(new NBitcoin.RPC.RPCException(NBitcoin.RPC.RPCErrorCode.RPC_INVALID_PARAMETER,
                "Block height out of range", null!))
            : Task.FromResult(state.Blocks[3].GetHash()));
        var writer = new Writer<BlockEpoch>();
        var task = state.Service.RegisterBlockEpochNtfn(new BlockEpoch(), writer, state.Context);
        Assert.Equal(3U, (await writer.Read()).Height);
        Assert.True(reads >= 3);
        await state.End(task);
    }

    [Fact]
    public async Task Given_NoBestBlock_When_RegisteringEpochs_Then_TipArrivesImmediatelyAndForkIsReplayed()
    {
        // Arrange
        using var state = new State();
        var writer = new Writer<BlockEpoch>();
        var task = state.Service.RegisterBlockEpochNtfn(new BlockEpoch(), writer, state.Context);
        // Act / Assert
        Assert.Equal(3U, (await writer.Read()).Height);
        var replacement = state.ReplaceTip();
        var epoch = await writer.Read();
        Assert.Equal(3U, epoch.Height);
        Assert.Equal(replacement.GetHash().ToBytes(), epoch.Hash.ToByteArray());
        await state.End(task);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_AHistoricalConfirmation_When_Registered_Then_ConfReorgAndReconfirmationAreDelivered(bool scriptOnly)
    {
        // Arrange
        using var state = new State();
        var tx = state.Funding();
        state.Blocks[2].Transactions.Add(tx);
        var writer = new Writer<ConfEvent>();
        var task = state.Service.RegisterConfirmationsNtfn(new ConfRequest
        {
            Txid = scriptOnly ? ByteString.Empty : ByteString.CopyFrom(tx.GetHash().ToBytes()),
            Script = ByteString.CopyFrom(tx.Outputs[0].ScriptPubKey.ToBytes()),
            NumConfs = 2,
            HeightHint = 1,
            IncludeBlock = true
        }, writer, state.Context);
        // Act / Assert
        var confirmation = await writer.Read();
        Assert.Equal(2U, confirmation.Conf.BlockHeight);
        Assert.Equal(tx.ToBytes(), confirmation.Conf.RawTx.ToByteArray());
        Assert.NotEmpty(confirmation.Conf.RawBlock);
        state.ReplaceFrom(2);
        Assert.Equal(ConfEvent.EventOneofCase.Reorg, (await writer.Read()).EventCase);
        state.Blocks[2].Transactions.Add(tx);
        state.ReplaceTip();
        Assert.Equal(ConfEvent.EventOneofCase.Conf, (await writer.Read()).EventCase);
        await state.End(task);
    }

    [Fact]
    public async Task Given_AHistoricalSpend_When_Reorged_Then_SpendIsRearmed()
    {
        // Arrange
        using var state = new State();
        var funding = state.Funding();
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new TxIn(new NBitcoin.OutPoint(funding.GetHash(), 0)));
        spend.Outputs.Add(Money.Satoshis(900), funding.Outputs[0].ScriptPubKey);
        state.Blocks[2].Transactions.Add(spend);
        var writer = new Writer<SpendEvent>();
        var task = state.Service.RegisterSpendNtfn(new SpendRequest
        {
            Outpoint = new Outpoint { Hash = ByteString.CopyFrom(funding.GetHash().ToBytes()), Index = 0 },
            Script = ByteString.CopyFrom(funding.Outputs[0].ScriptPubKey.ToBytes()),
            HeightHint = 1
        }, writer, state.Context);
        // Act / Assert
        var first = await writer.Read();
        Assert.Equal(spend.ToBytes(), first.Spend.RawSpendingTx.ToByteArray());
        Assert.Equal(2U, first.Spend.SpendingHeight);
        state.ReplaceFrom(2);
        Assert.Equal(SpendEvent.EventOneofCase.Reorg, (await writer.Read()).EventCase);
        state.ReplaceTip().Transactions.Add(spend);
        Assert.Equal(3U, (await writer.Read()).Spend.SpendingHeight);
        await state.End(task);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_AHaltedOrPrunedChain_When_Registering_Then_ItErrorsInsteadOfGoingSilent(bool coreRpcError)
    {
        // Arrange
        using var state = new State();
        state.Monitor.SetupGet(m => m.IsChainProcessingHalted).Returns(true);
        // Act / Assert
        var halt = await Assert.ThrowsAsync<RpcException>(() => state.Service.RegisterBlockEpochNtfn(new BlockEpoch(), new Writer<BlockEpoch>(), state.Context));
        Assert.Equal(StatusCode.Unavailable, halt.StatusCode);
        state.Monitor.SetupGet(m => m.IsChainProcessingHalted).Returns(false);
        if (coreRpcError)
            state.Chain.Setup(c => c.GetBlockAsync(1U)).ThrowsAsync(new NBitcoin.RPC.RPCException(
                NBitcoin.RPC.RPCErrorCode.RPC_MISC_ERROR, "Block not available (pruned data)", null!));
        else state.Chain.Setup(c => c.GetBlockAsync(1U)).ReturnsAsync((Block?)null);
        var pruned = await Assert.ThrowsAsync<RpcException>(() => state.Service.RegisterConfirmationsNtfn(new ConfRequest
        {
            Script = ByteString.CopyFrom(new byte[] { 0x51 }),
            NumConfs = 1,
            HeightHint = 1
        }, new Writer<ConfEvent>(), state.Context));
        Assert.Equal(StatusCode.FailedPrecondition, pruned.StatusCode);
    }

    [Fact]
    public async Task Given_RegistrationLimit_When_CallerCancels_Then_TheSlotIsReleased()
    {
        // Arrange
        using var state = new State();
        var service = new ChainNotifierService(state.Chain.Object, state.Monitor.Object,
            Options.Create(new LndGrpcOptions { MaxChainNotifierRegistrations = 1 }));
        var writer = new Writer<BlockEpoch>();
        var task = service.RegisterBlockEpochNtfn(new BlockEpoch(), writer, state.Context);
        await writer.Read();
        // Act / Assert
        var exhausted = await Assert.ThrowsAsync<RpcException>(() => service.RegisterBlockEpochNtfn(new BlockEpoch(), writer, state.Context));
        Assert.Equal(StatusCode.ResourceExhausted, exhausted.StatusCode);
        await state.End(task);
        // A cancelled new caller reaches its write, not the exhausted registration check.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RegisterBlockEpochNtfn(new BlockEpoch(), writer, state.Context));
    }

    [Fact]
    public async Task Given_AScriptOnlyWitnessRegistration_When_TheFundingPredatesTheHint_Then_TheSpendIsReported()
    {
        // Arrange
        using var state = new State();
        using var key = new Key();
        var tx = state.Funding();
        tx.Inputs[0].WitScript = new WitScript(Op.GetPushOp(new byte[72]), Op.GetPushOp(key.PubKey.ToBytes()));
        state.Blocks[2].Transactions.Add(tx);
        var writer = new Writer<SpendEvent>();
        // Act
        var task = state.Service.RegisterSpendNtfn(new SpendRequest
        {
            Script = ByteString.CopyFrom(key.PubKey.WitHash.ScriptPubKey.ToBytes()),
            HeightHint = 2
        }, writer, state.Context);
        var result = await writer.Read();
        // Assert
        Assert.Equal(tx.Inputs[0].PrevOut.Hash.ToBytes(), result.Spend.SpendingOutpoint.Hash.ToByteArray());
        Assert.Equal(2U, result.Spend.SpendingHeight);
        await state.End(task);
    }

    private sealed class State : IDisposable
    {
        public Dictionary<uint, Block> Blocks { get; } = [];
        public Mock<IBitcoinChainService> Chain { get; } = new();
        public Mock<IBlockchainMonitor> Monitor { get; } = new();
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        public ServerCallContext Context { get; }
        public ChainNotifierService Service { get; }
        private uint _nonce;

        public State()
        {
            Stop.CancelAfter(TimeSpan.FromSeconds(15));
            for (uint height = 0; height <= 3; height++) Blocks[height] = Make(height);
            Chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => Blocks.GetValueOrDefault(h));
            Chain.Setup(c => c.GetBlockHashAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => Blocks[h].GetHash());
            Chain.Setup(c => c.GetCurrentBlockHeightAsync()).ReturnsAsync(() => Blocks.Keys.Max());
            Monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(3U);
            var context = new Mock<ServerCallContext>();
            context.Protected().SetupGet<CancellationToken>("CancellationTokenCore").Returns(Stop.Token);
            Context = context.Object;
            Service = new ChainNotifierService(Chain.Object, Monitor.Object, Options.Create(new LndGrpcOptions()));
        }
        private Block Make(uint height)
        {
            var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
            block.Header.Nonce = ++_nonce;
            block.Header.HashPrevBlock = height == 0 ? uint256.Zero : Blocks[height - 1].GetHash();
            return block;
        }
        public Block ReplaceTip() => Blocks[3] = Make(3);
        public void ReplaceFrom(uint height) { for (var i = height; i <= 3; i++) Blocks[i] = Make(i); }
        public NBitcoin.Transaction Funding()
        {
            var tx = Network.RegTest.CreateTransaction();
            tx.Inputs.Add(new TxIn(new NBitcoin.OutPoint(uint256.One, 0)));
            tx.Outputs.Add(Money.Satoshis(1000), new Script(OpcodeType.OP_TRUE));
            return tx;
        }
        public async Task End(Task task)
        {
            await Stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        public void Dispose() => Stop.Dispose();
    }

    private sealed class Writer<T> : IServerStreamWriter<T>
    {
        private readonly Channel<T> _items = Channel.CreateUnbounded<T>();
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message) { _items.Writer.TryWrite(message); return Task.CompletedTask; }
        public Task WriteAsync(T message, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return WriteAsync(message); }
        public async Task<T> Read() => await _items.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }
}