using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

using Bitcoin.Gossip;
using Bitcoin.Wallet.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Interfaces;
using Domain.Money;

/// <summary>
/// The Esplora funding txid source (BOLT 7 plan D12, pruned nodes): the index's txid at a SCID position is used only
/// with a merkle proof that reaches our node's header at that position, the output still comes from our node's
/// <c>gettxout</c>, a lying or unavailable index is transient (never the peer's fault), 429s pause every request, and
/// a bitcoind that cannot serve a block logs the one-time hint to configure Esplora.
/// </summary>
public class EsploraTxIdSourceTests
{
    private const string EsploraUrl = "http://esplora.test/api";
    private const uint FundingHeight = 101;
    private const uint FundingIndex = 3;
    private const long FundingSatoshis = 1_000_000;

    private static readonly Key s_bitcoinKey1 = new(Enumerable.Repeat((byte)0x31, 32).ToArray());
    private static readonly Key s_bitcoinKey2 = new(Enumerable.Repeat((byte)0x32, 32).ToArray());
    private static readonly Key s_otherKey = new(Enumerable.Repeat((byte)0x33, 32).ToArray());

    private readonly InstrumentedChain _chain;
    private readonly FakeEsploraHandler _esplora;
    private readonly Transaction _fundingTx;
    private readonly Transaction _otherTx;

    /// <summary>Tip 100, then block 101 = [coinbase, filler, other, funding tx (vout 1 the 2-of-2), filler]: 5 txs.</summary>
    public EsploraTxIdSourceTests()
    {
        _chain = new InstrumentedChain(new FakeBitcoinChain());
        _fundingTx = CreateFundingTx(s_bitcoinKey1.PubKey, s_bitcoinKey2.PubKey, FundingSatoshis);
        _otherTx = CreateFundingTx(s_otherKey.PubKey, s_bitcoinKey2.PubKey, FundingSatoshis);
        _chain.Inner.Mine(CreateFiller(), _otherTx, _fundingTx, CreateFiller());
        for (var i = 0; i < 5; i++)
            _chain.Inner.Mine();
        _esplora = new FakeEsploraHandler(_chain.Inner);
    }

    private static ShortChannelId FundingScid => new(FundingHeight, FundingIndex, 1);

    [Fact]
    public async Task Given_IndexWithValidProof_When_Verify_Then_FoundWithOutputFromOurNode()
    {
        // Arrange: bitcoind cannot serve the block (pruned); only the index knows the txid list
        _chain.PrunedHeights.Add(FundingHeight);
        using var source = CreateSource();
        using var lookup = CreateLookup(source);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var result = await lookup.VerifyAsync(FundingScid, Compact(s_bitcoinKey1.PubKey), Compact(s_bitcoinKey2.PubKey),
                                              LightningMoney.Satoshis(FundingSatoshis), ct);

        // Assert: the txid is the funding tx, amount/script/depth from our gettxout; one txid and one proof request,
        // the merkle root and nTx from our node's header (the pruned block itself is never read)
        Assert.Equal(FundingOutputStatus.Found, result.Status);
        Assert.Equal(1, _chain.HeaderSummaryCalls);
        Assert.Null(await _chain.GetBlockAsync(_chain.Inner[FundingHeight].GetHash()));
        Assert.Equal(_fundingTx.GetHash().ToBytes(), (byte[])result.TransactionId!.Value);
        Assert.Equal(LightningMoney.Satoshis(FundingSatoshis), result.Amount);
        Assert.Equal(_fundingTx.Outputs[1].ScriptPubKey.ToBytes(), result.ScriptPubKey);
        Assert.Equal(6u, result.Confirmations);
        Assert.Equal(0, _chain.BlockTxIdCalls);
        Assert.Equal(1, _chain.UnspentOutputCalls);
        Assert.Equal(
            [$"block/{_chain.Inner[FundingHeight].GetHash()}/txid/{FundingIndex}", $"tx/{_fundingTx.GetHash()}/merkle-proof"],
            _esplora.Requests);
    }

