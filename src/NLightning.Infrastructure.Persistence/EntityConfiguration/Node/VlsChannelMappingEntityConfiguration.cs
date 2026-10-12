using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Node;

using Domain.Crypto.Constants;
using Entities.Node;
using Enums;
using ValueConverters;

public static class VlsChannelMappingEntityConfiguration
{
    public static void ConfigureVlsChannelMappingEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<VlsChannelMappingEntity>(entity =>
        {
            // No channel FK: allocation must be durable before an opening can create its BOLT channel row.
            entity.HasKey(e => e.KeyIndex);
            entity.Property(e => e.KeyIndex).ValueGeneratedNever();
            entity.Property(e => e.PeerId).HasConversion<CompactPubKeyConverter>();
            entity.Property(e => e.ChannelId).HasConversion<ChannelIdConverter>();
            entity.Property(e => e.SignerIdentity).HasMaxLength(CryptoConstants.CompactPubkeyLen);
            entity.Property(e => e.VlsChannelId).HasMaxLength(41);
            entity.Property(e => e.Network).HasMaxLength(64);
            entity.Property(e => e.AllocationResponse).IsConcurrencyToken();
            entity.HasIndex(e => e.DbId).IsUnique();
            entity.HasIndex(e => e.ChannelId).IsUnique();
            entity.HasIndex(e => e.AllocationRequestId).IsUnique();
            if (databaseType == DatabaseType.PostgreSql)
                entity.Property(e => e.DbId).HasColumnType("numeric(20,0)");
            if (databaseType == DatabaseType.MicrosoftSql)
            {
                entity.Property(e => e.DbId).HasColumnType("decimal(20,0)");
                entity.Property(e => e.PeerId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
                entity.Property(e => e.ChannelId).HasColumnType("varbinary(32)");
                entity.Property(e => e.SignerIdentity).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
                entity.Property(e => e.VlsChannelId).HasColumnType("varbinary(41)");
                entity.Property(e => e.AllocationEnvelope).HasColumnType("varbinary(max)");
                entity.Property(e => e.AllocationResponse).HasColumnType("varbinary(max)");
            }
        });
    }
}