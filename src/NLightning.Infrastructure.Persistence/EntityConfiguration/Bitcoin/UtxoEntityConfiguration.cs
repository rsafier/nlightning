using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Bitcoin;

using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Entities.Bitcoin;
using Enums;
using ValueConverters;

public static class UtxoEntityConfiguration
{
    public static void ConfigureUtxoEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<UtxoEntity>(entity =>
        {
            // Set Primary Key
            entity.HasKey(e => new { e.TransactionId, e.Index });

            // Set Required props
            entity.Property(e => e.AmountSats)
                  .IsRequired();
            entity.Property(e => e.BlockHeight)
                  .IsRequired();
            entity.Property(e => e.AddressIndex).IsRequired(false);
            entity.Property(e => e.IsAddressChange).IsRequired(false);
            entity.Property(e => e.AddressType).IsRequired(false);

            // Set Optional props
            entity.Property(e => e.LockedToChannelId)
                  .IsRequired(false)
                  .HasConversion<ChannelIdConverter>();
            entity.Property(e => e.UsedInTransactionId)
                  .IsRequired(false)
                  .HasConversion<TxIdConverter>();

            // Set converters
            entity.Property(x => x.TransactionId)
                  .HasConversion<TxIdConverter>();

            entity.Property(e => e.SilentPaymentTransactionId).HasConversion<TxIdConverter>();
            entity.HasOne(e => e.SilentPayment).WithOne(e => e.Utxo)
                  .HasForeignKey<UtxoEntity>(e => new { e.SilentPaymentTransactionId, e.SilentPaymentIndex })
                  .OnDelete(DeleteBehavior.Restrict);
            var quote = databaseType == DatabaseType.MicrosoftSql ? "[" : "\"";
            var close = databaseType == DatabaseType.MicrosoftSql ? "]" : "\"";
            string Column(string name) => databaseType == DatabaseType.PostgreSql
                ? string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()))
                : quote + name + close;
            var address = Column("AddressIndex");
            var change = Column("IsAddressChange");
            var type = Column("AddressType");
            var spTx = Column("SilentPaymentTransactionId");
            var spIndex = Column("SilentPaymentIndex");
            entity.ToTable(t => t.HasCheckConstraint("CK_Utxos_Ownership",
                $"(({address} IS NOT NULL AND {change} IS NOT NULL AND {type} IS NOT NULL AND {spTx} IS NULL AND {spIndex} IS NULL) OR " +
                $"({address} IS NULL AND {change} IS NULL AND {type} IS NULL AND {spTx} IS NOT NULL AND {spIndex} IS NOT NULL AND {spTx} = {Column("TransactionId")} AND {spIndex} = {Column("Index")}))"));

            // Set indexes
            entity.HasIndex(x => x.AddressType);
            entity.HasIndex(x => new { x.AddressIndex, x.IsAddressChange, x.AddressType });
            entity.HasIndex(x => x.LockedToChannelId);
            entity.HasIndex(x => x.UsedInTransactionId);

            switch (databaseType)
            {
                case DatabaseType.MicrosoftSql:
                    OptimizeConfigurationForSqlServer(entity);
                    break;
                case DatabaseType.PostgreSql:
                    OptimizeConfigurationForPostgres(entity);
                    break;
                case DatabaseType.Sqlite:
                default:
                    // Nothing to be done
                    break;
            }
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<UtxoEntity> entity)
    {
        entity.Property(e => e.SilentPaymentTransactionId).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.TransactionId).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.LockedToChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");

        entity.HasIndex(x => x.AddressType)
              .IsCreatedOnline();
        entity.HasIndex(x => new { x.AddressIndex, x.IsAddressChange, x.AddressType })
              .IsCreatedOnline();
        entity.HasIndex(x => x.LockedToChannelId)
              .IsCreatedOnline();
        entity.HasIndex(x => x.UsedInTransactionId)
              .IsCreatedOnline();
    }

    private static void OptimizeConfigurationForPostgres(EntityTypeBuilder<UtxoEntity> entity)
    {
        entity.HasIndex(x => x.AddressType)
              .IsCreatedConcurrently();
        entity.HasIndex(x => new { x.AddressIndex, x.IsAddressChange, x.AddressType })
              .IsCreatedConcurrently();
        entity.HasIndex(x => x.LockedToChannelId)
              .IsCreatedConcurrently();
        entity.HasIndex(x => x.UsedInTransactionId)
              .IsCreatedConcurrently();
    }
}