    [Fact]
    public async Task Given_ProvenTxId_When_LookedUpAgain_Then_CachedWithoutRequests()
    {
        // Arrange
        using var source = CreateSource();
        using var lookup = CreateLookup(source);
        var ct = TestContext.Current.CancellationToken;
        await lookup.LookupAsync(FundingScid, ct);

        // Act
        var again = await lookup.LookupAsync(FundingScid, ct);

        // Assert: still two requests, gettxout asked again (the output can be spent since)
        Assert.Equal(FundingOutputStatus.Found, again.Status);
        Assert.Equal(2, _esplora.Requests.Count);
        Assert.Equal(2, _chain.UnspentOutputCalls);
    }

    [Fact]
    public async Task Given_OtherKeys_When_Verify_Then_ScriptMismatchFromOurNodesOutput()
    {
        // Arrange: the index is honest; the announcement names keys the output does not pay to
        using var source = CreateSource();
        using var lookup = CreateLookup(source);

        // Act
        var result = await lookup.VerifyAsync(FundingScid, Compact(s_otherKey.PubKey), Compact(s_bitcoinKey2.PubKey),
                                              cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.ScriptMismatch, result.Status);
    }

    [Fact]
    public async Task Given_SpentFundingOutput_When_Lookup_Then_OurGetTxOutRejectsIt()
    {
        // Arrange: the index proves the txid; our node says the output is spent
        _chain.Inner.Mine(CreateSpend(new OutPoint(_fundingTx.GetHash(), 1)));
        using var source = CreateSource();
        using var lookup = CreateLookup(source);

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.OutputSpentOrMissing, result.Status);
    }

    [Fact]
    public async Task Given_IndexNamingAnotherTxOfTheBlock_When_Lookup_Then_ProofPositionMismatchIsTransient()
    {
        // Arrange: the index answers the other 2-of-2 (index 2) for index 3, with that tx's honest proof (pos 2)
        _esplora.TxIdOverride = (txId, index) => index == FundingIndex ? _otherTx.GetHash() : txId;
        var logger = new CapturingLogger<EsploraTxIdSource>();
        using var source = CreateSource(logger: logger);
        using var lookup = CreateLookup(source);

        // Act
        var result = await lookup.VerifyAsync(FundingScid, Compact(s_otherKey.PubKey), Compact(s_bitcoinKey2.PubKey),
                                              cancellationToken: TestContext.Current.CancellationToken);

        // Assert: never Found (the lie would match these keys) and never a chain contradiction held against the peer
        Assert.Equal(FundingOutputStatus.ChainUnavailable, result.Status);
        Assert.True(result.IsTransient);
        Assert.Equal(0, _chain.UnspentOutputCalls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("merkle proof"));
    }

    [Fact]
    public async Task Given_IndexForgingProofPosition_When_Lookup_Then_MerkleRootMismatchIsTransient()
    {
        // Arrange: the other tx at index 3 with its own branch but the claimed position 3
        _esplora.TxIdOverride = (txId, index) => index == FundingIndex ? _otherTx.GetHash() : txId;
        _esplora.ProofOverride = (txId, proof) => txId == _otherTx.GetHash() && proof is { } p
                                                      ? (p.Branch, (int)FundingIndex)
                                                      : proof;
        using var source = CreateSource();
        using var lookup = CreateLookup(source);

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.ChainUnavailable, result.Status);
        Assert.Equal(0, _chain.UnspentOutputCalls);
    }

    [Fact]
    public async Task Given_IndexAnsweringUnknownTxId_When_Lookup_Then_NoProofIsTransient()
    {
        // Arrange: a txid that is in no block, so the index has no proof for it
        _esplora.TxIdOverride = (_, _) => RandomUtils.GetUInt256();
        using var source = CreateSource();
        using var lookup = CreateLookup(source);

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.ChainUnavailable, result.Status);
        Assert.Equal(0, _chain.UnspentOutputCalls);
    }

    [Fact]
    public async Task Given_IndexOnAnotherChain_When_Lookup_Then_UnknownBlockIsTransient()
    {
        // Arrange: the index does not know our block hash (another network or not indexed yet): 404
        _esplora.Scripted.Enqueue(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var source = CreateSource();
        using var lookup = CreateLookup(source);

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert: asked with our own node's block hash; the one-time chain check (NL-424) then found our genesis,
        // so the index is not refused
        Assert.Equal(FundingOutputStatus.ChainUnavailable, result.Status);
        Assert.Equal([$"block/{_chain.Inner[FundingHeight].GetHash()}/txid/{FundingIndex}", "block-height/0"],
                     _esplora.Requests);
        Assert.False(source.Refused);
    }

    [Fact]
    public async Task Given_ServerError_When_Lookup_Then_Transient()
    {
        // Arrange
        _esplora.Scripted.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var source = CreateSource();
        using var lookup = CreateLookup(source);

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert: the failing lookup, then the one-time chain check (NL-424), which the index answers
        Assert.Equal(FundingOutputStatus.ChainUnavailable, result.Status);
        Assert.Equal(["block-height/0"], _esplora.Requests.Skip(1).ToArray());
        Assert.Single(_esplora.Requests, r => r.StartsWith("block/"));
        Assert.False(source.Refused);
    }

    [Fact]
    public async Task Given_IndexBeyondOurTxCount_When_Lookup_Then_OutOfRangeWithoutAskingTheIndex()
    {
        // Arrange: block 101 has 5 transactions by our node's header
        using var source = CreateSource();
        using var lookup = CreateLookup(source);

        // Act
        var result = await lookup.LookupAsync(new ShortChannelId(FundingHeight, 5, 0),
                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.TransactionIndexOutOfRange, result.Status);
        Assert.Empty(_esplora.Requests);
    }

    [Fact]
    public async Task Given_AnIndexOnAnotherChain_When_ALookupFails_Then_TheMismatchIsAnErrorAndTheSourceIsRefused()
    {
        // Arrange (NL-424): the index does not know our block (404) and serves another chain's genesis
        _esplora.Scripted.Enqueue(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        _esplora.BlockHeightOverride = _ => RandomUtils.GetUInt256();
        var logger = new CapturingLogger<EsploraTxIdSource>();
        using var source = CreateSource(logger: logger);
        using var lookup = CreateLookup(source);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var first = await lookup.LookupAsync(FundingScid, ct);
        var second = await lookup.LookupAsync(FundingScid, ct);

        // Assert: the mismatch is an error, logged once; the refused source fails every later lookup at once,
        // without the index
        Assert.Equal(FundingOutputStatus.ChainUnavailable, first.Status);
        Assert.Equal(FundingOutputStatus.ChainUnavailable, second.Status);
        Assert.True(source.Refused);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("another chain"));
        Assert.Equal([$"block/{_chain.Inner[FundingHeight].GetHash()}/txid/{FundingIndex}", "block-height/0"],
                     _esplora.Requests);
    }

    [Fact]
    public async Task Given_AnIndexOnOurChain_When_ALookupFailsOnce_Then_TheCheckConfirmsItAndLookupsGoOn()
    {
        // Arrange: the index serves our genesis; the one block is just not indexed (yet)
        _esplora.Scripted.Enqueue(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        var logger = new CapturingLogger<EsploraTxIdSource>();
        using var source = CreateSource(logger: logger);
        using var lookup = CreateLookup(source);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var failed = await lookup.LookupAsync(FundingScid, ct);

        // Assert: confirmed on our chain (not an error, not refused); the lookup succeeds once the index catches up
        Assert.Equal(FundingOutputStatus.ChainUnavailable, failed.Status);
        Assert.False(source.Refused);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("serves our chain"));
        Assert.Equal(FundingOutputStatus.Found, (await lookup.LookupAsync(FundingScid, ct)).Status);
        Assert.Equal(4, _esplora.Requests.Count);
    }

    [Fact]
    public async Task Given_AnIndexThatCannotBeAsked_When_ALookupFails_Then_TheCheckWarnsInsteadOfRefusing()
    {
        // Arrange: both the position and the chain check run into a server error
        _esplora.Scripted.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        _esplora.Scripted.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var logger = new CapturingLogger<EsploraTxIdSource>();
        using var source = CreateSource(logger: logger);
        using var lookup = CreateLookup(source);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var result = await lookup.LookupAsync(FundingScid, ct);

        // Assert: inconclusive is a warning, and the source stays in use
        Assert.Equal(FundingOutputStatus.ChainUnavailable, result.Status);
        Assert.False(source.Refused);
        Assert.Contains(logger.Entries,
                        e => e.Level == LogLevel.Warning && e.Message.Contains("Could not check which chain"));
        Assert.Equal(FundingOutputStatus.Found, (await lookup.LookupAsync(FundingScid, ct)).Status);
    }

    [Fact]
    public async Task Given_TooManyRequestsWithRetryAfter_When_Lookup_Then_PausesThenSucceeds()
    {
        // Arrange: the first request is answered 429 with Retry-After 3 s
        var clock = new ManualTimeProvider();
        _esplora.Scripted.Enqueue(() => FakeEsploraHandler.TooManyRequests(TimeSpan.FromSeconds(3)));
        var logger = new CapturingLogger<EsploraTxIdSource>();
        using var source = CreateSource(clock, logger: logger);
        using var lookup = CreateLookup(source);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var pending = lookup.LookupAsync(FundingScid, ct);
        await WaitForAsync(() => clock.PendingTimers == 1, ct);
        var requestsDuringPause = _esplora.Requests.Count;
        clock.Advance(TimeSpan.FromMilliseconds(2_999));
        var waitedAt2999Ms = !pending.IsCompleted && _esplora.Requests.Count == 1;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert: nothing sent during the pause, then the same request again and the proof
        Assert.Equal(1, requestsDuringPause);
        Assert.True(waitedAt2999Ms);
        Assert.Equal(FundingOutputStatus.Found, result.Status);
        Assert.Equal(3, _esplora.Requests.Count);
        Assert.Equal(_esplora.Requests[0], _esplora.Requests[1]);
        Assert.Equal(1, source.RateLimitedResponses);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("429"));
    }

    [Fact]
    public async Task Given_HeaderWithoutTxCount_When_IndexProvesAnInnerNode_Then_TransientWithoutAskingTheIndex()
    {
        // Arrange: our node's header has no nTx (assumeutxo); the index names the inner node over txs 2 and 3 as the
        // "txid" at index 1 with the shorter branch that reaches the root from there
        _chain.PrunedHeights.Add(FundingHeight);
        _chain.UnknownHeaderTxCount = true;
        var block = _chain.Inner[FundingHeight];
        var leaves = block.Transactions.Select(t => t.GetHash()).ToList();
        var fullBranch = FakeEsploraHandler.Branch(leaves, 2);
        var inner = NBitcoin.Crypto.Hashes.DoubleSHA256(leaves[2].ToBytes().Concat(leaves[3].ToBytes()).ToArray());
        _esplora.TxIdOverride = (txId, index) => index == 1 ? inner : txId;
        _esplora.ProofOverride = (txId, proof) => txId == inner ? (fullBranch.Skip(1).ToList(), 1) : proof;
        Assert.True(EsploraTxIdSource.VerifyMerkleProof(inner, fullBranch.Skip(1).ToList(), 1,
                                                        block.Header.HashMerkleRoot, 4)); // a real proof one level short
        using var source = CreateSource();
        using var lookup = CreateLookup(source);

        // Act
        var result = await lookup.LookupAsync(new ShortChannelId(FundingHeight, 1, 0),
                                              TestContext.Current.CancellationToken);

        // Assert: unprovable, so transient; never OutputSpentOrMissing from a gettxout of the fake txid; the index
        // was asked once — for the chain check (NL-424), never for the position
        Assert.Equal(FundingOutputStatus.ChainUnavailable, result.Status);
        Assert.True(result.IsTransient);
        Assert.Equal(["block-height/0"], _esplora.Requests);
        Assert.Equal(0, _chain.UnspentOutputCalls);
        Assert.False(EsploraTxIdSource.VerifyMerkleProof(inner, fullBranch.Skip(1).ToList(), 1,
                                                         block.Header.HashMerkleRoot, 0));
    }

    [Fact]
    public async Task Given_LongRetryAfter_When_Lookups_Then_TransientAtOnceWithoutHoldingTheSlot()
    {
        // Arrange: 429 with Retry-After 60 s, longer than the 5 s the lookup may wait inline
        var clock = new ManualTimeProvider();
        _esplora.Scripted.Enqueue(() => FakeEsploraHandler.TooManyRequests(TimeSpan.FromSeconds(60)));
        using var source = CreateSource(clock);
        using var lookup = CreateLookup(source);
        var ct = TestContext.Current.CancellationToken;

        // Act: no timer is ever started, so both complete without advancing the clock
        var first = await lookup.LookupAsync(FundingScid, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        var duringPause = await lookup.LookupAsync(FundingScid, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        var requestsDuringPause = _esplora.Requests.Count;
        clock.Advance(TimeSpan.FromSeconds(60));
        var after = await lookup.LookupAsync(FundingScid, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert: the refused request and the pause-skipping chain check (NL-424), nothing else during the pause,
        // then the index again
        Assert.Equal(FundingOutputStatus.ChainUnavailable, first.Status);
        Assert.Equal(FundingOutputStatus.ChainUnavailable, duringPause.Status);
        Assert.Equal(2, requestsDuringPause);
        Assert.Equal(0, clock.PendingTimers);
        Assert.Equal(FundingOutputStatus.Found, after.Status);
        Assert.Equal(4, _esplora.Requests.Count);
    }

    [Fact]
    public async Task Given_TooManyRequestsBeyondRetries_When_Lookup_Then_DoublingBackoffThenTransient()
    {
        // Arrange: 429s without Retry-After, one retry allowed, initial backoff 2 s
        var clock = new ManualTimeProvider();
        for (var i = 0; i < 2; i++)
            _esplora.Scripted.Enqueue(() => FakeEsploraHandler.TooManyRequests());
        using var source = CreateSource(clock, new FundingTxIdSourceOptions
        {
            FundingTxIdSource = FundingTxIdSourceKind.Esplora,
            EsploraUrl = EsploraUrl,
            EsploraRequestsPerSecond = 100,
            EsploraMaxRetries = 1,
            EsploraInitialBackoff = TimeSpan.FromSeconds(2)
        });
        using var lookup = CreateLookup(source);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var pending = lookup.LookupAsync(FundingScid, ct);
        await WaitForAsync(() => clock.PendingTimers == 1, ct);
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert: gave up after the retry (then the one-time chain check, NL-424); the second 429 started a 4 s
        // pause for the next request
        Assert.Equal(FundingOutputStatus.ChainUnavailable, result.Status);
        Assert.Equal(3, _esplora.Requests.Count);
        Assert.Equal(2, source.RateLimitedResponses);
        var next = lookup.LookupAsync(FundingScid, ct);
        await WaitForAsync(() => clock.PendingTimers == 1, ct);
        clock.Advance(TimeSpan.FromMilliseconds(3_999));
        Assert.Equal(3, _esplora.Requests.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(FundingOutputStatus.Found, (await next.WaitAsync(TimeSpan.FromSeconds(10), ct)).Status);
    }

    [Fact]
    public async Task Given_PoliteRate_When_TwoLookups_Then_ThirdRequestWaitsForItsToken()
    {
        // Arrange: 2 requests per second, burst 2; each lookup costs two requests
        var clock = new ManualTimeProvider();
        _chain.Inner.Mine(CreateFundingTx(s_bitcoinKey1.PubKey, s_otherKey.PubKey, FundingSatoshis));
        using var source = CreateSource(clock, new FundingTxIdSourceOptions
        {
            FundingTxIdSource = FundingTxIdSourceKind.Esplora,
            EsploraUrl = EsploraUrl
        });
        using var lookup = CreateLookup(source);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(FundingOutputStatus.Found, (await lookup.LookupAsync(FundingScid, ct)).Status);

        // Act
        var second = lookup.LookupAsync(new ShortChannelId(_chain.Inner.TipHeight, 1, 1), ct);
        await WaitForAsync(() => clock.PendingTimers == 1, ct);
        var requestsBeforeToken = _esplora.Requests.Count;
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await WaitForAsync(() => _esplora.Requests.Count == 3 && clock.PendingTimers == 1, ct);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var result = await second.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert
        Assert.Equal(2, requestsBeforeToken);
        Assert.Equal(FundingOutputStatus.Found, result.Status);
        Assert.Equal(4, _esplora.Requests.Count);
    }

    [Fact]
    public async Task Given_PrunedBitcoindWithoutIndex_When_Lookups_Then_HintLoggedOnce()
    {
        // Arrange: the default bitcoind source against a pruned block
        _chain.PrunedHeights.Add(FundingHeight);
        var logger = new CapturingLogger<FundingOutputLookup>();
        using var lookup = new FundingOutputLookup(_chain, logger);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var first = await lookup.LookupAsync(FundingScid, ct);
        var second = await lookup.LookupAsync(new ShortChannelId(FundingHeight, 2, 1), ct);

        // Assert
        Assert.Equal(FundingOutputStatus.BlockUnavailable, first.Status);
        Assert.Equal(FundingOutputStatus.BlockUnavailable, second.Status);
        var (level, message) = Assert.Single(logger.Entries, e => e.Message.Contains("Gossip:FundingTxIdSource=Esplora"));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("Gossip:EsploraUrl", message);
    }

    [Fact]
    public async Task Given_EsploraSource_When_BlockPrunedAtBitcoind_Then_NoHint()
    {
        // Arrange
        _chain.PrunedHeights.Add(FundingHeight);
        var logger = new CapturingLogger<FundingOutputLookup>();
        using var source = CreateSource();
        using var lookup = new FundingOutputLookup(_chain, logger, txIdSource: source);

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.Found, result.Status);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("pruned"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    public void Given_EveryPositionOfABlock_When_VerifyMerkleProof_Then_ValidOnlyAtItsPosition(int txCount)
    {
        // Arrange
        var block = CreateBlock(txCount);
        var leaves = block.Transactions.Select(t => t.GetHash()).ToList();
        var root = block.GetMerkleRoot().Hash;

        for (var position = 0; position < txCount; position++)
        {
            // Act
            var branch = FakeEsploraHandler.Branch(leaves, position);
            var valid = EsploraTxIdSource.VerifyMerkleProof(leaves[position], branch, (uint)position, root, txCount);
            var unknownCount = EsploraTxIdSource.VerifyMerkleProof(leaves[position], branch, (uint)position, root, 0);
            var otherTx = EsploraTxIdSource.VerifyMerkleProof(leaves[(position + 1) % txCount], branch,
                                                              (uint)position, root, txCount);

            // Assert
            Assert.True(valid, $"position {position} of {txCount}");
            Assert.False(unknownCount, $"position {position} of {txCount} without nTx");
            Assert.Equal(txCount == 1, otherTx);
        }
    }

    [Fact]
    public void Given_MempoolSpaceProofOfMainnetBlock100000_When_Verified_Then_ReachesTheHeaderMerkleRoot()
    {
        // Arrange: GET https://mempool.space/api/tx/fff2...02c4/merkle-proof (block 100,000, 4 txs, merkleroot from
        // getblockheader), as served: hashes in display order
        const string json = """
                            {"block_height":100000,"merkle":["8c14f0db3df150123e6f3dbbf30f8b955a8249b62ac1d1ff16284aefa3d06d87",
                             "8e30899078ca1813be036a073bbf80b86cdddde1c96e9e9c99e9e3782df4ae49"],"pos":1}
                            """;
        var txId = uint256.Parse("fff2525b8931402dd09222c50775608f75787bd2b87e56995a7bdd30f79702c4");
        var merkleRoot = uint256.Parse("f3e94742aca4b5ef85488dc37c06c3282295ffec960994b2c0d5ac2a25a95766");

        // Act
        var parsed = EsploraTxIdSource.TryParseMerkleProof(json, out var branch, out var position);
        var valid = EsploraTxIdSource.VerifyMerkleProof(txId, branch, position, merkleRoot, 4);
        var otherPosition = EsploraTxIdSource.VerifyMerkleProof(txId, branch, 0, merkleRoot, 4);

        // Assert
        Assert.True(parsed);
        Assert.Equal(1u, position);
        Assert.True(valid);
        Assert.False(otherPosition);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void Given_DuplicatedPaddingPosition_When_VerifyMerkleProof_Then_Refused(int txCount)
    {
        // Arrange: CVE-2012-2459, the last transaction claimed at the padding position txCount
        var block = CreateBlock(txCount);
        var leaves = block.Transactions.Select(t => t.GetHash()).ToList();
        var padded = leaves.Append(leaves[^1]).ToList();
        var branch = FakeEsploraHandler.Branch(padded, txCount);
        var root = block.GetMerkleRoot().Hash;

        // Act
        var withCount = EsploraTxIdSource.VerifyMerkleProof(leaves[^1], branch, (uint)txCount, root, txCount);
        var withoutCount = EsploraTxIdSource.VerifyMerkleProof(leaves[^1], branch, (uint)txCount, root, 0);

        // Assert
        Assert.False(withCount);
        Assert.False(withoutCount);
    }

    [Fact]
    public void Given_InnerNodeAsTxId_When_VerifyMerkleProofWithCount_Then_WrongDepthRefused()
    {
        // Arrange: a 4-tx block; the hash of txs 0 and 1 claimed as a txid at position 0 with a one-level branch
        var block = CreateBlock(4);
        var leaves = block.Transactions.Select(t => t.GetHash()).ToList();
        var fullBranch = FakeEsploraHandler.Branch(leaves, 0);
        var inner = NBitcoin.Crypto.Hashes.DoubleSHA256(leaves[0].ToBytes().Concat(leaves[1].ToBytes()).ToArray());

        // Act
        var result = EsploraTxIdSource.VerifyMerkleProof(inner, [fullBranch[1]], 0, block.GetMerkleRoot().Hash, 4);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("{\"block_height\":1,\"merkle\":[],\"pos\":0}", true)]
    [InlineData("{\"merkle\":[\"zz\"],\"pos\":0}", false)]
    [InlineData("{\"merkle\":[],\"pos\":-1}", false)]
    [InlineData("{\"pos\":0}", false)]
    [InlineData("[]", false)]
    [InlineData("not json", false)]
    public void Given_ProofJson_When_Parsed_Then_OnlyWellFormedAccepted(string json, bool expected)
    {
        // Act
        var parsed = EsploraTxIdSource.TryParseMerkleProof(json, out _, out _);

        // Assert
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mempool.space/api")]
    [InlineData("ftp://esplora.test")]
    public void Given_EsploraWithoutUsableUrl_When_Created_Then_Throws(string? url)
    {
        // Arrange
        var options = new FundingTxIdSourceOptions { FundingTxIdSource = FundingTxIdSourceKind.Esplora, EsploraUrl = url };

        // Act / Assert
        Assert.NotEmpty(options.GetValidationErrors());
        Assert.Throws<ArgumentException>(() => CreateSource(options: options));
    }

    [Fact]
    public void Given_DefaultOptions_When_Validated_Then_BitcoindAndValid()
    {
        // Arrange
        var options = new FundingTxIdSourceOptions();

        // Act / Assert
        Assert.Equal(FundingTxIdSourceKind.Bitcoind, options.FundingTxIdSource);
        Assert.Empty(options.GetValidationErrors());
        Assert.Equal(2, options.EsploraRequestsPerSecond);
    }

    [Fact]
    public async Task Given_EsploraConfigured_When_AddGossipBitcoinServices_Then_LookupUsesTheIndex()
    {
        // Arrange
        _chain.PrunedHeights.Add(FundingHeight);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBitcoinChainService>(_chain);
        services.Configure<FundingTxIdSourceOptions>(o =>
        {
            o.FundingTxIdSource = FundingTxIdSourceKind.Esplora;
            o.EsploraUrl = EsploraUrl + "/";
            o.EsploraRequestsPerSecond = 100;
        });
        services.AddGossipBitcoinServices(_ => _esplora);
        await using var provider = services.BuildServiceProvider();

        // Act
        var lookup = provider.GetRequiredService<IFundingOutputLookup>();
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.Found, result.Status);
        Assert.Equal(2, _esplora.Requests.Count);
    }

    [Fact]
    public void Given_EsploraWithoutUrl_When_LookupResolved_Then_Throws()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBitcoinChainService>(_chain);
        services.Configure<FundingTxIdSourceOptions>(o => o.FundingTxIdSource = FundingTxIdSourceKind.Esplora);
        services.AddGossipBitcoinServices(_ => _esplora);
        using var provider = services.BuildServiceProvider();

        // Act / Assert
        var exception = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IFundingOutputLookup>);
        Assert.Contains("EsploraUrl", exception.Message);
    }

    [Fact]
    public async Task Given_FakeChain_When_DefaultGetBlockHeaderSummary_Then_MerkleRootAndCount()
    {
        // Arrange: the interface default reads the whole block
        IBitcoinChainService chain = _chain.Inner;
        var block = _chain.Inner[FundingHeight];

        // Act
        var summary = await chain.GetBlockHeaderSummaryAsync(block.GetHash());
        var unknown = await chain.GetBlockHeaderSummaryAsync(RandomUtils.GetUInt256());

        // Assert
        Assert.NotNull(summary);
        Assert.Equal(block.Header.HashMerkleRoot, summary.Value.MerkleRoot);
        Assert.Equal(5, summary.Value.TxCount);
        Assert.Null(unknown);
    }

    private EsploraTxIdSource CreateSource(TimeProvider? clock = null, FundingTxIdSourceOptions? options = null,
                                           ILogger<EsploraTxIdSource>? logger = null) =>
        new(_chain, new HttpClient(_esplora, disposeHandler: false), logger ?? NullLogger<EsploraTxIdSource>.Instance,
            Microsoft.Extensions.Options.Options.Create(options ?? new FundingTxIdSourceOptions
            {
                FundingTxIdSource = FundingTxIdSourceKind.Esplora,
                EsploraUrl = EsploraUrl,
                EsploraRequestsPerSecond = 100
            }), clock, ownsHttpClient: true);

    private FundingOutputLookup CreateLookup(IFundingTxIdSource source) =>
        new(_chain, NullLogger<FundingOutputLookup>.Instance, txIdSource: source);

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not met");
            await Task.Delay(10, ct);
        }
    }

    private static Block CreateBlock(int txCount)
    {
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        for (var i = 0; i < txCount; i++)
            block.AddTransaction(CreateFiller());
        block.UpdateMerkleRoot();
        return block;
    }

    private static Transaction CreateFundingTx(PubKey key1, PubKey key2, long satoshis)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0));
        tx.Outputs.Add(Money.Satoshis(50_000), new Key().PubKey.WitHash.ScriptPubKey);
        var ordered = new[] { key1, key2 }.OrderBy(k => k, PubKeyComparer.Instance).ToArray();
        tx.Outputs.Add(Money.Satoshis(satoshis),
                       PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, ordered).WitHash.ScriptPubKey);
        return tx;
    }

    private static Transaction CreateFiller()
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0));
        tx.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
        return tx;
    }

    private static Transaction CreateSpend(OutPoint outPoint)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(outPoint);
        tx.Outputs.Add(Money.Satoshis(900_000), new Key().PubKey.WitHash.ScriptPubKey);
        return tx;
    }

    private static CompactPubKey Compact(PubKey key) => key.ToBytes();
}