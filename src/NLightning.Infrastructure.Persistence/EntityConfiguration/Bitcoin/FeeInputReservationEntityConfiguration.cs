using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Bitcoin;

using Domain.Bitcoin.Transactions.Constants;
using Entities.Bitcoin;
using Enums;
using ValueConverters;

public static class FeeInputReservationEntityConfiguration
{
    /// <summary>The longest <see cref="FeeInputReservationEntity.Purpose"/>.</summary>
    public const int PurposeMaxLength = 128;

    /// <summary>The longest stored script (a P2WPKH or P2TR scriptPubKey is 22 or 34 bytes).</summary>
    public const int ScriptMaxLength = 64;

    public static void ConfigureFeeInputReservationEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        // Migration AddFeeInputReservations (BOLT 5 plan O7-T1)
        modelBuilder.Entity<FeeInputReservationEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Purpose)
                  .HasMaxLength(PurposeMaxLength)
                  .IsRequired();
            entity.Property(e => e.FeeSats).IsRequired();
            entity.Property(e => e.ChangeAmountSats).IsRequired();
            entity.Property(e => e.ChangeScript)
                  .HasMaxLength(ScriptMaxLength)
                  .IsRequired(false);
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            entity.HasMany(e => e.Inputs)
                  .WithOne()
                  .HasForeignKey(i => i.ReservationId)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                entity.Property(e => e.ChangeScript).HasColumnType($"varbinary({ScriptMaxLength})");
        });

        modelBuilder.Entity<FeeInputReservationInputEntity>(entity =>
        {
            // One reservation per outpoint
            entity.HasKey(e => new { e.TransactionId, e.Index });
            entity.Property(e => e.TransactionId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired();
            entity.Property(e => e.ReservationId).IsRequired();
            entity.Property(e => e.AmountSats).IsRequired();
            entity.Property(e => e.AddressType).IsRequired();
            entity.Property(e => e.ScriptPubKey)
                  .HasMaxLength(ScriptMaxLength)
                  .IsRequired();
            entity.Property(e => e.Position).IsRequired();

            entity.HasIndex(e => e.ReservationId);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeInputConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeInputConfigurationForSqlServer(EntityTypeBuilder<FeeInputReservationInputEntity> entity)
    {
        entity.Property(e => e.TransactionId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.ScriptPubKey).HasColumnType($"varbinary({ScriptMaxLength})");
    }
}