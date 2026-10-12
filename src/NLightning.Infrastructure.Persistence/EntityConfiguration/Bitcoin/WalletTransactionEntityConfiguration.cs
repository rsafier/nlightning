using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Bitcoin;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Crypto.Constants;
using Entities.Bitcoin;
using Enums;
using ValueConverters;

public static class WalletTransactionEntityConfiguration
{
    public static void ConfigureWalletTransactionEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<WalletTransactionEntity>(entity =>
        {
            // Keyed by txid (migration AddWalletTransactions, NL-1187)
            entity.HasKey(e => e.TransactionId);

            entity.Property(e => e.TransactionId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired();
            entity.Property(e => e.RawTransaction).IsRequired();
            entity.Property(e => e.BlockHeight).IsRequired(false);
            entity.Property(e => e.BlockHash)
                  .HasMaxLength(CryptoConstants.Sha256HashLen)
                  .IsRequired(false);
            entity.Property(e => e.Timestamp)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(e => e.OurOutputs).IsRequired();
            entity.Property(e => e.OurInputs).IsRequired();

            // GetTransactions reads a height range; a reorg unconfirms the ones above the fork
            entity.HasIndex(e => e.BlockHeight);

            if (databaseType == DatabaseType.MicrosoftSql)
            {
                entity.Property(e => e.TransactionId)
                      .HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
                entity.Property(e => e.RawTransaction).HasColumnType("varbinary(max)");
                entity.Property(e => e.BlockHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
            }
        });
    }
}