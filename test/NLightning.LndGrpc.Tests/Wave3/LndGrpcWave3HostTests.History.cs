using Microsoft.Extensions.DependencyInjection;
using Moq;
using NBitcoin;

namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Accounting.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using LndGrpc.Macaroons;
using LndGrpc.Services;
using Testing.Lnd.Lnrpc;
using AddressType = Domain.Bitcoin.Enums.AddressType;

/// <summary>
/// <c>GetTransactions</c>' history gaps (NL-1187) and the imported-tapscript overlap (NL-1253): the wallet's durable
/// history without the accounting feed (a held feed gate, or bitcoind's block pruned), a reorg's unconfirmed rows, the
/// outputs held since before the accounting cutover, and one transaction reported by the durable history, the feed and
/// the imported history at once, counted once.
/// </summary>
public sealed partial class LndGrpcWave3HostTests
{
    private const uint ChainTip = 150;

    [Fact]
    public async Task Given_ADurableHistoryRowAndNoFeedEvent_When_GetTransactions_Then_ItIsListedWithoutBitcoind()
    {
        // Arrange: our 100,000 sat output spent at 120 to 60,000 away with 39,000 change (fee 1,000); the accounting
        // feed has nothing (its gate was held) and bitcoind no longer serves the block (pruned)
        using var change = new Key();
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new TxIn(new NBitcoin.OutPoint(RandomUtils.GetUInt256(), 3)));
        spend.Outputs.Add(Money.Satoshis(60_000), new Key().PubKey.WitHash.ScriptPubKey);
        spend.Outputs.Add(Money.Satoshis(39_000), change.PubKey.WitHash.ScriptPubKey);
        var blockHash = RandomUtils.GetUInt256();
        _walletHistory.Add(new WalletTransactionRecord(new TxId(spend.GetHash().ToBytes()), spend.ToBytes(), 120,
                                                       blockHash.ToBytes(), DateTimeOffset.FromUnixTimeSeconds(1_700),
                                                       [1], [new WalletTransactionInput(0, 100_000)]));
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(),
                                                                              cancellationToken: Ct);

        // Assert
        var listed = Assert.Single(response.Transactions);
        Assert.Equal(spend.GetHash().ToString(), listed.TxHash);
        Assert.Equal(-61_000, listed.Amount);
        Assert.Equal(1_000, listed.TotalFees);
        Assert.Equal(120, listed.BlockHeight);
        Assert.Equal(31, listed.NumConfirmations);
        Assert.Equal(blockHash.ToString(), listed.BlockHash);
        Assert.Equal(1_700, listed.TimeStamp);
        Assert.Equal(Convert.ToHexStringLower(spend.ToBytes()), listed.RawTxHex);
        Assert.Equal(new[] { false, true }, listed.OutputDetails.Select(o => o.IsOurAddress));
        Assert.True(Assert.Single(listed.PreviousOutpoints).IsOurOutput);
        _chain.Verify(c => c.GetBlockAsync(It.IsAny<uint>()), Times.Never());
    }

    [Fact]
    public async Task Given_TheFeedAndTheDurableHistoryReportOneTransaction_When_GetTransactions_Then_ItCountsOnce()
    {
        // Arrange: a 100,000 sat deposit at 101 in both sources
        var deposit = Network.RegTest.CreateTransaction();
        deposit.Inputs.Add(new TxIn(new NBitcoin.OutPoint(RandomUtils.GetUInt256(), 0)));
        deposit.Outputs.Add(Money.Satoshis(100_000), new Key().PubKey.WitHash.ScriptPubKey);
        var depositId = new TxId(deposit.GetHash().ToBytes());
        _walletHistory.Add(new WalletTransactionRecord(depositId, deposit.ToBytes(), 101, new byte[32],
                                                       DateTimeOffset.UnixEpoch, [0], []));
        AddEvent(AccountingEventKind.WalletReceived, depositId, 0, 101, 100_000_000);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(),
                                                                              cancellationToken: Ct);

        // Assert
        var listed = Assert.Single(response.Transactions);
        Assert.Equal(100_000, listed.Amount);
        Assert.Equal(0, listed.TotalFees);
    }

    [Fact]
    public async Task Given_AnOutputImportedAsTapscriptAndOwnedByTheWallet_When_GetTransactions_Then_EachOutputAndInputCountsOnce()
    {
        // Arrange: deposit D at 130 pays 50,000 to S (a wallet address imported as tapscript too), 20,000 to S2
        // (imported only) and 30,000 away; P at 140 spends both to 69,000 away (fee 1,000)
        using var shared = new Key();
        using var importedOnly = new Key();
        var sharedScript = shared.PubKey.GetTaprootFullPubKey().ScriptPubKey;
        var importedScript = importedOnly.PubKey.GetTaprootFullPubKey().ScriptPubKey;
        var deposit = Network.RegTest.CreateTransaction();
        deposit.Inputs.Add(new TxIn(new NBitcoin.OutPoint(RandomUtils.GetUInt256(), 0)));
        deposit.Outputs.Add(Money.Satoshis(50_000), sharedScript);
        deposit.Outputs.Add(Money.Satoshis(20_000), importedScript);
        deposit.Outputs.Add(Money.Satoshis(30_000), new Key().PubKey.WitHash.ScriptPubKey);
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new TxIn(new NBitcoin.OutPoint(deposit.GetHash(), 0)));
        spend.Inputs.Add(new TxIn(new NBitcoin.OutPoint(deposit.GetHash(), 1)));
        spend.Outputs.Add(Money.Satoshis(69_000), new Key().PubKey.WitHash.ScriptPubKey);
        var blocks = SetUpChain(120, ChainTip, (130, deposit), (140, spend));
        var depositId = new TxId(deposit.GetHash().ToBytes());
        var spendId = new TxId(spend.GetHash().ToBytes());

        // The wallet's own view of the shared output, in the durable history and the feed
        _walletHistory.Add(new WalletTransactionRecord(depositId, deposit.ToBytes(), 130,
                                                       blocks[130].GetHash().ToBytes(), DateTimeOffset.UnixEpoch, [0],
                                                       []));
        _walletHistory.Add(new WalletTransactionRecord(spendId, spend.ToBytes(), 140, blocks[140].GetHash().ToBytes(),
                                                       DateTimeOffset.UnixEpoch, [],
                                                       [new WalletTransactionInput(0, 50_000)]));
        AddEvent(AccountingEventKind.WalletReceived, depositId, 0, 130, 50_000_000);
        AddEvent(AccountingEventKind.WalletOutputSpent, depositId, 0, 140, -50_000_000,
                 ("spentBy", spendId.ToString()));

        // Both scripts imported (the tracker scans from 120)
        _importedScripts.Add(new ImportedTapscript(sharedScript.ToBytes(), shared.PubKey.TaprootInternalKey.ToBytes(),
                                                   [1], 120));
        _importedScripts.Add(new ImportedTapscript(importedScript.ToBytes(),
                                                   importedOnly.PubKey.TaprootInternalKey.ToBytes(), [2], 120));
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(),
                                                                              cancellationToken: Ct);

        // Assert: before NL-1253 the shared output counted twice (deposit 120,000, spend -120,000)
        Assert.Equal(2, response.Transactions.Count);
        var listedDeposit = Assert.Single(response.Transactions, t => t.TxHash == deposit.GetHash().ToString());
        Assert.Equal(70_000, listedDeposit.Amount);
        Assert.Equal(new[] { true, true, false }, listedDeposit.OutputDetails.Select(o => o.IsOurAddress));
        Assert.Equal(0, listedDeposit.TotalFees);
        Assert.Equal(blocks[130].GetHash().ToString(), listedDeposit.BlockHash);
        var listedSpend = Assert.Single(response.Transactions, t => t.TxHash == spend.GetHash().ToString());
        Assert.Equal(-70_000, listedSpend.Amount);
        Assert.Equal(1_000, listedSpend.TotalFees);
        Assert.All(listedSpend.PreviousOutpoints, p => Assert.True(p.IsOurOutput));
        Assert.Equal(2, listedSpend.PreviousOutpoints.Count);
    }

    [Fact]
    public async Task Given_RowsAReorgUnconfirmed_When_GetTransactions_Then_OnlyTheOneStillInTheMempoolIsListedUnconfirmed()
    {
        // Arrange: two deposits whose block a reorg disconnected; bitcoind's mempool holds the first again, the new
        // branch conflicted the second
        var back = CreateDeposit(40_000);
        var conflicted = CreateDeposit(41_000);
        _walletHistory.Add(new WalletTransactionRecord(new TxId(back.GetHash().ToBytes()), back.ToBytes(), null, null,
                                                       DateTimeOffset.UnixEpoch, [0], []));
        _walletHistory.Add(new WalletTransactionRecord(new TxId(conflicted.GetHash().ToBytes()), conflicted.ToBytes(),
                                                       null, null, DateTimeOffset.UnixEpoch, [0], []));
        _chain.Setup(c => c.GetTransactionAsync(back.GetHash())).ReturnsAsync(back);
        _chain.Setup(c => c.GetTransactionAsync(conflicted.GetHash())).ReturnsAsync((NBitcoin.Transaction?)null);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var all = await connection.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(),
                                                                         cancellationToken: Ct);
        var confirmedOnly = await connection.LightningClient.GetTransactionsAsync(
                                new GetTransactionsRequest { StartHeight = 1, EndHeight = (int)ChainTip },
                                cancellationToken: Ct);

        // Assert
        var listed = Assert.Single(all.Transactions);
        Assert.Equal(back.GetHash().ToString(), listed.TxHash);
        Assert.Equal(0, listed.BlockHeight);
        Assert.Equal(0, listed.NumConfirmations);
        Assert.Equal("", listed.BlockHash);
        Assert.Equal(40_000, listed.Amount);
        Assert.Empty(confirmedOnly.Transactions);
    }

    [Fact]
    public async Task Given_OutputsHeldSinceBeforeTheCutover_When_GetTransactions_Then_DepositsAndResolvableSendsAreListed()
    {
        // Arrange: no feed event and no durable row; the wallet holds a deposit from 90, the change of our own send at
        // 95 (its parent paid our wallet address) and the change of a send at 96 whose parent bitcoind cannot return
        var receive = Address(0, false);
        var parentAddress = Address(1, false);
        var changeAddress = Address(2, true);
        _walletAddresses.AddRange([receive, parentAddress, changeAddress]);
        var deposit = CreateDeposit(25_000);
        var parent = Network.RegTest.CreateTransaction();
        parent.Inputs.Add(new TxIn(new NBitcoin.OutPoint(RandomUtils.GetUInt256(), 0)));
        parent.Outputs.Add(Money.Satoshis(80_000), BitcoinAddress.Create(parentAddress.Address, Network.RegTest));
        var send = Network.RegTest.CreateTransaction();
        send.Inputs.Add(new TxIn(new NBitcoin.OutPoint(parent.GetHash(), 0)));
        send.Outputs.Add(Money.Satoshis(50_000), new Key().PubKey.WitHash.ScriptPubKey);
        send.Outputs.Add(Money.Satoshis(29_000), BitcoinAddress.Create(changeAddress.Address, Network.RegTest));
        var orphan = Network.RegTest.CreateTransaction();
        orphan.Inputs.Add(new TxIn(new NBitcoin.OutPoint(RandomUtils.GetUInt256(), 0)));
        orphan.Outputs.Add(Money.Satoshis(10_000), BitcoinAddress.Create(changeAddress.Address, Network.RegTest));
        var sendBlock = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        sendBlock.Transactions.Add(send);
        var orphanBlock = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        orphanBlock.Transactions.Add(orphan);
        _chain.Setup(c => c.GetBlockAsync(95)).ReturnsAsync(sendBlock);
        _chain.Setup(c => c.GetBlockAsync(96)).ReturnsAsync(orphanBlock);
        _chain.Setup(c => c.GetTransactionAsync(parent.GetHash())).ReturnsAsync(parent);
        _unspent.Add(new UtxoModel(new TxId(deposit.GetHash().ToBytes()), 0, LightningMoney.Satoshis(25_000), 90,
                                   receive));
        _unspent.Add(new UtxoModel(new TxId(send.GetHash().ToBytes()), 1, LightningMoney.Satoshis(29_000), 95,
                                   changeAddress));
        _unspent.Add(new UtxoModel(new TxId(orphan.GetHash().ToBytes()), 0, LightningMoney.Satoshis(10_000), 96,
                                   changeAddress));
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(),
                                                                              cancellationToken: Ct);

        // Assert: the deposit and our send (its input from the parent: -(50,000 + 1,000 fee)); not the unresolvable send
        Assert.Equal(2, response.Transactions.Count);
        var listedDeposit = Assert.Single(response.Transactions, t => t.TxHash == deposit.GetHash().ToString());
        Assert.Equal(25_000, listedDeposit.Amount);
        Assert.Equal(90, listedDeposit.BlockHeight);
        var listedSend = Assert.Single(response.Transactions, t => t.TxHash == send.GetHash().ToString());
        Assert.Equal(-51_000, listedSend.Amount);
        Assert.Equal(1_000, listedSend.TotalFees);
        Assert.Equal(95, listedSend.BlockHeight);
        Assert.True(Assert.Single(listedSend.PreviousOutpoints).IsOurOutput);
    }

    [Fact]
    public async Task Given_AConfirmationThePassiveFeedPublished_When_GetTransactions_Then_ItDescribesTheSameTransaction()
    {
        // Arrange: the chain monitor's description of our spend (SubscribeTransactions' source) and the durable row it
        // wrote from that description in the block's save
        using var change = new Key();
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new TxIn(new NBitcoin.OutPoint(RandomUtils.GetUInt256(), 1)));
        spend.Inputs.Add(new TxIn(new NBitcoin.OutPoint(RandomUtils.GetUInt256(), 0)));
        spend.Outputs.Add(Money.Satoshis(70_000), new Key().PubKey.WitHash.ScriptPubKey);
        spend.Outputs.Add(Money.Satoshis(25_000), change.PubKey.WitHash.ScriptPubKey);
        var blockHash = RandomUtils.GetUInt256();
        var observed = new Domain.Bitcoin.Events.WalletTransactionEventArgs(
            spend.ToHex(), 25_000 - 96_500, 1_500, 140, blockHash.ToString(), DateTimeOffset.FromUnixTimeSeconds(9_000),
            "", [1], [0, 1], spend.GetHash().ToString(), ourInputAmounts: [60_000, 36_500]);
        _walletHistory.Add(new WalletTransactionRecord(new TxId(spend.GetHash().ToBytes()), spend.ToBytes(), 140,
                                                       blockHash.ToBytes(), observed.Timestamp, [1],
                                                       [new WalletTransactionInput(0, 60_000),
                                                        new WalletTransactionInput(1, 36_500)]));
        var published = _services!.GetRequiredService<LightningService>().ToTransactionEvent(observed);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var listed = Assert.Single((await connection.LightningClient.GetTransactionsAsync(
                                        new GetTransactionsRequest(), cancellationToken: Ct)).Transactions);

        // Assert
        Assert.Equal(published.TxHash, listed.TxHash);
        Assert.Equal(published.Amount, listed.Amount);
        Assert.Equal(published.TotalFees, listed.TotalFees);
        Assert.Equal(published.BlockHash, listed.BlockHash);
        Assert.Equal(published.BlockHeight, listed.BlockHeight);
        Assert.Equal(published.TimeStamp, listed.TimeStamp);
        Assert.Equal(published.RawTxHex, listed.RawTxHex);
        Assert.Equal(published.OutputDetails.Select(o => (o.OutputIndex, o.IsOurAddress, o.Amount)),
                     listed.OutputDetails.Select(o => (o.OutputIndex, o.IsOurAddress, o.Amount)));
        Assert.Equal(published.PreviousOutpoints.Select(o => (o.Outpoint, o.IsOurOutput)),
                     listed.PreviousOutpoints.Select(o => (o.Outpoint, o.IsOurOutput)));
    }

    private static NBitcoin.Transaction CreateDeposit(long amountSat)
    {
        var deposit = Network.RegTest.CreateTransaction();
        deposit.Inputs.Add(new TxIn(new NBitcoin.OutPoint(RandomUtils.GetUInt256(), 0)));
        deposit.Outputs.Add(Money.Satoshis(amountSat), new Key().PubKey.WitHash.ScriptPubKey);
        return deposit;
    }

    private static WalletAddressModel Address(uint index, bool isChange) =>
        new(AddressType.P2Wpkh, index, isChange,
            new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString());

    /// <summary>A linked regtest chain from <paramref name="from"/> to <paramref name="to"/> behind the mocked chain
    /// service (what the imported tracker scans), with the given transactions in their blocks.</summary>
    private Dictionary<uint, Block> SetUpChain(uint from, uint to,
                                               params (uint Height, NBitcoin.Transaction Transaction)[] transactions)
    {
        var blocks = new Dictionary<uint, Block>();
        var previous = uint256.Zero;
        for (var height = from; height <= to; height++)
        {
            var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
            block.Header.HashPrevBlock = previous;
            block.Header.Nonce = height;
            foreach (var (_, transaction) in transactions.Where(t => t.Height == height))
                block.Transactions.Add(transaction);
            block.UpdateMerkleRoot();
            blocks[height] = block;
            previous = block.GetHash();
        }

        _chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => blocks.GetValueOrDefault(h));
        _chain.Setup(c => c.GetBlockHashAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => blocks[h].GetHash());
        return blocks;
    }
}