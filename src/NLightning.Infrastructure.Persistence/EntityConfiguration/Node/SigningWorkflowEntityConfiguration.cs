using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Node;

using Entities.Node;
using Enums;
using ValueConverters;

public static class SigningWorkflowEntityConfiguration
{
    public static void ConfigureSigningWorkflowEntities(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<SigningWorkflowEntity>(entity =>
        {
            entity.HasKey(e => e.WorkflowId);
            entity.Property(e => e.WorkflowId).ValueGeneratedNever();
            entity.Property(e => e.ChannelId).HasConversion<ChannelIdConverter>();
            entity.Property(e => e.ActiveChannelId).HasConversion<ChannelIdConverter>();
            entity.Property(e => e.SnapshotFingerprint).HasMaxLength(32);
            entity.Property(e => e.SignerIdentity).HasMaxLength(33);
            entity.Property(e => e.Network).HasMaxLength(64);
            entity.HasIndex(e => e.ActiveChannelId).IsUnique();
            entity.HasIndex(e => new { e.ChannelId, e.State });
            entity.Property(e => e.State).IsConcurrencyToken();
            if (databaseType == DatabaseType.MicrosoftSql)
            {
                entity.Property(e => e.ChannelId).HasColumnType("varbinary(32)");
                entity.Property(e => e.ActiveChannelId).HasColumnType("varbinary(32)");
                entity.Property(e => e.SnapshotFingerprint).HasColumnType("varbinary(32)");
                entity.Property(e => e.SignerIdentity).HasColumnType("varbinary(33)");
            }
        });
        modelBuilder.Entity<SigningRequestEntity>(entity =>
        {
            entity.HasKey(e => e.RequestId);
            entity.Property(e => e.RequestId).ValueGeneratedNever();
            entity.HasIndex(e => new { e.WorkflowId, e.Ordinal }).IsUnique();
            entity.HasOne<SigningWorkflowEntity>().WithMany().HasForeignKey(e => e.WorkflowId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.ArgumentFingerprint).HasMaxLength(32);
            entity.Property(e => e.State).IsConcurrencyToken();
            if (databaseType == DatabaseType.MicrosoftSql)
            {
                entity.Property(e => e.Envelope).HasColumnType("varbinary(max)");
                entity.Property(e => e.Response).HasColumnType("varbinary(max)");
                entity.Property(e => e.ArgumentFingerprint).HasColumnType("varbinary(32)");
            }
        });
    }
}