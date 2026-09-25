using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Payment;

using Domain.Channels.Constants;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Entities.Payment;
using Enums;
using ValueConverters;

public static class ForwardCircuitEntityConfiguration
{
    public static void ConfigureForwardCircuitEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<ForwardCircuitEntity>(entity =>
        {
            // Keyed by the incoming HTLC: an HtlcOrigin.Forwarded names it
            entity.HasKey(e => new { e.IncomingChannelId, e.IncomingHtlcId });

            entity.Property(e => e.IncomingChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.IncomingHtlcId).IsRequired();
            entity.Property(e => e.IncomingAmountMsat).IsRequired();
            entity.Property(e => e.IncomingCltvExpiry).IsRequired();
            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();
            entity.Property(e => e.IncomingSharedSecret).IsRequired();
            entity.Property(e => e.OutgoingShortChannelId)
                  .HasConversion<ShortChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.OutgoingAmountMsat).IsRequired();
            entity.Property(e => e.OutgoingCltvExpiry).IsRequired();
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.OutgoingChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.OutgoingHtlcId).IsRequired(false);
            entity.Property(e => e.ResolvedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired(false);

            // Startup replay reads the unresolved circuits; a downstream resolution finds its circuit by the outgoing
            // HTLC
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.OutgoingChannelId, e.OutgoingHtlcId });

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<ForwardCircuitEntity> entity)
    {
        entity.Property(e => e.IncomingChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.IncomingSharedSecret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
        entity.Property(e => e.OutgoingShortChannelId).HasColumnType($"varbinary({ShortChannelId.Length})");
        entity.Property(e => e.OutgoingChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
    }
}