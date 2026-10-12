using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;

namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

using Bitcoin.Gossip;
using Bitcoin.Options;
using Bitcoin.Wallet;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Constants;

/// <summary>
/// Smoke test of <see cref="BitcoinChainService.GetBlockTxIdsAsync"/> and <see cref="FundingOutputLookup"/> against a
/// real regtest bitcoind (BOLT 7 plan G2-T2). Explicit: it needs a bitcoind with a funded wallet, named by
/// <c>NLTG_BITCOIND_RPC</c> (e.g. <c>http://127.0.0.1:18443</c>), <c>NLTG_BITCOIND_RPC_USER</c> and
/// <c>NLTG_BITCOIND_RPC_PASSWORD</c>; run with <c>-- xUnit.Explicit=only</c>.
/// </summary>
public class FundingOutputLookupBitcoindTests
{
    [Fact(Explicit = true)]
    public async Task Given_RegtestBitcoind_When_LookupOfA2Of2Output_Then_FoundAndOutOfRangeHeightIsNull()
    {
        // Arrange: send to the P2WSH of a 2-of-2 and mine it
        var ct = TestContext.Current.CancellationToken;
        var endpoint = Environment.GetEnvironmentVariable("NLTG_BITCOIND_RPC") ?? "http://127.0.0.1:18443";
        var user = Environment.GetEnvironmentVariable("NLTG_BITCOIND_RPC_USER") ?? "u";
        var password = Environment.GetEnvironmentVariable("NLTG_BITCOIND_RPC_PASSWORD") ?? "p";
        var chain = new BitcoinChainService(new OptionsWrapper<BitcoinOptions>(new BitcoinOptions
        {
            RpcEndpoint = endpoint,
            RpcUser = user,
            RpcPassword = password,
            ZmqHost = "127.0.0.1",
            ZmqBlockPort = 1,
            ZmqTxPort = 1
        }), NullLogger<BitcoinChainService>.Instance, new OptionsWrapper<NodeOptions>(new NodeOptions
        {
            BitcoinNetwork = NetworkConstants.Regtest
        }));
        var rpc = new RPCClient($"{user}:{password}", endpoint, Network.RegTest);
        var key1 = new Key();
        var key2 = new Key();
        var script = FundingOutputLookup.TryCreateFundingScriptPubKey(key1.PubKey.ToBytes(), key2.PubKey.ToBytes())!;
        var address = new Script(script).GetDestinationAddress(Network.RegTest)!;
        var txId = await rpc.SendToAddressAsync(address, Money.Satoshis(123_456), cancellationToken: ct);
        var blockHashes = await rpc.GenerateToAddressAsync(1, new Key().PubKey.WitHash.GetAddress(Network.RegTest),
                                                           ct);
        var height = await chain.GetCurrentBlockHeightAsync();

        // Act
        var block = await chain.GetBlockTxIdsAsync(height);
        var aboveTip = await chain.GetBlockTxIdsAsync(height + 1);
        var txIndex = block!.Value.TxIds.ToList().IndexOf(txId);
        var tx = (await chain.GetBlockAsync(height))!.Transactions[txIndex]; // no txindex: read it from the block
        var vout = tx.Outputs.FindIndex(o => o.ScriptPubKey.ToBytes().AsSpan().SequenceEqual(script));
        using var lookup = new FundingOutputLookup(chain, NullLogger<FundingOutputLookup>.Instance);
        var result = await lookup.VerifyAsync(new ShortChannelId(height, (uint)txIndex, (ushort)vout),
                                              (CompactPubKey)key2.PubKey.ToBytes(),
                                              (CompactPubKey)key1.PubKey.ToBytes(),
                                              LightningMoney.Satoshis(123_456), ct);
        var wrongIndex = await lookup.LookupAsync(new ShortChannelId(height, (uint)block.Value.TxIds.Count, 0), ct);

        // Assert
        Assert.Equal(blockHashes[0], block.Value.BlockHash);
        Assert.True(txIndex > 0);
        Assert.Null(aboveTip);
        Assert.Equal(FundingOutputStatus.Found, result.Status);
        Assert.Equal(1u, result.Confirmations);
        Assert.Equal(FundingOutputStatus.TransactionIndexOutOfRange, wrongIndex.Status);
    }
}