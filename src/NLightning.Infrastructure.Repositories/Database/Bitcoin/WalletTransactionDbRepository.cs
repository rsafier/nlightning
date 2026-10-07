using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Bitcoin;

using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

/// <summary>The wallet's durable transaction history (NL-1187, table <c>WalletTransactions</c>).</summary>
public sealed class WalletTransactionDbRepository(NLightningDbContext context)
    : BaseDbRepository<WalletTransactionEntity>(context), IWalletTransactionDbRepository
{
    /// <inheritdoc />
    public async Task StageConfirmedAsync(WalletTransactionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var entity = await DbSet.FindAsync(record.TxId);
        if (entity is null)
        {
            Insert(new WalletTransactionEntity
            {
                TransactionId = record.TxId,
                RawTransaction = record.RawTransaction,
                BlockHeight = record.BlockHeight,
                BlockHash = record.BlockHash,
                Timestamp = record.Timestamp,
                OurOutputs = EncodeOutputs(record.OurOutputs),
                OurInputs = EncodeInputs(record.OurInputs)
            });
            return;
        }

        // Ownership is a property of the transaction and the wallet, not of the block: keep what either description
        // found (a replayed block's spends no longer find the outputs its first pass removed)
        var outputs = DecodeOutputs(entity.OurOutputs).Union(record.OurOutputs).Order().ToList();
        var inputs = DecodeInputs(entity.OurInputs).ToDictionary(i => i.InputIndex);
        foreach (var input in record.OurInputs)
            inputs.TryAdd(input.InputIndex, input);

        entity.RawTransaction = record.RawTransaction;
        entity.BlockHeight = record.BlockHeight;
        entity.BlockHash = record.BlockHash;
        entity.Timestamp = record.Timestamp;
        entity.OurOutputs = EncodeOutputs(outputs);
        entity.OurInputs = EncodeInputs(inputs.Values.OrderBy(i => i.InputIndex));
    }

    /// <inheritdoc />
    public async Task<int> UnconfirmAboveAsync(uint height)
    {
        var entities = await DbSet.Where(e => e.BlockHeight != null && e.BlockHeight > height).ToListAsync();
        foreach (var entity in entities)
        {
            entity.BlockHeight = null;
            entity.BlockHash = null;
        }

        return entities.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WalletTransactionRecord>> GetHistoryAsync(uint startHeight, uint endHeight,
                                                                               bool includeUnconfirmed,
                                                                               CancellationToken cancellationToken)
    {
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => (e.BlockHeight != null && e.BlockHeight >= startHeight
                                                                     && e.BlockHeight <= endHeight)
                                           || (includeUnconfirmed && e.BlockHeight == null))
                                  .ToListAsync(cancellationToken);
        return entities.Select(e => new WalletTransactionRecord(e.TransactionId, e.RawTransaction, e.BlockHeight,
                                                                e.BlockHash, e.Timestamp,
                                                                DecodeOutputs(e.OurOutputs),
                                                                DecodeInputs(e.OurInputs)))
                       .ToList();
    }

    internal static string EncodeOutputs(IEnumerable<uint> outputs) =>
        string.Join(',', outputs.Select(o => o.ToString(CultureInfo.InvariantCulture)));

    internal static string EncodeInputs(IEnumerable<WalletTransactionInput> inputs) =>
        string.Join(',', inputs.Select(i => string.Create(CultureInfo.InvariantCulture,
                                                          $"{i.InputIndex}:{i.AmountSat}")));

    internal static List<uint> DecodeOutputs(string text) =>
        text.Length == 0
            ? []
            : text.Split(',').Select(o => uint.Parse(o, CultureInfo.InvariantCulture)).ToList();

    internal static List<WalletTransactionInput> DecodeInputs(string text)
    {
        if (text.Length == 0)
            return [];

        var inputs = new List<WalletTransactionInput>();
        foreach (var part in text.Split(','))
        {
            var separator = part.IndexOf(':');
            inputs.Add(new WalletTransactionInput(uint.Parse(part.AsSpan(0, separator), CultureInfo.InvariantCulture),
                                                  long.Parse(part.AsSpan(separator + 1),
                                                             CultureInfo.InvariantCulture)));
        }

        return inputs;
    }
}