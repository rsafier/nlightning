using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Bitcoin;

using Entities.Bitcoin;
using Enums;
using ValueConverters;

public static class SilentPaymentEntityConfiguration
{
    public static void ConfigureSilentPaymentEntities(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<SilentPaymentOutputEntity>(entity =>
        {
            entity.HasKey(e => new { e.TransactionId, e.Index });
            entity.Property(e => e.TransactionId).HasConversion<TxIdConverter>();
            entity.Property(e => e.BlockHash).HasConversion<HashConverter>();
            entity.Property(e => e.SpentByTransactionId).HasConversion<TxIdConverter>();
            entity.Property(e => e.OutputKey).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Tweak).HasMaxLength(32).IsRequired();
            entity.HasIndex(e => e.BlockHeight);
            entity.HasIndex(e => e.SpentByTransactionId);
            if (databaseType == DatabaseType.MicrosoftSql)
            {
                entity.Property(e => e.TransactionId).HasColumnType("varbinary(32)");
                entity.Property(e => e.BlockHash).HasColumnType("varbinary(32)");
                entity.Property(e => e.SpentByTransactionId).HasColumnType("varbinary(32)");
                entity.Property(e => e.OutputKey).HasColumnType("varbinary(32)");
                entity.Property(e => e.Tweak).HasColumnType("varbinary(32)");
            }
        });
        modelBuilder.Entity<SilentPaymentLabelEntity>(entity =>
        {
            entity.HasKey(e => e.M);
            entity.Property(e => e.M).ValueGeneratedNever();
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
        });
        modelBuilder.Entity<SilentPaymentScanStateEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.RescanCursorHash).HasConversion<HashConverter>();
            entity.Property(e => e.LiveCursorHash).HasConversion<HashConverter>();
            entity.Property(e => e.PrevoutSource).HasMaxLength(128);
            if (databaseType == DatabaseType.MicrosoftSql)
            {
                entity.Property(e => e.RescanCursorHash).HasColumnType("varbinary(32)");
                entity.Property(e => e.LiveCursorHash).HasColumnType("varbinary(32)");
            }
            var id = databaseType == DatabaseType.PostgreSql ? "id" : databaseType == DatabaseType.MicrosoftSql ? "[Id]" : "\"Id\"";
            entity.ToTable(t => t.HasCheckConstraint("CK_SilentPaymentScanState_Singleton", $"{id} = 0"));
        });
    }
}