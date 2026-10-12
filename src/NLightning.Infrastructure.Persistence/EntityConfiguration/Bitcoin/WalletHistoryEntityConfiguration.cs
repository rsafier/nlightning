using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Bitcoin;

using Entities.Bitcoin;
using Enums;
using ValueConverters;

public static class WalletHistoryEntityConfiguration
{
    public static void ConfigureWalletHistoryEntities(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<WalletHistoryRescanStateEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.CursorHash).HasMaxLength(32);
            entity.Property(e => e.Error).HasMaxLength(1024);
            if (databaseType == DatabaseType.MicrosoftSql)
                entity.Property(e => e.CursorHash).HasColumnType("varbinary(32)");
        });
        modelBuilder.Entity<WalletTransactionLabelEntity>(entity =>
        {
            entity.HasKey(e => e.TransactionId);
            entity.Property(e => e.TransactionId).HasConversion<TxIdConverter>().IsRequired();
            entity.Property(e => e.Label).HasMaxLength(500).IsRequired();
            if (databaseType == DatabaseType.MicrosoftSql)
                entity.Property(e => e.TransactionId).HasColumnType("varbinary(32)");
        });
    }
}