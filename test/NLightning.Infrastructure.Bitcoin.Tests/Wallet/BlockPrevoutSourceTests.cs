using System.Net;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet.SilentPayments;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;

public class BlockPrevoutSourceTests
{
    private static readonly byte[] s_script = [0x00, 0x14, .. new byte[20]];

    [Fact]
    public async Task Given_ActualCore31Capture_When_WireSourcesAreParsed_Then_AllSixSpentScriptTypesAgree()
    {
        // Arrange: captured from the real cluster proof, whose raw transaction source also agrees.
        var fixture = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Wallet", "Fixtures", "core31-prevouts.json"), TestContext.Current.CancellationToken));
        var block = Block.Parse((string)fixture["block_hex"]!, Network.RegTest);
        using var rest = new MemoryStream(Convert.FromHexString((string)fixture["rest_hex"]!));

        // Act
        var undo = await BlockPrevoutSource.ParseVerboseAsync(new StringReader(fixture["getblock3"]!.ToString()),
            block, cancellationToken: TestContext.Current.CancellationToken);
        var binary = await BlockPrevoutSource.ParseRestAsync(rest, block, TestContext.Current.CancellationToken);

        // Assert: six independently funded script classes plus one earlier-in-block child.
        Assert.Equal(7, undo.Count);
        Assert.Equal(undo.Count, binary.Count);
        foreach (var (id, previous) in undo)
        {
            var values = binary[id];
            Assert.Equal(previous.Count, values.Count);
            for (var i = 0; i < previous.Count; i++)
            {
                Assert.Equal(previous[i].AmountSat, values[i].AmountSat);
                Assert.Equal((byte[])previous[i].ScriptPubKey, (byte[])values[i].ScriptPubKey);
            }
        }
        var inputs = undo.Values.SelectMany(values => values).ToArray();
        Assert.Equal(6, inputs.Count(input => input.AmountSat == 200_000));
        Assert.Single(inputs, input => input.AmountSat == 198_000);
        // P2PKH, nested P2WPKH, native P2WPKH, P2WSH, and three taproot inputs (two paths plus child).
        Assert.Equal(new[] { 22, 23, 25, 34, 34, 34, 34 }, inputs.Select(input => ((byte[])input.ScriptPubKey).Length).Order().ToArray());
    }

