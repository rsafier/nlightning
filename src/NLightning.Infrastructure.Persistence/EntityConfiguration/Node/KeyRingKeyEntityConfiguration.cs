using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Node;

using Entities.Node;
using Enums;

public static class KeyRingKeyEntityConfiguration
{
    public static void ConfigureKeyRingKeyEntity(this ModelBuilder builder, DatabaseType type)
    {
        builder.Entity<KeyRingKeyEntity>(entity =>
        {
            entity.ToTable("KeyRingKeys");
            entity.HasKey(e => new { e.Family, e.Index });
            entity.Property(e => e.Index).ValueGeneratedNever();
            entity.Property(e => e.PublicKey).IsRequired().HasMaxLength(33);
            if (type == DatabaseType.MicrosoftSql)
                entity.Property(e => e.PublicKey).HasColumnType("varbinary(33)");
            entity.HasIndex(e => e.PublicKey).IsUnique();
        });
    }
}