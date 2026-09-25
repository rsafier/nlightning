using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class HtlcEntityConfiguration
{
    public static void ConfigureHtlcEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<HtlcEntity>(entity =>
        {
            // Configure the composite key using ChannelId, HtlcId, and Direction
            entity.HasKey(h => new { h.ChannelId, h.HtlcId, h.Direction });

            // Set required props
            entity.Property(e => e.HtlcId).IsRequired();
            entity.Property(e => e.Direction).IsRequired();
            entity.Property(e => e.AmountMsat).IsRequired();
            entity.Property(e => e.CltvExpiry).IsRequired();
            entity.Property(e => e.State).IsRequired();

            // Required byte[] properties
            entity.Property(h => h.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(h => h.PaymentHash).IsRequired();
            entity.Property(h => h.OnionRoutingPacket).IsRequired();

            // Nullable properties
            entity.Property(h => h.PathKey).IsRequired(false);
            entity.Property(h => h.RemovalKind).IsRequired(false);
            entity.Property(h => h.PaymentPreimage).IsRequired(false);
            entity.Property(h => h.FailReason).IsRequired(false);
            entity.Property(h => h.FailureCode).IsRequired(false);
            entity.Property(h => h.Sha256OfOnion).IsRequired(false);
            entity.Property(h => h.KnownPreimage).IsRequired(false);
            entity.Property(h => h.OnionSharedSecret).IsRequired(false);

            // Origin of an HTLC we offered (migration AddInvoicesPaymentsAndCircuits, ONION M4-T7); the indexes serve
            // the startup replay lookups "which HTLC carries this payment / this forwarded incoming HTLC"
            entity.Property(h => h.OriginKind).IsRequired(false);
            entity.Property(h => h.OriginPaymentHash).IsRequired(false);
            entity.Property(h => h.OriginIncomingChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired(false);
            entity.Property(h => h.OriginIncomingHtlcId).IsRequired(false);
            entity.HasIndex(h => h.OriginPaymentHash);
            entity.HasIndex(h => new { h.OriginIncomingChannelId, h.OriginIncomingHtlcId });

            if (databaseType == DatabaseType.MicrosoftSql)
            {
                OptimizeConfigurationForSqlServer(entity);
            }
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<HtlcEntity> entity)
    {
        entity.Property(h => h.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(h => h.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(h => h.PaymentPreimage).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
        entity.Property(h => h.OnionRoutingPacket).HasColumnType("varbinary(max)");
        entity.Property(h => h.PathKey).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(h => h.FailReason).HasColumnType("varbinary(max)");
        entity.Property(h => h.Sha256OfOnion).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(h => h.KnownPreimage).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
        entity.Property(h => h.OnionSharedSecret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
        entity.Property(h => h.OriginPaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(h => h.OriginIncomingChannelId)
              .HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
    }
}