    [Fact]
    public async Task Given_VerboseUndoData_When_Read_Then_InputOrderAmountsAndHexArePreserved()
    {
        // Arrange: asm is deliberately wrong; only hex is authoritative.
        var block = CreateBlock();
        var json = CreateVerbose(block);
        var inputs = (JArray)json["result"]!["tx"]![1]!["vin"]!;
        inputs[0]["prevout"]!["scriptPubKey"]!["asm"] = "wrong";
        inputs[1]["prevout"]!["value"] = 0.00000001m;

        // Act
        var previous = await BlockPrevoutSource.ParseVerboseAsync(new StringReader(json.ToString()), block, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var values = Assert.Single(previous).Value;
        Assert.Equal(2, values.Count);
        Assert.Equal(125_000UL, values[0].AmountSat);
        Assert.Equal(1UL, values[1].AmountSat);
        Assert.Equal(s_script, (byte[])values[0].ScriptPubKey);
    }

    [Fact]
    public async Task Given_LargeExactBtcAmount_When_Read_Then_NoSatoshiIsRoundedThroughFloatingPoint()
    {
        // Arrange: sixteen significant digits exceed double-to-decimal's guaranteed precision.
        var block = CreateBlock();
        var json = CreateVerbose(block);
        json["result"]!["tx"]![1]!["vin"]![0]!["prevout"]!["value"] = 12_345_678.12345678m;

        // Act
        var previous = await BlockPrevoutSource.ParseVerboseAsync(new StringReader(json.ToString()), block, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1_234_567_812_345_678UL, Assert.Single(previous).Value[0].AmountSat);
    }

    [Fact]
    public async Task Given_MissingUndoData_When_Read_Then_ItFailsInsteadOfSkippingTheInput()
    {
        // Arrange
        var block = CreateBlock();
        var json = CreateVerbose(block);
        ((JObject)json["result"]!["tx"]![1]!["vin"]![0]!).Remove("prevout");

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => BlockPrevoutSource.ParseVerboseAsync(
            new StringReader(json.ToString()), block, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_PrunedRpcError_When_Read_Then_TheFailureIsExplicit()
    {
        // Arrange
        var json = "{\"result\":null,\"error\":{\"code\":-1,\"message\":\"Block not available (pruned data)\"}}";

        // Act / Assert
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BlockPrevoutSource.ParseVerboseAsync(
            new StringReader(json), CreateBlock(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("pruned", error.Message);
    }

    [Fact]
    public async Task Given_NonCandidateTransaction_When_Read_Then_NoPrevoutIsRequiredForIt()
    {
        // Arrange
        var block = CreateBlock();
        var ignored = Network.RegTest.CreateTransaction();
        ignored.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        ignored.Outputs.Add(Money.Satoshis(100), new Script(s_script));
        block.Transactions.Add(ignored);
        var json = CreateVerbose(block);
        ((JObject)json["result"]!["tx"]![2]!["vin"]![0]!).Remove("prevout");

        // Act
        var previous = await BlockPrevoutSource.ParseVerboseAsync(new StringReader(json.ToString()), block, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(previous);
    }

    [Fact]
    public async Task Given_MismatchedOutpoint_When_Read_Then_DataIsRejected()
    {
        // Arrange
        var block = CreateBlock();
        var json = CreateVerbose(block);
        json["result"]!["tx"]![1]!["vin"]![0]!["vout"] = 9;

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => BlockPrevoutSource.ParseVerboseAsync(
            new StringReader(json.ToString()), block, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_CoreRestWireFormat_When_Read_Then_CoinbaseIsEmptyAndOutputsMatch()
    {
        // Arrange: Core SerializeBlockUndo emits normal CTxOut, not the on-disk compressed Coin format.
        var block = CreateBlock();
        using var stream = CreateRest();

        // Act
        var previous = await BlockPrevoutSource.ParseRestAsync(stream, block, TestContext.Current.CancellationToken);

        // Assert
        var values = Assert.Single(previous).Value;
        Assert.Equal(2, values.Count);
        Assert.All(values, value => Assert.Equal(125_000UL, value.AmountSat));
        Assert.All(values, value => Assert.Equal(s_script, (byte[])value.ScriptPubKey));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(40)]
    public async Task Given_TruncatedRestData_When_Read_Then_ItFails(int length)
    {
        // Arrange
        var block = CreateBlock();
        using var valid = CreateRest();
        using var truncated = new MemoryStream(valid.ToArray()[..length]);

        // Act / Assert
        await Assert.ThrowsAnyAsync<Exception>(() => BlockPrevoutSource.ParseRestAsync(truncated, block, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_NoncanonicalRestCount_When_Read_Then_ItFails()
    {
        // Arrange
        using var stream = new MemoryStream([253, 2, 0]);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => BlockPrevoutSource.ParseRestAsync(stream, CreateBlock(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_TrailingRestData_When_Read_Then_ItFails()
    {
        // Arrange
        using var valid = CreateRest();
        using var stream = new MemoryStream([.. valid.ToArray(), 0]);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => BlockPrevoutSource.ParseRestAsync(stream, CreateBlock(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_StalledRestBody_When_Cancelled_Then_ParsingStops()
    {
        // Arrange: consume the transaction vector header, then stall on a network body read.
        using var valid = CreateRest();
        using var stream = new StalledReadStream(valid.ToArray()[..3]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var parsing = BlockPrevoutSource.ParseRestAsync(stream, CreateBlock(), cancellation.Token);
        await stream.Stalled.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        cancellation.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parsing);
    }

    [Fact]
    public async Task Given_UndoAndRestUnsupported_When_AutoProbes_Then_ItUsesBoundedLookupAndInBlockOutputs()
    {
        // Arrange
        var block = CreateBlock();
        var parent = Network.RegTest.CreateTransaction();
        parent.Inputs.Add(new TxIn(new OutPoint(uint256.One, 7)));
        parent.Outputs.Add(Money.Satoshis(125_000), new Script(s_script));
        parent.Outputs.Add(Money.Satoshis(125_000), new Script(s_script));
        block.Transactions[1].Inputs[0].PrevOut = new OutPoint(parent.GetHash(), 0);
        block.Transactions[1].Inputs[1].PrevOut = new OutPoint(parent.GetHash(), 1);
        var child = Network.RegTest.CreateTransaction();
        child.Inputs.Add(new TxIn(new OutPoint(block.Transactions[1].GetHash(), 0)));
        child.Outputs.Add(Money.Satoshis(90_000), new Script([0x51, 0x20, .. new byte[32]]));
        block.Transactions.Add(child);
        using var handler = new FakeNode(block, parent);
        using var http = new HttpClient(handler);
        var source = new BlockPrevoutSource(http, new Uri("http://localhost/"), Network.RegTest, SilentPaymentPrevoutSource.Auto);
        var domain = new BitcoinBlock(block.ToBytes(), new Hash(block.GetHash().ToBytes()), block.Transactions.Count);

        // Act: two scans reuse the same confirmed parent; the child never needs a lookup.
        var first = await source.GetPrevoutsAsync(domain, 10, TestContext.Current.CancellationToken);
        var second = await source.GetPrevoutsAsync(domain, 10, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(SilentPaymentPrevoutSource.GetRawTransaction, source.Source);
        Assert.Equal(2, first.Count);
        Assert.Equal(100_000UL, first[new TxId(child.GetHash().ToBytes())][0].AmountSat);
        Assert.Equal(first.Keys, second.Keys);
        Assert.Equal(1, handler.RawLookups);
        Assert.Equal(1, handler.VerboseRequests);
        Assert.Equal(1, handler.RestRequests);
    }

    [Fact]
    public async Task Given_ForcedRestUnavailable_When_Probed_Then_ItDoesNotSilentlyFallBack()
    {
        // Arrange
        var block = CreateBlock();
        using var handler = new FakeNode(block, block.Transactions[0]);
        using var http = new HttpClient(handler);
        var source = new BlockPrevoutSource(http, new Uri("http://localhost/"), Network.RegTest, SilentPaymentPrevoutSource.Rest);

        // Act / Assert
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ProbeAsync(TestContext.Current.CancellationToken));
        Assert.Contains("-rest", error.Message);
        Assert.Equal(0, handler.RawLookups);
        Assert.Equal(0, handler.VerboseRequests);
    }

    [Fact]
    public async Task Given_BlockBelowPruneHeight_When_Scanned_Then_ItRefusesWithPrunedError()
    {
        // Arrange
        var block = CreateBlock();
        block.Transactions[1].Inputs[0].PrevOut = new OutPoint(block.Transactions[0].GetHash(), 0);
        using var handler = new FakeNode(block, block.Transactions[0]) { PruneHeight = 20 };
        using var http = new HttpClient(handler);
        var source = new BlockPrevoutSource(http, new Uri("http://localhost/"), Network.RegTest, SilentPaymentPrevoutSource.GetRawTransaction);
        var domain = new BitcoinBlock(block.ToBytes(), new Hash(block.GetHash().ToBytes()), block.Transactions.Count);

        // Act / Assert
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetPrevoutsAsync(domain, 10, TestContext.Current.CancellationToken));
        Assert.Contains("pruned", error.Message);
        Assert.Contains("pruneheight 20", error.Message);
    }

    private sealed class FakeNode(Block block, Transaction parent) : HttpMessageHandler
    {
        public int RawLookups { get; private set; }
        public int VerboseRequests { get; private set; }
        public int RestRequests { get; private set; }
        public uint? PruneHeight { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                RestRequests++;
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("undo not available") };
            }
            var rpc = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var method = (string)rpc["method"]!;
            JToken result;
            switch (method)
            {
                case "getbestblockhash":
                    result = block.GetHash().ToString();
                    break;
                case "getblock" when (int)rpc["params"]![1]! == 0:
                    result = Convert.ToHexString(block.ToBytes());
                    break;
                case "getblock":
                    VerboseRequests++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("{\"result\":null,\"error\":{\"code\":-8,\"message\":\"verbosity unsupported\"}}")
                    };
                case "getrawtransaction":
                    RawLookups++;
                    // The test explicitly permits only the external parent, including startup probing.
                    if ((string)rpc["params"]![0]! != parent.GetHash().ToString())
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        {
                            Content = new StringContent("{\"result\":null,\"error\":{\"code\":-5,\"message\":\"transaction not found\"}}")
                        };
                    result = parent.ToHex();
                    break;
                case "getblockchaininfo":
                    result = new JObject { ["pruned"] = PruneHeight.HasValue, ["pruneheight"] = PruneHeight };
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected RPC: {method}");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JObject { ["result"] = result, ["error"] = null }.ToString())
            };
        }
    }

    private static Block CreateBlock()
    {
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        var coinbase = Network.RegTest.CreateTransaction();
        coinbase.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, uint.MaxValue)));
        coinbase.Outputs.Add(Money.Coins(50), new Script(s_script));
        block.Transactions.Add(coinbase);
        var candidate = Network.RegTest.CreateTransaction();
        candidate.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        candidate.Inputs.Add(new TxIn(new OutPoint(uint256.One, 1)));
        candidate.Outputs.Add(Money.Satoshis(100_000), new Script([0x51, 0x20, .. new byte[32]]));
        block.Transactions.Add(candidate);
        return block;
    }

    private static JObject CreateVerbose(Block block)
    {
        return new JObject
        {
            ["result"] = new JObject
            {
                ["tx"] = new JArray(block.Transactions.Select(tx => new JObject
                {
                    ["txid"] = tx.GetHash().ToString(),
                    ["vin"] = new JArray(tx.Inputs.Select(input => new JObject
                    {
                        ["txid"] = input.PrevOut.Hash.ToString(),
                        ["vout"] = input.PrevOut.N,
                        ["prevout"] = new JObject
                        {
                            ["value"] = 0.00125m,
                            ["scriptPubKey"] = new JObject { ["hex"] = Convert.ToHexString(s_script) }
                        }
                    }))
                }))
            },
            ["error"] = null
        };
    }

    private static MemoryStream CreateRest()
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)2); // transactions
        writer.Write((byte)0); // coinbase input vector
        writer.Write((byte)2); // candidate inputs
        for (var i = 0; i < 2; i++)
        {
            writer.Write(125_000L);
            writer.Write((byte)s_script.Length);
            writer.Write(s_script);
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class StalledReadStream(byte[] prefix) : MemoryStream(prefix)
    {
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length)
                return await base.ReadAsync(buffer, cancellationToken);
            Stalled.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

}