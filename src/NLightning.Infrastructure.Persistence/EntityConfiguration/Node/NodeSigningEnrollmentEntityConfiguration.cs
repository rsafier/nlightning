using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Node;

using Entities.Node;
using Enums;

public static class NodeSigningEnrollmentEntityConfiguration
{
    public static void ConfigureNodeSigningEnrollmentEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<NodeSigningEnrollmentEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.NodeId).HasMaxLength(128);
            entity.Property(e => e.OwnerId).HasMaxLength(128);
            entity.Property(e => e.SignerId).HasMaxLength(128);
            entity.Property(e => e.Network).HasMaxLength(64);
            entity.Property(e => e.NodePublicKey).HasMaxLength(33);
            if (databaseType == DatabaseType.MicrosoftSql)
                entity.Property(e => e.NodePublicKey).HasColumnType("varbinary(33)");
        });
    }
}