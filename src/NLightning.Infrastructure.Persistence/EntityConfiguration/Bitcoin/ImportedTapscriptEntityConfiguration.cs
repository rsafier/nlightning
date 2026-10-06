using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Bitcoin;

using Entities.Bitcoin;
using Enums;

public static class ImportedTapscriptEntityConfiguration
{
    public static void ConfigureImportedTapscriptEntity(this ModelBuilder builder, DatabaseType type)
    {
        builder.Entity<ImportedTapscriptEntity>(entity =>
        {
            entity.ToTable("ImportedTapscripts");
            entity.HasKey(e => e.Script);
            entity.Property(e => e.Script).HasMaxLength(34).IsRequired();
            entity.Property(e => e.InternalKey).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Definition).IsRequired();
            if (type == DatabaseType.MicrosoftSql)
            {
                entity.Property(e => e.Script).HasColumnType("varbinary(34)");
                entity.Property(e => e.InternalKey).HasColumnType("varbinary(32)");
            }
        });
    }
}