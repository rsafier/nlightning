using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Builders;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Interfaces;
using Outputs;

public class FundingTransactionBuilder : IFundingTransactionBuilder
{
    private readonly Network _network;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FundingTransactionBuilder> _logger;

    public FundingTransactionBuilder(IOptions<NodeOptions> nodeOptions, IServiceProvider serviceProvider,
                                     ILogger<FundingTransactionBuilder> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
    }

    public FundingTransactionBuildResult Build(FundingTransactionModel transaction)
    {
        var coins = transaction.Utxos.ToArray();
        if (coins.Length == 0)
            throw new ArgumentException("UTXO set cannot be empty");

        var totalInputAmount = LightningMoney.Zero;
        foreach (var coin in coins)
            totalInputAmount += coin.Amount;

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Building funding transaction with {UtxoCount} UTXOs for amount {FundingAmount}",
                             coins.Length, transaction.FundingOutput.Amount);

        // Create a new Bitcoin transaction
        var tx = Transaction.Create(_network);

        // Set the transaction version as per BOLT spec
        tx.Version = TransactionConstants.FundingTransactionVersion;

        // Add all inputs from the UTXO set, ordered per BIP 69 (previous txid in display byte order, then vout), so
        // rebuilding the transaction from the same UTXOs always yields the same txid regardless of their source order
        var orderedCoins = coins.OrderBy(c => Enumerable.Reverse((byte[])c.TxId).ToArray(),
                                         ByteArrayLexicographicComparer.Instance)
                                .ThenBy(c => c.Index);
        foreach (var coin in orderedCoins)
            tx.Inputs.Add(new OutPoint(new uint256(coin.TxId), coin.Index));

        // Convert and add the funding output
        var fundingOutput = new FundingOutput(transaction.FundingOutput.Amount,
                                              new PubKey(transaction.FundingOutput.LocalFundingPubKey),
                                              new PubKey(transaction.FundingOutput.RemoteFundingPubKey));
        var fundingTxOut = fundingOutput.ToTxOut();
        tx.Outputs.Add(fundingTxOut);

        var requiredAmount = fundingOutput.Amount + transaction.Fee;
        if (totalInputAmount < requiredAmount)
            throw new InsufficientFundsException(requiredAmount, totalInputAmount);

        // Check if we are paying a change address
        if (transaction.ChangeAddress is not null)
        {
            var changeAmount = transaction.ChangeAmount ?? totalInputAmount - requiredAmount;
            if (requiredAmount + changeAmount > totalInputAmount)
                throw new InsufficientFundsException(requiredAmount + changeAmount, totalInputAmount);

            var bitcoinAddress = BitcoinAddress.Create(transaction.ChangeAddress.Address, _network);
            tx.Outputs.Add(new TxOut(Money.Satoshis(changeAmount.Satoshi), bitcoinAddress));
        }

        // Order the outputs per BIP 69 (amount, then scriptPubKey) so the funding output's position is not fixed
        var orderedOutputs = tx.Outputs.OrderBy(o => o.Value.Satoshi)
                               .ThenBy(o => o.ScriptPubKey.ToBytes(), ByteArrayLexicographicComparer.Instance)
                               .ToList();
        tx.Outputs.Clear();
        tx.Outputs.AddRange(orderedOutputs);

        var fundingOutputIndex = tx.Outputs.FindIndex(o => ReferenceEquals(o, fundingTxOut));
        if (fundingOutputIndex < 0)
            throw new InvalidOperationException("Funding output missing after ordering the outputs");
        var txId = tx.GetHash();

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Built funding transaction {TxId} with funding output at index {Index}", txId,
                                   fundingOutputIndex);

        // Return the unsigned transaction (it needs to be signed by the signer afterwards)
        return new FundingTransactionBuildResult(new SignedTransaction(txId.ToBytes(), tx.ToBytes()),
                                                 (ushort)fundingOutputIndex);
    }

    private sealed class ByteArrayLexicographicComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayLexicographicComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}